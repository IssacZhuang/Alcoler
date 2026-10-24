namespace Alcoler.Localization;

// Adding a UI language = one entry in Loc.SupportedLanguages plus a Strings.{Code}.json catalog.
public sealed record LanguageDefinition(
    string Code,
    string NativeName,
    string[] OsLanguagePrefixes,
    bool UseCjkCompactUnits);
