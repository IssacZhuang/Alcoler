using System.Text.Encodings.Web;
using System.Text.Json;

namespace Alcoler.Kernel.Tools;

internal static class ToolJson
{
    // Matches JSON.stringify output for the reference result contracts: compact, non-ASCII kept literal.
    internal static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();

    internal static string? StringOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int? IntOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    internal static bool? BoolOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
