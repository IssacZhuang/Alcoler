using System.Text;
using System.Text.Encodings.Web;
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

    public static int EstimateMessageTokens(ConversationMessage message) =>
        ContextBuilder.Project(message).Sum(EstimateModelMessageTokens);

    public static int EstimateModelMessageTokens(ModelInputMessage message)
    {
        long contentCharacters = 0;
        long toolCharacters = 0;
        var contentCount = 0;
        foreach (var block in message.Content)
        {
            if (block is InputToolCallBlock call)
            {
                toolCharacters += call.Name.Length + CompactJson(call.Input).Length;
                continue;
            }

            var text = block switch
            {
                InputTextBlock value => value.Text,
                InputReasoningBlock value => value.Text,
                InputImageBlock image => $"[Attached {image.MimeType}]",
                InputToolResultBlock result => result.Content,
                _ => string.Empty,
            };
            if (text.Length > 0)
            {
                if (contentCount++ > 0)
                    contentCharacters += 2;
                contentCharacters += text.Length;
            }
            if (block is InputToolResultBlock { Images: { } images })
            {
                foreach (var image in images)
                {
                    if (contentCount++ > 0)
                        contentCharacters += 2;
                    contentCharacters += $"[Attached {image.MimeType}]".Length;
                }
            }
        }
        return (int)Math.Min(int.MaxValue, (contentCharacters + toolCharacters + 2) / 3);
    }

    public static int EstimateHistoryTokens(IReadOnlyList<ConversationMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
            total += EstimateMessageTokens(message);
        return total;
    }

    // Provider input usage excludes the committed assistant unless a context-usage total is available.
    public static (int Tokens, string Source) EstimateCurrentInputTokens(
        IReadOnlyList<ConversationMessage> history,
        int anchorIndex,
        TokenUsage? usage)
    {
        var anchor = usage?.ContextUsageTokens ?? usage?.InputTokens;
        if (anchor is null)
            return (EstimateHistoryTokens(history), EstimateSource);

        var tail = 0;
        var startIndex = usage?.ContextUsageTokens is null ? anchorIndex : anchorIndex + 1;
        for (var i = Math.Max(0, startIndex); i < history.Count; i++)
            tail += EstimateMessageTokens(history[i]);
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

    private static readonly JsonSerializerOptions CompactJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string CompactJson(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? "{}" : JsonSerializer.Serialize(element, CompactJsonOptions);

    private static bool IsDoubleWeight(char c) => c >= '\u4E00' && c <= '\u9FFF';
}
