using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class GrepTool : ITool
{
    internal const int DefaultHeadLimit = 250;
    internal const int MaxModelChars = 20_000;
    private const int DefaultTimeoutMs = 30_000;
    private const int BinaryProbeBytes = 8192;

    private static readonly Regex LineBreakRegex = new("\r?\n", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string[]> TypeExtensions = new(StringComparer.Ordinal)
    {
        ["c"] = [".c", ".h"],
        ["cpp"] = [".cc", ".cpp", ".cxx", ".hpp", ".hh", ".hxx"],
        ["cs"] = [".cs"],
        ["csharp"] = [".cs"],
        ["css"] = [".css"],
        ["go"] = [".go"],
        ["html"] = [".html", ".htm"],
        ["java"] = [".java"],
        ["javascript"] = [".js", ".jsx", ".mjs", ".cjs"],
        ["js"] = [".js", ".jsx", ".mjs", ".cjs"],
        ["json"] = [".json", ".jsonc"],
        ["kotlin"] = [".kt", ".kts"],
        ["kt"] = [".kt", ".kts"],
        ["markdown"] = [".md", ".markdown"],
        ["md"] = [".md", ".markdown"],
        ["php"] = [".php"],
        ["py"] = [".py"],
        ["python"] = [".py"],
        ["rb"] = [".rb"],
        ["ruby"] = [".rb"],
        ["rs"] = [".rs"],
        ["rust"] = [".rs"],
        ["scala"] = [".scala"],
        ["sh"] = [".sh", ".bash", ".zsh"],
        ["sql"] = [".sql"],
        ["swift"] = [".swift"],
        ["toml"] = [".toml"],
        ["ts"] = [".ts", ".tsx", ".mts", ".cts"],
        ["tsx"] = [".tsx"],
        ["typescript"] = [".ts", ".tsx", ".mts", ".cts"],
        ["txt"] = [".txt"],
        ["xml"] = [".xml"],
        ["yaml"] = [".yaml", ".yml"],
    };

    public ToolContract Contract { get; } = new(
        "Grep",
        """
        Content search built on .NET regex. Prefer this over `grep`/`rg` via Bash — results integrate with the permission UI and file links.

        - Full regex syntax (e.g. "log.*Error", "function\s+\w+"). .NET regex, not grep — escape literal braces (`interface\{\}`).
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
        var regexOptions = RegexOptions.CultureInvariant;
        if (ignoreCase)
            regexOptions |= RegexOptions.IgnoreCase;
        if (multiline)
            regexOptions |= RegexOptions.Singleline;
        Regex regex;
        try
        {
            regex = new Regex(pattern, regexOptions);
        }
        catch (ArgumentException)
        {
            return ToolExecutionResult.Text($"Invalid grep regular expression: {pattern}", isError: true);
        }

        var glob = ToolInput.StringOrNull(context.Input, "glob");
        var typeFilter = ToolInput.StringOrNull(context.Input, "type");
        var showLineNumbers = ToolInput.BoolOrDefault(context.Input, "-n", true);
        var onlyMatching = mode == "content" && ToolInput.BoolOrDefault(context.Input, "-o", false);
        var contextLines = Math.Max(0, ToolInput.IntOrNull(context.Input, "context") ?? ToolInput.IntOrNull(context.Input, "-C") ?? 0);
        var beforeContext = Math.Max(0, ToolInput.IntOrNull(context.Input, "-B") ?? contextLines);
        var afterContext = Math.Max(0, ToolInput.IntOrNull(context.Input, "-A") ?? contextLines);
        var headLimit = ToolInput.IntOrNull(context.Input, "head_limit");
        var offset = Math.Max(0, ToolInput.IntOrNull(context.Input, "offset") ?? 0);

        var globFilters = new List<GlobFilter>();
        if (glob is not null)
            globFilters.AddRange(SplitGlobPatterns(glob).Select(static candidate => new GlobFilter(candidate)));

        var filterRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(isSingleFile ? Path.GetDirectoryName(searchPath)! : searchPath));
        List<string> candidates;
        if (isSingleFile)
            candidates = [searchPath];
        else
            candidates = [.. FileSearchSupport.EnumerateFiles(searchPath).OrderBy(static path => path, StringComparer.Ordinal)];

        var contentEntries = new List<ContentEntry>();
        var countEntries = new List<(string Path, int Count)>();
        var matchingFiles = new List<(DateTime MtimeUtc, string Path)>();
        var numMatches = 0;

        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = ToPosixRelative(filterRoot, file);
            if (globFilters.Count > 0 && !globFilters.Any(filter => filter.Matches(relativePath, Path.GetFileName(file))))
                continue;
            if (typeFilter is not null && !MatchesFileType(file, typeFilter))
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

            var search = multiline
                ? SearchMultilineContent(file, content, regex, onlyMatching, beforeContext, afterContext)
                : SearchLineContent(file, content, regex, onlyMatching, beforeContext, afterContext);
            if (search.MatchCount == 0)
                continue;

            numMatches += search.MatchCount;
            matchingFiles.Add((FileSearchSupport.GetLastWriteTimeUtcSafe(file), file));
            if (mode == "content")
                contentEntries.AddRange(search.Entries);
            else if (mode == "count")
                countEntries.Add((file, search.MatchCount));
        }

        matchingFiles.Sort(FileSearchSupport.CompareNewestFirst);
        var root = context.Workspace.Root;

        string text;
        if (mode == "files_with_matches")
        {
            var filenames = matchingFiles.ConvertAll(match => FileSearchSupport.ToDisplayPath(root, match.Path));
            var (page, appliedLimit, appliedOffset) = ApplyHeadLimit(filenames, headLimit, offset);
            if (page.Count == 0)
                return ToolExecutionResult.Text("No files found");
            text = $"Found {page.Count} {Plural(page.Count, "file")}{LimitInfoPrefix(appliedLimit, appliedOffset)}\n{string.Join("\n", page)}";
        }
        else if (mode == "count")
        {
            var (page, appliedLimit, appliedOffset) = ApplyHeadLimit(countEntries, headLimit, offset);
            var body = page.Count == 0
                ? "No matches found"
                : string.Join("\n", page.Select(entry => $"{FileSearchSupport.ToDisplayPath(root, entry.Path)}:{entry.Count}"));
            var summary = $"Found {numMatches} total {Plural(numMatches, "occurrence")} across {matchingFiles.Count} {Plural(matchingFiles.Count, "file")}.";
            var limitInfo = FormatLimitInfo(appliedLimit, appliedOffset);
            text = $"{body}\n\n{summary}{(limitInfo.Length > 0 ? $" with pagination = {limitInfo}" : "")}";
        }
        else
        {
            var (page, appliedLimit, appliedOffset) = ApplyHeadLimit(contentEntries, headLimit, offset);
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

    private readonly record struct ContentEntry(string Path, int? LineNumber, string Text);

    private readonly record struct LineSearch(int MatchCount, List<ContentEntry> Entries);

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

    private static string FormatContentEntry(string root, ContentEntry entry, bool showLineNumbers)
    {
        var path = FileSearchSupport.ToDisplayPath(root, entry.Path);
        return showLineNumbers && entry.LineNumber is int lineNumber
            ? $"{path}:{lineNumber}:{entry.Text}"
            : $"{path}:{entry.Text}";
    }

    private static LineSearch SearchLineContent(
        string file,
        string content,
        Regex regex,
        bool onlyMatching,
        int beforeContext,
        int afterContext)
    {
        var lines = SplitLines(content);
        if (onlyMatching)
        {
            var entriesByLine = new Dictionary<int, List<ContentEntry>>();
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in regex.Matches(lines[index]))
                {
                    if (!entriesByLine.TryGetValue(index, out var perLine))
                        entriesByLine[index] = perLine = [];
                    perLine.AddRange(OnlyMatchingEntries(file, index + 1, match.Value));
                }
            }

            var ranges = entriesByLine.Keys.Select(static index => (Start: index, End: index)).ToList();
            return new(entriesByLine.Count, BuildContextEntries(file, lines, ranges, entriesByLine, beforeContext, afterContext));
        }

        var matchingRanges = new List<(int Start, int End)>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (regex.IsMatch(lines[index]))
                matchingRanges.Add((index, index));
        }

        var entries = ContextLineIndexes(lines.Length, matchingRanges, beforeContext, afterContext)
            .Select(index => new ContentEntry(file, index + 1, lines[index]))
            .ToList();
        return new(matchingRanges.Count, entries);
    }

    private static LineSearch SearchMultilineContent(
        string file,
        string content,
        Regex regex,
        bool onlyMatching,
        int beforeContext,
        int afterContext)
    {
        if (!onlyMatching)
        {
            var entries = new List<ContentEntry>();
            foreach (Match match in regex.Matches(content))
                entries.Add(new ContentEntry(file, LineNumberForIndex(content, match.Index), FirstLine(match.Value)));
            return new(entries.Count, entries);
        }

        var entriesByLine = new Dictionary<int, List<ContentEntry>>();
        var ranges = new List<(int Start, int End)>();
        foreach (Match match in regex.Matches(content))
        {
            var startLine = LineNumberForIndex(content, match.Index);
            foreach (var entry in OnlyMatchingEntries(file, startLine, match.Value))
            {
                var lineIndex = entry.LineNumber is int lineNumber ? lineNumber - 1 : startLine - 1;
                if (!entriesByLine.TryGetValue(lineIndex, out var perLine))
                    entriesByLine[lineIndex] = perLine = [];
                perLine.Add(entry);
            }

            var endLine = match.Value.Length == 0
                ? startLine
                : LineNumberForIndex(content, match.Index + match.Value.Length - 1);
            ranges.Add((startLine - 1, endLine - 1));
        }

        return new(ranges.Count, BuildContextEntries(file, SplitLines(content), ranges, entriesByLine, beforeContext, afterContext));
    }

    private static List<ContentEntry> BuildContextEntries(
        string file,
        string[] lines,
        List<(int Start, int End)> ranges,
        Dictionary<int, List<ContentEntry>> entriesByLine,
        int beforeContext,
        int afterContext)
    {
        var matchedIndexes = new HashSet<int>();
        foreach (var (start, end) in ranges)
        {
            for (var index = start; index <= end; index++)
                matchedIndexes.Add(index);
        }

        var entries = new List<ContentEntry>();
        foreach (var lineIndex in ContextLineIndexes(lines.Length, ranges, beforeContext, afterContext))
        {
            if (entriesByLine.TryGetValue(lineIndex, out var matched))
            {
                entries.AddRange(matched);
                continue;
            }
            if (matchedIndexes.Contains(lineIndex))
                continue;
            entries.Add(new ContentEntry(file, lineIndex + 1, lines[lineIndex]));
        }
        return entries;
    }

    private static IEnumerable<int> ContextLineIndexes(int lineCount, List<(int Start, int End)> ranges, int beforeContext, int afterContext)
    {
        var indexes = new SortedSet<int>();
        foreach (var (start, end) in ranges)
        {
            for (var index = Math.Max(0, start - beforeContext); index <= Math.Min(lineCount - 1, end + afterContext); index++)
                indexes.Add(index);
        }
        return indexes;
    }

    private static IEnumerable<ContentEntry> OnlyMatchingEntries(string file, int? lineNumber, string text)
    {
        if (text.Length == 0)
        {
            yield return new ContentEntry(file, lineNumber, "");
            yield break;
        }

        var subLine = 0;
        foreach (var part in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (part.Length > 0)
                yield return new ContentEntry(file, lineNumber + subLine, part);
            subLine++;
        }
    }

    // Line boundaries are LF/CRLF only; a trailing line break is not an extra line and a lone CR stays in the line.
    private static string[] SplitLines(string content)
    {
        if (content.Length == 0)
            return [];
        var trimmed = content.EndsWith('\n') ? content[..^1] : content;
        trimmed = trimmed.EndsWith('\r') ? trimmed[..^1] : trimmed;
        return LineBreakRegex.Split(trimmed);
    }

    private static string FirstLine(string value)
    {
        var newline = value.IndexOf('\n');
        var first = newline < 0 ? value : value[..newline];
        return first.EndsWith('\r') ? first[..^1] : first;
    }

    private static int LineNumberForIndex(string content, int index) =>
        1 + content.AsSpan(0, index).Count('\n');

    private static string ToPosixRelative(string root, string filePath) =>
        Path.GetRelativePath(root, filePath).Replace('\\', '/');

    private static bool MatchesFileType(string path, string type)
    {
        var normalized = type.ToLowerInvariant().TrimStart('.');
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return TypeExtensions.TryGetValue(normalized, out var extensions)
            ? extensions.Contains(extension)
            : extension == "." + normalized;
    }

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

    private sealed class GlobFilter
    {
        private readonly Regex _pathRegex;
        private readonly Regex? _fileNameRegex;

        public GlobFilter(string pattern)
        {
            var normalized = NormalizeGlobPattern(pattern);
            _pathRegex = GlobPatternToRegex(normalized);
            _fileNameRegex = normalized.Contains('/') ? null : _pathRegex;
        }

        public bool Matches(string relativePath, string fileName) =>
            _pathRegex.IsMatch(relativePath) || (_fileNameRegex?.IsMatch(fileName) ?? false);
    }

    private static List<string> SplitGlobPatterns(string glob)
    {
        var patterns = new List<string>();
        foreach (var raw in glob.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Contains('{') && raw.Contains('}'))
            {
                patterns.Add(raw);
                continue;
            }
            patterns.AddRange(raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
        return patterns;
    }

    private static string NormalizeGlobPattern(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static Regex GlobPatternToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var c = pattern[index];
            if (c == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                    {
                        builder.Append("(?:.*/)?");
                        index += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        index += 1;
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
                continue;
            }

            if (c == '?')
            {
                builder.Append("[^/]");
                continue;
            }

            if (c == '{')
            {
                var closing = pattern.IndexOf('}', index + 1);
                if (closing > index)
                {
                    var alternatives = string.Join(
                        "|",
                        pattern.Substring(index + 1, closing - index - 1).Split(',').Select(static part => Regex.Escape(part)));
                    builder.Append("(?:").Append(alternatives).Append(')');
                    index = closing;
                    continue;
                }
            }

            builder.Append(Regex.Escape(c.ToString()));
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant);
    }
}
