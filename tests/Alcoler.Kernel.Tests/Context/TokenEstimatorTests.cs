using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Context;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class TokenEstimatorTests
{
    [Fact]
    public void EstimateTokens_PlainAscii_CeilsDividedByThree()
    {
        Assert.Equal(0, TokenEstimator.EstimateTokens(""));
        Assert.Equal(1, TokenEstimator.EstimateTokens("ab"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("abc"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("abcd"));
        Assert.Equal(3, TokenEstimator.EstimateTokens("abcdefghi"));
    }

    [Fact]
    public void EstimateTokens_CjkCountsDouble()
    {
        Assert.Equal(1, TokenEstimator.EstimateTokens("中"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("中文"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("ab中"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("\u4DBF\u4E00"));
    }

    [Fact]
    public void EstimateTokens_OnlyUnifiedCjkGetsExtraWeightWhileAstralUsesUtf16Length()
    {
        Assert.Equal(1, TokenEstimator.EstimateTokens("\u3400\u3400\u4DBF"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("\uFF21\uFF21\uFFEF"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("\u4E00\u4E00\u9FFF"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("\uFF21"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("\U00020000"));
        Assert.Equal(1, TokenEstimator.EstimateTokens("a\U0002A700"));
        Assert.Equal(2, TokenEstimator.EstimateTokens("aa\U0002A700"));
    }

    [Fact]
    public void EstimateMessageTokens_CountsEveryPartKind()
    {
        var callId = StrongId.NewToolCallId();
        var message = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.User,
        [
            new TextPart(StrongId.NewPartId(), "abc"),
                new ReasoningPart(StrongId.NewPartId(), "reasoning text"),
                new ToolCallPart(StrongId.NewPartId(), callId, "Read", TestJson.Element(new { path = "src/Program.cs" })),
                new ToolResultPart(StrongId.NewPartId(), callId, [new ToolResultText("0123456789")]),
                new FilePart(StrongId.NewPartId(), "alcoler-artifact://s/a.png", "image/png", "a.png"),
                new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, "reminder content"),
        ],
            DateTimeOffset.UtcNow);

        var content = string.Join("\n\n", "abc", "reasoning text", "0123456789", "[media file: a.png]", "reminder content");
        var characters = content.Length + "Read".Length + TestJson.Element(new { path = "src/Program.cs" }).GetRawText().Length;
        var expected = (characters + 2) / 3;

        Assert.Equal(expected, TokenEstimator.EstimateMessageTokens(message));
    }

    [Fact]
    public void EstimateMessageTokens_ToolCallInputJsonIsCounted()
    {
        var callId = StrongId.NewToolCallId();
        var longInput = TestJson.Element(new { path = new string('p', 300) });
        var withCall = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [new ToolCallPart(StrongId.NewPartId(), callId, "Read", longInput)],
            DateTimeOffset.UtcNow);
        var withoutCall = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [new TextPart(StrongId.NewPartId(), "x")],
            DateTimeOffset.UtcNow);

        var callTokens = TokenEstimator.EstimateMessageTokens(withCall);
        Assert.Equal(("Read".Length + longInput.GetRawText().Length + 2) / 3, callTokens);
        Assert.True(callTokens > TokenEstimator.EstimateMessageTokens(withoutCall));
    }

    [Fact]
    public void EstimateHistoryTokens_SumsMessages()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserText("hello world"),
            TestMessages.AssistantText("hi there"),
        };
        var expected = TokenEstimator.EstimateTokens("hello world") + TokenEstimator.EstimateTokens("hi there");

        Assert.Equal(expected, TokenEstimator.EstimateHistoryTokens(history));
        Assert.Equal(0, TokenEstimator.EstimateHistoryTokens([]));
    }

    [Fact]
    public void EstimateCurrentInputTokens_UsesProviderAnchorPlusLocalTail()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserText("one"),
            TestMessages.AssistantText("two"),
            TestMessages.AssistantText("three"),
            TestMessages.UserText("four"),
        };
        var usage = new TokenUsage(ContextUsageTokens: 1_000);

        var (tokens, source) = TokenEstimator.EstimateCurrentInputTokens(history, anchorIndex: 1, usage);

        Assert.Equal("provider_usage", source);
        Assert.Equal(1_000 + TokenEstimator.EstimateHistoryTokens(history.Skip(2).ToList()), tokens);
    }

    [Fact]
    public void EstimateCurrentInputTokens_FallsBackToInputTokens()
    {
        var history = new List<ConversationMessage> { TestMessages.UserText("one"), TestMessages.AssistantText("two") };
        var usage = new TokenUsage(InputTokens: 500);

        var (tokens, source) = TokenEstimator.EstimateCurrentInputTokens(history, anchorIndex: 1, usage);

        Assert.Equal("provider_usage", source);
        Assert.Equal(500 + TokenEstimator.EstimateMessageTokens(history[1]), tokens);
    }

    [Fact]
    public void EstimateModelMessageTokens_UsesUnweightedJoinedContentAndCountsReasoning()
    {
        var message = new ModelInputMessage(MessageRole.Assistant,
            [new InputTextBlock("中文中"), new InputReasoningBlock("abcdef"), new InputTextBlock("")]);

        Assert.Equal((3 + 2 + 6 + 2) / 3, TokenEstimator.EstimateModelMessageTokens(message));
        Assert.Equal(1, TokenEstimator.EstimateMessageTokens(TestMessages.UserText("中文中")));
        Assert.Equal(2, TokenEstimator.EstimateTokens("中文中"));
    }

    [Fact]
    public void EstimateMessageTokens_CountsToolNameAndNormalizedInputAndOmitsHiddenAttachments()
    {
        var input = JsonDocument.Parse("{ \"value\" : \"abc\" }").RootElement.Clone();
        var message = TestMessages.AssistantText("") with
        {
            Parts = [new ToolCallPart(StrongId.NewPartId(), StrongId.NewToolCallId(), "ToolName", input)],
        };

        Assert.Equal(("ToolName{\"value\":\"abc\"}".Length + 2) / 3, TokenEstimator.EstimateMessageTokens(message));
        Assert.Equal(0, TokenEstimator.EstimateMessageTokens(TestMessages.UserAttachment(AttachmentKind.SystemReminder, "hidden", providerVisible: false)));
        Assert.Equal(("ToolName{}".Length + 2) / 3, TokenEstimator.EstimateModelMessageTokens(
            new ModelInputMessage(MessageRole.Assistant,
                [new InputToolCallBlock(StrongId.NewToolCallId(), "ToolName", default)])));
    }

    [Fact]
    public void EstimateRequestTokens_KeepsMarkdownAndNonAsciiLiteral()
    {
        var tool = new ToolContract("Describe", "don't use `x` — 中文", TestJson.Element(new { type = "object" }));
        var request = new ModelRequest([], [tool], new ModelCallOptions());

        var expected = TokenEstimator.EstimateTokens(
            "{\"name\":\"Describe\",\"description\":\"don't use `x` — 中文\",\"inputSchema\":{\"type\":\"object\"}}");

        Assert.Equal(expected, TokenEstimator.EstimateRequestTokens(request));
    }

    [Fact]
    public void EstimateModelMessageTokens_ImageCountsPlaceholderNotBase64Payload()
    {
        var message = new ModelInputMessage(MessageRole.User, [new InputImageBlock("image/png", new string('a', 50_000))]);

        Assert.Equal(("[Attached image/png]".Length + 2) / 3, TokenEstimator.EstimateModelMessageTokens(message));
    }

    [Fact]
    public void EstimateCurrentInputTokens_WithoutUsage_EstimatesWholeHistory()
    {
        var history = new List<ConversationMessage> { TestMessages.UserText("one"), TestMessages.AssistantText("two") };

        var (tokens, source) = TokenEstimator.EstimateCurrentInputTokens(history, anchorIndex: 1, usage: null);

        Assert.Equal("estimate", source);
        Assert.Equal(TokenEstimator.EstimateHistoryTokens(history), tokens);
    }
}

internal static class TestJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    internal static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
}
