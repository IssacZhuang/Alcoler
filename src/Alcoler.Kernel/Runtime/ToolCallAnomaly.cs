using System.Text;
using System.Text.Json;

namespace Alcoler.Kernel.Runtime;

/// <summary>
/// Detects repeated identical tool calls within a turn, mirroring ZCode model-anomaly.ts:
/// a reminder fires on the 3rd consecutive call with the same name and canonical input,
/// at most 3 reminders per turn; any different call resets the streak.
/// </summary>
public sealed class ToolCallAnomalyTracker
{
    public const int WarningThreshold = 3;
    public const int MaxWarningsPerTurn = 3;

    private string? _signature;
    private int _streakCount;
    private int _warningsInjected;

    public void ResetStreak()
    {
        _signature = null;
        _streakCount = 0;
    }

    /// <summary>Returns the warning body when the call is the Nth consecutive identical call, else null.</summary>
    public string? Track(string toolName, JsonElement input)
    {
        var signature = BuildSignature(toolName, input);
        if (string.Equals(_signature, signature, StringComparison.Ordinal))
        {
            _streakCount++;
        }
        else
        {
            _signature = signature;
            _streakCount = 1;
        }

        if (_streakCount != WarningThreshold)
            return null;

        if (_warningsInjected >= MaxWarningsPerTurn)
            return null;
        _warningsInjected++;
        return BuildRepeatedToolCallReminderBody(toolName, _streakCount);
    }

    internal static string BuildRepeatedToolCallReminderBody(string toolName, int observedCount) =>
        string.Join("\n",
        [
            $"You have called {toolName} with the same input {observedCount} times in a row.",
            "Do not repeat the exact same tool call again unless the user explicitly asked you to retry it unchanged.",
            "Use the existing result to take a different next step, explain the blocker, or ask the user for guidance.",
        ]);

    internal static string BuildSignature(string toolName, JsonElement input) =>
        $"{JsonSerializer.Serialize(toolName)}:{CanonicalJson(input)}";

    // Key-sorted JSON so property order in the model's input never masks a repeat.
    private static string CanonicalJson(JsonElement element)
    {
        var builder = new StringBuilder();
        AppendCanonical(element, builder);
        return builder.ToString();
    }

    private static void AppendCanonical(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    AppendCanonical(property.Value, builder);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                for (var i = 0; i < element.GetArrayLength(); i++)
                {
                    if (i > 0)
                        builder.Append(',');
                    AppendCanonical(element[i], builder);
                }
                builder.Append(']');
                break;
            default:
                builder.Append(JsonSerializer.Serialize(element));
                break;
        }
    }
}
