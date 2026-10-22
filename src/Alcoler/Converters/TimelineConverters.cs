using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using Alcoler.Contracts.Projections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Alcoler.Converters;

public static class TimelineConverters
{
    public static readonly ToolCallStatusGlyphConverter StatusGlyph = new();
    public static readonly ToolCallStatusBrushConverter StatusBrush = new();
    public static readonly ToolRowBorderBrushConverter ToolRowBorder = new();
    public static readonly NonEmptyConverter NonEmpty = new();
    public static readonly JoinLinesConverter JoinLines = new();
    public static readonly TerminalToolConverter TerminalTool = new();
}

internal static class TimelineBrushes
{
    private static readonly ConcurrentDictionary<string, IBrush> FallbackBrushes = new();

    // Theme color tokens are merged at runtime; the fallback applies only while a key is absent.
    public static IBrush Resolve(string resourceKey, string fallbackHex)
    {
        var app = Application.Current;
        if (app is not null && app.TryGetResource(resourceKey, app.ActualThemeVariant, out var resource)
            && resource is IBrush themed)
        {
            return themed;
        }
        return FallbackBrushes.GetOrAdd(fallbackHex,
            static hex => new ImmutableSolidColorBrush(Color.Parse(hex)));
    }
}

public sealed class ToolCallStatusGlyphConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<ToolCallStatus, string> Glyphs =
        new Dictionary<ToolCallStatus, string>
        {
            [ToolCallStatus.Pending] = "\u25CB",
            [ToolCallStatus.Running] = "\u25D0",
            [ToolCallStatus.AwaitingPermission] = "\u25B2",
            [ToolCallStatus.Succeeded] = "\u2713",
            [ToolCallStatus.Failed] = "\u2715",
            [ToolCallStatus.Cancelled] = "\u2298",
        };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ToolCallStatus status && Glyphs.TryGetValue(status, out var glyph) ? glyph : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ToolCallStatusBrushConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<ToolCallStatus, (string Key, string Fallback)> Colors =
        new Dictionary<ToolCallStatus, (string, string)>
        {
            [ToolCallStatus.Pending] = ("AlcolerTextSecondary", "#9E9E9E"),
            [ToolCallStatus.Running] = ("AlcolerInfo", "#4FC1FF"),
            [ToolCallStatus.AwaitingPermission] = ("AlcolerWarning", "#E5A50A"),
            [ToolCallStatus.Succeeded] = ("AlcolerSuccess", "#6CCB5F"),
            [ToolCallStatus.Failed] = ("AlcolerError", "#F45B69"),
            [ToolCallStatus.Cancelled] = ("AlcolerError", "#D07F7F"),
        };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ToolCallStatus status || !Colors.TryGetValue(status, out var color))
        {
            return null;
        }
        return TimelineBrushes.Resolve(color.Key, color.Fallback);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ToolRowBorderBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
            ? TimelineBrushes.Resolve("AlcolerError", "#F45B69")
            : TimelineBrushes.Resolve("AlcolerBorder", "#3F3F46");

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NonEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null => false,
            string text => text.Length > 0,
            IEnumerable collection => collection.Cast<object?>().Any(),
            _ => true,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class JoinLinesConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable collection
            ? string.Join("\n\n", collection.Cast<object?>().OfType<string>())
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TerminalToolConverter : IValueConverter
{
    private static readonly HashSet<string> TerminalTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bash",
        "Shell",
        "Terminal",
        "RunCommand",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isTerminal = value is string toolName && TerminalTools.Contains(toolName);
        return string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)
            ? !isTerminal
            : isTerminal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
