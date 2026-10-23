using System.Globalization;
using System.Text.Json;

namespace Alcoler.Localization;

// UI-string catalog only: kernel prompts, agent instructions, and session data stay English.
public static class Loc
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string Chinese = "zh";

    private static readonly Lazy<Dictionary<string, string>> EnglishCatalog = new(() => LoadCatalog(English));
    private static readonly Lazy<Dictionary<string, string>> ChineseCatalog = new(() => LoadCatalog(Chinese));

    public static string Language { get; private set; } = English;

    public static bool IsChinese => Language == Chinese;

    public static void SetLanguage(string language) =>
        Language = string.Equals(language, Chinese, StringComparison.OrdinalIgnoreCase) ? Chinese : English;

    // "auto" follows the OS UI language; unknown values fall back to English.
    public static string ResolveLanguage(string configured)
    {
        if (string.Equals(configured, Chinese, StringComparison.OrdinalIgnoreCase))
        {
            return Chinese;
        }
        if (string.IsNullOrWhiteSpace(configured)
            || string.Equals(configured, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? Chinese
                : English;
        }
        return English;
    }

    public static string Get(string key, params object?[] args)
    {
        var template = Language == Chinese && ChineseCatalog.Value.TryGetValue(key, out var chinese)
            ? chinese
            : EnglishCatalog.Value.TryGetValue(key, out var english)
                ? english
                : key;
        if (args.Length == 0)
        {
            return template;
        }
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static Dictionary<string, string> LoadCatalog(string language)
    {
        using var stream = typeof(Loc).Assembly
            .GetManifestResourceStream($"Alcoler.Localization.Strings.{language}.json")
            ?? throw new InvalidOperationException($"Missing embedded UI string catalog for '{language}'.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"UI string catalog for '{language}' is empty or invalid.");
    }
}
