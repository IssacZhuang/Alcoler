using System.Text.RegularExpressions;

namespace Alcoler.Adapters.Tools;

/// <summary>
/// Fallback engine: .NET regex over the managed file walker, used when the
/// native alco-grep library is unavailable. Does not implement ignore-file
/// rules; the native engine is the reference-aligned path.
/// </summary>
internal sealed class ManagedGrepEngine : IGrepEngine
{
    private const int BinaryProbeBytes = 8192;
    private static readonly Regex LineBreakRegex = new("\r?\n", RegexOptions.CultureInvariant);

    public GrepEngineResult Search(GrepEngineRequest request, CancellationToken cancellationToken)
    {
        var regexOptions = RegexOptions.CultureInvariant;
        if (request.IgnoreCase)
            regexOptions |= RegexOptions.IgnoreCase;
        if (request.Multiline)
            regexOptions |= RegexOptions.Singleline;
        Regex regex;
        try
        {
            regex = new Regex(request.Pattern, regexOptions);
        }
        catch (ArgumentException)
        {
            throw new GrepPatternException(request.Pattern);
        }

        var globFilters = request.GlobPatterns.Select(static candidate => new GrepGlobFilter(candidate)).ToList();
        List<string> candidates = request.IsSingleFile
            ? [request.SearchPath]
            : [.. FileSearchSupport.EnumerateFiles(request.SearchPath).OrderBy(static path => path, StringComparer.Ordinal)];

        var result = new GrepEngineResult();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GrepGlobSupport.ToPosixRelative(request.FilterRoot, file);
            if (globFilters.Count > 0 && !globFilters.Any(filter => filter.Matches(relativePath, Path.GetFileName(file))))
                continue;
            if (request.TypeFilter is not null && !GrepTypeMap.MatchesFile(file, request.TypeFilter))
                continue;
            if (LooksBinary(file))
                continue;

            string content;
            try
            {
                content = File.ReadAllTextAsync(file, cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var search = request.Multiline
                ? SearchMultilineContent(file, content, regex, request.OnlyMatching, request.BeforeContext, request.AfterContext)
                : SearchLineContent(file, content, regex, request.OnlyMatching, request.BeforeContext, request.AfterContext);
            if (search.MatchCount == 0)
                continue;

            result.MatchedFiles.Add(file);
            if (request.Mode == "content")
                result.ContentEntries.AddRange(search.Entries);
            else if (request.Mode == "count")
                result.CountEntries.Add((file, search.MatchCount));
        }
        return result;
    }

    private readonly record struct LineSearch(int MatchCount, List<GrepContentEntry> Entries);

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
            var entriesByLine = new Dictionary<int, List<GrepContentEntry>>();
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
            .Select(index => new GrepContentEntry(file, index + 1, lines[index]))
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
            var entries = new List<GrepContentEntry>();
            foreach (Match match in regex.Matches(content))
                entries.Add(new GrepContentEntry(file, LineNumberForIndex(content, match.Index), FirstLine(match.Value)));
            return new(entries.Count, entries);
        }

        var entriesByLine = new Dictionary<int, List<GrepContentEntry>>();
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

    private static List<GrepContentEntry> BuildContextEntries(
        string file,
        string[] lines,
        List<(int Start, int End)> ranges,
        Dictionary<int, List<GrepContentEntry>> entriesByLine,
        int beforeContext,
        int afterContext)
    {
        var matchedIndexes = new HashSet<int>();
        foreach (var (start, end) in ranges)
        {
            for (var index = start; index <= end; index++)
                matchedIndexes.Add(index);
        }

        var entries = new List<GrepContentEntry>();
        foreach (var lineIndex in ContextLineIndexes(lines.Length, ranges, beforeContext, afterContext))
        {
            if (entriesByLine.TryGetValue(lineIndex, out var matched))
            {
                entries.AddRange(matched);
                continue;
            }
            if (matchedIndexes.Contains(lineIndex))
                continue;
            entries.Add(new GrepContentEntry(file, lineIndex + 1, lines[lineIndex]));
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

    private static IEnumerable<GrepContentEntry> OnlyMatchingEntries(string file, int? lineNumber, string text)
    {
        if (text.Length == 0)
        {
            yield return new GrepContentEntry(file, lineNumber, "");
            yield break;
        }

        var subLine = 0;
        foreach (var part in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (part.Length > 0)
                yield return new GrepContentEntry(file, lineNumber + subLine, part);
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
