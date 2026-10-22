using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Alcoler.Adapters.Tools;

internal static class GlobRegexCache
{
    private const int Capacity = 1024;

    private static readonly ConcurrentDictionary<(char Kind, string Pattern), Regex> Cache = new();

    public static Regex Get(char kind, string pattern, Func<string, Regex> build)
    {
        // Patterns come from tool input; beyond Capacity the regex is built per call instead of cached.
        if (Cache.Count >= Capacity)
            return build(pattern);
        return Cache.GetOrAdd((kind, pattern), static (key, factory) => factory(key.Pattern), build);
    }

    // Mirrors Regex.Escape for a single character so pattern assembly avoids per-char strings.
    public static void AppendEscaped(StringBuilder builder, char c)
    {
        // Match Regex.Escape: whitespace becomes a two-character escape, other specials a backslash prefix.
        switch (c)
        {
            case ' ':
                builder.Append('\\').Append(' ');
                return;
            case '\t':
                builder.Append('\\').Append('t');
                return;
            case '\n':
                builder.Append('\\').Append('n');
                return;
            case '\r':
                builder.Append('\\').Append('r');
                return;
            case '\f':
                builder.Append('\\').Append('f');
                return;
            case '\\':
            case '*':
            case '+':
            case '?':
            case '|':
            case '{':
            case '[':
            case '(':
            case ')':
            case '^':
            case '$':
            case '.':
            case '#':
                builder.Append('\\');
                break;
        }
        builder.Append(c);
    }

    public static void AppendEscaped(StringBuilder builder, ReadOnlySpan<char> value)
    {
        foreach (var c in value)
            AppendEscaped(builder, c);
    }
}

internal static class GlobMatcher
{
    public static Regex ToRegex(string pattern) =>
        GlobRegexCache.Get('m', pattern, static value => BuildRegex(value));

    private static Regex BuildRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var builder = new StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c != '*')
            {
                if (c == '?')
                    builder.Append("[^/]");
                else
                    GlobRegexCache.AppendEscaped(builder, c);
                continue;
            }

            if (i + 1 >= normalized.Length || normalized[i + 1] != '*')
            {
                builder.Append("[^/]*");
                continue;
            }

            i++;
            while (i + 1 < normalized.Length && normalized[i + 1] == '*')
                i++;
            if (i + 1 < normalized.Length && normalized[i + 1] == '/')
            {
                builder.Append("(?:.*/)?");
                i++;
            }
            else
            {
                builder.Append(".*");
            }
        }
        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant);
    }
}
