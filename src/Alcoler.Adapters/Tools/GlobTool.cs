using System.Text.Json;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class GlobTool : ITool
{
    internal const int MaxEntries = 100;
    internal const int MaxModelChars = 100_000;
    private const int DefaultTimeoutMs = 30_000;

    public ToolContract Contract { get; } = new(
        "Glob",
        "Fast file pattern matching. Supports glob patterns like \"**/*.js\" or \"src/**/*.ts\". Returns matching file paths sorted by modification time.",
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "minLength": 1, "description": "The glob pattern to match files against" },
            "path": { "type": "string", "description": "The directory to search in. If not specified, the workspace root will be used. IMPORTANT: Omit this field to use the default directory. DO NOT enter \"undefined\" or \"null\" - simply omit it for the default behavior. Must be a valid directory path if provided." }
          },
          "required": ["pattern"],
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
        AllowedInPlanMode: true,
        DefaultTimeoutMs: DefaultTimeoutMs);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var pattern = ToolInput.String(context.Input, "pattern").Trim();
        if (pattern.Length == 0)
            return ValueTask.FromResult(ToolExecutionResult.Text("Glob pattern must not be empty", isError: true));

        var basePath = ToolInput.StringOrNull(context.Input, "path");
        string searchRoot;
        try
        {
            searchRoot = basePath is null ? context.Workspace.Root : context.Workspace.ResolvePath(basePath);
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(ToolExecutionResult.Text(ex.Message, isError: true));
        }

        if (!Directory.Exists(searchRoot))
            return ValueTask.FromResult(ToolExecutionResult.Text(
                $"Directory does not exist: {searchRoot}.",
                isError: true));

        searchRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(searchRoot));
        var regex = GlobMatcher.ToRegex(pattern);

        var matches = new List<(DateTime MtimeUtc, string RelativePath)>();
        foreach (var file in FileSearchSupport.EnumerateFiles(searchRoot))
        {
            var relative = Path.GetRelativePath(searchRoot, file).Replace('\\', '/');
            if (regex.IsMatch(relative))
                matches.Add((FileSearchSupport.GetLastWriteTimeUtcSafe(file), relative));
        }

        cancellationToken.ThrowIfCancellationRequested();
        matches.Sort(FileSearchSupport.CompareNewestFirst);
        var truncated = matches.Count > MaxEntries;
        var filenames = matches.Take(MaxEntries).Select(static match => match.RelativePath).ToList();

        if (filenames.Count == 0)
            return ValueTask.FromResult(ToolExecutionResult.Text("No files found"));

        var text = string.Join("\n", filenames);
        if (truncated)
            text += "\n(Results are truncated. Consider using a more specific path or pattern.)";
        return ValueTask.FromResult(ToolResultBudget.ApplyBudget(ToolExecutionResult.Text(text), MaxModelChars));
    }
}
