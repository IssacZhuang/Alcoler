using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Alcoler.Localization;

// UI-string catalog only: kernel prompts, agent instructions, and session data stay English.
public static class Loc
{
    // Config preference sentinel meaning "follow the OS UI language"; not a language code.
    public const string Auto = "auto";

    public static readonly IReadOnlyList<LanguageDefinition> SupportedLanguages =
    [
        new("en", "English", ["en"], UseCjkCompactUnits: false),
        new("zh", "中文", ["zh"], UseCjkCompactUnits: true),
    ];

    // The source language; missing keys in other catalogs fall back to its catalog.
    public static LanguageDefinition Fallback => SupportedLanguages[0];

    private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> Catalogs =
        new(StringComparer.OrdinalIgnoreCase);

    public static string Language => Current.Code;

    public static LanguageDefinition Current { get; private set; } = Fallback;

    public static void SetLanguage(string? code) => Current = Find(code) ?? Fallback;

    public static string ResolveLanguage(string? configured)
    {
        if (Find(configured) is { } exact)
        {
            return exact.Code;
        }
        if (string.IsNullOrWhiteSpace(configured)
            || string.Equals(configured.Trim(), Auto, StringComparison.OrdinalIgnoreCase))
        {
            return ResolveOsLanguage();
        }
        return Fallback.Code;
    }

    // Returns a storable preference: a registered language code, or Auto for anything else.
    public static string NormalizeConfiguredLanguage(string? configured) =>
        Find(configured) is { } match ? match.Code : Auto;

    public static string Get(string key, params object?[] args)
    {
        var template = CatalogFor(Current.Code).TryGetValue(key, out var value) ? value
            : CatalogFor(Fallback.Code).TryGetValue(key, out var fallback) ? fallback
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

    private static LanguageDefinition? Find(string? code)
    {
        var normalized = code?.Trim();
        return string.IsNullOrEmpty(normalized)
            ? null
            : SupportedLanguages.FirstOrDefault(language =>
                string.Equals(language.Code, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveOsLanguage()
    {
        var osLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return SupportedLanguages.FirstOrDefault(language =>
            language.OsLanguagePrefixes.Contains(osLanguage, StringComparer.OrdinalIgnoreCase))?.Code
            ?? Fallback.Code;
    }

    private static Dictionary<string, string> CatalogFor(string code) =>
        Catalogs.GetOrAdd(code, static language =>
            new Lazy<Dictionary<string, string>>(() => LoadCatalog(language))).Value;

    private static Dictionary<string, string> LoadCatalog(string language)
    {
        using var stream = typeof(Loc).Assembly
            .GetManifestResourceStream($"Alcoler.Localization.Strings.{language}.json")
            ?? throw new InvalidOperationException($"Missing embedded UI string catalog for '{language}'.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"UI string catalog for '{language}' is empty or invalid.");
    }
}
