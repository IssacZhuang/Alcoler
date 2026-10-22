using System.Buffers;
using System.Collections;
using System.Text;
using System.Text.Json;

namespace Alcoler.Browser;

public static class BrowserJson
{
    public static JsonElement Element(object? value)
    {
        using var document = JsonDocument.Parse(Serialize(value));
        return document.RootElement.Clone();
    }

    public static string Serialize(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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
}
