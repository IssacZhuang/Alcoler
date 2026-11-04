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
                    toolCharacters += call.ToolName.Length + CompactJsonLength(call.Input);
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
                toolCharacters += call.Name.Length + CompactJsonLength(call.Input);
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
        TokenUsage? usage,
        Func<ConversationMessage, int>? estimateMessage = null)
    {
        var estimator = estimateMessage ?? EstimateMessageTokens;
        var anchor = usage?.ContextUsageTokens ?? usage?.InputTokens;
        if (anchor is null)
        {
            var total = 0;
            foreach (var message in history)
                total += estimator(message);
            return (total, EstimateSource);
        }

        var tail = 0;
        var startIndex = usage?.ContextUsageTokens is null ? anchorIndex : anchorIndex + 1;
        for (var i = Math.Max(0, startIndex); i < history.Count; i++)
            tail += estimator(history[i]);
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
        private readonly PooledJsonBuffer _buffer = new();
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

        public void Dispose()
        {
            try { _writer.Dispose(); }
            finally { _buffer.Dispose(); }
        }

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

    private sealed class PooledJsonBuffer : IBufferWriter<byte>, IDisposable
    {
        private const int MaxPooledLength = 4 << 20;
        // Idle arrays total less than 8 MiB globally; larger outliers are never pooled.
        private static readonly ArrayPool<byte> Pool = CreatePool();
        private byte[] _buffer = [];
        private int _written;
        private bool _disposed;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void Clear()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _buffer.AsSpan(0, _written).Clear();
            _written = 0;
        }

        public void Advance(int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > _buffer.Length - _written)
                throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            var buffer = _buffer;
            _buffer = [];
            _written = 0;
            Return(buffer);
        }

        private void EnsureCapacity(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            sizeHint = Math.Max(1, sizeHint);
            if (sizeHint <= _buffer.Length - _written)
                return;
            var required = checked(_written + sizeHint);
            var capacity = (int)Math.Max(required, Math.Min(Array.MaxLength, (long)_buffer.Length * 2));
            var next = capacity <= MaxPooledLength ? Pool.Rent(capacity) : new byte[capacity];
            _buffer.AsSpan(0, _written).CopyTo(next);
            Return(_buffer);
            _buffer = next;
        }

        private static void Return(byte[] buffer)
        {
            if (buffer.Length is > 0 and <= MaxPooledLength)
                Pool.Return(buffer, clearArray: true);
        }

        private static ArrayPool<byte> CreatePool() => ArrayPool<byte>.Create(MaxPooledLength, 1);
    }

    // Text and tool JSON must retain the provider request encoder's escaping.
    private static readonly JsonWriterOptions RelaxedWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
                    WriteImageEstimate(writer, image);
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
                    if (result.Images is { Count: > 0 } images)
                    {
                        writer.WritePropertyName("images");
                        writer.WriteStartArray();
                        foreach (var image in images)
                        {
                            writer.WriteStartObject();
                            WriteImageEstimate(writer, image);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled model content block {block.GetType().Name}.");
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteImageEstimate(Utf8JsonWriter writer, InputImageBlock image)
    {
        writer.WriteString("type", "image");
        writer.WriteString("mimeType", image.MimeType);
        writer.WriteString("data", $"[Attached {image.MimeType}]");
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

    // JsonSerializer.Serialize(element, options) is exactly element.WriteTo through a
    // Utf8JsonWriter with the same options, so GetCharCount over the written bytes equals
    // the serialized string's Length without allocating it.
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _compactJsonBuffer;
    [ThreadStatic]
    private static Utf8JsonWriter? _compactJsonWriter;

    private static int CompactJsonLength(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
            return 2;
        var buffer = _compactJsonBuffer ??= new ArrayBufferWriter<byte>();
        var writer = _compactJsonWriter ??= new Utf8JsonWriter(buffer, RelaxedWriterOptions);
        buffer.Clear();
        writer.Reset(buffer);
        element.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetCharCount(buffer.WrittenSpan);
    }
}
