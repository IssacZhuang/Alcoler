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
