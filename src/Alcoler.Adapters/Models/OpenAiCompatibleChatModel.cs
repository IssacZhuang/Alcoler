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

/// <param name="UseLegacyMaxTokens">
/// Sends <c>max_tokens</c> instead of <c>max_completion_tokens</c> for providers that
/// reject the newer field; the default covers OpenAI and current compatible endpoints.
/// </param>
public sealed record OpenAiCompatibleModelOptions(
    string Model,
    string ApiKey,
    string BaseUrl,
    bool UseLegacyMaxTokens = false)
{
    public string ChatCompletionsUrl => $"{BaseUrl.TrimEnd('/')}/chat/completions";
}

public sealed class OpenAiCompatibleChatModel(HttpClient httpClient, OpenAiCompatibleModelOptions options, ModelProperties? properties = null, string? providerId = null) : IChatModel
{
    private const int DefaultMaxTokens = 32768;
    private readonly ModelProperties _requestProperties = properties ?? new ModelProperties();

    public ModelDescriptor Descriptor { get; } = new(
        providerId ?? "openai-compatible",
        options.Model,
        options.Model,
        properties ?? new ModelProperties(ContextWindowTokens: 128_000, DefaultMaxOutputTokens: DefaultMaxTokens));

    public void ValidateOptions(ModelCallOptions callOptions) => ModelRequestOptions.Validate(_requestProperties, callOptions, DefaultMaxTokens);

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
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
            var state = new OpenAiStreamState();
            await foreach (var frame in SseHttp.ReadFramesAsync(response!, cancellationToken).ConfigureAwait(false))
            {
                if (frame.Event == SseHttp.FailureEventName)
                {
                    yield return new ModelStreamFailed(SseHttp.DeserializeFailure(frame.Data));
                    yield break;
                }
                if (frame.Data.Length == 0)
                    continue;
                if (frame.Data is "[DONE]")
                {
                    foreach (var e in state.Complete())
                        yield return e;
                    yield break;
                }

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
                        Phase: ModelFailurePhase.ResponseBody, HttpStatusCode: (int)response!.StatusCode);
                }
                if (dispatchFailure is not null)
                {
                    yield return new ModelStreamFailed(dispatchFailure);
                    yield break;
                }
                foreach (var e in events)
                    yield return e;
            }
            foreach (var e in state.Complete())
                yield return e;
        }
    }

    public bool SupportsNonStreamingGeneration => true;

    public ValueTask<IReadOnlyList<ModelStreamEvent>> GenerateAsync(ModelRequest request, CancellationToken cancellationToken) =>
        ModelHttpGeneration.SendAsync(httpClient, () => BuildRequest(request, stream: false), DecodeGeneration, cancellationToken);

    private static IReadOnlyList<ModelStreamEvent> DecodeGeneration(JsonElement root)
    {
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            throw new JsonException("Chat completion has no choices.");
        var choice = choices[0];
        var message = choice.GetProperty("message");
        var delta = message.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value);
        if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array)
            delta["tool_calls"] = tools.EnumerateArray().Select((tool, index) => new
            {
                index,
                id = tool.GetProperty("id").GetString(),
                function = tool.GetProperty("function"),
            }).ToArray();
        var state = new OpenAiStreamState();
        var events = state.Dispatch(JsonSerializer.SerializeToElement(new
        {
            choices = new[] { new { delta, finish_reason = choice.TryGetProperty("finish_reason", out var reason) ? reason.GetString() : null } },
            usage = root.TryGetProperty("usage", out var usage) ? usage : default(JsonElement?),
        })).ToList();
        events.AddRange(state.Complete());
        if (message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String && reasoning.GetString() is { Length: > 0 })
        {
            var deltaIndex = events.FindIndex(e => e is ModelReasoningDelta);
            events.Insert(deltaIndex, new ModelReasoningStarted());
            events.Insert(deltaIndex + 2, new ModelReasoningEnded());
        }
        return events;
    }

    private HttpRequestMessage BuildRequest(ModelRequest request, bool stream = true)
    {
        var normalized = ReasoningHistoryNormalizer.Normalize(
            request.Messages, keepReasoning: true, Descriptor.Properties.SupportsImage, Descriptor);
        ModelRequestOptions.ValidateRequest(request with { Messages = normalized }, Descriptor.Properties);
        var messages = new List<object>();
        foreach (var input in ModelRequestSystemBlocks.Normalize(normalized, Descriptor.Properties.SupportsMidConversationSystem))
            messages.AddRange(ToProviderMessages(input));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["stream"] = stream,
            ["messages"] = messages,
        };
        if (stream)
            payload["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        if (request.Tools.Count > 0)
            payload["tools"] = request.Tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = ModelRequestOptions.ToolSchema(t.InputSchema, Descriptor.Properties),
                },
            }).ToList();

        var message = new HttpRequestMessage(HttpMethod.Post, options.ChatCompletionsUrl)
        {
            Content = new StringContent(
                ModelRequestOptions.Encode(payload, _requestProperties, request.Options,
                    options.UseLegacyMaxTokens ? "max_tokens" : "max_completion_tokens", DefaultMaxTokens), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return message;
    }

    private static IEnumerable<object> ToProviderMessages(ModelInputMessage message)
    {
        if (message.Role == MessageRole.Assistant)
        {
            var text = string.Concat(message.Content.OfType<InputTextBlock>().Select(b => b.Text));
            var reasoning = string.Concat(message.Content.OfType<InputReasoningBlock>().Select(b => b.Text));
            var toolCalls = message.Content.OfType<InputToolCallBlock>().ToList();
            var assistant = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = text.Length > 0 ? text : null,
            };
            if (reasoning.Length > 0)
                assistant["reasoning_content"] = reasoning;
            if (toolCalls.Count > 0)
                assistant["tool_calls"] = toolCalls.Select(t => new Dictionary<string, object?>
                {
                    ["id"] = t.Id.Value,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = t.Name,
                        ["arguments"] = t.Input.GetRawText(),
                    },
                }).ToList();
            yield return assistant;
            yield break;
        }

        foreach (var block in message.Content)
        {
            if (block is InputToolResultBlock toolResult)
            {
                yield return new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = toolResult.Id.Value,
                    ["content"] = toolResult.Content,
                };
            }
        }

        var textBlocks = message.Content.OfType<InputTextBlock>().ToList();
        var imageBlocks = message.Content.OfType<InputImageBlock>().ToList();
        if (imageBlocks.Count > 0)
        {
            var content = new List<object>();
            foreach (var text in textBlocks)
                content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = text.Text });
            foreach (var image in imageBlocks)
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = $"data:{image.MimeType};base64,{image.Base64Data}",
                    },
                });
            yield return new Dictionary<string, object?>
            {
                ["role"] = message.Role == MessageRole.System ? "system" : "user",
                ["content"] = content,
            };
        }
        else if (textBlocks.Count > 0)
        {
            yield return new Dictionary<string, object?>
            {
                ["role"] = message.Role == MessageRole.System ? "system" : "user",
                ["content"] = textBlocks.Count == 1
                    ? textBlocks[0].Text
                    : textBlocks.Select(t => new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = t.Text,
                    }).ToList(),
            };
        }
    }

    private sealed class OpenAiStreamState
    {
        private readonly SortedDictionary<int, OpenTool> _tools = [];
        private bool _textOpen;
        private bool _textClosed;
        private string? _finishReason;
        private TokenUsage? _usage;
        private bool _finished;

        public IEnumerable<ModelStreamEvent> Dispatch(JsonElement root)
        {
            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                _usage = usage.ValueKind == JsonValueKind.Object ? ReadUsage(usage) : null;
                if (_finishReason is not null)
                {
                    foreach (var e in Finish())
                        yield return e;
                }
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                yield break;

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                foreach (var e in DispatchDelta(delta))
                    yield return e;
            }
            if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                finishReason.ValueKind == JsonValueKind.String)
            {
                _finishReason = finishReason.GetString();
                foreach (var e in CloseOpenContent())
                    yield return e;
            }
        }

        public IEnumerable<ModelStreamEvent> Complete()
        {
            if (_finished)
                yield break;
            if (_finishReason is not null || _usage is not null)
            {
                foreach (var e in Finish())
                    yield return e;
            }
            else
            {
                yield return new ModelStreamFailed(new ModelFailure(
                    ModelFailureKind.StreamProtocolError,
                    "openai-compatible stream ended without finish_reason or usage", Phase: ModelFailurePhase.ResponseBody));
            }
        }

        private IEnumerable<ModelStreamEvent> Finish()
        {
            _finished = true;
            foreach (var e in CloseOpenContent())
                yield return e;
            yield return new ModelStreamFinished(MapFinishReason(_finishReason), _usage, _finishReason,
                ContextWindowExceeded: _finishReason == "model_context_window_exceeded");
        }

        private IEnumerable<ModelStreamEvent> CloseOpenContent()
        {
            if (_textOpen && !_textClosed)
            {
                _textClosed = true;
                yield return new ModelTextEnded();
            }
            foreach (var tool in _tools.Values)
            {
                if (tool.Closed)
                    continue;
                tool.Closed = true;
                yield return new ModelToolInputEnded(tool.Id!.Value);
                yield return new ModelToolCallReady(
                    new ToolCall(tool.Id!.Value, tool.Name!, ParseArguments(tool.Arguments)));
            }
        }

        private IEnumerable<ModelStreamEvent> DispatchDelta(JsonElement delta)
        {
            if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                reasoning.ValueKind == JsonValueKind.String)
            {
                var reasoningText = reasoning.GetString();
                if (reasoningText is { Length: > 0 })
                    yield return new ModelReasoningDelta(reasoningText);
            }

            if (delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (text is { Length: > 0 })
                {
                    if (!_textOpen)
                    {
                        _textOpen = true;
                        yield return new ModelTextStarted();
                    }
                    yield return new ModelTextDelta(text);
                }
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in toolCalls.EnumerateArray())
                {
                    var index = entry.TryGetProperty("index", out var indexElement)
                        ? indexElement.GetInt32()
                        : 0;
                    if (!_tools.TryGetValue(index, out var tool))
                    {
                        tool = new OpenTool();
                        _tools[index] = tool;
                    }
                    if (entry.TryGetProperty("id", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.String)
                        tool.Id = new ToolCallId(idElement.GetString()!);
                    if (entry.TryGetProperty("function", out var function) &&
                        function.ValueKind == JsonValueKind.Object)
                    {
                        if (function.TryGetProperty("name", out var nameElement) &&
                            nameElement.ValueKind == JsonValueKind.String)
                            tool.Name = nameElement.GetString();
                        if (function.TryGetProperty("arguments", out var argumentsElement) &&
                            argumentsElement.ValueKind == JsonValueKind.String)
                            tool.Arguments.Append(argumentsElement.GetString() ?? "");
                    }
                    if (tool.Id is not null && tool.Name is not null)
                    {
                        if (!tool.Started)
                        {
                            tool.Started = true;
                            yield return new ModelToolInputStarted(tool.Id.Value, tool.Name);
                        }
                        if (tool.Arguments.Length > tool.EmittedCharacters)
                        {
                            var fragment = tool.Arguments.ToString(tool.EmittedCharacters, tool.Arguments.Length - tool.EmittedCharacters);
                            tool.EmittedCharacters = tool.Arguments.Length;
                            yield return new ModelToolInputDelta(tool.Id.Value, fragment);
                        }
                    }
                }
            }
        }

        private static TokenUsage? ReadUsage(JsonElement usage)
        {
            var promptTokens = SseHttp.ReadInt(usage, "prompt_tokens");
            var completionTokens = SseHttp.ReadInt(usage, "completion_tokens");
            int? cachedTokens = null;
            if (usage.TryGetProperty("prompt_tokens_details", out var details) &&
                details.ValueKind == JsonValueKind.Object)
                cachedTokens = SseHttp.ReadInt(details, "cached_tokens");
            if (promptTokens is null && completionTokens is null && cachedTokens is null)
                return null;
            return new TokenUsage(
                InputTokens: promptTokens,
                OutputTokens: completionTokens,
                ContextUsageTokens: promptTokens,
                CacheReadTokens: cachedTokens);
        }

        private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

        private static JsonElement ParseArguments(StringBuilder arguments)
        {
            var text = arguments.ToString();
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

        private static ModelFinishReason MapFinishReason(string? finishReason) => finishReason switch
        {
            "stop" => ModelFinishReason.Stop,
            "tool_calls" or "function_call" => ModelFinishReason.ToolCalls,
            // Gateway providers surface the output-limit family under raw non-standard
            // reasons; all of them mean the response was truncated.
            "length" or "max_tokens" or "max_output_tokens" or "model_context_window_exceeded" => ModelFinishReason.Length,
            "content_filter" => ModelFinishReason.ContentFilter,
            _ => ModelFinishReason.Other,
        };
    }

    private sealed class OpenTool
    {
        public ToolCallId? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
        public bool Started { get; set; }
        public int EmittedCharacters { get; set; }
        public bool Closed { get; set; }
    }
}
