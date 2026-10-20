using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class ThinkingSignatureRecoveryTests
{
    [Theory]
    [InlineData("signature in thinking block is wrong")]
    [InlineData("`thinking` block signature cannot be modified")]
    [InlineData("redacted_thinking payload has an invalid signature")]
    [InlineData("thinking block was rejected: invalid signature")]
    public void NarrowMessages_AreSignatureRejections(string message)
    {
        Assert.True(ThinkingSignatureRecovery.IsSignatureRejection(
            new ModelFailure(ModelFailureKind.InvalidRequest, message)));
    }

    [Theory]
    [InlineData("thinking block is too large")]
    [InlineData("invalid signature on request body")]
    [InlineData("unknown parameter in request")]
    public void WideMessages_AreNotSignatureRejections(string message)
    {
        Assert.False(ThinkingSignatureRecovery.IsSignatureRejection(
            new ModelFailure(ModelFailureKind.InvalidRequest, message)));
    }

    [Fact]
    public void OtherFailureKinds_AreNeverSignatureRejections()
    {
        Assert.False(ThinkingSignatureRecovery.IsSignatureRejection(
            new ModelFailure(ModelFailureKind.ContextOverflow, "signature in thinking block")));
        Assert.False(ThinkingSignatureRecovery.IsSignatureRejection(
            new ModelFailure(ModelFailureKind.ProviderError, "signature in thinking block")));
    }

    [Fact]
    public void StripReasoningParts_KeepsTextAndAddsPlaceholderForReasoningOnlyMessages()
    {
        var withText = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [new ReasoningPart(StrongId.NewPartId(), "secret"), new TextPart(StrongId.NewPartId(), "answer")],
            DateTimeOffset.UtcNow);
        var reasoningOnly = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [new ReasoningPart(StrongId.NewPartId(), "secret")],
            DateTimeOffset.UtcNow);
        var userMessage = TestUser("hi");

        var stripped = ThinkingSignatureRecovery.StripReasoningParts([userMessage, withText, reasoningOnly]);

        Assert.Same(userMessage, stripped[0]);
        Assert.Equal([userMessage.Id, withText.Id, reasoningOnly.Id], stripped.Select(m => m.Id).ToArray());
        Assert.Equal("answer", Assert.IsType<TextPart>(Assert.Single(stripped[1].Parts)).Text);
        var placeholder = Assert.IsType<TextPart>(Assert.Single(stripped[2].Parts));
        Assert.Equal(ThinkingSignatureRecovery.RemovedThinkingText, placeholder.Text);
    }

    [Fact]
    public void StripReasoningParts_ReturnsInputReferenceWhenNothingToStrip()
    {
        IReadOnlyList<ConversationMessage> history = [TestUser("hi"), TestUser("again")];

        Assert.Same(history, ThinkingSignatureRecovery.StripReasoningParts(history));
    }

    private static ConversationMessage TestUser(string text) =>
        new(StrongId.NewMessageId(), MessageRole.User, [new TextPart(StrongId.NewPartId(), text)], DateTimeOffset.UtcNow);
}
