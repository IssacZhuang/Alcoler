using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Models;

namespace Alcoler.Adapters.Models;

public sealed record OpenAiResponsesModelOptions(string Model, string ApiKey, string BaseUrl)
{
    public string ResponsesUrl => $"{BaseUrl.TrimEnd('/')}/responses";
}

public sealed class OpenAiResponsesChatModel(
    HttpClient httpClient,
    OpenAiResponsesModelOptions options,
    ModelProperties? properties = null,
    string? providerId = null,
    Func<CancellationToken, ValueTask<string>>? accessTokenSource = null) : IChatModel
{
    private const int DefaultMaxTokens = 32768;
    private readonly ModelProperties _requestProperties = properties ?? new ModelProperties();
    // ChatGPT account access: the bearer token is resolved per request because it
    // expires and rotates; options.ApiKey is only a placeholder for the static path.
    private readonly Func<CancellationToken, ValueTask<string>>? _accessTokenSource = accessTokenSource;

    private bool IsAccount => _accessTokenSource is not null;

    public ModelDescriptor Descriptor { get; } = new(providerId ?? "openai-responses", options.Model, options.Model,
        properties ?? new ModelProperties(ContextWindowTokens: 128_000, DefaultMaxOutputTokens: DefaultMaxTokens));

    public void ValidateOptions(ModelCallOptions callOptions) => ModelRequestOptions.Validate(_requestProperties, callOptions, DefaultMaxTokens);

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpRequestMessage? built = null;
        ModelFailure? failure = null;
        var apiKey = options.ApiKey;
        if (_accessTokenSource is { } resolveToken)
        {
            try { apiKey = await resolveToken(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = new ModelFailure(ModelFailureKind.AuthenticationFailed, ex.Message,
                    Phase: ModelFailurePhase.RequestSetup);
            }
            if (failure is not null)
            {
                yield return new ModelStreamFailed(failure);
                yield break;
            }
        }
        try { built = BuildRequest(request, apiKey: apiKey); }
        catch (ArgumentException ex) { failure = new ModelFailure(ModelFailureKind.InvalidRequest, ex.Message, Phase: ModelFailurePhase.RequestSetup); }
        if (failure is not null)
        {
            yield return new ModelStreamFailed(failure);
            yield break;
        }
        using var httpRequest = built!;
        HttpResponseMessage? response = null;
        try { response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { failure = ModelFailureClassifier.FromException(ex, cancellationToken) with { Phase = ModelFailurePhase.RequestSetup }; }
        if (failure is not null)
        {
            yield return new ModelStreamFailed(failure);
            yield break;
        }
        using (response!)
        {
            var state = new ResponsesStreamState();
            await foreach (var frame in SseHttp.ReadFramesAsync(response!, cancellationToken).ConfigureAwait(false))
            {
                if (frame.Event == SseHttp.FailureEventName)
                {
                    yield return new ModelStreamFailed(SseHttp.DeserializeFailure(frame.Data));
                    yield break;
                }
                if (frame.Data.Length == 0 || frame.Data == "[DONE]") continue;
                List<ModelStreamEvent> events = [];
                try
                {
                    using var document = JsonDocument.Parse(frame.Data);
                    events = state.Dispatch(document.RootElement, frame.Event).ToList();
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    failure = new ModelFailure(ModelFailureKind.StreamProtocolError, $"Invalid Responses stream: {ex.Message}",
                        Phase: ModelFailurePhase.ResponseBody, HttpStatusCode: (int)response!.StatusCode);
                }
                if (failure is not null)
                {
                    yield return new ModelStreamFailed(failure);
                    yield break;
                }
                foreach (var e in events) yield return e;
                if (state.Terminal) yield break;
            }
            yield return new ModelStreamFailed(new ModelFailure(ModelFailureKind.StreamProtocolError,
                "Responses stream ended without response.completed, response.incomplete, or response.failed.",
                Phase: ModelFailurePhase.ResponseBody, HttpStatusCode: (int)response!.StatusCode));
        }
    }

    public bool SupportsNonStreamingGeneration => true;

    public async ValueTask<IReadOnlyList<ModelStreamEvent>> GenerateAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        if (!IsAccount)
        {
            return await ModelHttpGeneration.SendAsync(httpClient, () => BuildRequest(request, stream: false), DecodeGeneration, cancellationToken);
        }
        // The account channel requires streaming; non-streaming callers get the stream aggregated locally.
        List<ModelStreamEvent> events = [];
        await foreach (var e in StreamAsync(request, cancellationToken).ConfigureAwait(false))
            events.Add(e);
        return events;
    }

    private static IReadOnlyList<ModelStreamEvent> DecodeGeneration(JsonElement root)
    {
        var status = root.TryGetProperty("status", out var value) ? value.GetString() : null;
        if (!root.TryGetProperty("output", out _) && status != "failed")
            throw new JsonException("Responses completion has no output.");
        var type = status switch
        {
            "failed" => "response.failed",
            "incomplete" => "response.incomplete",
            _ => "response.completed",
        };
        return new ResponsesStreamState().Dispatch(JsonSerializer.SerializeToElement(new { type, response = root }), type).ToList();
    }

    private HttpRequestMessage BuildRequest(ModelRequest request, bool stream = true, string? apiKey = null)
    {
        var normalized = ReasoningHistoryNormalizer.Normalize(
            request.Messages, keepReasoning: true, Descriptor.Properties.SupportsImage, Descriptor);
        ModelRequestOptions.ValidateRequest(request with { Messages = normalized }, Descriptor.Properties);
        var input = new List<object>();
        var reasoningItems = new Dictionary<string, Dictionary<string, object?>>();
        var encryptedReasoning = normalized.Where(message => message.Role == MessageRole.Assistant)
            .SelectMany(message => message.Content.OfType<InputReasoningBlock>())
            .Where(block => block.ProviderItemId is { Length: > 0 } && block.EncryptedContent is not null)
            .GroupBy(block => block.ProviderItemId!)
            .ToDictionary(group => group.Key, group => group.Last().EncryptedContent!);
        foreach (var message in ModelRequestSystemBlocks.Normalize(normalized, Descriptor.Properties.SupportsMidConversationSystem))
        {
            var text = new List<object>();
            void FlushText()
            {
                if (text.Count == 0) return;
                input.Add(new Dictionary<string, object?>
                {
                    ["role"] = message.Role switch { MessageRole.Assistant => "assistant", MessageRole.System => "system", _ => "user" },
                    ["content"] = text.ToArray(),
                });
                text.Clear();
            }
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case InputTextBlock t:
                        text.Add(new Dictionary<string, object?>
                        {
                            ["type"] = message.Role == MessageRole.Assistant ? "output_text" : "input_text",
                            ["text"] = t.Text,
                        });
                        break;
                    case InputImageBlock image:
                        text.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:{image.MimeType};base64,{image.Base64Data}",
                        });
                        break;
                    case InputReasoningBlock reasoning when reasoning.EncryptedContent is not null ||
                        reasoning.ProviderItemId is { } replayId && encryptedReasoning.ContainsKey(replayId):
                        FlushText();
                        var encryptedContent = reasoning.ProviderItemId is { } encryptedId
                            ? encryptedReasoning.GetValueOrDefault(encryptedId) ?? reasoning.EncryptedContent
                            : reasoning.EncryptedContent;
                        var summary = new List<object>();
                        if (reasoning.Text.Length > 0)
                            summary.Add(new Dictionary<string, object?> { ["type"] = "summary_text", ["text"] = reasoning.Text });
                        if (reasoning.ProviderItemId is { Length: > 0 } itemId && reasoningItems.TryGetValue(itemId, out var existing))
                        {
                            ((List<object>)existing["summary"]!).AddRange(summary);
                            existing["encrypted_content"] = encryptedContent;
                        }
                        else
                        {
                            var item = new Dictionary<string, object?>
                            {
                                ["type"] = "reasoning",
                                ["encrypted_content"] = encryptedContent,
                                ["summary"] = summary,
                            };
                            if (reasoning.ProviderItemId is { Length: > 0 } reasoningId)
                            {
                                item["id"] = reasoningId;
                                reasoningItems[reasoningId] = item;
                            }
                            input.Add(item);
                        }
                        break;
                    case InputToolCallBlock call:
                        FlushText();
                        input.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "function_call",
                            ["call_id"] = call.Id.Value,
                            ["name"] = call.Name,
                            ["arguments"] = call.Input.GetRawText(),
                        });
                        break;
                    case InputToolResultBlock result:
                        FlushText();
                        input.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = result.Id.Value,
                            ["output"] = result.Content,
                        });
                        break;
                }
            }
            FlushText();
        }
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["input"] = input,
            ["stream"] = stream,
            ["store"] = false,
            ["include"] = new[] { "reasoning.encrypted_content" },
        };
        if (request.Tools.Count > 0)
            payload["tools"] = request.Tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = ModelRequestOptions.ToolSchema(t.InputSchema, Descriptor.Properties),
            }).ToArray();
        var body = ModelRequestOptions.Encode(payload, _requestProperties, request.Options, "max_output_tokens", DefaultMaxTokens);
        if (IsAccount)
            body = OpenAiResponsesAccountShaping.Shape(body);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, options.ResponsesUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey ?? options.ApiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return httpRequest;
    }

    private sealed class ResponsesStreamState
    {
        private readonly Dictionary<string, OpenTool> _tools = [];
        private readonly Dictionary<int, string> _itemIds = [];
        private readonly Dictionary<string, OpenText> _texts = [];
        private readonly Dictionary<string, string?> _reasoningContent = [];
        private bool _started;
        public bool Terminal { get; private set; }

        public IEnumerable<ModelStreamEvent> Dispatch(JsonElement root, string? eventName)
        {
            var type = String(root, "type") ?? eventName;
            if (!_started)
            {
                _started = true;
                yield return new ModelStreamStarted();
            }
            switch (type)
            {
                case "response.output_item.added":
                case "response.output_item.done":
                    {
                        var item = root.GetProperty("item");
                        var id = String(item, "id") ?? Key(root);
                        if (SseHttp.ReadInt(root, "output_index") is { } index) _itemIds[index] = id;
                        if (String(item, "type") == "function_call")
                        {
                            foreach (var e in RegisterTool(id, item)) yield return e;
                            if (type.EndsWith(".done", StringComparison.Ordinal))
                                foreach (var e in FinishTool(_tools[id], String(item, "arguments"))) yield return e;
                        }
                        else if (String(item, "type") == "reasoning")
                        {
                            _reasoningContent[id] = String(item, "encrypted_content");
                            foreach (var e in TextDelta($"{id}:r:0", "", reasoning: true)) yield return e;
                            if (type.EndsWith(".done", StringComparison.Ordinal))
                                foreach (var e in ItemContent(item, id)) yield return e;
                        }
                        else if (type.EndsWith(".done", StringComparison.Ordinal))
                        {
                            foreach (var e in ItemContent(item, id)) yield return e;
                        }
                        break;
                    }
                case "response.function_call_arguments.delta":
                    {
                        var tool = GetTool(root);
                        var delta = String(root, "delta") ?? "";
                        if (!tool.Closed && delta.Length > 0)
                        {
                            tool.Arguments.Append(delta);
                            yield return new ModelToolInputDelta(tool.Id, delta);
                        }
                        break;
                    }
                case "response.function_call_arguments.done":
                    foreach (var e in FinishTool(GetTool(root), String(root, "arguments"))) yield return e;
                    break;
                case "response.output_text.delta":
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    {
                        var reasoning = type != "response.output_text.delta";
                        foreach (var e in TextDelta(TextKey(root, reasoning), String(root, "delta") ?? "", reasoning)) yield return e;
                        break;
                    }
                case "response.output_text.done":
                case "response.reasoning_summary_text.done":
                case "response.reasoning_text.done":
                    {
                        var reasoning = type != "response.output_text.done";
                        foreach (var e in TextDone(TextKey(root, reasoning), String(root, "text"), reasoning, close: !reasoning)) yield return e;
                        break;
                    }
                case "response.completed":
                case "response.incomplete":
                    {
                        var response = root.GetProperty("response");
                        if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                            foreach (var item in output.EnumerateArray())
                            {
                                var id = String(item, "id") ?? "";
                                if (String(item, "type") == "function_call")
                                {
                                    foreach (var e in RegisterTool(id, item)) yield return e;
                                    foreach (var e in FinishTool(_tools[id], String(item, "arguments"))) yield return e;
                                }
                                else foreach (var e in ItemContent(item, id)) yield return e;
                            }
                        foreach (var text in _texts.Values)
                            foreach (var e in CloseText(text)) yield return e;
                        foreach (var tool in _tools.Values)
                            foreach (var e in FinishTool(tool, null)) yield return e;
                        var reason = type == "response.incomplete"
                            ? response.TryGetProperty("incomplete_details", out var details) ? String(details, "reason") : null
                            : null;
                        Terminal = true;
                        yield return new ModelStreamFinished(type == "response.incomplete"
                            ? reason == "content_filter" ? ModelFinishReason.ContentFilter : ModelFinishReason.Length
                            : _tools.Count > 0 ? ModelFinishReason.ToolCalls : ModelFinishReason.Stop, ReadUsage(response),
                            RawFinishReason: reason ?? (type == "response.completed" ? "completed" : "incomplete"),
                            ContextWindowExceeded: reason is "context_length_exceeded" or "model_context_window_exceeded");
                        break;
                    }
                case "response.failed":
                case "error":
                    {
                        var error = root;
                        if (root.TryGetProperty("response", out var r)) error = r;
                        if (error.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object) error = nested;
                        var code = String(error, "code") ?? String(error, "type");
                        var message = String(error, "message") ?? "Responses request failed.";
                        Terminal = true;
                        yield return new ModelStreamFailed(new ModelFailure(code switch
                        {
                            "rate_limit_exceeded" => ModelFailureKind.RateLimited,
                            "invalid_api_key" => ModelFailureKind.AuthenticationFailed,
                            "context_length_exceeded" => ModelFailureKind.ContextOverflow,
                            "invalid_request_error" or "invalid_value" => ModelFailureKind.InvalidRequest,
                            _ => ModelFailureKind.ProviderError,
                        }, message, code, Phase: ModelFailurePhase.ResponseBody));
                        break;
                    }
            }
        }

        private string Key(JsonElement root)
        {
            if (String(root, "item_id") is { } id) return id;
            var index = SseHttp.ReadInt(root, "output_index") ?? 0;
            return _itemIds.GetValueOrDefault(index) ?? $"index_{index}";
        }

        private string TextKey(JsonElement root, bool reasoning) => $"{Key(root)}:{(reasoning ? "r" : "t")}:{SseHttp.ReadInt(root, reasoning ? "summary_index" : "content_index") ?? 0}";

        private OpenTool GetTool(JsonElement root) => _tools.GetValueOrDefault(Key(root))
            ?? throw new JsonException("Tool arguments arrived without a function_call output item.");

        private IEnumerable<ModelStreamEvent> RegisterTool(string key, JsonElement item)
        {
            if (_tools.ContainsKey(key)) yield break;
            var id = String(item, "call_id") ?? throw new JsonException("Function call is missing call_id.");
            var name = String(item, "name") ?? throw new JsonException("Function call is missing name.");
            var tool = new OpenTool(new ToolCallId(id), name);
            _tools.Add(key, tool);
            yield return new ModelToolInputStarted(tool.Id, tool.Name);
        }

        private static IEnumerable<ModelStreamEvent> FinishTool(OpenTool tool, string? completeArguments)
        {
            if (tool.Closed) yield break;
            if (tool.Arguments.Length == 0 && completeArguments is { Length: > 0 })
            {
                tool.Arguments.Append(completeArguments);
                yield return new ModelToolInputDelta(tool.Id, completeArguments);
            }
            var arguments = completeArguments ?? tool.Arguments.ToString();
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Function arguments must be a JSON object.");
            tool.Closed = true;
            yield return new ModelToolInputEnded(tool.Id);
            yield return new ModelToolCallReady(new ToolCall(tool.Id, tool.Name, document.RootElement.Clone()));
        }

        private IEnumerable<ModelStreamEvent> TextDelta(string key, string delta, bool reasoning)
        {
            if (!_texts.TryGetValue(key, out var text))
            {
                if (reasoning)
                    foreach (var previous in _texts.Values.Where(value => value.Reasoning && !value.Closed))
                        foreach (var e in CloseText(previous)) yield return e;
                text = new OpenText(reasoning)
                {
                    ItemId = reasoning ? key[..key.LastIndexOf(":r:", StringComparison.Ordinal)] : null,
                };
                text.EncryptedContent = text.ItemId is { } id ? _reasoningContent.GetValueOrDefault(id) : null;
                _texts[key] = text;
                yield return reasoning ? new ModelReasoningStarted() : new ModelTextStarted();
            }
            if (!text.Closed && delta.Length > 0)
            {
                text.HasDelta = true;
                yield return reasoning ? new ModelReasoningDelta(delta) : new ModelTextDelta(delta);
            }
        }

        private IEnumerable<ModelStreamEvent> TextDone(string key, string? fullText, bool reasoning, bool close = true)
        {
            if (!_texts.TryGetValue(key, out var text) || !text.HasDelta && !text.Closed)
                foreach (var e in TextDelta(key, fullText ?? "", reasoning)) yield return e;
            if (close && _texts.TryGetValue(key, out text))
                foreach (var e in CloseText(text)) yield return e;
        }

        private static IEnumerable<ModelStreamEvent> CloseText(OpenText text)
        {
            if (text.Closed) yield break;
            text.Closed = true;
            yield return text.Reasoning
                ? new ModelReasoningEnded(EncryptedContent: text.EncryptedContent, ProviderItemId: text.ItemId)
                : new ModelTextEnded();
        }

        private IEnumerable<ModelStreamEvent> ItemContent(JsonElement item, string id)
        {
            var reasoning = String(item, "type") == "reasoning";
            if (reasoning)
            {
                foreach (var e in TextDelta($"{id}:r:0", "", reasoning: true)) yield return e;
            }
            if (item.TryGetProperty(reasoning ? "summary" : "content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var part in content.EnumerateArray())
                {
                    if (String(part, "type") is "output_text" or "summary_text" or "reasoning_text")
                        foreach (var e in TextDone($"{id}:{(reasoning ? "r" : "t")}:{i}", String(part, "text"), reasoning, close: !reasoning)) yield return e;
                    i++;
                }
            }
            if (reasoning)
            {
                foreach (var text in _texts.Values.Where(text => text.Reasoning && text.ItemId == id && !text.Closed))
                {
                    text.EncryptedContent = String(item, "encrypted_content");
                    foreach (var e in CloseText(text)) yield return e;
                }
            }
        }

        private static TokenUsage? ReadUsage(JsonElement response)
        {
            if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
            var input = SseHttp.ReadInt(usage, "input_tokens");
            var output = SseHttp.ReadInt(usage, "output_tokens");
            int? cached = usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
                ? SseHttp.ReadInt(details, "cached_tokens") : null;
            return new TokenUsage(input, output, input, cached);
        }

        private static string? String(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private sealed class OpenTool(ToolCallId id, string name)
    {
        public ToolCallId Id { get; } = id;
        public string Name { get; } = name;
        public StringBuilder Arguments { get; } = new();
        public bool Closed { get; set; }
    }

    private sealed class OpenText(bool reasoning)
    {
        public bool Reasoning { get; } = reasoning;
        public string? ItemId { get; set; }
        public string? EncryptedContent { get; set; }
        public bool HasDelta { get; set; }
        public bool Closed { get; set; }
    }
}
