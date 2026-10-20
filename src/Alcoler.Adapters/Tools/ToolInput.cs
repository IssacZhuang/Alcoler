using System.Text.Json;

namespace Alcoler.Adapters.Tools;

internal static class ToolInput
{
    public static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();

    public static string String(JsonElement input, string name) =>
        input.GetProperty(name).GetString()!;

    public static string? StringOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? IntOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    public static bool BoolOrDefault(JsonElement input, string name, bool fallback) =>
        input.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
