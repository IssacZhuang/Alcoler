using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;

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
        long contentCharacters = 0;
        long toolCharacters = 0;
        var contentCount = 0;
        var blockCount = 0;
        var steer = message.Presentation == MessagePresentation.UserSteer;

        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart text:
                    blockCount++;
                    CountContent(ref contentCharacters, ref contentCount,
                        steer ? Steering.FormattedUserSteerLength(text.Text) : text.Text.Length);
                    break;
                case ReasoningPart reasoning:
                    blockCount++;
                    CountContent(ref contentCharacters, ref contentCount, reasoning.Text.Length);
                    break;
                case ToolCallPart call:
                    blockCount++;
                    toolCharacters += call.ToolName.Length + CompactJson(call.Input).Length;
                    break;
                case ToolResultPart result:
                    blockCount++;
                    CountContent(ref contentCharacters, ref contentCount, ToolResultContentLength(result));
                    break;
                case AttachmentPart attachment when attachment.ProviderVisible:
                    blockCount++;
                    CountContent(ref contentCharacters, ref contentCount, attachment.Content.Length);
                    break;
                case FilePart file when file.ProviderReady:
                    blockCount++;
                    CountContent(ref contentCharacters, ref contentCount,
                        MediaFilePlaceholderLength(file.FileName ?? file.ArtifactUri));
                    break;
            }
        }

        if (blockCount == 0 && message.Role == MessageRole.Assistant)
        {
            CountContent(ref contentCharacters, ref contentCount, "(no content)".Length);
        }
        return (int)Math.Min(int.MaxValue, (contentCharacters + toolCharacters + 2) / 3);
    }

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

            var textLength = block switch
            {
                InputTextBlock value => value.Text.Length,
                InputReasoningBlock value => value.Text.Length,
                InputImageBlock image => AttachedImagePlaceholderLength(image.MimeType),
                InputToolResultBlock result => result.Content.Length,
                _ => 0,
            };
            if (textLength > 0)
            {
                if (contentCount++ > 0)
                    contentCharacters += 2;
                contentCharacters += textLength;
            }
            if (block is InputToolResultBlock { Images: { } images })
            {
                foreach (var image in images)
                {
                    if (contentCount++ > 0)
                        contentCharacters += 2;
                    contentCharacters += AttachedImagePlaceholderLength(image.MimeType);
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
        using var meter = new JsonLengthMeter();
        long total = 0;
        foreach (var message in request.Messages)
            total += meter.MeasureMessage(message).Tokens;
        foreach (var tool in request.Tools)
            total += meter.MeasureTool(tool).Tokens;
        return (int)Math.Min(int.MaxValue, total);
    }

    internal static string SerializeTool(ToolContract tool)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, RelaxedWriterOptions))
        {
            WriteTool(writer, tool);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string SerializeMessage(ModelInputMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, RelaxedWriterOptions))
        {
            WriteMessage(writer, message);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string RawJson(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? string.Empty : element.GetRawText();

    internal sealed class JsonLengthMeter : IDisposable
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly Utf8JsonWriter _writer;

        internal JsonLengthMeter() => _writer = new Utf8JsonWriter(_buffer, RelaxedWriterOptions);

        internal (int Characters, int Tokens) MeasureTool(ToolContract tool)
        {
            Reset();
            WriteTool(_writer, tool);
            return Complete();
        }

        internal (int Characters, int Tokens) MeasureMessage(ModelInputMessage message)
        {
            Reset();
            WriteMessage(_writer, message);
            return Complete();
        }

        public void Dispose() => _writer.Dispose();

        private void Reset()
        {
            _buffer.Clear();
            _writer.Reset(_buffer);
        }

        private (int Characters, int Tokens) Complete()
        {
            _writer.Flush();
            return MeasureUtf8(_buffer.WrittenSpan);
        }
    }

    // Must match the provider request encoder so estimates track the bytes actually sent.
    private static readonly JsonWriterOptions RelaxedWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void WriteTool(Utf8JsonWriter writer, ToolContract tool)
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

    private static void WriteMessage(Utf8JsonWriter writer, ModelInputMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("role", RoleName(message.Role));
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

    private static string RoleName(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        _ => role.ToString().ToLowerInvariant(),
    };

    private static int ToolResultContentLength(ToolResultPart result)
    {
        var textLength = 0;
        var count = 0;
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case ToolResultText text:
                    textLength += text.Text.Length;
                    count++;
                    break;
                case ToolResultMedia media:
                    textLength += MediaFilePlaceholderLength(media.FileName ?? media.ArtifactUri);
                    count++;
                    break;
            }
        }
        return count > 1 ? textLength + count - 1 : textLength;
    }

    private static int MediaFilePlaceholderLength(string fileNameOrUri) =>
        "[media file: ".Length + fileNameOrUri.Length + 1;

    private static int AttachedImagePlaceholderLength(string mimeType) => 11 + mimeType.Length;

    private static void CountContent(ref long characters, ref int count, int length)
    {
        if (length == 0)
            return;
        if (count++ > 0)
            characters += 2;
        characters += length;
    }

    private static (int Characters, int Tokens) MeasureUtf8(ReadOnlySpan<byte> utf8)
    {
        var characters = 0;
        var weighted = 0;
        while (!utf8.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(utf8, out var rune, out var consumed) != OperationStatus.Done)
            {
                characters++;
                weighted++;
                utf8 = utf8[1..];
                continue;
            }
            var length = rune.Utf16SequenceLength;
            characters += length;
            weighted += IsDoubleWeight(rune.Value) ? 2 : length;
            utf8 = utf8[consumed..];
        }
        return (characters, (weighted + 2) / 3);
    }

    private static bool IsDoubleWeight(char c) => c >= '\u4E00' && c <= '\u9FFF';

    private static bool IsDoubleWeight(int value) => value is >= 0x4E00 and <= 0x9FFF;

    private static string CompactJson(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? "{}" : JsonSerializer.Serialize(element, CompactJsonOptions);
}
