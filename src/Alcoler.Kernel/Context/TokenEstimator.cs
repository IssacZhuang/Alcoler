using System.Text;
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

    internal static string SerializeTool(ToolContract tool)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            if (tool.InputSchema.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNull("inputSchema");
            }
            else
            {
                writer.WritePropertyName("inputSchema");
                tool.InputSchema.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static string SerializeMessage(ModelInputMessage message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("role", message.Role.ToString().ToLowerInvariant());
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            foreach (var block in message.Content)
            {
                writer.WriteStartObject();
                switch (block)
                {
                    case InputTextBlock text:
                        writer.WriteString("type", "text");
                        writer.WriteString("text", text.Text);
                        break;
                    case InputReasoningBlock reasoning:
                        writer.WriteString("type", "reasoning");
                        writer.WriteString("text", reasoning.Text);
                        break;
                    case InputImageBlock image:
                        writer.WriteString("type", "image");
                        writer.WriteString("mimeType", image.MimeType);
                        writer.WriteString("data", image.Base64Data);
                        break;
                    case InputToolCallBlock call:
                        writer.WriteString("type", "tool_call");
                        writer.WriteString("id", call.Id.Value);
                        writer.WriteString("name", call.Name);
                        writer.WritePropertyName("input");
                        call.Input.WriteTo(writer);
                        break;
                    case InputToolResultBlock result:
                        writer.WriteString("type", "tool_result");
                        writer.WriteString("id", result.Id.Value);
                        writer.WriteString("content", result.Content);
                        writer.WriteBoolean("isError", result.IsError);
                        break;
                    default:
                        throw new InvalidOperationException($"Unhandled model content block {block.GetType().Name}.");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static string RawJson(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? string.Empty : element.GetRawText();

    // Astral code points weigh 2 through their surrogate pair; a UTF-16 char cannot itself exceed 0xFFFF.
    private static bool IsDoubleWeight(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF')
        || (c >= '\u3400' && c <= '\u4DBF')
        || (c >= '\uFF00' && c <= '\uFFEF');
}
