using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Alcoler.Adapters.Web;

/// <summary>
/// Readable-content extraction for WebFetch: script/style/noscript bodies must never
/// reach the model, and block markup must become markdown structure before summarization.
/// </summary>
public static class HtmlToMarkdownConverter
{
    private static readonly Regex Comments = Compile("<!--[\\s\\S]*?-->");
    private static readonly Regex Scripts = Compile("<script\\b[\\s\\S]*?</script>");
    private static readonly Regex Styles = Compile("<style\\b[\\s\\S]*?</style>");
    private static readonly Regex NoScripts = Compile("<noscript\\b[\\s\\S]*?</noscript>");
    private static readonly Regex Headings = Compile("<h([1-6])\\b[^>]*>([\\s\\S]*?)</h\\1>");
    private static readonly Regex Links = Compile("<a\\b[^>]*href=[\"']([^\"']+)[\"'][^>]*>([\\s\\S]*?)</a>");
    private static readonly Regex ListItems = Compile("<li\\b[^>]*>([\\s\\S]*?)</li>");
    private static readonly Regex LineBreaks = Compile("<br\\s*/?>");
    private static readonly Regex BlockClosers = Compile("</(?:p|div|section|article|header|footer|tr|table|ul|ol)>");
    private static readonly Regex AnyTag = Compile("<[^>]+>");
    private static readonly Regex HexEntities = Compile("&#x([0-9a-f]+);");
    private static readonly Regex DecimalEntities = Compile("&#(\\d+);");

    public static string ExtractReadableContent(string body, string contentType)
    {
        var mime = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!IsTextLikeMime(mime))
            throw new InvalidOperationException(
                $"Unsupported WebFetch content type: {(mime.Length == 0 ? "unknown" : mime)}");

        if (mime is "text/html" or "application/xhtml+xml" || contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return HtmlToMarkdown(body);
        return body.Trim();
    }

    public static string HtmlToMarkdown(string html)
    {
        var content = Comments.Replace(html, "");
        content = Scripts.Replace(content, "");
        content = Styles.Replace(content, "");
        content = NoScripts.Replace(content, "");

        content = Headings.Replace(content, m => $"\n{new string('#', int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))} {m.Groups[2].Value}\n");
        content = Links.Replace(content, "[$2]($1)");
        content = ListItems.Replace(content, "\n- $1");
        content = LineBreaks.Replace(content, "\n");
        content = BlockClosers.Replace(content, "\n");
        content = AnyTag.Replace(content, "");

        return CollapseBlankLines(DecodeHtmlEntities(content)).Trim();
    }

    public static string DecodeHtmlEntities(string value) =>
        DecimalEntities.Replace(
            HexEntities.Replace(
                value
                    .Replace("&nbsp;", " ")
                    .Replace("&amp;", "&")
                    .Replace("&lt;", "<")
                    .Replace("&gt;", ">")
                    .Replace("&quot;", "\"")
                    .Replace("&#39;", "'"),
                m => FromCodePoint(m.Groups[1].Value, 16, m.Value)),
            m => FromCodePoint(m.Groups[1].Value, 10, m.Value));

    private static bool IsTextLikeMime(string mime) =>
        mime.Length == 0
        || mime.StartsWith("text/", StringComparison.Ordinal)
        || mime is "application/json" or "application/xml" or "application/xhtml+xml"
            or "application/javascript" or "application/x-javascript"
        || mime.EndsWith("+json", StringComparison.Ordinal)
        || mime.EndsWith("+xml", StringComparison.Ordinal);

    private static string FromCodePoint(string digits, int fromBase, string fallback)
    {
        try
        {
            return char.ConvertFromUtf32(Convert.ToInt32(digits, fromBase));
        }
        catch (ArgumentOutOfRangeException)
        {
            return fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    // Single-pass equivalent of split + CollapsibleSpaces.Replace(raw, " ").Trim() per line + join:
    // space/tab runs collapse to one space, then leading/trailing whitespace drops; a blank line is
    // skipped when nothing was kept yet or the previous kept line is already blank.
    private static string CollapseBlankLines(string content)
    {
        var builder = new StringBuilder(content.Length);
        var hasKept = false;
        var lastKeptBlank = false;
        var start = 0;
        while (true)
        {
            var newline = content.IndexOf('\n', start);
            var end = newline < 0 ? content.Length : newline;
            var first = start;
            while (first < end && char.IsWhiteSpace(content[first]))
                first++;
            var last = end - 1;
            while (last >= first && char.IsWhiteSpace(content[last]))
                last--;
            var blank = last < first;
            if (!blank || (hasKept && !lastKeptBlank))
            {
                if (hasKept)
                    builder.Append('\n');
                for (var index = first; index <= last; index++)
                {
                    var current = content[index];
                    if (current is ' ' or '\t')
                    {
                        if (index > first && content[index - 1] is ' ' or '\t')
                            continue;
                        builder.Append(' ');
                    }
                    else
                    {
                        builder.Append(current);
                    }
                }
                hasKept = true;
                lastKeptBlank = blank;
            }
            if (newline < 0)
                break;
            start = newline + 1;
        }
        return builder.ToString();
    }

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
