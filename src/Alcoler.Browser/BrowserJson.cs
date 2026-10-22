using System.Buffers;
using System.Collections;
using System.Text;
using System.Text.Json;

namespace Alcoler.Browser;

public static class BrowserJson
{
    public static JsonElement Element(object? value)
    {
        var buffer = new PooledBufferWriter();
        try
        {
            WriteTo(buffer, value);
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return document.RootElement.Clone();
        }
        finally { buffer.Dispose(); }
    }

    public static string Serialize(object? value)
    {
        var buffer = new PooledBufferWriter();
        try
        {
            WriteTo(buffer, value);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally { buffer.Dispose(); }
    }

    public static string SerializeObjectWithout(JsonElement value, string firstExcluded, string secondExcluded)
    {
        var buffer = new PooledBufferWriter();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (property.NameEquals(firstExcluded) || property.NameEquals(secondExcluded)) continue;
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally { buffer.Dispose(); }
    }

    private static void WriteTo(IBufferWriter<byte> buffer, object? value)
    {
        using var writer = new Utf8JsonWriter(buffer);
        Write(writer, value);
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case JsonElement element: element.WriteTo(writer); break;
            case string text: writer.WriteStringValue(text); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case int number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case uint number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case float number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case IReadOnlyDictionary<string, object?> properties:
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (var item in sequence) Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentException($"Unsupported browser protocol value '{value.GetType().Name}'.", nameof(value));
        }
    }

    private sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(1024);
        private int _written;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public void Advance(int count) => _written += count;

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

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 1) sizeHint = 1;
            if (_buffer.Length - _written >= sizeHint) return;
            var next = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _written + sizeHint));
            _buffer.AsSpan(0, _written).CopyTo(next);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = next;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            _buffer = [];
            if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
