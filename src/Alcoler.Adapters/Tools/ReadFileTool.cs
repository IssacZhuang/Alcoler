using System.Text.Json;
using System.Text;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class ReadFileTool : ITool
{
    private const string TruncationNote =
        "[File content truncated at the 75,000 character limit; continue reading with an offset and limit to see the remaining lines]";

    public ToolContract Contract { get; } = new(
        "ReadFile",
        "Reads a UTF-8 text file from the workspace and returns cat -n style numbered lines. Use offset (1-based line number) and limit to page through large files.",
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "File path, relative to the workspace root or absolute inside it." },
            "offset": { "type": "integer", "minimum": 1, "description": "1-based line number to start reading from." },
            "limit": { "type": "integer", "minimum": 1, "description": "Maximum number of lines to return." }
          },
          "required": ["file_path"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.None,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var rawPath = ToolInput.String(context.Input, "file_path");
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

        var offset = ToolInput.IntOrNull(context.Input, "offset") ?? 1;
        var limit = ToolInput.IntOrNull(context.Input, "limit");

        var lines = new List<string>();
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096))
        {
            string? line;
            var lineNumber = 0;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                lineNumber++;
                if (lineNumber < offset)
                    continue;
                if (limit is { } maxLines && lines.Count >= maxLines)
                    break;
                lines.Add($"{lineNumber,6}\t{line}");
            }
        }

        if (lines.Count == 0)
            return ToolExecutionResult.Text("(the requested offset/limit range is outside the file)");

        var text = string.Join("\n", lines);
        if (text.Length > ToolResultLimits.ReadToolMaxChars)
            text = text[..ToolResultLimits.ReadToolMaxChars] + "\n" + TruncationNote;
        return ToolExecutionResult.Text(text);
    }
}
