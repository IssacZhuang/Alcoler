using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
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
        Assert.Equal(2, TokenEstimator.EstimateTokens("\u4DBF\u4E00"));
    }

    [Fact]
    public void EstimateTokens_FullWidthAndAstralCountDouble()
    {
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
                new ToolCallPart(StrongId.NewPartId(), callId, "ReadFile", TestJson.Element(new { path = "src/Program.cs" })),
                new ToolResultPart(StrongId.NewPartId(), callId, [new ToolResultText("0123456789")]),
                new FilePart(StrongId.NewPartId(), "alcoler-artifact://s/a.png", "image/png", "a.png"),
                new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, "reminder content"),
        ],
            DateTimeOffset.UtcNow);

        var expected = TokenEstimator.EstimateTokens("abc")
            + TokenEstimator.EstimateTokens("reasoning text")
            + TokenEstimator.EstimateTokens(TestJson.Element(new { path = "src/Program.cs" }).GetRawText())
            + TokenEstimator.EstimateTokens("0123456789")
            + TokenEstimator.EstimateTokens("a.png")
            + TokenEstimator.EstimateTokens("reminder content");

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
            [new ToolCallPart(StrongId.NewPartId(), callId, "ReadFile", longInput)],
            DateTimeOffset.UtcNow);
        var withoutCall = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [new TextPart(StrongId.NewPartId(), "x")],
            DateTimeOffset.UtcNow);

        var callTokens = TokenEstimator.EstimateMessageTokens(withCall);
        Assert.Equal(TokenEstimator.EstimateTokens(longInput.GetRawText()), callTokens);
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
        var tail = new ModelRequest(
            [new ModelInputMessage(MessageRole.Assistant, [new InputTextBlock("three")]),
                new ModelInputMessage(MessageRole.User, [new InputTextBlock("four")])], [], new ModelCallOptions());
        Assert.Equal(1_000 + TokenEstimator.EstimateRequestTokens(tail), tokens);
    }

    [Fact]
    public void EstimateCurrentInputTokens_FallsBackToInputTokens()
    {
        var history = new List<ConversationMessage> { TestMessages.UserText("one"), TestMessages.AssistantText("two") };
        var usage = new TokenUsage(InputTokens: 500);

        var (tokens, source) = TokenEstimator.EstimateCurrentInputTokens(history, anchorIndex: 1, usage);

        Assert.Equal("provider_usage", source);
        Assert.Equal(500, tokens);
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
