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

namespace Alcoler.Adapters.Models;

public sealed record AnthropicModelOptions(
    string Model,
    string ApiKey,
    string BaseUrl = "https://api.anthropic.com")
{
    public string MessagesUrl => $"{BaseUrl.TrimEnd('/')}/v1/messages";
}

public sealed class AnthropicChatModel(HttpClient httpClient, AnthropicModelOptions options, ModelProperties? properties = null) : IChatModel
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxTokens = 32768;

    public ModelDescriptor Descriptor { get; } = new(
        "anthropic",
        options.Model,
        options.Model,
        properties ?? new ModelProperties(ContextWindowTokens: 200_000, DefaultMaxOutputTokens: DefaultMaxTokens));

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = BuildRequest(request);
        HttpResponseMessage? response = null;
        ModelFailure? sendFailure = null;
        try
        {
            response = await httpClient.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sendFailure = ModelFailureClassifier.FromException(ex, cancellationToken);
        }
        if (sendFailure is not null)
        {
            yield return new ModelStreamFailed(sendFailure);
            yield break;
        }

        using (response!)
        {
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
                catch (JsonException ex)
                {
                    dispatchFailure = new ModelFailure(ModelFailureKind.StreamProtocolError, ex.Message);
                }
                if (dispatchFailure is not null)
                {
                    yield return new ModelStreamFailed(dispatchFailure);
                    yield break;
                }
                foreach (var e in events)
                    yield return e;
            }
            if (!state.SawTerminal)
                yield return new ModelStreamFailed(new ModelFailure(
                    ModelFailureKind.StreamProtocolError, "anthropic stream ended without message_stop"));
        }
    }

    private HttpRequestMessage BuildRequest(ModelRequest request)
    {
        var (conversation, systemBlocks) = ModelRequestSystemBlocks.Extract(request.Messages);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["max_tokens"] = request.Options.MaxOutputTokens ?? Descriptor.Properties.DefaultMaxOutputTokens ?? DefaultMaxTokens,
            ["stream"] = true,
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
                ["input_schema"] = t.InputSchema,
            }).ToList();

        var message = new HttpRequestMessage(HttpMethod.Post, options.MessagesUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("x-api-key", options.ApiKey);
        message.Headers.Add("anthropic-version", AnthropicVersion);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return message;
    }

    private static object ToProviderMessage(ModelInputMessage message)
    {
        var blocks = new List<Dictionary<string, object?>>(message.Content.Count);
        foreach (var block in message.Content)
        {
            Dictionary<string, object?> providerBlock = block switch
            {
                InputTextBlock text => new() { ["type"] = "text", ["text"] = text.Text },
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
                    ["content"] = toolResult.Content,
                    ["is_error"] = toolResult.IsError,
                },
                _ => throw new InvalidOperationException($"unsupported content block {block.GetType().Name}"),
            };
            blocks.Add(providerBlock);
        }
        if (message.CacheControl is not null && blocks.Count > 0)
            blocks[^1]["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" };

        return new Dictionary<string, object?>
        {
            ["role"] = message.Role == MessageRole.User ? "user" : "assistant",
            ["content"] = blocks,
        };
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
                            _blocks[index] = new OpenBlock("thinking");
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
                            yield return new ModelReasoningEnded(open.Signature);
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
                    yield return new ModelStreamFinished(MapStopReason(_stopReason), BuildUsage());
                    yield break;

                case "error":
                {
                    var error = root.GetProperty("error");
                    var errorType = error.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var errorMessage = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    yield return new ModelStreamFailed(new ModelFailure(
                        MapErrorKind(errorType), errorMessage ?? "anthropic stream error", errorType));
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
            "end_turn" or "stop_sequence" or null => ModelFinishReason.Stop,
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
    }

    private static JsonElement ParseInput(StringBuilder json)
    {
        var text = json.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        }
    }
}
