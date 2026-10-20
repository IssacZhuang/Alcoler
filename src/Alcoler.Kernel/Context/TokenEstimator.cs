using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Context;

public static class TokenEstimator
{
    public const string ProviderUsageSource = "provider_usage";
    public const string EstimateSource = "estimate";

    public static int EstimateTokens(string text)
    {
        var weighted = 0;
        foreach (var c in text)
            weighted += IsDoubleWeight(c) ? 2 : 1;
        return (weighted + 2) / 3;
    }

    public static int EstimateMessageTokens(ConversationMessage message)
    {
        var total = 0;
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart text:
                    total += EstimateTokens(text.Text);
                    break;
                case ReasoningPart reasoning:
                    total += EstimateTokens(reasoning.Text);
                    break;
                case ToolCallPart toolCall:
                    total += EstimateTokens(RawJson(toolCall.Input));
                    break;
                case ToolResultPart result:
                    foreach (var block in result.Content)
                        if (block is ToolResultText resultText)
                            total += EstimateTokens(resultText.Text);
                    break;
                case FilePart file:
                    if (file.FileName is not null)
                        total += EstimateTokens(file.FileName);
                    break;
                case AttachmentPart attachment:
                    total += EstimateTokens(attachment.Content);
                    break;
            }
        }
        return total;
    }

    public static int EstimateHistoryTokens(IReadOnlyList<ConversationMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
            total += EstimateMessageTokens(message);
        return total;
    }

    // Anchor is ContextUsageTokens when reported, else InputTokens of the same committed step.
    public static (int Tokens, string Source) EstimateCurrentInputTokens(
        IReadOnlyList<ConversationMessage> history,
        int anchorIndex,
        TokenUsage? usage)
    {
        var anchor = usage?.ContextUsageTokens ?? usage?.InputTokens;
        if (anchor is null)
            return (EstimateHistoryTokens(history), EstimateSource);

        var tail = 0;
        for (var i = anchorIndex + 1; i < history.Count; i++)
            foreach (var projected in ContextBuilder.Project(history[i]))
                tail += EstimateTokens(SerializeMessage(projected));
        return (anchor.Value + tail, ProviderUsageSource);
    }

    public static int EstimateRequestTokens(ModelRequest request)
    {
        var total = request.Messages.Sum(message => (long)EstimateTokens(SerializeMessage(message)));
        total += request.Tools.Sum(tool => (long)EstimateTokens(SerializeTool(tool)));
        return (int)Math.Min(int.MaxValue, total);
    }

    internal static string SerializeTool(ToolContract tool) => JsonSerializer.Serialize(new
    {
        name = tool.Name,
        description = tool.Description,
        inputSchema = tool.InputSchema.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : tool.InputSchema,
    });

    internal static string SerializeMessage(ModelInputMessage message) => JsonSerializer.Serialize(new
    {
        role = message.Role.ToString().ToLowerInvariant(),
        content = message.Content.Select(block => block switch
        {
            InputTextBlock text => (object)new { type = "text", text = text.Text },
            InputReasoningBlock reasoning => new { type = "reasoning", text = reasoning.Text },
            InputImageBlock image => new { type = "image", mimeType = image.MimeType, data = image.Base64Data },
            InputToolCallBlock call => new { type = "tool_call", id = call.Id.Value, name = call.Name, input = call.Input },
            InputToolResultBlock result => new { type = "tool_result", id = result.Id.Value, content = result.Content, isError = result.IsError },
            _ => throw new InvalidOperationException($"Unhandled model content block {block.GetType().Name}."),
        }),
    });

    internal static string RawJson(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? string.Empty : element.GetRawText();

    // Astral code points weigh 2 through their surrogate pair; a UTF-16 char cannot itself exceed 0xFFFF.
    private static bool IsDoubleWeight(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF')
        || (c >= '\u3400' && c <= '\u4DBF')
        || (c >= '\uFF00' && c <= '\uFFEF');
}
