using System.Buffers;
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
    private const byte MatchedLineFlag = 1;
    private const byte ContextLineFlag = 2;

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
        var typeFilter = request.TypeFilter is { } type ? GrepTypeMap.CreateFilter(type) : (GrepTypeFilter?)null;
        List<string> candidates = request.IsSingleFile
            ? [request.SearchPath]
            : [.. FileSearchSupport.EnumerateFiles(request.SearchPath).OrderBy(static path => path, StringComparer.Ordinal)];

        var result = new GrepEngineResult();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (globFilters.Count > 0)
            {
                var relativePath = GrepGlobSupport.ToPosixRelative(request.FilterRoot, file);
                if (!globFilters.Any(filter => filter.Matches(relativePath, Path.GetFileName(file))))
                    continue;
            }
            if (typeFilter is { } typeMatcher && !typeMatcher.Matches(file))
                continue;
            if (LooksBinary(file))
                continue;

            string content;
            try
            {
                content = File.ReadAllText(file);
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
            var entriesByLine = new List<GrepContentEntry>?[lines.Length];
            var ranges = new List<(int Start, int End)>();
            var flags = new byte[lines.Length];
            var matchedLines = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (Match match in regex.Matches(lines[index]))
                {
                    if (entriesByLine[index] is null)
                    {
                        entriesByLine[index] = [];
                        ranges.Add((index, index));
                        flags[index] |= MatchedLineFlag;
                        matchedLines++;
                    }
                    AppendOnlyMatchingEntries(entriesByLine[index]!, file, index + 1, match.Value);
                }
            }

            return new(matchedLines, BuildContextEntries(file, lines, ranges, entriesByLine, flags, beforeContext, afterContext));
        }

        var matchingRanges = new List<(int Start, int End)>();
        var matchingFlags = new byte[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            if (!regex.IsMatch(lines[index]))
                continue;
            matchingRanges.Add((index, index));
            matchingFlags[index] |= MatchedLineFlag;
        }

        MarkContextLines(matchingFlags, matchingRanges, beforeContext, afterContext);
        var matchedEntries = new List<GrepContentEntry>(matchingRanges.Count);
        for (var index = 0; index < lines.Length; index++)
        {
            if ((matchingFlags[index] & ContextLineFlag) != 0)
                matchedEntries.Add(new GrepContentEntry(file, index + 1, lines[index]));
        }
        return new(matchingRanges.Count, matchedEntries);
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

        var lines = SplitLines(content);
        var entriesByLine = new List<GrepContentEntry>?[lines.Length];
        var ranges = new List<(int Start, int End)>();
        var flags = new byte[lines.Length];
        var matchEntries = new List<GrepContentEntry>();
        foreach (Match match in regex.Matches(content))
        {
            var startLine = LineNumberForIndex(content, match.Index);
            matchEntries.Clear();
            AppendOnlyMatchingEntries(matchEntries, file, startLine, match.Value);
            foreach (var entry in matchEntries)
            {
                var lineIndex = entry.LineNumber is int lineNumber ? lineNumber - 1 : startLine - 1;
                if ((uint)lineIndex >= (uint)entriesByLine.Length)
                    continue;
                if (entriesByLine[lineIndex] is not { } perLine)
                    entriesByLine[lineIndex] = perLine = [];
                perLine.Add(entry);
            }

            var endLine = match.Value.Length == 0
                ? startLine
                : LineNumberForIndex(content, match.Index + match.Value.Length - 1);
            ranges.Add((startLine - 1, endLine - 1));
            MarkMatchedLines(flags, startLine - 1, endLine - 1);
        }

        return new(ranges.Count, BuildContextEntries(file, lines, ranges, entriesByLine, flags, beforeContext, afterContext));
    }

    private static List<GrepContentEntry> BuildContextEntries(
        string file,
        string[] lines,
        List<(int Start, int End)> ranges,
        List<GrepContentEntry>?[] entriesByLine,
        byte[] flags,
        int beforeContext,
        int afterContext)
    {
        MarkContextLines(flags, ranges, beforeContext, afterContext);
        var entries = new List<GrepContentEntry>();
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (entriesByLine[lineIndex] is { } matched)
            {
                entries.AddRange(matched);
                continue;
            }
            if ((flags[lineIndex] & MatchedLineFlag) != 0)
                continue;
            if ((flags[lineIndex] & ContextLineFlag) != 0)
                entries.Add(new GrepContentEntry(file, lineIndex + 1, lines[lineIndex]));
        }
        return entries;
    }

    private static void MarkMatchedLines(byte[] flags, int start, int end)
    {
        var from = Math.Max(0, start);
        var to = Math.Min(flags.Length - 1, end);
        for (var index = from; index <= to; index++)
            flags[index] |= MatchedLineFlag;
    }

    private static void MarkContextLines(byte[] flags, List<(int Start, int End)> ranges, int beforeContext, int afterContext)
    {
        foreach (var (start, end) in ranges)
        {
            for (var index = Math.Max(0, start - beforeContext); index <= Math.Min(flags.Length - 1, end + afterContext); index++)
                flags[index] |= ContextLineFlag;
        }
    }

    private static void AppendOnlyMatchingEntries(List<GrepContentEntry> target, string file, int? lineNumber, string text)
    {
        if (text.Length == 0)
        {
            target.Add(new GrepContentEntry(file, lineNumber, ""));
            return;
        }

        var subLine = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
                continue;
            var end = index;
            if (end > start && text[end - 1] == '\r')
                end--;
            if (end > start)
                target.Add(new GrepContentEntry(file, lineNumber + subLine, text[start..end]));
            subLine++;
            start = index + 1;
        }
        if (start < text.Length)
            target.Add(new GrepContentEntry(file, lineNumber + subLine, text[start..]));
    }

    // Line boundaries are LF/CRLF only; a trailing line break is not an extra line and a lone CR stays in the line.
    private static string[] SplitLines(string content)
    {
        if (content.Length == 0)
            return [];
        var end = content.Length;
        if (content[end - 1] == '\n')
            end--;
        if (end > 0 && content[end - 1] == '\r')
            end--;
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < end; index++)
        {
            if (content[index] != '\n')
                continue;
            var lineEnd = index;
            if (lineEnd > start && content[lineEnd - 1] == '\r')
                lineEnd--;
            lines.Add(content[start..lineEnd]);
            start = index + 1;
        }
        lines.Add(content[start..end]);
        return lines.ToArray();
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
            var buffer = ArrayPool<byte>.Shared.Rent(BinaryProbeBytes);
            try
            {
                var read = stream.Read(buffer, 0, BinaryProbeBytes);
                return buffer.AsSpan(0, read).Contains((byte)0);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
