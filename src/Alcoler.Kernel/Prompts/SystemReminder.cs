using System.Text.RegularExpressions;

namespace Alcoler.Kernel.Prompts;

public static partial class SystemReminder
{
    [GeneratedRegex(@"</?system-reminder\b", RegexOptions.IgnoreCase)]
    private static partial Regex NestedTagPattern();

    public static string Wrap(string text) =>
        string.IsNullOrEmpty(text)
            ? throw new ArgumentException("System reminder body cannot be empty.", nameof(text))
            : NestedTagPattern().IsMatch(text)
                ? throw new ArgumentException("System reminder body must not include nested system-reminder tags.", nameof(text))
                : $"<system-reminder>\n{text}\n</system-reminder>";

    // The context_prefix source is the only one the reference wraps with a trailing newline.
    public static string WrapContextPrefix(string text) => Wrap(text) + "\n";
}
