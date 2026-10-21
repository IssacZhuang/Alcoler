using System.Net;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
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
