using System.Text.Json;
using System.Text;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class EditFileTool : ITool
{
    private const int PreviewContextLines = 3;

    public ToolContract Contract { get; } = new(
        "EditFile",
        "Replaces exactly one occurrence of old_string with new_string in a workspace file (or every occurrence when replace_all is true). old_string must be unique unless replace_all is set.",
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "File path, relative to the workspace root or absolute inside it." },
            "old_string": { "type": "string", "minLength": 1, "description": "Exact text to replace; must appear exactly once unless replace_all is true." },
            "new_string": { "type": "string", "description": "Replacement text; empty string deletes old_string." },
            "replace_all": { "type": "boolean", "description": "Replace every occurrence of old_string instead of requiring a unique match." }
          },
          "required": ["file_path", "old_string", "new_string"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: false,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Workspace,
        RiskLevel: RiskLevel.Medium,
        NeedsApproval: false,
        AllowedInPlanMode: false);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (input.TryGetProperty("old_string", out var oldString)
            && input.TryGetProperty("new_string", out var newString)
            && oldString.ValueKind == JsonValueKind.String
            && newString.ValueKind == JsonValueKind.String
            && string.Equals(oldString.GetString(), newString.GetString(), StringComparison.Ordinal))
        {
            return ValueTask.FromResult<ToolInputIssue?>(new ToolInputIssue(
                "no_op_edit",
                "new_string must differ from old_string."));
        }
        return ValueTask.FromResult<ToolInputIssue?>(null);
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var rawPath = ToolInput.String(context.Input, "file_path");
        var oldString = ToolInput.String(context.Input, "old_string");
        var newString = ToolInput.String(context.Input, "new_string");
        var replaceAll = ToolInput.BoolOrDefault(context.Input, "replace_all", false);

        string path;
        try
        {
            path = context.Workspace.ResolvePath(rawPath);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }

        if (!File.Exists(path))
            return ToolExecutionResult.Text(
                $"File does not exist: {path}. Provide a file_path relative to the workspace root, or absolute inside it.",
                isError: true);

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var matchCount = CountOccurrences(text, oldString);
        if (matchCount == 0)
            return ToolExecutionResult.Text(
                $"old_string was not found in {path}. Ensure it matches the file content exactly, including whitespace and indentation.",
                isError: true);
        if (matchCount > 1 && !replaceAll)
            return ToolExecutionResult.Text(
                $"old_string matches {matchCount} locations in {path}. Include more surrounding lines in old_string so it matches exactly once, or set replace_all to true.",
                isError: true);

        var changedIndex = text.IndexOf(oldString, StringComparison.Ordinal);
        var newText = replaceAll
            ? text.Replace(oldString, newString, StringComparison.Ordinal)
            : string.Concat(text.AsSpan(0, changedIndex), newString, text.AsSpan(changedIndex + oldString.Length));
        await File.WriteAllTextAsync(path, newText, cancellationToken);

        return ToolExecutionResult.Text(
            $"The file {path} was edited ({(replaceAll ? matchCount : 1)} occurrence(s) replaced).\n{BuildPreview(newText, changedIndex)}");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string BuildPreview(string text, int changedIndex)
    {
        var changedLine = text.AsSpan(0, changedIndex).Count('\n') + 1;
        var lines = text.Split('\n');
        var from = Math.Max(1, changedLine - PreviewContextLines);
        var to = Math.Min(lines.Length, changedLine + PreviewContextLines);
        var preview = new StringBuilder();
        for (var lineNumber = from; lineNumber <= to; lineNumber++)
            preview.Append($"{lineNumber,6}\t{lines[lineNumber - 1].TrimEnd('\r')}\n");
        return $"Changed lines {from}-{to}:\n{preview.ToString().TrimEnd('\n')}";
    }
}
