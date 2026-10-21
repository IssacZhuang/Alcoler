using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Tools;

using Alcoler.Kernel.Models;
using Alcoler.Kernel.Runtime;

namespace Alcoler.Adapters.Models;

public sealed record AnthropicModelOptions(
    string Model,
    string ApiKey,
    string BaseUrl = "https://api.anthropic.com")
{
    public string MessagesUrl => $"{AnthropicEndpoint.NormalizeBaseUrl(BaseUrl)}/messages";
}

public sealed class AnthropicChatModel(HttpClient httpClient, AnthropicModelOptions options, ModelProperties? properties = null, string? providerId = null) : IChatModel
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxTokens = 32768;
    private readonly ModelProperties _requestProperties = properties ?? new ModelProperties();
    private bool _isRequestScope;
    private bool _signatureRepairAttempted;
    private IReadOnlyList<ModelInputMessage>? _requestMessages;

    public ModelDescriptor Descriptor { get; } = new(
        providerId ?? "anthropic",
        options.Model,
        options.Model,
        properties ?? new ModelProperties(ContextWindowTokens: 200_000, DefaultMaxOutputTokens: DefaultMaxTokens));

    public void ValidateOptions(ModelCallOptions callOptions) => ModelRequestOptions.Validate(_requestProperties, callOptions, DefaultMaxTokens);

    public bool InlineToolResultImages => true;

    public IChatModel CreateRequestScope() => new AnthropicChatModel(httpClient, options, properties, providerId)
    {
        _isRequestScope = true,
    };

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_isRequestScope)
        {
            await foreach (var streamEvent in CreateRequestScope().StreamAsync(request, cancellationToken).ConfigureAwait(false))
                yield return streamEvent;
            yield break;
        }
        _requestMessages ??= ReasoningHistoryNormalizer.Normalize(request.Messages, keepReasoning: true,
            Descriptor.Properties.SupportsImage, Descriptor);
        request = request with { Messages = _requestMessages };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var state = new AttemptState();
            var pending = new List<ModelStreamEvent>();
            var committed = false;
            var repair = false;
            await foreach (var streamEvent in StreamAttemptAsync(request, state, cancellationToken).ConfigureAwait(false))
            {
                if (streamEvent is ModelStreamFailed failed && !committed && !_signatureRepairAttempted &&
                    state.StatusCode == 400 && ThinkingSignatureRecovery.IsSignatureRejection(failed.Failure, state.StatusCode.Value))
                {
                    var repaired = ThinkingSignatureRecovery.RepairRequestMessages(request.Messages);
                    if (!ReferenceEquals(repaired, request.Messages))
                    {
                        _signatureRepairAttempted = true;
                        _requestMessages = repaired;
                        request = request with { Messages = repaired };
                        repair = true;
                        break;
                    }
                }
                var retrySafe = streamEvent switch
                {
                    ModelTextDelta delta => delta.Delta.Length == 0,
                    ModelReasoningDelta delta => delta.Delta.Length == 0,
                    ModelStreamStarted or ModelTextStarted or ModelTextEnded or ModelReasoningStarted or
                        ModelReasoningEnded or ModelToolInputStarted or ModelToolInputDelta or ModelToolInputEnded => true,
                    _ => false,
                };
                if (!committed && retrySafe)
                {
                    pending.Add(streamEvent);
                    continue;
                }
                foreach (var prelude in pending)
                    yield return prelude;
                pending.Clear();
                committed = true;
                yield return streamEvent;
            }
            if (!repair)
                yield break;
        }
    }

    public bool SupportsNonStreamingGeneration => true;

    public async ValueTask<IReadOnlyList<ModelStreamEvent>> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        request = request with
        {
            Messages = ReasoningHistoryNormalizer.Normalize(request.Messages, keepReasoning: true,
                Descriptor.Properties.SupportsImage, Descriptor),
        };
        for (var attempt = 0; ; attempt++)
        {
            var events = await ModelHttpGeneration.SendAsync(httpClient, () => BuildRequest(request, stream: false),
                DecodeGeneration, cancellationToken).ConfigureAwait(false);
            if (attempt == 0 && events.OfType<ModelStreamFailed>().FirstOrDefault()?.Failure is { HttpStatusCode: 400 } failure &&
                ThinkingSignatureRecovery.IsSignatureRejection(failure, 400))
            {
                var repaired = ThinkingSignatureRecovery.RepairRequestMessages(request.Messages);
                if (!ReferenceEquals(repaired, request.Messages))
                {
                    request = request with { Messages = repaired };
                    continue;
                }
            }
            return events;
        }
    }

    private static IReadOnlyList<ModelStreamEvent> DecodeGeneration(JsonElement root)
    {
        var state = new AnthropicStreamState();
        var events = new List<ModelStreamEvent>();
        void Dispatch(object value) => events.AddRange(state.Dispatch(JsonSerializer.SerializeToElement(value)));
        Dispatch(new { type = "message_start", message = root });
        var index = 0;
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            var type = block.GetProperty("type").GetString();
            Dispatch(new { type = "content_block_start", index, content_block = block });
            if (type == "text")
                Dispatch(new { type = "content_block_delta", index, delta = new { type = "text_delta", text = block.GetProperty("text").GetString() } });
            else if (type == "thinking")
                Dispatch(new { type = "content_block_delta", index, delta = new { type = "thinking_delta", thinking = block.GetProperty("thinking").GetString() } });
            else if (type == "tool_use")
                Dispatch(new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = block.GetProperty("input").GetRawText() } });
            Dispatch(new { type = "content_block_stop", index });
            index++;
        }
        Dispatch(new { type = "message_delta", delta = new { stop_reason = root.TryGetProperty("stop_reason", out var reason) ? reason.GetString() : null } });
        Dispatch(new { type = "message_stop" });
        return events;
    }

    private sealed class AttemptState
    {
        public int? StatusCode { get; set; }
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamAttemptAsync(
        ModelRequest request,
        AttemptState attemptState,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpRequestMessage? built = null;
        ModelFailure? validationFailure = null;
        try { built = BuildRequest(request); }
        catch (ArgumentException ex) { validationFailure = new ModelFailure(ModelFailureKind.InvalidRequest, ex.Message, Phase: ModelFailurePhase.RequestSetup); }
        if (validationFailure is not null)
        {
            yield return new ModelStreamFailed(validationFailure);
            yield break;
        }
        using var httpRequest = built!;
        HttpResponseMessage? response = null;
        ModelFailure? sendFailure = null;
        try
        {
            response = await httpClient.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sendFailure = ModelFailureClassifier.FromException(ex, cancellationToken) with { Phase = ModelFailurePhase.RequestSetup };
        }
        if (sendFailure is not null)
        {
            yield return new ModelStreamFailed(sendFailure);
            yield break;
        }

        using (response!)
        {
            attemptState.StatusCode = (int)response!.StatusCode;
            var state = new AnthropicStreamState();
            await foreach (var frame in SseHttp.ReadFramesAsync(response!, cancellationToken).ConfigureAwait(false))
            {
                if (frame.Event == SseHttp.FailureEventName)
                {
                    yield return new ModelStreamFailed(SseHttp.DeserializeFailure(frame.Data));
                    yield break;
                }
                if (frame.Data.Length == 0 || frame.Data is "[DONE]")
                    continue;

                ModelFailure? dispatchFailure = null;
                IEnumerable<ModelStreamEvent> events = [];
                try
                {
                    using var document = JsonDocument.Parse(frame.Data);
                    events = state.Dispatch(document.RootElement).ToList();
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    dispatchFailure = new ModelFailure(ModelFailureKind.StreamProtocolError, ex.Message,
                        Phase: ModelFailurePhase.ResponseBody, HttpStatusCode: (int)response.StatusCode);
                }
                if (dispatchFailure is not null)
                {
                    yield return new ModelStreamFailed(dispatchFailure);
                    yield break;
                }
                foreach (var e in events)
                    yield return e;
                if (state.SawTerminal)
                    yield break;
            }
            if (!state.SawTerminal)
                yield return new ModelStreamFailed(new ModelFailure(
                    ModelFailureKind.StreamProtocolError, "anthropic stream ended without message_stop",
                    Phase: ModelFailurePhase.ResponseBody, HttpStatusCode: (int)response.StatusCode));
        }
    }

    private HttpRequestMessage BuildRequest(ModelRequest request, bool stream = true)
    {
        var messages = request.Messages;
        ModelRequestOptions.ValidateRequest(request with { Messages = messages }, Descriptor.Properties);
        var (conversation, systemBlocks) = ModelRequestSystemBlocks.ExtractLeading(messages, Descriptor.Properties.SupportsMidConversationSystem);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["stream"] = stream,
            ["messages"] = conversation.Select(ToProviderMessage).ToList(),
        };
        if (systemBlocks.Count > 0)
            payload["system"] = systemBlocks.Select(b => new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = b,
                ["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" },
            }).ToList();
        if (request.Tools.Count > 0)
            payload["tools"] = request.Tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = ModelRequestOptions.ToolSchema(t.InputSchema, Descriptor.Properties),
            }).ToList();

        var message = new HttpRequestMessage(HttpMethod.Post, options.MessagesUrl)
        {
            Content = new StringContent(
                ModelRequestOptions.Encode(payload, _requestProperties, request.Options, "max_tokens", DefaultMaxTokens), Encoding.UTF8, "application/json"),
        };
        AnthropicEndpoint.AddAuthHeaders(message, options.ApiKey);
        message.Headers.Add("anthropic-version", AnthropicVersion);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return message;
    }

    private static object ToProviderMessage(ModelInputMessage message)
    {
        var blocks = new List<Dictionary<string, object?>>(message.Content.Count);
        foreach (var block in message.Content)
        {
            if (block is InputReasoningBlock { Text.Length: 0, Signature: null, RedactedData: null, EncryptedContent: null, ProviderItemId: null })
                continue;
            Dictionary<string, object?> providerBlock = block switch
            {
                InputTextBlock text => new() { ["type"] = "text", ["text"] = text.Text },
                InputReasoningBlock { Signature: null, RedactedData: not null } reasoning => new()
                {
                    ["type"] = "redacted_thinking",
                    ["data"] = reasoning.RedactedData,
                },
                InputReasoningBlock reasoning => new()
                {
                    ["type"] = "thinking",
                    ["thinking"] = reasoning.Text,
                    ["signature"] = reasoning.Signature ?? "",
                },
                InputImageBlock image => ToProviderImage(image),
                InputToolCallBlock toolCall => new()
                {
                    ["type"] = "tool_use",
                    ["id"] = toolCall.Id.Value,
                    ["name"] = toolCall.Name,
                    ["input"] = toolCall.Input,
                },
                InputToolResultBlock toolResult => new()
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = toolResult.Id.Value,
                    ["content"] = ToolResultContent(toolResult),
                    ["is_error"] = toolResult.IsError,
                },
                _ => throw new InvalidOperationException($"unsupported content block {block.GetType().Name}"),
            };
            blocks.Add(providerBlock);
        }
        if (message.CacheControl is not null && blocks.Count > 0)
            blocks[^1]["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" };

        if (message.Role == MessageRole.System && message.CacheControl is null &&
            blocks.Count == 1 && blocks[0].GetValueOrDefault("type") is "text")
            return new Dictionary<string, object?> { ["role"] = "system", ["content"] = blocks[0]["text"] };

        return new Dictionary<string, object?>
        {
            ["role"] = message.Role switch
            {
                MessageRole.User => "user",
                MessageRole.System => "system",
                _ => "assistant",
            },
            ["content"] = blocks,
        };
    }

    private static Dictionary<string, object?> ToProviderImage(InputImageBlock image) => new()
    {
        ["type"] = "image",
        ["source"] = new Dictionary<string, object?>
        {
            ["type"] = "base64",
            ["media_type"] = image.MimeType,
            ["data"] = image.Base64Data,
        },
    };

    private static object ToolResultContent(InputToolResultBlock result)
    {
        if (result.Images is not { Count: > 0 })
            return result.Content;
        var content = new List<Dictionary<string, object?>>();
        if (result.Content.Length > 0)
            content.Add(new() { ["type"] = "text", ["text"] = result.Content });
        content.AddRange(result.Images.Select(ToProviderImage));
        return content;
    }

    private sealed class AnthropicStreamState
    {
        private readonly Dictionary<int, OpenBlock> _blocks = [];
        private int? _inputTokens;
        private int? _outputTokens;
        private int? _cacheReadTokens;
        private int? _cacheWriteTokens;
        private string? _stopReason;

        public bool SawTerminal { get; private set; }

        public IEnumerable<ModelStreamEvent> Dispatch(JsonElement root)
        {
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("usage", out var usage))
                        ReadUsage(usage, includeOutput: true);
                    yield return new ModelStreamStarted();
                    yield break;

                case "content_block_start":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        var block = root.GetProperty("content_block");
                        var blockType = block.GetProperty("type").GetString();
                        switch (blockType)
                        {
                            case "text":
                                _blocks[index] = new OpenBlock("text");
                                yield return new ModelTextStarted();
                                break;
                            case "thinking":
                                _blocks[index] = new OpenBlock("thinking")
                                {
                                    Signature = block.TryGetProperty("signature", out var signature) ? signature.GetString() : null,
                                };
                                yield return new ModelReasoningStarted();
                                break;
                            case "redacted_thinking":
                                _blocks[index] = new OpenBlock("redacted_thinking")
                                {
                                    RedactedData = block.GetProperty("data").GetString(),
                                };
                                yield return new ModelReasoningStarted();
                                break;
                            case "tool_use":
                                {
                                    var id = new ToolCallId(block.GetProperty("id").GetString()!);
                                    var name = block.GetProperty("name").GetString()!;
                                    _blocks[index] = new OpenBlock("tool_use") { ToolId = id, ToolName = name, Json = new() };
                                    yield return new ModelToolInputStarted(id, name);
                                    break;
                                }
                        }
                        yield break;
                    }

                case "content_block_delta":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        var delta = root.GetProperty("delta");
                        var deltaType = delta.GetProperty("type").GetString();
                        var open = _blocks.GetValueOrDefault(index);
                        switch (deltaType)
                        {
                            case "text_delta":
                                yield return new ModelTextDelta(delta.GetProperty("text").GetString() ?? "");
                                break;
                            case "thinking_delta":
                                yield return new ModelReasoningDelta(delta.GetProperty("thinking").GetString() ?? "");
                                break;
                            case "signature_delta":
                                if (open is not null)
                                    open.Signature = delta.GetProperty("signature").GetString();
                                break;
                            case "input_json_delta":
                                {
                                    var partialJson = delta.GetProperty("partial_json").GetString() ?? "";
                                    if (open?.ToolId is { } toolId)
                                    {
                                        open.Json!.Append(partialJson);
                                        yield return new ModelToolInputDelta(toolId, partialJson);
                                    }
                                    break;
                                }
                        }
                        yield break;
                    }

                case "content_block_stop":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        if (!_blocks.Remove(index, out var open))
                            yield break;
                        switch (open.Kind)
                        {
                            case "text":
                                yield return new ModelTextEnded();
                                break;
                            case "thinking":
                            case "redacted_thinking":
                                yield return new ModelReasoningEnded(open.Signature, open.RedactedData);
                                break;
                            case "tool_use":
                                {
                                    var toolId = open.ToolId!.Value;
                                    yield return new ModelToolInputEnded(toolId);
                                    yield return new ModelToolCallReady(new ToolCall(toolId, open.ToolName!, ParseInput(open.Json!)));
                                    break;
                                }
                        }
                        yield break;
                    }

                case "message_delta":
                    if (root.TryGetProperty("delta", out var stopDelta) &&
                        stopDelta.TryGetProperty("stop_reason", out var stopReason) &&
                        stopReason.ValueKind == JsonValueKind.String)
                        _stopReason = stopReason.GetString();
                    if (root.TryGetProperty("usage", out var deltaUsage))
                        ReadUsage(deltaUsage, includeOutput: true);
                    yield break;

                case "message_stop":
                    SawTerminal = true;
                    yield return new ModelStreamFinished(MapStopReason(_stopReason), BuildUsage(), _stopReason,
                        ContextWindowExceeded: _stopReason == "model_context_window_exceeded");
                    yield break;

                case "error":
                    {
                        SawTerminal = true;
                        var error = root.GetProperty("error");
                        var errorType = error.TryGetProperty("type", out var t) ? t.GetString() : null;
                        var errorMessage = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                        yield return new ModelStreamFailed(new ModelFailure(
                            MapErrorKind(errorType), errorMessage ?? "anthropic stream error", errorType, Phase: ModelFailurePhase.ResponseBody));
                        yield break;
                    }
            }
        }

        private void ReadUsage(JsonElement usage, bool includeOutput)
        {
            _inputTokens = SseHttp.ReadInt(usage, "input_tokens") ?? _inputTokens;
            _cacheReadTokens = SseHttp.ReadInt(usage, "cache_read_input_tokens") ?? _cacheReadTokens;
            _cacheWriteTokens = SseHttp.ReadInt(usage, "cache_creation_input_tokens") ?? _cacheWriteTokens;
            if (includeOutput)
                _outputTokens = SseHttp.ReadInt(usage, "output_tokens") ?? _outputTokens;
        }

        private TokenUsage? BuildUsage()
        {
            if (_inputTokens is null && _outputTokens is null && _cacheReadTokens is null && _cacheWriteTokens is null)
                return null;
            var contextUsage = (_inputTokens ?? 0) + (_cacheReadTokens ?? 0) + (_cacheWriteTokens ?? 0);
            return new TokenUsage(
                InputTokens: _inputTokens,
                OutputTokens: _outputTokens,
                ContextUsageTokens: contextUsage,
                CacheReadTokens: _cacheReadTokens,
                CacheWriteTokens: _cacheWriteTokens);
        }

        private static ModelFinishReason MapStopReason(string? stopReason) => stopReason switch
        {
            "end_turn" or "stop_sequence" => ModelFinishReason.Stop,
            "tool_use" => ModelFinishReason.ToolCalls,
            "max_tokens" => ModelFinishReason.Length,
            "refusal" => ModelFinishReason.ContentFilter,
            _ => ModelFinishReason.Other,
        };

        private static ModelFailureKind MapErrorKind(string? errorType) => errorType switch
        {
            "rate_limit_error" => ModelFailureKind.RateLimited,
            "authentication_error" or "permission_error" => ModelFailureKind.AuthenticationFailed,
            "invalid_request_error" => ModelFailureKind.InvalidRequest,
            "timeout_error" => ModelFailureKind.Timeout,
            "api_error" or "overloaded_error" => ModelFailureKind.ProviderError,
            _ => ModelFailureKind.ProviderError,
        };
    }

    private sealed class OpenBlock(string kind)
    {
        public string Kind { get; } = kind;
        public ToolCallId? ToolId { get; set; }
        public string? ToolName { get; set; }
        public StringBuilder? Json { get; set; }
        public string? Signature { get; set; }
        public string? RedactedData { get; set; }
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement ParseInput(StringBuilder json)
    {
        var text = json.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return EmptyObject;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject;
        }
    }
}
