using System.Net;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Models;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public class OpenAiResponsesChatModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_SerializesUserImagesAsInputImages(bool withText)
    {
        var handler = Handler("""{"type":"response.completed","response":{"output":[]}}""");
        var model = new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "key", "https://test/v1"),
            new ModelProperties(SupportsImage: true));
        List<ModelContentBlock> content = withText ? [new InputTextBlock("caption")] : [];
        content.Add(new InputImageBlock("image/png", "aW1hZ2U="));
        await Collect(model, new ModelRequest([new(MessageRole.User, content)], [], new()));
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies)!);
        var message = Assert.Single(body.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var blocks = message.GetProperty("content");
        Assert.Equal(withText ? 2 : 1, blocks.GetArrayLength());
        if (withText)
            Assert.Equal("caption", blocks[0].GetProperty("text").GetString());
        var image = blocks[blocks.GetArrayLength() - 1];
        Assert.Equal("input_image", image.GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,aW1hZ2U=", image.GetProperty("image_url").GetString());
    }

    [Fact]
    public async Task StreamingTextReasoningToolsAndCachedUsage_AreDecodedOnce()
    {
        var handler = Handler(
            """{"type":"response.created","response":{"id":"resp_1"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"id":"reason_1","type":"reasoning","summary":[]}}""",
            """{"type":"response.reasoning_summary_text.delta","item_id":"reason_1","summary_index":0,"delta":"Consider it"}""",
            """{"type":"response.reasoning_summary_text.done","item_id":"reason_1","summary_index":0,"text":"Consider it"}""",
            """{"type":"response.output_item.added","output_index":1,"item":{"id":"msg_1","type":"message","role":"assistant","content":[]}}""",
            """{"type":"response.output_text.delta","item_id":"msg_1","content_index":0,"delta":"Hello"}""",
            """{"type":"response.output_text.done","item_id":"msg_1","content_index":0,"text":"Hello"}""",
            """{"type":"response.output_item.added","output_index":2,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"read_file","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":2,"delta":"{\"path\":"}""",
            """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":2,"delta":"\"file.cs\"}"}""",
            """{"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"path\":\"file.cs\"}"}""",
            """{"type":"response.output_item.done","output_index":2,"item":{"id":"fc_1","type":"function_call","call_id":"call_1","name":"read_file","arguments":"{\"path\":\"file.cs\"}"}}""",
            """{"type":"response.completed","response":{"status":"completed","output":[{"id":"msg_1","type":"message","content":[{"type":"output_text","text":"Hello"}]},{"id":"reason_1","type":"reasoning","summary":[{"type":"summary_text","text":"Consider it"}]},{"id":"fc_1","type":"function_call","call_id":"call_1","name":"read_file","arguments":"{\"path\":\"file.cs\"}"}],"usage":{"input_tokens":100,"output_tokens":20,"input_tokens_details":{"cached_tokens":70}}}}""");
        var events = await Collect(Model(handler));
        Assert.Single(events.OfType<ModelStreamStarted>());
        Assert.Equal("Hello", Assert.Single(events.OfType<ModelTextDelta>()).Delta);
        Assert.Single(events.OfType<ModelTextEnded>());
        Assert.Equal("Consider it", Assert.Single(events.OfType<ModelReasoningDelta>()).Delta);
        Assert.Single(events.OfType<ModelReasoningEnded>());
        Assert.Equal("call_1", Assert.Single(events.OfType<ModelToolInputStarted>()).Id.Value);
        Assert.Equal(2, events.OfType<ModelToolInputDelta>().Count());
        Assert.Single(events.OfType<ModelToolInputEnded>());
        var call = Assert.Single(events.OfType<ModelToolCallReady>()).ToolCall;
        Assert.Equal("file.cs", call.Input.GetProperty("path").GetString());
        Assert.Equal("read_file", call.Name);
        var finish = Assert.Single(events.OfType<ModelStreamFinished>());
        Assert.Equal(ModelFinishReason.ToolCalls, finish.FinishReason);
        Assert.Equal(new TokenUsage(100, 20, 100, 70), finish.Usage);
        Assert.Empty(events.OfType<ModelStreamFailed>());
    }

    [Fact]
    public async Task Request_UsesResponsesInputAndFlatTools()
    {
        var handler = Handler("""{"type":"response.completed","response":{"output":[]}}""");
        var model = Model(handler);
        var request = new ModelRequest([
            new ModelInputMessage(MessageRole.System, [new InputTextBlock("rules")]),
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")]),
            new ModelInputMessage(MessageRole.Assistant, [new InputTextBlock("checking"), new InputToolCallBlock(new ToolCallId("call_7"), "read_file", JsonSerializer.SerializeToElement(new { path = "a.cs" }))]),
            new ModelInputMessage(MessageRole.User, [new InputToolResultBlock(new ToolCallId("call_7"), "body"), new InputTextBlock("continue")])],
            [new ToolContract("read_file", "Reads files", JsonSerializer.SerializeToElement(new { type = "object" }))], new ModelCallOptions(MaxOutputTokens: 1234));
        await Collect(model, request);
        Assert.Equal(new Uri("https://test/v1/responses"), Assert.Single(handler.Requests).RequestUri);
        Assert.Equal("Bearer key", handler.Requests[0].Headers.Authorization!.ToString());
        var body = JsonDocument.Parse(handler.RequestBodies[0]!).RootElement;
        Assert.False(body.TryGetProperty("messages", out _));
        Assert.False(body.TryGetProperty("stream_options", out _));
        Assert.Equal(1234, body.GetProperty("max_output_tokens").GetInt32());
        var input = body.GetProperty("input");
        Assert.Equal("system", input[0].GetProperty("role").GetString());
        Assert.Equal("input_text", input[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("output_text", input[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("function_call", input[3].GetProperty("type").GetString());
        Assert.Equal("call_7", input[3].GetProperty("call_id").GetString());
        Assert.Equal("{\"path\":\"a.cs\"}", input[3].GetProperty("arguments").GetString());
        Assert.Equal("function_call_output", input[4].GetProperty("type").GetString());
        Assert.Equal("body", input[4].GetProperty("output").GetString());
        Assert.Equal("continue", input[5].GetProperty("content")[0].GetProperty("text").GetString());
        var tool = body.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("parameters").GetProperty("type").GetString());
        Assert.False(tool.TryGetProperty("function", out _));
    }

    [Fact]
    public async Task CompletedOnlyOutput_SynthesizesTextAndTools()
    {
        var events = await Collect(Model(Handler("""{"type":"response.completed","response":{"output":[{"id":"m","type":"message","content":[{"type":"output_text","text":"answer"}]},{"id":"f","type":"function_call","call_id":"c","name":"tool","arguments":"{}"}]}}""")));
        Assert.Equal("answer", Assert.Single(events.OfType<ModelTextDelta>()).Delta);
        Assert.Single(events.OfType<ModelToolCallReady>());
        Assert.Single(events.OfType<ModelStreamFinished>());
    }

    [Theory]
    [InlineData("max_output_tokens", ModelFinishReason.Length)]
    [InlineData("content_filter", ModelFinishReason.ContentFilter)]
    public async Task Incomplete_IsTerminal(string reason, ModelFinishReason expected)
    {
        var events = await Collect(Model(Handler(JsonSerializer.Serialize(new { type = "response.incomplete", response = new { output = Array.Empty<object>(), incomplete_details = new { reason } } }))));
        Assert.Equal(expected, Assert.Single(events.OfType<ModelStreamFinished>()).FinishReason);
        Assert.Empty(events.OfType<ModelStreamFailed>());
    }

    [Theory]
    [InlineData("{\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"too long\"}}}", ModelFailureKind.ContextOverflow)]
    [InlineData("{\"type\":\"error\",\"code\":\"invalid_request_error\",\"message\":\"bad body\"}", ModelFailureKind.InvalidRequest)]
    [InlineData("{\"type\":\"response.created\"}", ModelFailureKind.StreamProtocolError)]
    [InlineData("not-json", ModelFailureKind.StreamProtocolError)]
    public async Task ErrorsAndMissingTerminal_AreFailures(string frame, ModelFailureKind kind)
    {
        var events = await Collect(Model(Handler(frame)));
        Assert.Equal(kind, Assert.Single(events.OfType<ModelStreamFailed>()).Failure.Kind);
        Assert.Empty(events.OfType<ModelStreamFinished>());
    }

    [Fact]
    public async Task HttpFailure_UsesSharedClassifier()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"bad key"}}"""),
        });
        Assert.Equal(ModelFailureKind.AuthenticationFailed, Assert.IsType<ModelStreamFailed>(Assert.Single(await Collect(Model(handler)))).Failure.Kind);
    }

    [Fact]
    public async Task EncryptedReasoning_IsDecodedAtItemEndAndReplayedStatelessly()
    {
        var handler = Handler(
            """{"type":"response.output_item.added","output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[]}}""",
            """{"type":"response.reasoning_summary_text.delta","item_id":"rs_1","summary_index":0,"delta":"Consider it"}""",
            """{"type":"response.reasoning_summary_text.done","item_id":"rs_1","summary_index":0,"text":"Consider it"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"id":"rs_1","type":"reasoning","encrypted_content":"cipher","summary":[{"type":"summary_text","text":"Consider it"}]}}""",
            """{"type":"response.completed","response":{"output":[{"id":"rs_1","type":"reasoning","encrypted_content":"cipher","summary":[{"type":"summary_text","text":"Consider it"}]}]}}""");
        var events = await Collect(Model(handler));
        var ended = Assert.Single(events.OfType<ModelReasoningEnded>());
        Assert.Equal("cipher", ended.EncryptedContent);
        Assert.Equal("rs_1", ended.ProviderItemId);
        Assert.Equal("Consider it", Assert.Single(events.OfType<ModelReasoningDelta>()).Delta);

        var replayHandler = Handler("""{"type":"response.completed","response":{"output":[]}}""");
        await Collect(Model(replayHandler), new ModelRequest([
            new(MessageRole.Assistant,
            [
                new InputReasoningBlock("Consider it", EncryptedContent: ended.EncryptedContent, ProviderItemId: ended.ProviderItemId),
                new InputReasoningBlock("Then act", EncryptedContent: "updated-cipher", ProviderItemId: ended.ProviderItemId),
                new InputTextBlock("answer"),
            ]),
            new(MessageRole.User, [new InputTextBlock("next")]),
        ], [], new()));
        using var body = JsonDocument.Parse(replayHandler.RequestBodies.Single()!);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("reasoning.encrypted_content", body.RootElement.GetProperty("include")[0].GetString());
        var input = body.RootElement.GetProperty("input");
        var reasoning = input[0];
        Assert.Equal("reasoning", reasoning.GetProperty("type").GetString());
        Assert.Equal("rs_1", reasoning.GetProperty("id").GetString());
        Assert.Equal("updated-cipher", reasoning.GetProperty("encrypted_content").GetString());
        Assert.Equal(2, reasoning.GetProperty("summary").GetArrayLength());
        Assert.Equal("Then act", reasoning.GetProperty("summary")[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task MetadataOnlyEncryptedReasoning_WithoutItemId_ReplaysWithoutInventingId()
    {
        var events = await Collect(Model(Handler("""{"type":"response.completed","response":{"output":[{"id":"rs_1","type":"reasoning","encrypted_content":"opaque","summary":[]}]}}""")));
        var ended = Assert.Single(events.OfType<ModelReasoningEnded>());
        Assert.Equal("opaque", ended.EncryptedContent);
        Assert.Equal("rs_1", ended.ProviderItemId);
        Assert.Empty(events.OfType<ModelReasoningDelta>());

        var handler = Handler("""{"type":"response.completed","response":{"output":[]}}""");
        await Collect(Model(handler), new ModelRequest([
            new(MessageRole.Assistant, [new InputReasoningBlock("", EncryptedContent: "opaque"), new InputTextBlock("answer")]),
            new(MessageRole.User, [new InputTextBlock("next")]),
        ], [], new()));
        using var body = JsonDocument.Parse(handler.RequestBodies.Single()!);
        var reasoning = body.RootElement.GetProperty("input")[0];
        Assert.Equal("opaque", reasoning.GetProperty("encrypted_content").GetString());
        Assert.False(reasoning.TryGetProperty("id", out _));
        Assert.Equal(0, reasoning.GetProperty("summary").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_RetryRebuildsIdenticalByteContentWithoutMutatingInputs(bool account)
    {
        var attempts = 0;
        var tokenResolutions = 0;
        var responses = new List<HttpResponseMessage>();
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.IsType<ByteArrayContent>(request.Content);
            Assert.Equal("application/json; charset=utf-8", request.Content.Headers.ContentType!.ToString());
            Assert.Equal("text/event-stream", Assert.Single(request.Headers.Accept).MediaType);
            var response = ++attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }
                : SegmentedStream.ToResponse("data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n");
            responses.Add(response);
            return response;
        });
        var properties = new ModelProperties(DefaultMaxOutputTokens: 128, ReasoningLevels: ["high"],
            ReasoningMapping: "{'reasoning':{'effort':reasoningLevel},'instructions':'mapped instructions'}",
            MaxOutputTokensMapping: "{'max_output_tokens':maxOutputTokens*2,'max_tokens':maxOutputTokens*2,'store':true,'metadata':{'nested':true},'temperature':0.7}");
        var model = new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "static-key", "https://test/v1"), properties,
            accessTokenSource: account ? _ => ValueTask.FromResult($"oauth-{++tokenResolutions}") : null);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string","description":"中文 <>&"}}}""");
        using var arguments = JsonDocument.Parse("""{"path":"a.cs"}""");
        var request = new ModelRequest([
            new(MessageRole.System, [new InputTextBlock("rules 中文 <>&")]),
            new(MessageRole.User, [new InputTextBlock("read it")]),
            new(MessageRole.Assistant,
            [
                new InputReasoningBlock("checking", EncryptedContent: "encrypted", ProviderItemId: "reason-1"),
                new InputToolCallBlock(new("call-1"), "Read", arguments.RootElement.Clone()),
            ]),
            new(MessageRole.User, [new InputToolResultBlock(new("call-1"), "don't use `x` — 中文")]),
        ], [new("Read", "Read a file", schema.RootElement.Clone())], new("high", 256), new("sess_child", "sess_parent"));
        var original = AlcolerJson.Serialize(request);
        var events = new List<ModelStreamEvent>();
        await foreach (var e in ModelStreamRunner.RunAsync(model, request,
            new() { MaxAttempts = 2, BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, JitterFactor = 0, IdleTimeout = Timeout.InfiniteTimeSpan }))
            events.Add(e);

        Assert.Equal(2, attempts);
        Assert.Single(events.OfType<ModelStreamFinished>());
        Assert.Empty(events.OfType<ModelStreamFailed>());
        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
        Assert.Equal(original, AlcolerJson.Serialize(request));
        Assert.NotSame(handler.Requests[0].Content, handler.Requests[1].Content);
        using var body = JsonDocument.Parse(handler.RequestBodies[0]!);
        var root = body.RootElement;
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("reasoning.encrypted_content", root.GetProperty("include")[0].GetString());
        Assert.Equal("mapped instructions", root.GetProperty("instructions").GetString());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal(128, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(!account, root.GetProperty("store").GetBoolean());
        Assert.Equal(account ? "developer" : "system", root.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.Equal("encrypted", root.GetProperty("input")[2].GetProperty("encrypted_content").GetString());
        Assert.Equal(account ? 2 : 0, tokenResolutions);
        if (account)
        {
            Assert.False(root.TryGetProperty("max_output_tokens", out _));
            Assert.False(root.TryGetProperty("metadata", out _));
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.Equal("subagent:parent", root.GetProperty("prompt_cache_key").GetString());
            Assert.All(handler.Requests, r => Assert.Equal("subagent:parent", Assert.Single(r.Headers.GetValues("session-id"))));
            Assert.All(handler.Requests, r => Assert.Equal("parent", Assert.Single(r.Headers.GetValues("thread-id"))));
            Assert.Equal("oauth-1", handler.Requests[0].Headers.Authorization!.Parameter);
            Assert.Equal("oauth-2", handler.Requests[1].Headers.Authorization!.Parameter);
        }
        else
        {
            Assert.Equal(128, root.GetProperty("max_output_tokens").GetInt32());
            Assert.False(root.TryGetProperty("prompt_cache_key", out _));
            Assert.All(handler.Requests, r => Assert.False(r.Headers.Contains("session-id")));
            Assert.All(handler.Requests, r => Assert.Equal("static-key", r.Headers.Authorization!.Parameter));
        }
        foreach (var sent in handler.Requests)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => sent.Content!.ReadAsByteArrayAsync());
        foreach (var response in responses)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("{'max_output_tokens':0}")]
    [InlineData("{'max_output_tokens':'128'}")]
    [InlineData("{'max_output_tokens':[]}")]
    public async Task AccountRequest_InvalidOmittedOutputLimitStillFailsBeforeHttp(string mapping)
    {
        var handler = Handler("unused");
        var model = new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "", "https://test/v1"),
            new ModelProperties(MaxOutputTokensMapping: mapping), accessTokenSource: _ => ValueTask.FromResult("oauth"));
        var failure = Assert.IsType<ModelStreamFailed>(Assert.Single(await Collect(model))).Failure;
        Assert.Equal(ModelFailureKind.InvalidRequest, failure.Kind);
        Assert.Equal(ModelFailurePhase.RequestSetup, failure.Phase);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_CancellationDuringSendDisposesByteContent(bool account)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The request must be canceled.");
        });
        var model = new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "key", "https://test/v1"),
            accessTokenSource: account ? _ => ValueTask.FromResult("oauth") : null);
        var request = new ModelRequest([new(MessageRole.User, [new InputTextBlock("hi")])], [], new());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(request, cancellation.Token)) { }
        });
        var content = Assert.IsType<ByteArrayContent>(Assert.Single(handler.Requests).Content);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.ReadAsByteArrayAsync());
    }

    private static RecordingHandler Handler(params string[] frames) => new((_, _) => SegmentedStream.ToResponse(string.Join("", frames.Select(f => "data: " + f + "\n\n")), 5));
    private static OpenAiResponsesChatModel Model(RecordingHandler handler) => new(new HttpClient(handler), new("test", "key", "https://test/v1"));
    private static async Task<List<ModelStreamEvent>> Collect(IChatModel model, ModelRequest? request = null)
    {
        request ??= new ModelRequest([new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")])], [], new ModelCallOptions());
        var events = new List<ModelStreamEvent>();
        await foreach (var e in model.StreamAsync(request, CancellationToken.None)) events.Add(e);
        return events;
    }
}
