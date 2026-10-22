using System.Buffers;
using System.Collections.Concurrent;
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
    // Cached outside the instance so record value equality stays field-based.
    private static readonly ConcurrentDictionary<string, string> ResponsesUrls = new(StringComparer.Ordinal);

    public string ResponsesUrl => ResponsesUrls.GetOrAdd(BaseUrl, static url => $"{url.TrimEnd('/')}/responses");
}

public sealed class OpenAiResponsesChatModel(
    HttpClient httpClient,
    OpenAiResponsesModelOptions options,
    ModelProperties? properties = null,
    string? providerId = null,
    Func<CancellationToken, ValueTask<string>>? accessTokenSource = null) : IChatModel
{
    private const int DefaultMaxTokens = 32768;
    private static readonly string[] ReasoningInclude = ["reasoning.encrypted_content"];
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
            var events = new List<ModelStreamEvent>();
            await foreach (var frame in SseHttp.ReadFramesAsync(response!, cancellationToken).ConfigureAwait(false))
            {
                if (frame.Event == SseHttp.FailureEventName)
                {
                    yield return new ModelStreamFailed(SseHttp.DeserializeFailure(frame.Data));
                    yield break;
                }
                if (frame.Data.Length == 0 || frame.Data == "[DONE]") continue;
                try
                {
                    using var document = JsonDocument.Parse(frame.Data);
                    events.Clear();
                    state.Dispatch(document.RootElement, frame.Event, events);
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
        var events = new List<ModelStreamEvent>();
        new ResponsesStreamState().Dispatch(JsonSerializer.SerializeToElement(new { type, response = root }), type, events);
        return events;
    }

    private HttpRequestMessage BuildRequest(ModelRequest request, bool stream = true, string? apiKey = null)
    {
        var normalized = ReasoningHistoryNormalizer.Normalize(
            request.Messages, keepReasoning: true, Descriptor.Properties.SupportsImage, Descriptor);
        ModelRequestOptions.ValidateRequest(request with { Messages = normalized }, Descriptor.Properties);
        var input = new List<object>();
        var reasoningItems = new Dictionary<string, Dictionary<string, object?>>();
        Dictionary<string, string>? encryptedReasoning = null;
        foreach (var message in normalized)
        {
            if (message.Role != MessageRole.Assistant)
                continue;
            foreach (var block in message.Content)
            {
                if (block is InputReasoningBlock { ProviderItemId: { Length: > 0 } providerItemId, EncryptedContent: { } encrypted })
                    (encryptedReasoning ??= [])[providerItemId] = encrypted;
            }
        }
        foreach (var message in ModelRequestSystemBlocks.Normalize(normalized, Descriptor.Properties.SupportsMidConversationSystem))
        {
            var text = new List<object>();
            void FlushText()
            {
                if (text.Count == 0) return;
                input.Add(new Dictionary<string, object?>
                {
                    ["role"] = message.Role switch { MessageRole.Assistant => "assistant", MessageRole.System => "system", _ => "user" },
                    ["content"] = text,
                });
                text = [];
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
                        reasoning.ProviderItemId is { } replayId && encryptedReasoning?.ContainsKey(replayId) == true:
                        FlushText();
                        var encryptedContent = reasoning.ProviderItemId is { } encryptedId
                            ? encryptedReasoning?.GetValueOrDefault(encryptedId) ?? reasoning.EncryptedContent
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
            ["include"] = ReasoningInclude,
        };
        if (request.Tools.Any(t => t.ProviderNative is not null))
            throw new ArgumentException("Provider-native tools are not supported by this model API.");
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
        private readonly List<TextKeyCache> _textKeys = [];
        private bool _started;
        public bool Terminal { get; private set; }

        public void Dispatch(JsonElement root, string? eventName, List<ModelStreamEvent> events)
        {
            var type = eventName ?? String(root, "type");
            if (!_started)
            {
                _started = true;
                events.Add(new ModelStreamStarted());
            }
            switch (type)
            {
                case "response.output_item.added":
                case "response.output_item.done":
                    {
                        var item = root.GetProperty("item");
                        var id = String(item, "id") ?? Key(root);
                        if (SseHttp.ReadInt(root, "output_index") is { } index) _itemIds[index] = id;
                        var itemType = String(item, "type");
                        if (itemType == "function_call")
                        {
                            RegisterTool(id, item, events);
                            if (type.EndsWith(".done", StringComparison.Ordinal))
                                FinishTool(_tools[id], String(item, "arguments"), events);
                        }
                        else if (itemType == "reasoning")
                        {
                            _reasoningContent[id] = String(item, "encrypted_content");
                            TextDelta(ItemTextKey(id, reasoning: true, 0), "", reasoning: true, events);
                            if (type.EndsWith(".done", StringComparison.Ordinal))
                                ItemContent(item, id, events);
                        }
                        else if (type.EndsWith(".done", StringComparison.Ordinal))
                        {
                            ItemContent(item, id, events);
                        }
                        break;
                    }
                case "response.function_call_arguments.delta":
                    {
                        var tool = GetTool(root);
                        var delta = String(root, "delta") ?? "";
                        if (!tool.Closed && delta.Length > 0)
                        {
                            SseHttp.AppendUtf8(tool.Arguments, delta);
                            events.Add(new ModelToolInputDelta(tool.Id, delta));
                        }
                        break;
                    }
                case "response.function_call_arguments.done":
                    FinishTool(GetTool(root), String(root, "arguments"), events);
                    break;
                case "response.output_text.delta":
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    {
                        var reasoning = type != "response.output_text.delta";
                        var key = TextKey(root, reasoning);
                        var delta = String(root, "delta") ?? "";
                        TextDelta(key, delta, reasoning, events);
                        break;
                    }
                case "response.output_text.done":
                case "response.reasoning_summary_text.done":
                case "response.reasoning_text.done":
                    {
                        var reasoning = type != "response.output_text.done";
                        var key = TextKey(root, reasoning);
                        var fullText = String(root, "text");
                        TextDone(key, fullText, reasoning, close: !reasoning, events);
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
                                    RegisterTool(id, item, events);
                                    FinishTool(_tools[id], String(item, "arguments"), events);
                                }
                                else ItemContent(item, id, events);
                            }
                        foreach (var text in _texts.Values)
                            CloseText(text, events);
                        foreach (var tool in _tools.Values)
                            FinishTool(tool, null, events);
                        var reason = type == "response.incomplete"
                            ? response.TryGetProperty("incomplete_details", out var details) ? String(details, "reason") : null
                            : null;
                        Terminal = true;
                        events.Add(new ModelStreamFinished(type == "response.incomplete"
                            ? reason == "content_filter" ? ModelFinishReason.ContentFilter : ModelFinishReason.Length
                            : _tools.Count > 0 ? ModelFinishReason.ToolCalls : ModelFinishReason.Stop, ReadUsage(response),
                            RawFinishReason: reason ?? (type == "response.completed" ? "completed" : "incomplete"),
                            ContextWindowExceeded: reason is "context_length_exceeded" or "model_context_window_exceeded"));
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
                        events.Add(new ModelStreamFailed(new ModelFailure(code switch
                        {
                            "rate_limit_exceeded" => ModelFailureKind.RateLimited,
                            "invalid_api_key" => ModelFailureKind.AuthenticationFailed,
                            "context_length_exceeded" => ModelFailureKind.ContextOverflow,
                            "invalid_request_error" or "invalid_value" => ModelFailureKind.InvalidRequest,
                            _ => ModelFailureKind.ProviderError,
                        }, message, code, Phase: ModelFailurePhase.ResponseBody)));
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

        private string TextKey(JsonElement root, bool reasoning)
        {
            var contentIndex = SseHttp.ReadInt(root, reasoning ? "summary_index" : "content_index") ?? 0;
            if (root.TryGetProperty("item_id", out var itemIdElement) && itemIdElement.ValueKind == JsonValueKind.String)
            {
                foreach (var entry in _textKeys)
                {
                    if (entry.ItemId is { } itemId && entry.Reasoning == reasoning &&
                        entry.ContentIndex == contentIndex && itemIdElement.ValueEquals(itemId))
                        return entry.Key;
                }
                var id = itemIdElement.GetString()!;
                var key = $"{id}:{(reasoning ? "r" : "t")}:{contentIndex}";
                _textKeys.Add(new TextKeyCache
                {
                    ItemId = id,
                    Reasoning = reasoning,
                    ContentIndex = contentIndex,
                    Key = key,
                });
                return key;
            }

            var outputIndex = SseHttp.ReadInt(root, "output_index") ?? 0;
            var resolved = _itemIds.GetValueOrDefault(outputIndex) ?? $"index_{outputIndex}";
            return ItemTextKey(resolved, reasoning, contentIndex);
        }

        private string ItemTextKey(string itemId, bool reasoning, int contentIndex)
        {
            foreach (var entry in _textKeys)
            {
                if (entry.ItemId is null && entry.Reasoning == reasoning &&
                    entry.ContentIndex == contentIndex && string.Equals(entry.ResolvedKey, itemId, StringComparison.Ordinal))
                    return entry.Key;
            }
            var key = $"{itemId}:{(reasoning ? "r" : "t")}:{contentIndex}";
            _textKeys.Add(new TextKeyCache
            {
                ResolvedKey = itemId,
                Reasoning = reasoning,
                ContentIndex = contentIndex,
                Key = key,
            });
            return key;
        }

        private OpenTool GetTool(JsonElement root) => _tools.GetValueOrDefault(Key(root))
            ?? throw new JsonException("Tool arguments arrived without a function_call output item.");

        private void RegisterTool(string key, JsonElement item, List<ModelStreamEvent> events)
        {
            if (_tools.ContainsKey(key)) return;
            var id = String(item, "call_id") ?? throw new JsonException("Function call is missing call_id.");
            var name = String(item, "name") ?? throw new JsonException("Function call is missing name.");
            var tool = new OpenTool(new ToolCallId(id), name);
            _tools.Add(key, tool);
            events.Add(new ModelToolInputStarted(tool.Id, tool.Name));
        }

        private static void FinishTool(OpenTool tool, string? completeArguments, List<ModelStreamEvent> events)
        {
            if (tool.Closed) return;
            if (tool.Arguments.WrittenCount == 0 && completeArguments is { Length: > 0 })
            {
                SseHttp.AppendUtf8(tool.Arguments, completeArguments);
                events.Add(new ModelToolInputDelta(tool.Id, completeArguments));
            }
            using var document = completeArguments is null
                ? ParseAccumulatedArguments(tool.Arguments.WrittenMemory)
                : JsonDocument.Parse(string.IsNullOrWhiteSpace(completeArguments) ? "{}" : completeArguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Function arguments must be a JSON object.");
            tool.Closed = true;
            events.Add(new ModelToolInputEnded(tool.Id));
            events.Add(new ModelToolCallReady(new ToolCall(tool.Id, tool.Name, document.RootElement.Clone())));
        }

        private static JsonDocument ParseAccumulatedArguments(ReadOnlyMemory<byte> utf8)
        {
            if (SseHttp.IsNullOrWhiteSpace(utf8.Span))
                return JsonDocument.Parse("{}");
            try
            {
                return JsonDocument.Parse(utf8);
            }
            catch (JsonException) when (utf8.Span.ContainsAnyExceptInRange((byte)0, (byte)0x7F))
            {
                // Reparse the decoded text so malformed-input messages keep their previous positions.
                return JsonDocument.Parse(Encoding.UTF8.GetString(utf8.Span));
            }
        }

        private void TextDelta(string key, string delta, bool reasoning, List<ModelStreamEvent> events)
        {
            if (!_texts.TryGetValue(key, out var text))
            {
                if (reasoning)
                {
                    foreach (var previous in _texts.Values)
                        if (previous.Reasoning && !previous.Closed)
                            CloseText(previous, events);
                }
                text = new OpenText(reasoning)
                {
                    ItemId = reasoning ? key[..key.LastIndexOf(":r:", StringComparison.Ordinal)] : null,
                };
                text.EncryptedContent = text.ItemId is { } id ? _reasoningContent.GetValueOrDefault(id) : null;
                _texts[key] = text;
                events.Add(reasoning ? new ModelReasoningStarted() : new ModelTextStarted());
            }
            if (!text.Closed && delta.Length > 0)
            {
                text.HasDelta = true;
                events.Add(reasoning ? new ModelReasoningDelta(delta) : new ModelTextDelta(delta));
            }
        }

        private void TextDone(string key, string? fullText, bool reasoning, bool close, List<ModelStreamEvent> events)
        {
            if (!_texts.TryGetValue(key, out var text) || (!text.HasDelta && !text.Closed))
                TextDelta(key, fullText ?? "", reasoning, events);
            if (close && _texts.TryGetValue(key, out text))
                CloseText(text, events);
        }

        private static void CloseText(OpenText text, List<ModelStreamEvent> events)
        {
            if (text.Closed) return;
            text.Closed = true;
            events.Add(text.Reasoning
                ? new ModelReasoningEnded(EncryptedContent: text.EncryptedContent, ProviderItemId: text.ItemId)
                : new ModelTextEnded());
        }

        private void ItemContent(JsonElement item, string id, List<ModelStreamEvent> events)
        {
            var reasoning = String(item, "type") == "reasoning";
            if (reasoning)
                TextDelta(ItemTextKey(id, reasoning: true, 0), "", reasoning: true, events);
            if (item.TryGetProperty(reasoning ? "summary" : "content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var part in content.EnumerateArray())
                {
                    if (String(part, "type") is "output_text" or "summary_text" or "reasoning_text")
                        TextDone(ItemTextKey(id, reasoning, i), String(part, "text"), reasoning, close: !reasoning, events);
                    i++;
                }
            }
            if (reasoning)
            {
                foreach (var text in _texts.Values)
                {
                    if (text.Reasoning && text.ItemId == id && !text.Closed)
                    {
                        text.EncryptedContent = String(item, "encrypted_content");
                        CloseText(text, events);
                    }
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

    private sealed class TextKeyCache
    {
        public string? ItemId { get; init; }
        public string? ResolvedKey { get; init; }
        public bool Reasoning { get; init; }
        public int ContentIndex { get; init; }
        public string Key { get; init; } = "";
    }

    private sealed class OpenTool(ToolCallId id, string name)
    {
        public ToolCallId Id { get; } = id;
        public string Name { get; } = name;
        public ArrayBufferWriter<byte> Arguments { get; } = new();
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
