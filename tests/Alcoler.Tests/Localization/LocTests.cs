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
    // Loc holds process-wide state; every test that switches restores English so
    // other suites keep asserting the English default.
    [Fact]
    public void DefaultLanguage_IsEnglish()
    {
        Assert.Equal(Loc.English, Loc.Language);
        Assert.False(Loc.IsChinese);
    }

    [Fact]
    public void Get_ReturnsEnglishByDefault()
    {
        Assert.Equal("Cancel", Loc.Get("common_cancel"));
    }

    [Fact]
    public void Get_ReturnsChineseAfterSwitch()
    {
        try
        {
            Loc.SetLanguage(Loc.Chinese);

            Assert.True(Loc.IsChinese);
            Assert.Equal("取消", Loc.Get("common_cancel"));
        }
        finally
        {
            Loc.SetLanguage(Loc.English);
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
    public void ResolveLanguage_ExplicitChinese_PassesThrough(string configured)
    {
        Assert.Equal(Loc.Chinese, Loc.ResolveLanguage(configured));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData(" en ")]
    public void ResolveLanguage_ExplicitEnglish_PassesThrough(string configured)
    {
        Assert.Equal(Loc.English, Loc.ResolveLanguage(configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fr")]
    [InlineData("garbage")]
    public void ResolveLanguage_NullOrUnknown_ResolvesToSupportedLanguage(string? configured)
    {
        Assert.True(Loc.ResolveLanguage(configured!) is Loc.English or Loc.Chinese);
    }

    [Fact]
    public void ResolveLanguage_Auto_FollowsSystemUiCulture()
    {
        Assert.Equal(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? Loc.Chinese : Loc.English,
            Loc.ResolveLanguage("auto"));
    }

    [Fact]
    public void SetLanguage_UnknownValue_FallsBackToEnglish()
    {
        Loc.SetLanguage(Loc.Chinese);
        Loc.SetLanguage("fr");

        Assert.Equal(Loc.English, Loc.Language);
    }

    [Fact]
    public void Catalogs_HaveIdenticalKeysAndPlaceholders()
    {
        var assembly = typeof(Loc).Assembly;
        var english = LoadCatalog(assembly, "en");
        var chinese = LoadCatalog(assembly, "zh");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            chinese.Keys.OrderBy(key => key, StringComparer.Ordinal));

        foreach (var (key, value) in english)
        {
            Assert.Equal(Placeholders(value), Placeholders(chinese[key]));
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

            Loc.SetLanguage(Loc.Chinese);

            Assert.Equal("构建", composer.ModeLabel);
        }
        finally
        {
            Loc.SetLanguage(Loc.English);
        }
    }
}
