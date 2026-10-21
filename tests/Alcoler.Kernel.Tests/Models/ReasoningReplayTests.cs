using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public class ReasoningReplayTests
{
    private static AnthropicChatModel CreateModel(RecordingHandler handler) =>
        new(new HttpClient(handler), new AnthropicModelOptions("claude-sonnet-4", "test-key", "https://unit.test"));

    private static ModelRequest Request(params ModelInputMessage[] messages) =>
        new(messages, [], new ModelCallOptions());

    private static async Task<JsonDocument> CaptureBodyAsync(ModelRequest request)
    {
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(
            """
            data: {"type":"message_start","message":{"usage":{"input_tokens":1}}}

            data: {"type":"message_stop"}

            """));
        var model = CreateModel(handler);
        await foreach (var _ in model.StreamAsync(request, CancellationToken.None))
        {
        }
        return JsonDocument.Parse(handler.RequestBodies.Single()!);
    }

    [Fact]
    public async Task Anthropic_ReplaysSignedThinkingBlocks()
    {
        var request = Request(
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")]),
            new ModelInputMessage(MessageRole.Assistant,
            [
                new InputReasoningBlock("pondering", "sig-abc"),
                new InputTextBlock("answer"),
            ]),
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("go on")]));

        using var body = await CaptureBodyAsync(request);

        var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("thinking", content[0].GetProperty("type").GetString());
        Assert.Equal("pondering", content[0].GetProperty("thinking").GetString());
        Assert.Equal("sig-abc", content[0].GetProperty("signature").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Anthropic_ReplaysUnsignedThinkingWithEmptySignature()
    {
        var request = Request(
            new ModelInputMessage(MessageRole.Assistant,
            [
                new InputReasoningBlock("bare thought"),
                new InputToolCallBlock(
                    new ToolCallId("call-1"),
                    "Read",
                    JsonSerializer.SerializeToElement(new { file_path = "a.txt" })),
            ]),
            new ModelInputMessage(MessageRole.User,
            [
                new InputToolResultBlock(new ToolCallId("call-1"), "content"),
            ]));

        using var body = await CaptureBodyAsync(request);

        var thinking = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("", thinking.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task Anthropic_TrailingReasoningOnlyAssistant_IsDropped()
    {
        var request = Request(
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")]),
            new ModelInputMessage(MessageRole.Assistant, [new InputReasoningBlock("trailing", "sig")]));

        var raw = await CaptureRawBodyAsync(request);
        using var body = JsonDocument.Parse(raw);
        var messages = body.RootElement.GetProperty("messages");
        Assert.True(messages.GetArrayLength() == 1, $"payload was: {raw}");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    private static async Task<string> CaptureRawBodyAsync(ModelRequest request)
    {
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(
            """
            data: {"type":"message_start","message":{"usage":{"input_tokens":1}}}

            data: {"type":"message_stop"}

            """));
        var model = CreateModel(handler);
        await foreach (var _ in model.StreamAsync(request, CancellationToken.None))
        {
        }
        return handler.RequestBodies.Single()!;
    }

    [Fact]
    public async Task Anthropic_EmptyAssistantContent_IsRepaired()
    {
        var request = Request(
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")]),
            new ModelInputMessage(MessageRole.Assistant, []),
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("again")]));

        using var body = await CaptureBodyAsync(request);

        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var repaired = messages[1].GetProperty("content");
        Assert.Equal("text", repaired[0].GetProperty("type").GetString());
        Assert.Equal("(no content)", repaired[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Anthropic_WhitespaceOnlyAssistant_RemovedAndUsersMerged()
    {
        var request = Request(
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("first")]),
            new ModelInputMessage(MessageRole.Assistant, [new InputTextBlock("   ")], new CacheControl()),
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("second")]));

        using var body = await CaptureBodyAsync(request);

        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        var content = messages[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("first\n", content[0].GetProperty("text").GetString());
        Assert.Equal("second", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Anthropic_ReasoningOnlyAssistantBetweenMessages_IsRemoved()
    {
        var request = Request(
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")]),
            new ModelInputMessage(MessageRole.Assistant, [new InputReasoningBlock("orphan", "sig")]),
            new ModelInputMessage(MessageRole.Assistant, [new InputTextBlock("real answer")]));

        using var body = await CaptureBodyAsync(request);

        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Anthropic_RedactedThinkingAndStartSignature_AreCapturedAndReplayed()
    {
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse("""
            data: {"type":"message_start","message":{}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"opaque"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"content_block_start","index":1,"content_block":{"type":"thinking","signature":"start-sig"}}

            data: {"type":"content_block_stop","index":1}

            data: {"type":"message_stop"}

            """));
        var events = new List<ModelStreamEvent>();
        await foreach (var e in CreateModel(handler).StreamAsync(Request(new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")])), CancellationToken.None))
            events.Add(e);
        Assert.Equal("opaque", events.OfType<ModelReasoningEnded>().First().RedactedData);
        Assert.Equal("start-sig", events.OfType<ModelReasoningEnded>().Last().ProviderSignature);
        using var body = await CaptureBodyAsync(Request(
            new(MessageRole.Assistant, [new InputReasoningBlock("", RedactedData: "opaque"), new InputTextBlock("answer")]),
            new(MessageRole.User, [new InputTextBlock("go on")])));
        var redacted = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("redacted_thinking", redacted.GetProperty("type").GetString());
        Assert.Equal("opaque", redacted.GetProperty("data").GetString());
    }

    [Theory]
    [InlineData("anthropic", "claude-sonnet-4", true)]
    [InlineData("other", "claude-sonnet-4", false)]
    [InlineData("anthropic", "old-model", false)]
    [InlineData(null, "old-model", true)]
    [InlineData("anthropic", null, true)]
    public async Task Anthropic_CrossModelPruning_PreservesUnsignedAndUnknownOrigin(string? provider, string? model, bool keepsSigned)
    {
        var original = new ModelInputMessage(MessageRole.Assistant,
            [new InputReasoningBlock("signed", "sig"), new InputReasoningBlock("unsigned"), new InputTextBlock("answer")],
            ProviderId: provider, ModelId: model);
        using var body = await CaptureBodyAsync(Request(original, new(MessageRole.User, [new InputTextBlock("next")])));
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(keepsSigned ? 3 : 2, content.GetArrayLength());
        Assert.Contains(content.EnumerateArray(), block => block.TryGetProperty("thinking", out var text) && text.GetString() == "unsigned");
        Assert.Equal(3, original.Content.Count);
    }

    [Fact]
    public async Task Anthropic_ExplicitProviderAliasesRetainSignedReasoning()
    {
        var handler = new RecordingHandler((_, _) => Success());
        var model = new AnthropicChatModel(new HttpClient(handler), new("model", "key"), providerId: "account:zai-team-coding-plan");
        var request = Request(new ModelInputMessage(MessageRole.Assistant,
            [new InputReasoningBlock("signed", "sig"), new InputTextBlock("answer")],
            ProviderId: "builtin:zai-coding-plan", ModelId: "model"), new(MessageRole.User, [new InputTextBlock("next")]));
        await foreach (var _ in model.StreamAsync(request, CancellationToken.None)) { }
        using var body = JsonDocument.Parse(handler.RequestBodies.Single()!);
        Assert.Equal("sig", body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("signature").GetString());
    }

    [Fact]
    public async Task Anthropic_UnsignedOnlyRejection_DoesNotGetRepairAttempt()
    {
        var handler = new RecordingHandler((_, _) => SignatureFailure());
        await foreach (var _ in CreateModel(handler).StreamAsync(Request(
            new ModelInputMessage(MessageRole.Assistant, [new InputReasoningBlock("unsigned"), new InputTextBlock("answer")]),
            new(MessageRole.User, [new InputTextBlock("next")])), CancellationToken.None)) { }
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(422, false)]
    public async Task Anthropic_SignatureRepair_IsSignedOnlyRequestCopyAndOneExtraAttempt(int status, bool repairs)
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) => ++calls == 1 ? SignatureFailure(status) : Success());
        var original = new ModelInputMessage(MessageRole.Assistant,
            [new InputReasoningBlock("signed", "sig"), new InputReasoningBlock("unsigned"), new InputTextBlock("  ")]);
        var events = new List<ModelStreamEvent>();
        await foreach (var e in Alcoler.Kernel.Models.ModelStreamRunner.RunAsync(CreateModel(handler),
            Request(original, new(MessageRole.User, [new InputTextBlock("next")])),
            new() { MaxAttempts = 1 }, CancellationToken.None)) events.Add(e);
        Assert.Equal(repairs ? 2 : 1, calls);
        Assert.Equal(repairs ? 0 : 1, events.OfType<ModelStreamFailed>().Count());
        Assert.Equal(3, original.Content.Count);
        if (repairs)
        {
            using var body = JsonDocument.Parse(handler.RequestBodies[1]!);
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("unsigned", content[0].GetProperty("thinking").GetString());
            Assert.Equal("[Thinking removed]", content[1].GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task Anthropic_RepairScopeSurvivesOrdinaryRetries_WithoutRepeatingOrRenormalizing()
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) => ++calls switch
        {
            1 => SignatureFailure(),
            2 => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("temporary") },
            _ => Success(),
        });
        var model = CreateModel(handler);
        var request = Request(new ModelInputMessage(MessageRole.Assistant,
            [new InputReasoningBlock("signed", "sig"), new InputTextBlock("  ")]), new(MessageRole.User, [new InputTextBlock("next")]));
        var events = new List<ModelStreamEvent>();
        await foreach (var e in Alcoler.Kernel.Models.ModelStreamRunner.RunAsync(model, request,
            new() { MaxAttempts = 2, BaseDelay = TimeSpan.Zero }, CancellationToken.None)) events.Add(e);
        Assert.Equal(3, calls);
        Assert.Single(events.OfType<ModelStreamFinished>());
        Assert.Equal(handler.RequestBodies[1], handler.RequestBodies[2]);
        Assert.Contains("[Thinking removed]", handler.RequestBodies[2]);
        await foreach (var _ in model.StreamAsync(request, CancellationToken.None)) { }
        Assert.Contains("sig", handler.RequestBodies[3]);
    }

    [Fact]
    public async Task Anthropic_SecondSignatureFailureIsNotRetried()
    {
        var handler = new RecordingHandler((_, _) => SignatureFailure());
        var events = new List<ModelStreamEvent>();
        await foreach (var e in CreateModel(handler).StreamAsync(Request(
            new(MessageRole.Assistant, [new InputReasoningBlock("signed", "sig"), new InputTextBlock("answer")]),
            new(MessageRole.User, [new InputTextBlock("next")])), CancellationToken.None)) events.Add(e);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(events.OfType<ModelStreamFailed>());
    }

    [Fact]
    public async Task Anthropic_ToolResultImages_AreNestedInResultContent()
    {
        var handler = new RecordingHandler((_, _) => Success());
        await foreach (var _ in CreateModel(handler, true).StreamAsync(Request(new ModelInputMessage(MessageRole.User,
            [new InputToolResultBlock(new("call"), "caption", Images: [new("image/png", "aW1n")])])), CancellationToken.None)) { }
        using var body = JsonDocument.Parse(handler.RequestBodies.Single()!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("content");
        Assert.Equal("caption", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("aW1n", content[1].GetProperty("source").GetProperty("data").GetString());
    }

    private static HttpResponseMessage SignatureFailure(int status = 400) => new((System.Net.HttpStatusCode)status)
    {
        Content = new StringContent("""{"error":{"type":"invalid_request_error","message":"signature in thinking block is invalid"}}"""),
    };

    private static HttpResponseMessage Success() => SegmentedStream.ToResponse("data: {\"type\":\"message_stop\"}\n\n");

    private static AnthropicChatModel CreateModel(RecordingHandler handler, bool supportsImages = false) =>
        new(new HttpClient(handler), new AnthropicModelOptions("claude-sonnet-4", "test-key", "https://unit.test"),
            supportsImages ? ImageCapableProperties() : null);

    private static Alcoler.Contracts.Models.ModelProperties ImageCapableProperties() =>
        new(SupportsImage: true, ContextWindowTokens: 200_000, DefaultMaxOutputTokens: 32_768);

    private static async Task<JsonDocument> CaptureImageBodyAsync(bool supportsImages)
    {
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(
            """
            data: {"type":"message_start","message":{"usage":{"input_tokens":1}}}

            data: {"type":"message_stop"}

            """));
        var model = CreateModel(handler, supportsImages);
        var request = Request(
            new ModelInputMessage(MessageRole.User,
            [
                new InputTextBlock("what is this?"),
                new InputImageBlock("image/png", "aW1hZ2U="),
            ]));
        await foreach (var _ in model.StreamAsync(request, CancellationToken.None))
        {
        }
        return JsonDocument.Parse(handler.RequestBodies.Single()!);
    }

    [Fact]
    public async Task Anthropic_ImageCapableModel_SerializesBase64ImageBlock()
    {
        using var body = await CaptureImageBodyAsync(supportsImages: true);

        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        var source = content[1].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal("aW1hZ2U=", source.GetProperty("data").GetString());
    }

    [Fact]
    public async Task Anthropic_ImageIncapableModel_DowngradesToPlaceholderText()
    {
        using var body = await CaptureImageBodyAsync(supportsImages: false);

        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Contains(
            "[Media omitted from provider request because the selected model does not support image input.]",
            content[1].GetProperty("text").GetString());
    }
}
