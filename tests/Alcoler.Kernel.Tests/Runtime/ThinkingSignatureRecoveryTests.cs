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

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(422)]
    public void Non400Responses_AreNotSignatureRejections(int statusCode)
    {
        Assert.False(ThinkingSignatureRecovery.IsSignatureRejection(
            new ModelFailure(ModelFailureKind.InvalidRequest, "signature in thinking block"), statusCode));
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
    public void RepairRequestMessages_RemovesOnlySignedOrRedactedAndPreservesTurnBoundaries()
    {
        var unsigned = new InputReasoningBlock("unsigned");
        var encrypted = new InputReasoningBlock("openai", EncryptedContent: "cipher");
        var signed = new InputReasoningBlock("signed", "sig");
        var withText = new ModelInputMessage(MessageRole.Assistant,
            [signed, new InputTextBlock("answer")], ProviderId: "anthropic", ModelId: "model");
        var reasoningOnly = new ModelInputMessage(MessageRole.Assistant,
            [new InputReasoningBlock("", RedactedData: ""), unsigned, new InputTextBlock("  ")]);
        var tool = new InputToolCallBlock(new ToolCallId("call"), "Read", System.Text.Json.JsonSerializer.SerializeToElement(new { }));
        var withTool = new ModelInputMessage(MessageRole.Assistant, [signed, unsigned, tool]);
        var user = new ModelInputMessage(MessageRole.User, [signed]);
        IReadOnlyList<ModelInputMessage> messages = [user, withText, reasoningOnly, withTool, new(MessageRole.Assistant, [encrypted])];

        var repaired = ThinkingSignatureRecovery.RepairRequestMessages(messages);

        Assert.Same(user, repaired[0]);
        Assert.Equal("answer", Assert.IsType<InputTextBlock>(Assert.Single(repaired[1].Content)).Text);
        Assert.Equal("anthropic", repaired[1].ProviderId);
        Assert.Equal("model", repaired[1].ModelId);
        Assert.Equal([unsigned, new InputTextBlock(ThinkingSignatureRecovery.RemovedThinkingText)], repaired[2].Content);
        Assert.Equal([unsigned, tool], repaired[3].Content);
        Assert.Same(messages[4], repaired[4]);
        Assert.Equal(3, messages[2].Content.Count);
    }

    [Fact]
    public void RepairRequestMessages_ReturnsInputReferenceWhenNothingToStrip()
    {
        IReadOnlyList<ModelInputMessage> messages =
        [
            new(MessageRole.Assistant, [new InputReasoningBlock("unsigned", "")]),
            new(MessageRole.Assistant, [new InputReasoningBlock("encrypted", EncryptedContent: "cipher")]),
        ];
        Assert.Same(messages, ThinkingSignatureRecovery.RepairRequestMessages(messages));
    }
}
