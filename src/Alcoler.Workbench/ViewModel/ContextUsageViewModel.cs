using System.Globalization;
using Alcoler.Contracts.Projections;

namespace Alcoler.Workbench.ViewModel;

public sealed record ContextUsageSegment(string Source, string Label, long Characters, double Fraction, string PercentText, double ToneOpacity);

public sealed class ContextUsageViewModel
{
    private static readonly string[] SourceOrder =
    [
        ContextUsageSources.Messages, ContextUsageSources.SystemPrompt, ContextUsageSources.MetaUserContext,
        ContextUsageSources.Skills, ContextUsageSources.ToolPrompt, ContextUsageSources.SystemToolSchemas,
        ContextUsageSources.McpToolSchemas,
    ];
    private static readonly double[] Tones = [1, .78, .58, .42, .28];

    public ContextUsageViewModel(ContextUsageState usage, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        Usage = usage;
        Fraction = usage.MaxTokens > 0 ? Math.Clamp((double)usage.UsedTokens / usage.MaxTokens, 0, 1) : 0;
        AccessibleText = $"Context usage: {usage.UsedTokens.ToString("N0", culture)} / {usage.MaxTokens.ToString("N0", culture)} tokens";
        Summary = $"{Compact(usage.UsedTokens, culture)}/{Compact(usage.MaxTokens, culture, 0)} ({Percent(Fraction, culture)})";
        var categories = usage.Breakdown.Where(item => item.Characters > 0 && SourceOrder.Contains(item.Source))
            .GroupBy(item => item.Source)
            .Select(group => new ContextUsageCategory(group.Key, group.Sum(item => item.Characters)))
            .OrderByDescending(item => item.Characters)
            .ThenBy(item => Array.IndexOf(SourceOrder, item.Source))
            .ToArray();
        var total = categories.Sum(item => (double)item.Characters);
        Segments = categories.Select((item, index) => new ContextUsageSegment(item.Source, Label(item.Source),
            item.Characters, item.Characters / total, Percent(item.Characters / total, culture), Tones[Math.Min(index, Tones.Length - 1)])).ToArray();
        CacheHitRate = usage.CacheInputTokens > 0 ? Math.Clamp((double)usage.CacheReadTokens / usage.CacheInputTokens, 0, 1) : null;
        CacheHitRateText = CacheHitRate is double rate && rate >= .78 ? Percent(rate, culture) : null;
        RingPath = BuildRingPath(Fraction);
    }

    public ContextUsageState Usage { get; }
    public bool IsVisible => Usage.UsedTokens > 0 && Usage.MaxTokens > 0;
    public double Fraction { get; }
    public string AccessibleText { get; }
    public string Summary { get; }
    public IReadOnlyList<ContextUsageSegment> Segments { get; }
    public bool HasBreakdown => Segments.Count > 0;
    public double? CacheHitRate { get; }
    public string? CacheHitRateText { get; }
    public bool HasCacheHitRate => CacheHitRateText is not null;
    public string RingPath { get; }

    private static string Label(string source) => source switch
    {
        ContextUsageSources.Messages => "Messages",
        ContextUsageSources.SystemPrompt => "System prompt",
        ContextUsageSources.MetaUserContext => "Meta user context",
        ContextUsageSources.Skills => "Skills",
        ContextUsageSources.ToolPrompt => "Tool prompt",
        ContextUsageSources.SystemToolSchemas => "System tools",
        ContextUsageSources.McpToolSchemas => "MCP tools",
        _ => source,
    };

    private static string Percent(double value, CultureInfo culture)
    {
        var rounded = Math.Round(value * 100, 1, MidpointRounding.AwayFromZero);
        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.PercentDecimalDigits = rounded % 1 == 0 ? 0 : 1;
        return (rounded / 100).ToString("P", format);
    }

    internal static string Compact(int value, CultureInfo culture, int maximumFractionDigits = 1)
    {
        var language = culture.TwoLetterISOLanguageName;
        var units = language is "zh" or "ja"
            ? new (double Scale, string Suffix)[] { (1e12, "兆"), (1e8, "亿"), (1e4, "万") }
            : language == "ko"
                ? [(1e12, "조"), (1e8, "억"), (1e4, "만")]
                : [(1e12, "T"), (1e9, "B"), (1e6, "M"), (1e3, "K")];
        if (language == "ja")
            units = [(1e12, "兆"), (1e8, "億"), (1e4, "万")];
        var format = maximumFractionDigits == 0 ? "0" : "0.#";
        foreach (var (scale, suffix) in units)
        {
            if (value >= scale)
                return (value / scale).ToString(format, culture) + suffix;
        }
        return value.ToString("N0", culture);
    }

    private static string BuildRingPath(double fraction)
    {
        if (fraction >= 1)
            return "M12 2A10 10 0 1 1 12 22A10 10 0 1 1 12 2";
        var angle = fraction * Math.Tau - Math.PI / 2;
        var x = 12 + Math.Cos(angle) * 10;
        var y = 12 + Math.Sin(angle) * 10;
        return FormattableString.Invariant($"M12 2A10 10 0 {(fraction > .5 ? 1 : 0)} 1 {x} {y}");
    }
}
