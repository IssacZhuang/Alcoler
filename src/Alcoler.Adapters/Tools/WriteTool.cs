using System.Text.Json;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class WriteTool : ITool
{
    private const string StorageSuffix = " (file state is current in your context \u2014 no need to Read it back)";

    public ToolContract Contract { get; } = new(
        "Write",
        """
        Writes a file to the local filesystem, overwriting if one exists.

        When to use: creating a new file, or fully replacing one you've already Read. Overwriting an existing file you haven't Read will fail. For partial changes, use Edit instead.
        """,
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "The absolute path to the file to write (must be absolute, not relative)" },
            "content": { "type": "string", "description": "The content to write to the file" }
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

        var exists = File.Exists(path);
        if (exists)
        {
            var failure = FileReadGuards.ReadStateFailure(path, context);
            if (failure is not null)
                return failure;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        context.FileReads?.RecordWrite(path);

        return ToolExecutionResult.Text(exists
            ? $"The file {rawPath} has been updated successfully.{StorageSuffix}"
            : $"File created successfully at: {rawPath}{StorageSuffix}");
    }
}
