using System.Globalization;
using Alcoler.Contracts.Projections;
using Alcoler.Workbench.ViewModel;
using Xunit;

namespace Alcoler.Workbench.Tests;

public class ContextUsageViewModelTests
{
    [Theory]
    [InlineData(0, 200000)]
    [InlineData(1000, 0)]
    [InlineData(-1, 200000)]
    public void NonPositiveUsage_IsHidden(int used, int max)
    {
        var model = new ContextUsageViewModel(new ContextUsageState(used, max, "estimate", []));
        Assert.False(model.IsVisible);
    }

    [Fact]
    public void SummaryAndAccessibility_UseCompactAndExactLocalizedCounts()
    {
        var model = new ContextUsageViewModel(new ContextUsageState(121500, 200000, "provider_usage", []),
            CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("121.5K/200K (60.8%)", model.Summary);
        Assert.Equal("Context usage: 121,500 / 200,000 tokens", model.AccessibleText);
        Assert.Equal(.6075, model.Fraction);
        var chinese = new ContextUsageViewModel(model.Usage, CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Contains("12.2万/20万", chinese.Summary);
    }

    [Fact]
    public void Percentage_RespectsLocaleSymbolPlacement()
    {
        var model = new ContextUsageViewModel(new ContextUsageState(1000, 2000, "estimate", []),
            CultureInfo.GetCultureInfo("tr-TR"));
        Assert.Contains("(%50)", model.Summary);
    }

    [Fact]
    public void Breakdown_AggregatesAndSortsCharacterFractionsWithoutTokenLabels()
    {
        var model = new ContextUsageViewModel(new ContextUsageState(1000, 200000, "estimate",
        [
            new(ContextUsageSources.SystemPrompt, 100), new(ContextUsageSources.Messages, 50),
            new(ContextUsageSources.Messages, 150), new(ContextUsageSources.Skills, 100),
            new(ContextUsageSources.ToolPrompt, 0), new("unknown", 10000),
        ]), CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal(new[] { ContextUsageSources.Messages, ContextUsageSources.SystemPrompt, ContextUsageSources.Skills },
            model.Segments.Select(segment => segment.Source));
        Assert.Equal(new[] { "50%", "25%", "25%" }, model.Segments.Select(segment => segment.PercentText));
        Assert.Equal(1, model.Segments.Sum(segment => segment.Fraction));
    }

    [Theory]
    [InlineData(779, 1000, false)]
    [InlineData(780, 1000, true)]
    [InlineData(0, 0, false)]
    public void CacheHitRate_UsesWeightedTotalsAndThreshold(long read, long input, bool visible)
    {
        var model = new ContextUsageViewModel(new ContextUsageState(1000, 200000, "provider_usage", [], read, input));
        Assert.Equal(visible, model.HasCacheHitRate);
    }
}
