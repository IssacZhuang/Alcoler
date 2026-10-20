using System.Text;
using System.Text.RegularExpressions;

namespace Alcoler.Adapters.Tools;

internal static class GlobMatcher
{
    public static Regex ToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var builder = new StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c != '*')
            {
                builder.Append(c == '?' ? "[^/]" : Regex.Escape(c.ToString()));
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
