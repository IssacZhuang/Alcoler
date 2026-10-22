using System.Text;
using System.Text.RegularExpressions;

namespace Alcoler.Adapters.Tools;

internal readonly record struct GrepContentEntry(string Path, int? LineNumber, string Text);

internal sealed class GrepPatternException(string pattern) : Exception
{
    public string Pattern { get; } = pattern;
}

internal sealed class GrepEngineRequest
{
    public required string Pattern { get; init; }
    public required string SearchPath { get; init; }
    public required string FilterRoot { get; init; }
    public required string Mode { get; init; }
    public required IReadOnlyList<string> GlobPatterns { get; init; }
    public string? TypeFilter { get; init; }
    public bool IsSingleFile { get; init; }
    public bool IgnoreCase { get; init; }
    public bool Multiline { get; init; }
    public bool OnlyMatching { get; init; }
    public int BeforeContext { get; init; }
    public int AfterContext { get; init; }
}

internal sealed class GrepEngineResult
{
    public List<GrepContentEntry> ContentEntries { get; } = [];
    public List<(string Path, long Count)> CountEntries { get; } = [];
    public List<string> MatchedFiles { get; } = [];
}

internal interface IGrepEngine
{
    GrepEngineResult Search(GrepEngineRequest request, CancellationToken cancellationToken);
}

internal static class GrepTypeMap
{
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

    public static bool MatchesFile(string path, string type) => CreateFilter(type).Matches(path);

    internal static GrepTypeFilter CreateFilter(string type)
    {
        var normalized = type.ToLowerInvariant().TrimStart('.');
        return new GrepTypeFilter(normalized,
            TypeExtensions.TryGetValue(normalized, out var extensions) ? extensions : null);
    }

    // The native engine receives type filters as include globs, like the reference's
    // type-to-glob translation; brace expansion is not needed because each extension
    // becomes its own glob.
    public static IReadOnlyList<string> ToGlobs(string type)
    {
        var normalized = type.ToLowerInvariant().TrimStart('.');
        if (!TypeExtensions.TryGetValue(normalized, out var extensions))
            return ["*." + normalized];
        var globs = new string[extensions.Length];
        for (var index = 0; index < extensions.Length; index++)
            globs[index] = "*" + extensions[index];
        return globs;
    }
}

internal readonly struct GrepTypeFilter
{
    private readonly string _normalized;
    private readonly string[]? _extensions;

    public GrepTypeFilter(string normalized, string[]? extensions)
    {
        _normalized = normalized;
        _extensions = extensions;
    }

    public bool Matches(string path)
    {
        var extension = Path.GetExtension(path);
        if (_extensions is null)
            return extension.Length == _normalized.Length + 1
                && extension[0] == '.'
                && extension.AsSpan(1).Equals(_normalized, StringComparison.OrdinalIgnoreCase);
        foreach (var candidate in _extensions)
        {
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

internal static class GrepGlobSupport
{
    public static List<string> SplitPatterns(string glob)
    {
        var patterns = new List<string>();
        var remaining = glob.AsSpan();
        while (!remaining.IsEmpty)
        {
            var start = 0;
            while (start < remaining.Length && char.IsWhiteSpace(remaining[start]))
                start++;
            if (start == remaining.Length)
                break;
            var end = start;
            while (end < remaining.Length && !char.IsWhiteSpace(remaining[end]))
                end++;
            AddPattern(patterns, remaining[start..end]);
            remaining = remaining[end..];
        }
        return patterns;
    }

    private static void AddPattern(List<string> patterns, ReadOnlySpan<char> token)
    {
        if (token.Contains('{') && token.Contains('}'))
        {
            patterns.Add(token.ToString());
            return;
        }
        while (true)
        {
            var comma = token.IndexOf(',');
            var part = (comma < 0 ? token : token[..comma]).Trim();
            if (!part.IsEmpty)
                patterns.Add(part.ToString());
            if (comma < 0)
                return;
            token = token[(comma + 1)..];
        }
    }

    public static string ToPosixRelative(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        return relative.Contains('\\') ? relative.Replace('\\', '/') : relative;
    }
}

internal sealed class GrepGlobFilter
{
    private readonly Regex _pathRegex;
    private readonly Regex? _fileNameRegex;

    public GrepGlobFilter(string pattern)
    {
        var normalized = NormalizeGlobPattern(pattern);
        _pathRegex = GlobPatternToRegex(normalized);
        _fileNameRegex = normalized.Contains('/') ? null : _pathRegex;
    }

    public bool Matches(string relativePath, string fileName) =>
        _pathRegex.IsMatch(relativePath) || (_fileNameRegex?.IsMatch(fileName) ?? false);

    private static string NormalizeGlobPattern(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static Regex GlobPatternToRegex(string pattern) =>
        GlobRegexCache.Get('g', pattern, static value => BuildGlobPatternRegex(value));

    private static Regex BuildGlobPatternRegex(string pattern)
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
                    builder.Append("(?:");
                    var alternatives = pattern.AsSpan(index + 1, closing - index - 1);
                    while (true)
                    {
                        var comma = alternatives.IndexOf(',');
                        var part = comma < 0 ? alternatives : alternatives[..comma];
                        GlobRegexCache.AppendEscaped(builder, part);
                        if (comma < 0)
                            break;
                        builder.Append('|');
                        alternatives = alternatives[(comma + 1)..];
                    }
                    builder.Append(')');
                    index = closing;
                    continue;
                }
            }

            GlobRegexCache.AppendEscaped(builder, c);
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant);
    }
}
