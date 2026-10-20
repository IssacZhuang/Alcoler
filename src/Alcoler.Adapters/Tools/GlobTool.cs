using System.Text.Json;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class GlobTool : ITool
{
    internal const int MaxEntries = 500;

    public ToolContract Contract { get; } = new(
        "Glob",
        "Finds files in the workspace matching a glob pattern. Supports ** for recursive directory matching, * within a single path segment, and ? for a single character. Returns up to 500 sorted paths.",
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "minLength": 1, "description": "Glob pattern matched against workspace-relative paths, e.g. **/*.cs or src/**/*.ts." },
            "path": { "type": "string", "description": "Directory to search in; defaults to the workspace root." }
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
        AllowedInPlanMode: true);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var pattern = ToolInput.String(context.Input, "pattern");
        var basePath = ToolInput.StringOrNull(context.Input, "path");

        string searchRoot;
        try
        {
            searchRoot = basePath is null
                ? context.Workspace.Root
                : context.Workspace.ResolvePath(basePath);
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

        string RelativePath(ref FileSystemEntry entry)
        {
            var fullPath = entry.ToSpecifiedFullPath();
            var relative = fullPath.Length > searchRoot.Length ? fullPath[(searchRoot.Length + 1)..] : fullPath;
            return relative.Replace('\\', '/');
        }

        var files = new FileSystemEnumerable<string>(
            searchRoot,
            (ref FileSystemEntry entry) => RelativePath(ref entry),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && regex.IsMatch(RelativePath(ref entry)),
        };

        var matches = files
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Take(MaxEntries + 1)
            .ToList();
        var capped = matches.Count > MaxEntries;
        if (capped)
            matches.RemoveAt(matches.Count - 1);

        if (matches.Count == 0)
            return ValueTask.FromResult(ToolExecutionResult.Text($"(no files matched pattern '{pattern}')"));

        var text = string.Join("\n", matches);
        if (capped)
            text += $"\n(results capped at {MaxEntries} matching paths)";
        return ValueTask.FromResult(ToolExecutionResult.Text(text));
    }
}
