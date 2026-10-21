using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

/// <summary>
/// Content search over the ripgrep engine (native alco-grep library first,
/// managed .NET regex fallback), preserving the reference tool contract and
/// output formats.
/// </summary>
public sealed class GrepTool : ITool
{
    internal const int DefaultHeadLimit = 250;
    internal const int MaxModelChars = 20_000;
    internal const int MaxColumnChars = 500;
    private const int DefaultTimeoutMs = 30_000;
    private const string TruncationSuffix = " [...truncated]";

    private static readonly Lazy<IGrepEngine> DefaultEngine = new(
        () => (IGrepEngine?)AlcoGrepEngine.TryCreate() ?? new ManagedGrepEngine());

    private readonly IGrepEngine _engine;

    public GrepTool() : this(DefaultEngine.Value)
    {
    }

    internal GrepTool(IGrepEngine engine)
    {
        _engine = engine;
    }

    public ToolContract Contract { get; } = new(
        "Grep",
        """
        Content search built on ripgrep. Prefer this over `grep`/`rg` via Bash — results integrate with the permission UI and file links.

        - Full regex syntax (e.g. "log.*Error", "function\s+\w+"). ripgrep's Rust regex, not grep — no look-around or backreferences; escape literal braces (`interface\{\}`).
        - Filter with `glob` (e.g. "**/*.tsx") or `type` (e.g. "js", "py", "rust").
        - `output_mode`: "content" (matching lines), "files_with_matches" (paths only, default), or "count".
        - `multiline: true` for patterns that span lines.
        """,
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "minLength": 1, "description": "The regular expression pattern to search for in file contents" },
            "path": { "type": "string", "description": "File or directory to search in (rg PATH). Defaults to current working directory." },
            "glob": { "type": "string", "description": "Glob pattern to filter files (e.g. \"*.js\", \"*.{ts,tsx}\") - maps to rg --glob" },
            "output_mode": { "type": "string", "enum": ["content", "files_with_matches", "count"], "description": "Output mode: \"content\" shows matching lines (supports -A/-B/-C context, -n line numbers, head_limit), \"files_with_matches\" shows file paths (supports head_limit), \"count\" shows match counts (supports head_limit). Defaults to \"files_with_matches\"." },
            "-B": { "type": "integer", "minimum": 0, "description": "Number of lines to show before each match (rg -B). Requires output_mode: \"content\", ignored otherwise." },
            "-A": { "type": "integer", "minimum": 0, "description": "Number of lines to show after each match (rg -A). Requires output_mode: \"content\", ignored otherwise." },
            "-C": { "type": "integer", "minimum": 0, "description": "Alias for context." },
            "context": { "type": "integer", "minimum": 0, "description": "Number of lines to show before and after each match (rg -C). Requires output_mode: \"content\", ignored otherwise." },
            "-n": { "type": "boolean", "description": "Show line numbers in output (rg -n). Requires output_mode: \"content\", ignored otherwise. Defaults to true." },
            "-i": { "type": "boolean", "description": "Case insensitive search (rg -i)" },
            "-o": { "type": "boolean", "description": "Print only the matched (non-empty) parts of each matching line, one match per output line (rg -o / --only-matching). Requires output_mode: \"content\", ignored otherwise. Defaults to false." },
            "type": { "type": "string", "description": "File type to search (rg --type). Common types: js, py, rust, go, java, etc. More efficient than include for standard file types." },
            "head_limit": { "type": "integer", "minimum": 0, "description": "Limit output to first N lines/entries, equivalent to \"| head -N\". Works across all output modes: content (limits output lines), files_with_matches (limits file paths), count (limits count entries). Defaults to 250 when unspecified. Pass 0 for unlimited (use sparingly — large result sets waste context)." },
            "offset": { "type": "integer", "minimum": 0, "description": "Skip first N lines/entries before applying head_limit, equivalent to \"| tail -n +N | head -N\". Works across all output modes. Defaults to 0." },
            "multiline": { "type": "boolean", "description": "Enable multiline mode where . matches newlines and patterns can span lines (rg -U --multiline-dotall). Default: false." }
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

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var pattern = ToolInput.String(context.Input, "pattern").Trim();
        if (pattern.Length == 0)
            return ToolExecutionResult.Text("Grep pattern must not be empty", isError: true);

        var mode = ToolInput.StringOrNull(context.Input, "output_mode") ?? "files_with_matches";
        if (mode is not ("content" or "files_with_matches" or "count"))
            return ToolExecutionResult.Text($"Invalid output_mode: {mode}", isError: true);

        var rawPath = ToolInput.StringOrNull(context.Input, "path");
        string searchPath;
        try
        {
            searchPath = rawPath is null ? context.Workspace.Root : context.Workspace.ResolvePath(rawPath);
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }

        var isSingleFile = File.Exists(searchPath);
        if (!isSingleFile && !Directory.Exists(searchPath))
            return ToolExecutionResult.Text($"Path does not exist: {searchPath}.", isError: true);

        var ignoreCase = ToolInput.BoolOrDefault(context.Input, "-i", false);
        var multiline = ToolInput.BoolOrDefault(context.Input, "multiline", false);
        var glob = ToolInput.StringOrNull(context.Input, "glob");
        var typeFilter = ToolInput.StringOrNull(context.Input, "type");
        var showLineNumbers = ToolInput.BoolOrDefault(context.Input, "-n", true);
        var onlyMatching = mode == "content" && ToolInput.BoolOrDefault(context.Input, "-o", false);
        var contextLines = Math.Max(0, ToolInput.IntOrNull(context.Input, "context") ?? ToolInput.IntOrNull(context.Input, "-C") ?? 0);
        var beforeContext = Math.Max(0, ToolInput.IntOrNull(context.Input, "-B") ?? contextLines);
        var afterContext = Math.Max(0, ToolInput.IntOrNull(context.Input, "-A") ?? contextLines);
        var headLimit = ToolInput.IntOrNull(context.Input, "head_limit");
        var offset = Math.Max(0, ToolInput.IntOrNull(context.Input, "offset") ?? 0);

        var globPatterns = glob is null ? [] : GrepGlobSupport.SplitPatterns(glob);
        var filterRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(isSingleFile ? Path.GetDirectoryName(searchPath)! : searchPath));

        var engineResult = new GrepEngineResult();
        if (!isSingleFile || SingleFilePassesFilters(searchPath, filterRoot, globPatterns, typeFilter))
        {
            var request = new GrepEngineRequest
            {
                Pattern = pattern,
                SearchPath = searchPath,
                FilterRoot = filterRoot,
                Mode = mode,
                GlobPatterns = globPatterns,
                TypeFilter = typeFilter,
                IsSingleFile = isSingleFile,
                IgnoreCase = ignoreCase,
                Multiline = multiline,
                OnlyMatching = onlyMatching,
                BeforeContext = beforeContext,
                AfterContext = afterContext,
            };
            try
            {
                engineResult = await Task.Run(() => _engine.Search(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (GrepPatternException)
            {
                return ToolExecutionResult.Text($"Invalid grep regular expression: {pattern}", isError: true);
            }
            catch (InvalidOperationException ex)
            {
                return ToolExecutionResult.Text(ex.Message, isError: true);
            }
        }

        var root = context.Workspace.Root;

        string text;
        if (mode == "files_with_matches")
        {
            var sorted = engineResult.MatchedFiles
                .Select(path => (MtimeUtc: FileSearchSupport.GetLastWriteTimeUtcSafe(path), Path: path))
                .ToList();
            sorted.Sort(FileSearchSupport.CompareNewestFirst);
            var filenames = sorted.ConvertAll(match => FileSearchSupport.ToDisplayPath(root, match.Path));
            var (page, appliedLimit, appliedOffset) = ApplyHeadLimit(filenames, headLimit, offset);
            if (page.Count == 0)
                return ToolExecutionResult.Text("No files found");
            text = $"Found {page.Count} {Plural(page.Count, "file")}{LimitInfoPrefix(appliedLimit, appliedOffset)}\n{string.Join("\n", page)}";
        }
        else if (mode == "count")
        {
            var entries = engineResult.CountEntries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList();
            var (page, appliedLimit, appliedOffset) = ApplyHeadLimit(entries, headLimit, offset);
            var numMatches = entries.Sum(entry => entry.Count);
            var body = page.Count == 0
                ? "No matches found"
                : string.Join("\n", page.Select(entry => $"{FileSearchSupport.ToDisplayPath(root, entry.Path)}:{entry.Count}"));
            var summary = $"Found {numMatches} total {Plural((int)numMatches, "occurrence")} across {entries.Count} {Plural(entries.Count, "file")}.";
            var limitInfo = FormatLimitInfo(appliedLimit, appliedOffset);
            text = $"{body}\n\n{summary}{(limitInfo.Length > 0 ? $" with pagination = {limitInfo}" : "")}";
        }
        else
        {
            // Stable ordering keeps within-file event order while native walk order becomes deterministic.
            var entries = engineResult.ContentEntries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList();
            var (page, appliedLimit, appliedOffset) = ApplyHeadLimit(entries, headLimit, offset);
            var body = page.Count == 0
                ? "No matches found"
                : string.Join("\n", page.Select(entry => FormatContentEntry(root, entry, showLineNumbers)));
            var limitInfo = FormatLimitInfo(appliedLimit, appliedOffset);
            text = limitInfo.Length > 0 ? $"{body}\n\n[Showing results with pagination = {limitInfo}]" : body;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxModelChars)
        {
            var artifact = await context.Workspace.SaveArtifactAsync(context.SessionId, $"grep-output-{Guid.NewGuid():N}.txt",
                "text/plain", bytes, cancellationToken).ConfigureAwait(false);
            text = BashModelContent.FormatPersistedOutputEnvelope(text, bytes.Length, artifact.Uri, decimalBytes: true);
        }
        return ToolExecutionResult.Text(text);
    }

    internal static bool SingleFilePassesFilters(string file, string filterRoot, IReadOnlyList<string> globPatterns, string? typeFilter)
    {
        if (globPatterns.Count > 0)
        {
            var relativePath = GrepGlobSupport.ToPosixRelative(filterRoot, file);
            var filters = globPatterns.Select(pattern => new GrepGlobFilter(pattern)).ToList();
            if (!filters.Any(filter => filter.Matches(relativePath, Path.GetFileName(file))))
                return false;
        }
        if (typeFilter is not null && !GrepTypeMap.MatchesFile(file, typeFilter))
            return false;
        return true;
    }

    private static (List<T> Page, int? AppliedLimit, int? AppliedOffset) ApplyHeadLimit<T>(List<T> items, int? headLimit, int offset)
    {
        if (headLimit == 0)
            return (items.Skip(offset).ToList(), null, offset > 0 ? offset : null);

        var effectiveLimit = Math.Max(0, headLimit ?? DefaultHeadLimit);
        var truncated = items.Count - offset > effectiveLimit;
        return (
            items.Skip(offset).Take(effectiveLimit).ToList(),
            truncated ? effectiveLimit : null,
            offset > 0 ? offset : null);
    }

    private static string FormatLimitInfo(int? appliedLimit, int? appliedOffset)
    {
        var parts = new List<string>();
        if (appliedLimit is int limit)
            parts.Add($"limit: {limit}");
        if (appliedOffset is int off)
            parts.Add($"offset: {off}");
        return string.Join(", ", parts);
    }

    private static string LimitInfoPrefix(int? appliedLimit, int? appliedOffset)
    {
        var limitInfo = FormatLimitInfo(appliedLimit, appliedOffset);
        return limitInfo.Length > 0 ? $" {limitInfo}" : "";
    }

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

    internal static string TruncateLine(string text) =>
        text.Length > MaxColumnChars ? text[..MaxColumnChars] + TruncationSuffix : text;

    private static string FormatContentEntry(string root, GrepContentEntry entry, bool showLineNumbers)
    {
        var path = FileSearchSupport.ToDisplayPath(root, entry.Path);
        return showLineNumbers && entry.LineNumber is int lineNumber
            ? $"{path}:{lineNumber}:{TruncateLine(entry.Text)}"
            : $"{path}:{TruncateLine(entry.Text)}";
    }
}
