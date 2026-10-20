using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Kernel.Tests.Context;

internal static class TestMessages
{
    internal static int Counter;

    internal static MessageId NextMessageId() => new($"msg_{++Counter}");
    internal static PartId NextPartId() => new($"part_{++Counter}");
    internal static ToolCallId NextToolCallId(string name) => new($"call_{name}_{++Counter}");

    internal static ConversationMessage UserText(string text) =>
        new(NextMessageId(), MessageRole.User, [new TextPart(NextPartId(), text)], DateTimeOffset.UtcNow);

    internal static ConversationMessage AssistantText(string text) =>
        new(NextMessageId(), MessageRole.Assistant, [new TextPart(NextPartId(), text)], DateTimeOffset.UtcNow);

    internal static ConversationMessage AssistantToolCall(ToolCallId callId, string toolName, object? input = null)
    {
        var element = JsonSerializer.SerializeToElement(input ?? new { });
        return new ConversationMessage(
            NextMessageId(),
            MessageRole.Assistant,
            [new ToolCallPart(NextPartId(), callId, toolName, element)],
            DateTimeOffset.UtcNow);
    }

    internal static ConversationMessage ToolResult(
        ToolCallId callId,
        IReadOnlyList<ToolResultBlock> content,
        bool isError = false) =>
        new(
            NextMessageId(),
            MessageRole.User,
            [new ToolResultPart(NextPartId(), callId, content, isError)],
            DateTimeOffset.UtcNow);

    internal static ConversationMessage ToolResult(ToolCallId callId, string text, bool isError = false) =>
        ToolResult(callId, [new ToolResultText(text)], isError);

    internal static ConversationMessage UserAttachment(AttachmentKind kind, string content, bool providerVisible = true) =>
        new(
            NextMessageId(),
            MessageRole.User,
            [new AttachmentPart(NextPartId(), kind, content, providerVisible)],
            DateTimeOffset.UtcNow);

    internal static (List<ConversationMessage> Messages, ToolCallId CallId) Round(
        string toolName,
        string resultText,
        bool isError = false)
    {
        var callId = NextToolCallId(toolName);
        var messages = new List<ConversationMessage>
        {
            AssistantToolCall(callId, toolName, new { path = "a.cs" }),
            ToolResult(callId, resultText, isError),
        };
        return (messages, callId);
    }

    internal static List<ConversationMessage> Rounds(int count, string toolName, int resultChars)
    {
        var history = new List<ConversationMessage>();
        for (var i = 0; i < count; i++)
            history.AddRange(Round(toolName, new string('x', resultChars)).Messages);
        return history;
    }
}
