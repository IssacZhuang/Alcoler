using System.Text.RegularExpressions;

namespace Alcoler.Adapters.Tools;

internal enum EditMatchStrategy
{
    Exact,
    QuoteNormalized,
    LineNumberPrefixStripped,
    EscapeNormalized,
    UnicodeEscapeNormalized,
    LineTrimmed,
    IndentationFlexible,
    BlockAnchor,
}

internal abstract record EditMatch
{
    private EditMatch()
    {
    }

    public sealed record Matched(string ActualString, EditMatchStrategy Strategy, int CandidateCount) : EditMatch;

    public sealed record Ambiguous(EditMatchStrategy Strategy, int CandidateCount) : EditMatch;

    public sealed record NotFound() : EditMatch;
}

/// <summary>
/// Exact matches are tried first; normalizations only run when the literal search is absent.
/// Broad matchers (line_trimmed, indentation_flexible, block_anchor) are skipped for
/// replace_all because a fuzzy match must never fan out over the whole file.
/// </summary>
internal static class EditMatchers
{
    private const double BlockAnchorMinSimilarity = 0.8;
    private const char LeftSingleCurlyQuote = '\u2018';
    private const char RightSingleCurlyQuote = '\u2019';
    private const char LeftDoubleCurlyQuote = '\u201C';
    private const char RightDoubleCurlyQuote = '\u201D';

    private static readonly Regex ColonLineNumberPrefix = new(@"^\d+: (.*)$", RegexOptions.Compiled);
    private static readonly Regex TabLineNumberPrefix = new(@"^\d+\t(.*)$", RegexOptions.Compiled);
    private static readonly Regex EscapedVisibleChar = new(@"\\([ntr""'`\\$])", RegexOptions.Compiled);
    private static readonly Regex EscapedUnicodeChar = new(@"(\\\\)|\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

    private static readonly EditMatchStrategy[] FallbackStrategies =
    [
        EditMatchStrategy.QuoteNormalized,
        EditMatchStrategy.LineNumberPrefixStripped,
        EditMatchStrategy.EscapeNormalized,
        EditMatchStrategy.UnicodeEscapeNormalized,
        EditMatchStrategy.LineTrimmed,
        EditMatchStrategy.IndentationFlexible,
        EditMatchStrategy.BlockAnchor,
    ];

    public static EditMatch FindEditMatch(string content, string search, bool replaceAll)
    {
        var exactCount = CountSubstringOccurrences(content, search);
        if (exactCount > 0)
            return new EditMatch.Matched(search, EditMatchStrategy.Exact, exactCount);

        foreach (var strategy in FallbackStrategies)
        {
            if (replaceAll && IsBroadMatcher(strategy))
                continue;
            var candidates = CollectCandidates(strategy, content, search);
            if (candidates.Count == 0)
                continue;
            return ToMatchResult(strategy, candidates);
        }

        return new EditMatch.NotFound();
    }

    public static string NormalizeLineEndings(string content) => content.Replace("\r\n", "\n");

    public static string NormalizeReplacementForMatch(EditMatchStrategy strategy, string newString) =>
        strategy == EditMatchStrategy.EscapeNormalized ? UnescapeVisibleCharacters(newString) : newString;

    public static string PreserveQuoteStyle(string oldString, string actualOldString, string newString)
    {
        if (oldString == actualOldString)
            return newString;

        var result = newString;
        if (actualOldString.Contains(LeftDoubleCurlyQuote) || actualOldString.Contains(RightDoubleCurlyQuote))
            result = ApplyCurlyDoubleQuotes(result);
        if (actualOldString.Contains(LeftSingleCurlyQuote) || actualOldString.Contains(RightSingleCurlyQuote))
            result = ApplyCurlySingleQuotes(result);
        return result;
    }

    internal static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
            previous[index] = index;

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var cost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private static bool IsBroadMatcher(EditMatchStrategy strategy) =>
        strategy is EditMatchStrategy.LineTrimmed
            or EditMatchStrategy.IndentationFlexible
            or EditMatchStrategy.BlockAnchor;

    // Internal for tests: indentation_flexible is shadowed by line_trimmed in the chain
    // (any block matching after common-indent removal also matches after per-line trim).
    internal static List<string> CollectCandidates(EditMatchStrategy strategy, string content, string search) =>
        strategy switch
        {
            EditMatchStrategy.QuoteNormalized => CollectNormalizedCandidates(content, search, NormalizeQuotes),
            EditMatchStrategy.LineNumberPrefixStripped => CollectLineNumberPrefixCandidates(content, search),
            EditMatchStrategy.EscapeNormalized => CollectEscapeNormalizedCandidates(content, search),
            EditMatchStrategy.UnicodeEscapeNormalized => CollectUnicodeEscapeNormalizedCandidates(content, search),
            EditMatchStrategy.LineTrimmed => CollectLineTrimmedCandidates(content, search),
            EditMatchStrategy.IndentationFlexible => CollectIndentationFlexibleCandidates(content, search),
            EditMatchStrategy.BlockAnchor => CollectBlockAnchorCandidates(content, search),
            _ => [],
        };

    private static EditMatch ToMatchResult(EditMatchStrategy strategy, List<string> candidates)
    {
        var first = candidates[0];
        for (var index = 1; index < candidates.Count; index++)
        {
            if (!string.Equals(candidates[index], first, StringComparison.Ordinal))
                return new EditMatch.Ambiguous(strategy, candidates.Count);
        }
        return new EditMatch.Matched(first, strategy, candidates.Count);
    }

    private static int CountSubstringOccurrences(string content, string search)
    {
        if (search.Length == 0)
            return 0;
        var count = 0;
        var position = 0;
        while (position <= content.Length)
        {
            var index = content.IndexOf(search, position, StringComparison.Ordinal);
            if (index < 0)
                break;
            count++;
            position = index + Math.Max(search.Length, 1);
        }
        return count;
    }

    private static List<string> CollectSubstringCandidates(string content, string search)
    {
        var candidates = new List<string>();
        if (search.Length == 0)
            return candidates;
        var position = 0;
        while (position <= content.Length)
        {
            var index = content.IndexOf(search, position, StringComparison.Ordinal);
            if (index < 0)
                break;
            candidates.Add(search);
            position = index + Math.Max(search.Length, 1);
        }
        return candidates;
    }

    private static List<string> CollectNormalizedCandidates(string content, string search, Func<string, string> normalize)
    {
        var candidates = new List<string>();
        var normalizedContent = normalize(content);
        var normalizedSearch = normalize(search);
        if (normalizedSearch.Length == 0)
            return candidates;
        var position = 0;
        while (position <= normalizedContent.Length)
        {
            var index = normalizedContent.IndexOf(normalizedSearch, position, StringComparison.Ordinal);
            if (index < 0)
                break;
            candidates.Add(content.Substring(index, search.Length));
            position = index + Math.Max(normalizedSearch.Length, 1);
        }
        return candidates;
    }

    private static List<string> CollectLineNumberPrefixCandidates(string content, string search)
    {
        var stripped = StripReadLineNumberPrefixes(search);
        if (stripped is null || stripped == search)
            return [];
        return CollectSubstringCandidates(content, stripped);
    }

    private static List<string> CollectEscapeNormalizedCandidates(string content, string search)
    {
        var unescaped = UnescapeVisibleCharacters(search);
        if (unescaped == search)
            return [];
        return CollectSubstringCandidates(content, unescaped);
    }

    private static List<string> CollectUnicodeEscapeNormalizedCandidates(string content, string search)
    {
        var unescaped = UnescapeUnicodeCharacters(search);
        if (unescaped == search)
            return [];
        return CollectSubstringCandidates(content, unescaped);
    }

    private static List<string> CollectLineTrimmedCandidates(string content, string search)
    {
        var contentLines = content.Split('\n');
        var searchLines = TrimTrailingEmptyLine(search.Split('\n'));
        if (searchLines.Length == 0)
            return [];

        var candidates = new List<string>();
        for (var index = 0; index <= contentLines.Length - searchLines.Length; index++)
        {
            var matched = true;
            for (var offset = 0; offset < searchLines.Length; offset++)
            {
                if (!contentLines[index + offset].AsSpan().Trim().SequenceEqual(searchLines[offset].AsSpan().Trim()))
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
                candidates.Add(string.Join("\n", contentLines, index, searchLines.Length));
        }
        return candidates;
    }

    private static List<string> CollectIndentationFlexibleCandidates(string content, string search)
    {
        var contentLines = content.Split('\n');
        var searchLines = TrimTrailingEmptyLine(search.Split('\n'));
        if (searchLines.Length < 2)
            return [];

        var normalizedSearch = RemoveCommonIndent(searchLines);
        var expectedLines = normalizedSearch.Split('\n');
        var candidates = new List<string>();
        for (var index = 0; index <= contentLines.Length - searchLines.Length; index++)
        {
            var minIndent = int.MaxValue;
            for (var offset = 0; offset < searchLines.Length; offset++)
            {
                var line = contentLines[index + offset];
                if (line.AsSpan().Trim().Length > 0)
                    minIndent = Math.Min(minIndent, LeadingIndentLength(line));
            }
            var matched = true;
            for (var offset = 0; offset < searchLines.Length; offset++)
            {
                var line = contentLines[index + offset];
                var comparable = line.AsSpan();
                if (minIndent != int.MaxValue && comparable.Trim().Length > 0)
                    comparable = comparable[minIndent..];
                if (!comparable.SequenceEqual(expectedLines[offset]))
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
                candidates.Add(string.Join("\n", contentLines, index, searchLines.Length));
        }
        return candidates;
    }

    private static List<string> CollectBlockAnchorCandidates(string content, string search)
    {
        var contentLines = content.Split('\n');
        var searchLines = TrimTrailingEmptyLine(search.Split('\n'));
        if (searchLines.Length < 3)
            return [];

        var first = searchLines[0].Trim();
        var last = searchLines[^1].Trim();
        var candidates = new List<string>();
        for (var index = 0; index <= contentLines.Length - searchLines.Length; index++)
        {
            var block = contentLines[index..(index + searchLines.Length)];
            if (block[0].Trim() != first || block[^1].Trim() != last)
                continue;
            if (AverageMiddleSimilarity(block, searchLines) < BlockAnchorMinSimilarity)
                continue;
            candidates.Add(string.Join("\n", block));
        }
        return candidates;
    }

    private static string? StripReadLineNumberPrefixes(string search)
    {
        var lines = search.Split('\n');
        var stripped = new string?[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var colonMatch = ColonLineNumberPrefix.Match(lines[index]);
            if (colonMatch.Success)
            {
                stripped[index] = colonMatch.Groups[1].Value;
                continue;
            }
            var tabMatch = TabLineNumberPrefix.Match(lines[index]);
            if (tabMatch.Success)
            {
                stripped[index] = tabMatch.Groups[1].Value;
                continue;
            }
            return null;
        }
        return string.Join("\n", stripped!);
    }

    private static string UnescapeVisibleCharacters(string search) =>
        EscapedVisibleChar.Replace(search, match => match.Groups[1].Value switch
        {
            "n" => "\n",
            "t" => "\t",
            "r" => "\r",
            var token => token,
        });

    private static string UnescapeUnicodeCharacters(string search) =>
        EscapedUnicodeChar.Replace(search, match =>
            match.Groups[1].Success
                ? match.Value
                : ((char)Convert.ToInt32(match.Groups[2].Value, 16)).ToString());

    private static string[] TrimTrailingEmptyLine(string[] lines) =>
        lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;

    private static string RemoveCommonIndent(string[] lines)
    {
        var nonEmpty = lines.Where(line => line.Trim().Length > 0).ToArray();
        if (nonEmpty.Length == 0)
            return string.Join("\n", lines);
        var minIndent = nonEmpty.Min(LeadingIndentLength);
        return string.Join("\n", lines.Select(line => line.Trim().Length == 0 ? line : line[minIndent..]));
    }

    private static int LeadingIndentLength(string line)
    {
        var length = 0;
        while (length < line.Length && (line[length] == '\t' || line[length] == ' '))
            length++;
        return length;
    }

    private static double AverageMiddleSimilarity(string[] actual, string[] expected)
    {
        if (actual.Length <= 2)
            return 1;
        var total = 0.0;
        var count = 0;
        for (var index = 1; index < actual.Length - 1; index++)
        {
            total += LineSimilarity(actual[index].Trim(), expected[index].Trim());
            count++;
        }
        return count == 0 ? 1 : total / count;
    }

    private static double LineSimilarity(string left, string right)
    {
        if (left == right)
            return 1;
        var maxLength = Math.Max(left.Length, right.Length);
        if (maxLength == 0)
            return 1;
        return 1 - (double)LevenshteinDistance(left, right) / maxLength;
    }

    private static string NormalizeQuotes(string value) => value
        .Replace(LeftSingleCurlyQuote, '\'')
        .Replace(RightSingleCurlyQuote, '\'')
        .Replace(LeftDoubleCurlyQuote, '"')
        .Replace(RightDoubleCurlyQuote, '"');

    private static string ApplyCurlyDoubleQuotes(string value)
    {
        var chars = value.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] != '"')
                continue;
            chars[index] = IsOpeningQuoteContext(chars, index) ? LeftDoubleCurlyQuote : RightDoubleCurlyQuote;
        }
        return new string(chars);
    }

    private static string ApplyCurlySingleQuotes(string value)
    {
        var chars = value.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] != '\'')
                continue;
            var previous = index > 0 ? (char?)chars[index - 1] : null;
            var next = index + 1 < chars.Length ? (char?)chars[index + 1] : null;
            chars[index] = IsLetter(previous) && IsLetter(next)
                ? RightSingleCurlyQuote
                : IsOpeningQuoteContext(chars, index) ? LeftSingleCurlyQuote : RightSingleCurlyQuote;
        }
        return new string(chars);
    }

    private static bool IsOpeningQuoteContext(char[] chars, int index)
    {
        if (index == 0)
            return true;
        var previous = chars[index - 1];
        return previous is ' ' or '\t' or '\n' or '\r' or '(' or '[' or '{' or '\u2014' or '\u2013';
    }

    private static bool IsLetter(char? value) => value is not null && char.IsLetter(value.Value);
}
