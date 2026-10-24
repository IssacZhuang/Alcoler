using System.Collections.Concurrent;
using System.Globalization;
using Alcoler.Contracts.Projections;
using Alcoler.Localization;

namespace Alcoler.ViewModel;

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
    private static readonly (double Scale, string Suffix)[] ChineseUnits = [(1e12, "兆"), (1e8, "亿"), (1e4, "万")];
    private static readonly (double Scale, string Suffix)[] JapaneseUnits = [(1e12, "兆"), (1e8, "億"), (1e4, "万")];
    private static readonly (double Scale, string Suffix)[] KoreanUnits = [(1e12, "조"), (1e8, "억"), (1e4, "만")];
    private static readonly (double Scale, string Suffix)[] WesternUnits = [(1e12, "T"), (1e9, "B"), (1e6, "M"), (1e3, "K")];
    private static readonly ConcurrentDictionary<(CultureInfo Culture, int Digits), NumberFormatInfo> PercentFormats = new();

    public ContextUsageViewModel(ContextUsageState usage, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        Usage = usage;
        Fraction = usage.MaxTokens > 0 ? Math.Clamp((double)usage.UsedTokens / usage.MaxTokens, 0, 1) : 0;
        AccessibleText = Loc.Get("context_accessible",
            usage.UsedTokens.ToString("N0", culture), usage.MaxTokens.ToString("N0", culture));
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
        OutputTokensPerSecond = usage.GenerationDurationMs > 0 && usage.OutputTokens > 0
            ? usage.OutputTokens * 1000.0 / usage.GenerationDurationMs
            : null;
        OutputSpeedText = OutputTokensPerSecond is { } speed
            ? speed.ToString("0.0", culture) + " tps"
            : null;
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
    public double? OutputTokensPerSecond { get; }
    public string? OutputSpeedText { get; }
    public bool HasOutputSpeed => OutputSpeedText is not null;
    public bool HasFooterStats => HasCacheHitRate || HasOutputSpeed;
    public string RingPath { get; }

    private static string Label(string source) => source switch
    {
        ContextUsageSources.Messages => Loc.Get("context_label_messages"),
        ContextUsageSources.SystemPrompt => Loc.Get("context_label_system_prompt"),
        ContextUsageSources.MetaUserContext => Loc.Get("context_label_meta_user_context"),
        ContextUsageSources.Skills => Loc.Get("context_label_skills"),
        ContextUsageSources.ToolPrompt => Loc.Get("context_label_tool_prompt"),
        ContextUsageSources.SystemToolSchemas => Loc.Get("context_label_system_tools"),
        ContextUsageSources.McpToolSchemas => Loc.Get("context_label_mcp_tools"),
        _ => source,
    };

    private static string Percent(double value, CultureInfo culture)
    {
        var rounded = Math.Round(value * 100, 1, MidpointRounding.AwayFromZero);
        var digits = rounded % 1 == 0 ? 0 : 1;
        var format = PercentFormats.GetOrAdd((culture, digits), key =>
        {
            var numberFormat = (NumberFormatInfo)key.Culture.NumberFormat.Clone();
            numberFormat.PercentDecimalDigits = key.Digits;
            return numberFormat;
        });
        return (rounded / 100).ToString("P", format);
    }

    internal static string Compact(int value, CultureInfo culture, int maximumFractionDigits = 1)
    {
        var units = Loc.Current.UseCjkCompactUnits
            ? ChineseUnits
            : culture.TwoLetterISOLanguageName switch
            {
                "ja" => JapaneseUnits,
                "ko" => KoreanUnits,
                "zh" => ChineseUnits,
                _ => WesternUnits,
            };
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
