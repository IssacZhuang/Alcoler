using System.Text.Json;
using System.Text.RegularExpressions;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class GrepTool : ITool
{
    private const int DefaultMaxResults = 100;
    private const int MaxResultsLimit = 10_000;
    private const int BinaryProbeBytes = 8192;
    private const int MaxLinePreviewChars = 1000;

    public ToolContract Contract { get; } = new(
        "Grep",
        "Searches for a text pattern across files in the workspace and returns matching lines as file:line:text. Binary files (containing a null byte in the first 8KB) are skipped. Use regex=true for regular expression patterns.",
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "minLength": 1, "description": "Text to search for, or a regular expression when regex is true." },
            "path": { "type": "string", "description": "Directory to search in; defaults to the workspace root." },
            "include": { "type": "string", "description": "Glob filter on file names, e.g. *.cs." },
            "regex": { "type": "boolean", "description": "Interpret pattern as a regular expression instead of literal text." },
            "max_results": { "type": "integer", "minimum": 1, "maximum": 10000, "description": "Maximum number of matching lines to return; defaults to 100." }
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

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var pattern = ToolInput.String(context.Input, "pattern");
        var basePath = ToolInput.StringOrNull(context.Input, "path");
        var include = ToolInput.StringOrNull(context.Input, "include");
        var useRegex = ToolInput.BoolOrDefault(context.Input, "regex", false);
        var maxResults = Math.Clamp(ToolInput.IntOrNull(context.Input, "max_results") ?? DefaultMaxResults, 1, MaxResultsLimit);

        string searchRoot;
        try
        {
            searchRoot = basePath is null
                ? context.Workspace.Root
                : context.Workspace.ResolvePath(basePath);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }

        if (!Directory.Exists(searchRoot))
            return ToolExecutionResult.Text(
                $"Directory does not exist: {searchRoot}.",
                isError: true);

        Regex matcher;
        try
        {
            matcher = new Regex(
                useRegex ? pattern : Regex.Escape(pattern),
                RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            return ToolExecutionResult.Text($"Invalid regular expression '{pattern}': {ex.Message}", isError: true);
        }

        var includeMatcher = include is null ? null : GlobMatcher.ToRegex(include);
        var files = Directory
            .EnumerateFiles(
                searchRoot,
                "*",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .OrderBy(static path => path, StringComparer.Ordinal);

        var results = new List<string>();
        var capped = false;
        foreach (var file in files)
        {
            if (includeMatcher is not null && !includeMatcher.IsMatch(Path.GetFileName(file)))
                continue;
            if (LooksBinary(file))
                continue;

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!matcher.IsMatch(line))
                    continue;
                results.Add($"{RelativeToWorkspace(context, file)}:{i + 1}:{Preview(line.TrimEnd())}");
                if (results.Count >= maxResults)
                {
                    capped = true;
                    break;
                }
            }
            if (capped)
                break;
        }

        if (results.Count == 0)
            return ToolExecutionResult.Text($"(no matches for pattern '{pattern}')");

        var text = string.Join("\n", results);
        if (capped)
            text += $"\n(results capped at {maxResults} matches)";
        return ToolExecutionResult.Text(text);
    }

    private static string RelativeToWorkspace(ToolExecutionContext context, string file) =>
        Path.GetRelativePath(context.Workspace.Root, file).Replace('\\', '/');

    private static string Preview(string line) =>
        line.Length <= MaxLinePreviewChars ? line : line[..MaxLinePreviewChars] + "...";

    private static bool LooksBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[BinaryProbeBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            return buffer.AsSpan(0, read).Contains((byte)0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
