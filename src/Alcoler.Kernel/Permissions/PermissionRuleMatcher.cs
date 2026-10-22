using System.Text.Json;
using Alcoler.Contracts.Permissions;

namespace Alcoler.Kernel.Permissions;

public static class PermissionRuleMatcher
{
    // Order matters: the first property present in the input becomes the content subject.
    private static readonly string[] SubjectProperties =
        ["command", "url", "file_path", "path", "pattern", "patch_text"];

    private const string WriteTool = "Write";
    private const string EditTool = "Edit";

    public static bool Matches(PermissionRule rule, string toolName, JsonElement input) =>
        ToolNameMatches(rule.ToolName, toolName) && ContentMatches(rule.Content, ExtractSubject(input));

    public static string? ExtractSubject(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in SubjectProperties)
        {
            if (input.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static bool ToolNameMatches(string ruleToolName, string toolName)
    {
        if (ruleToolName.EndsWith('*'))
            return toolName.AsSpan().StartsWith(ruleToolName.AsSpan(0, ruleToolName.Length - 1),
                StringComparison.OrdinalIgnoreCase);

        if (string.Equals(ruleToolName, toolName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Write and Edit are the same tool family; a rule written for either name covers both.
        return (IsWrite(ruleToolName) && IsEdit(toolName)) || (IsEdit(ruleToolName) && IsWrite(toolName));
    }

    private static bool IsWrite(string name) => string.Equals(name, WriteTool, StringComparison.OrdinalIgnoreCase);

    private static bool IsEdit(string name) => string.Equals(name, EditTool, StringComparison.OrdinalIgnoreCase);

    private static bool ContentMatches(string? ruleContent, string? subject)
    {
        if (ruleContent is null || ruleContent == "*")
            return true;
        if (subject is null)
            return false;
        if (ruleContent.EndsWith('*'))
        {
            var prefix = ruleContent.AsSpan(0, ruleContent.Length - 1);
            // "prefix:*" is the wildcard convention: the ":" only separates the prefix from the "*".
            if (prefix.EndsWith(':'))
                prefix = prefix[..^1];
            return subject.AsSpan().StartsWith(prefix, StringComparison.Ordinal);
        }
        return subject.AsSpan().SequenceEqual(ruleContent.AsSpan());
    }
}
