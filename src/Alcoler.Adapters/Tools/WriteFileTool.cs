using System.Text.Json;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class WriteFileTool : ITool
{
    public ToolContract Contract { get; } = new(
        "WriteFile",
        "Writes text content to a file inside the workspace, creating parent directories as needed. Overwrites the file when it already exists.",
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "File path, relative to the workspace root or absolute inside it." },
            "content": { "type": "string", "description": "Full file content to write." }
          },
          "required": ["file_path", "content"],
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

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var rawPath = ToolInput.String(context.Input, "file_path");
        var content = ToolInput.String(context.Input, "content");

        string path;
        try
        {
            path = context.Workspace.ResolvePath(rawPath);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content, cancellationToken);

        var lineCount = content.Length == 0 ? 0 : content.Split('\n').Length;
        return ToolExecutionResult.Text($"File written: {path} ({lineCount} lines)");
    }
}
