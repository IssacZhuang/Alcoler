using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alcoler.Contracts.Permissions;
using Alcoler.Localization;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests.Localization;

public sealed class LocTests
{
    // Loc holds process-wide state; every test that switches restores the fallback
    // language so other suites keep asserting the English default.
    [Fact]
    public void DefaultLanguage_IsFallbackLanguage()
    {
        Assert.Equal(Loc.Fallback, Loc.Current);
        Assert.Equal(Loc.Fallback.Code, Loc.Language);
    }

    [Fact]
    public void SupportedLanguages_HaveUniqueCaseInsensitiveCodes()
    {
        Assert.NotEmpty(Loc.SupportedLanguages);
        Assert.Equal(
            Loc.SupportedLanguages.Count,
            Loc.SupportedLanguages.Select(language => language.Code.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Get_ReturnsFallbackCatalogByDefault()
    {
        Assert.Equal("Cancel", Loc.Get("common_cancel"));
    }

    [Fact]
    public void Get_ReturnsChineseAfterSwitch()
    {
        try
        {
            Loc.SetLanguage("zh");

            Assert.Equal("zh", Loc.Language);
            Assert.Equal("取消", Loc.Get("common_cancel"));
        }
        finally
        {
            Loc.SetLanguage(Loc.Fallback.Code);
        }
    }

    [Fact]
    public void Get_FormatsArgumentsWithCurrentCulture()
    {
        Assert.Equal("3 files changed", Loc.Get("timeline_files_changed_many", 3));
    }

    [Fact]
    public void Get_MissingKey_FallsBackToKeyItself()
    {
        Assert.Equal("no_such_key", Loc.Get("no_such_key"));
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("ZH")]
    [InlineData(" zh ")]
    public void ResolveLanguage_ExplicitChinese_PassesThrough(string configured)
    {
        Assert.Equal("zh", Loc.ResolveLanguage(configured));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData(" en ")]
    public void ResolveLanguage_ExplicitEnglish_PassesThrough(string configured)
    {
        Assert.Equal("en", Loc.ResolveLanguage(configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fr")]
    [InlineData("garbage")]
    public void ResolveLanguage_NullOrUnknown_ResolvesToSupportedLanguage(string? configured)
    {
        Assert.Contains(Loc.ResolveLanguage(configured!), Loc.SupportedLanguages.Select(language => language.Code));
    }

    [Fact]
    public void ResolveLanguage_Auto_FollowsSystemUiCulture()
    {
        var osLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var expected = Loc.SupportedLanguages
            .FirstOrDefault(language => language.OsLanguagePrefixes.Contains(osLanguage, StringComparer.OrdinalIgnoreCase))
            ?? Loc.Fallback;

        Assert.Equal(expected.Code, Loc.ResolveLanguage("auto"));
    }

    [Theory]
    [InlineData("zh", "zh")]
    [InlineData(" ZH ", "zh")]
    [InlineData("en", "en")]
    [InlineData("garbage", Loc.Auto)]
    [InlineData("", Loc.Auto)]
    [InlineData(null, Loc.Auto)]
    public void NormalizeConfiguredLanguage_ReturnsStorablePreference(string? configured, string expected)
    {
        Assert.Equal(expected, Loc.NormalizeConfiguredLanguage(configured));
    }

    [Fact]
    public void SetLanguage_UnknownValue_FallsBackToFallbackLanguage()
    {
        Loc.SetLanguage("zh");
        Loc.SetLanguage("fr");

        Assert.Equal(Loc.Fallback, Loc.Current);
    }

    [Fact]
    public void Catalogs_HaveIdenticalKeysAndPlaceholders()
    {
        var assembly = typeof(Loc).Assembly;
        var fallback = LoadCatalog(assembly, Loc.Fallback.Code);
        Assert.NotEmpty(fallback);

        foreach (var language in Loc.SupportedLanguages.Skip(1))
        {
            var catalog = LoadCatalog(assembly, language.Code);

            Assert.Equal(
                fallback.Keys.OrderBy(key => key, StringComparer.Ordinal),
                catalog.Keys.OrderBy(key => key, StringComparer.Ordinal));

            foreach (var (key, value) in fallback)
            {
                Assert.Equal(Placeholders(value), Placeholders(catalog[key]));
            }
        }

        static Dictionary<string, string> LoadCatalog(Assembly assembly, string language)
        {
            using var stream = assembly.GetManifestResourceStream($"Alcoler.Localization.Strings.{language}.json")
                ?? throw new InvalidOperationException($"Missing embedded catalog for '{language}'.");
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException($"Catalog for '{language}' is empty.");
        }

        static HashSet<string> Placeholders(string template)
        {
            var matches = Regex.Matches(template, @"\{\d+\}");
            return [.. matches.Select(match => match.Value)];
        }
    }

    [Fact]
    public void ChineseLanguage_SwitchesViewModelText()
    {
        try
        {
            var composer = new ComposerViewModel();
            composer.Mode = PermissionMode.Build;
            Assert.Equal("Build", composer.ModeLabel);

            Loc.SetLanguage("zh");

            Assert.Equal("构建", composer.ModeLabel);
        }
        finally
        {
            Loc.SetLanguage(Loc.Fallback.Code);
        }
    }
}
