using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Alcoler.Adapters.Tools;

internal sealed class ManagedGrepEngine : IGrepEngine
{
    private const int BinaryProbeBytes = 8192;
    private const byte MatchedLineFlag = 1;
    private const byte ContextLineFlag = 2;

    private const int MaxCachedPatterns = 128;
    private const int MaxCachedPatternLength = 4096;
    private static readonly object RegexCacheSync = new();
    private static readonly Dictionary<(string Pattern, RegexOptions Options), Regex> RegexCache = new();

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
            regex = GetRegex(request.Pattern, regexOptions);
        }
        catch (ArgumentException)
        {
            throw new GrepPatternException(request.Pattern);
        }

        var globFilters = request.GlobPatterns.Select(static candidate => new GrepGlobFilter(candidate)).ToList();
        var typeFilter = request.TypeFilter is { } type ? GrepTypeMap.CreateFilter(type) : (GrepTypeFilter?)null;
        IEnumerable<string> candidates = request.IsSingleFile
            ? [request.SearchPath]
            : FileSearchSupport.EnumerateFiles(request.SearchPath);

        var result = new GrepResultCollector(request);
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
            if (!TryReadNonBinaryText(file, out var content))
                continue;

            var count = request.Multiline
                ? SearchMultilineContent(file, content, regex, request, result, cancellationToken)
                : SearchLineContent(file, content, regex, request, result, cancellationToken);
            if (count > 0 && request.Mode != "content")
                result.AddFile(file, count);
        }
        return result.Complete();
    }

    private static Regex GetRegex(string pattern, RegexOptions options)
    {
        if (pattern.Length > MaxCachedPatternLength)
            return new Regex(pattern, options);
        lock (RegexCacheSync)
        {
            if (RegexCache.TryGetValue((pattern, options), out var cached))
                return cached;
            var regex = new Regex(pattern, options);
            if (RegexCache.Count >= MaxCachedPatterns)
                RegexCache.Remove(RegexCache.Keys.First());
            RegexCache.Add((pattern, options), regex);
            return regex;
        }
    }

    private static long SearchLineContent(
        string file,
        string content,
        Regex regex,
        GrepEngineRequest request,
        GrepResultCollector result,
        CancellationToken cancellationToken)
    {
        var lines = SplitLines(content);
        var flags = request.Mode == "content" ? new byte[lines.Length] : null;
        var matchedLines = 0L;
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!regex.IsMatch(lines[index].Span))
                continue;
            matchedLines++;
            if (flags is not null)
                flags[index] |= MatchedLineFlag;
        }
        if (flags is null)
            return matchedLines;

        MarkContextLines(flags, request.BeforeContext, request.AfterContext);
        if (request.OnlyMatching)
        {
            var emitter = new ContextEmitter(file, lines, flags, result);
            for (var index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((flags[index] & MatchedLineFlag) == 0)
                    continue;
                var line = lines[index].Span;
                foreach (var match in regex.EnumerateMatches(line))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    emitter.AddMatchParts(index + 1, line.Slice(match.Index, match.Length));
                }
            }
            emitter.Complete();
        }
        else
        {
            for (var index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((flags[index] & ContextLineFlag) != 0)
                    result.AddContent(file, index + 1, lines[index].Span);
            }
        }
        return matchedLines;
    }

    private static long SearchMultilineContent(
        string file,
        string content,
        Regex regex,
        GrepEngineRequest request,
        GrepResultCollector result,
        CancellationToken cancellationToken)
    {
        var matches = 0L;
        var cursor = 0;
        var lineNumber = 1;
        if (!request.OnlyMatching || request.Mode != "content")
        {
            foreach (var match in regex.EnumerateMatches(content))
            {
                cancellationToken.ThrowIfCancellationRequested();
                matches++;
                if (request.Mode != "content")
                    continue;
                AdvanceLineNumber(content, match.Index, ref cursor, ref lineNumber);
                result.AddContent(file, lineNumber, FirstLine(content.AsSpan(match.Index, match.Length)));
            }
            return matches;
        }

        var lines = SplitLines(content);
        var flags = new byte[lines.Length];
        foreach (var match in regex.EnumerateMatches(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            matches++;
            AdvanceLineNumber(content, match.Index, ref cursor, ref lineNumber);
            var endLine = lineNumber + (match.Length == 0 ? 0 : content.AsSpan(match.Index, match.Length - 1).Count('\n'));
            for (var index = lineNumber - 1; index <= Math.Min(flags.Length - 1, endLine - 1); index++)
                flags[index] |= MatchedLineFlag;
        }
        MarkContextLines(flags, request.BeforeContext, request.AfterContext);
        var emitter = new ContextEmitter(file, lines, flags, result);
        cursor = 0;
        lineNumber = 1;
        foreach (var match in regex.EnumerateMatches(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdvanceLineNumber(content, match.Index, ref cursor, ref lineNumber);
            emitter.AddMatchParts(lineNumber, content.AsSpan(match.Index, match.Length));
        }
        emitter.Complete();
        return matches;
    }

    private static void AdvanceLineNumber(string content, int index, ref int cursor, ref int lineNumber)
    {
        lineNumber += content.AsSpan(cursor, index - cursor).Count('\n');
        cursor = index;
    }

    private static void MarkContextLines(byte[] flags, int beforeContext, int afterContext)
    {
        var markedEnd = -1;
        for (var index = 0; index < flags.Length; index++)
        {
            if ((flags[index] & MatchedLineFlag) == 0)
                continue;
            var from = Math.Max(markedEnd + 1, Math.Max(0, index - beforeContext));
            var to = (int)Math.Min(flags.Length - 1L, (long)index + afterContext);
            for (var context = from; context <= to; context++)
                flags[context] |= ContextLineFlag;
            markedEnd = to;
        }
    }

    private sealed class ContextEmitter(string file, ReadOnlyMemory<char>[] lines, byte[] flags, GrepResultCollector result)
    {
        private int _nextLine;

        public void AddMatchParts(int lineNumber, ReadOnlySpan<char> text)
        {
            if (text.IsEmpty)
            {
                AddPart(lineNumber, text);
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
                    AddPart(lineNumber + subLine, text[start..end]);
                subLine++;
                start = index + 1;
            }
            if (start < text.Length)
                AddPart(lineNumber + subLine, text[start..]);
        }

        public void Complete() => EmitContextUntil(lines.Length);

        private void AddPart(int lineNumber, ReadOnlySpan<char> text)
        {
            var index = lineNumber - 1;
            if ((uint)index >= (uint)lines.Length)
                return;
            EmitContextUntil(index);
            result.AddContent(file, lineNumber, text);
            _nextLine = Math.Max(_nextLine, index + 1);
        }

        private void EmitContextUntil(int end)
        {
            while (_nextLine < end)
            {
                if ((flags[_nextLine] & MatchedLineFlag) == 0 && (flags[_nextLine] & ContextLineFlag) != 0)
                    result.AddContent(file, _nextLine + 1, lines[_nextLine].Span);
                _nextLine++;
            }
        }
    }

    // A trailing line break is not an extra line; only LF and CRLF delimit lines.
    private static ReadOnlyMemory<char>[] SplitLines(string content)
    {
        if (content.Length == 0)
            return [];
        var end = content.Length;
        if (content[end - 1] == '\n')
            end--;
        if (end > 0 && content[end - 1] == '\r')
            end--;
        var lines = new List<ReadOnlyMemory<char>>();
        var start = 0;
        for (var index = 0; index < end; index++)
        {
            if (content[index] != '\n')
                continue;
            var lineEnd = index;
            if (lineEnd > start && content[lineEnd - 1] == '\r')
                lineEnd--;
            lines.Add(content.AsMemory(start, lineEnd - start));
            start = index + 1;
        }
        lines.Add(content.AsMemory(start, end - start));
        return lines.ToArray();
    }

    private static ReadOnlySpan<char> FirstLine(ReadOnlySpan<char> value)
    {
        var newline = value.IndexOf('\n');
        var first = newline < 0 ? value : value[..newline];
        return first.EndsWith("\r", StringComparison.Ordinal) ? first[..^1] : first;
    }

    // The probe and BOM-aware decode must use the same open file.
    private static bool TryReadNonBinaryText(string file, out string content)
    {
        content = "";
        try
        {
            using var stream = File.OpenRead(file);
            var probe = ArrayPool<byte>.Shared.Rent(BinaryProbeBytes);
            bool binary;
            try
            {
                var read = stream.Read(probe, 0, BinaryProbeBytes);
                binary = probe.AsSpan(0, read).Contains((byte)0);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(probe);
            }
            if (binary)
                return false;
            stream.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            content = reader.ReadToEnd();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
