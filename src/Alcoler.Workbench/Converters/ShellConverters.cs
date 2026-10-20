using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Alcoler.Workbench.Converters;

internal static class ShellBrushes
{
    public static IBrush? Resolve(string key) =>
        Application.Current is { } app
        && app.Resources.TryGetResource(key, app.ActualThemeVariant, out var value)
        && value is IBrush brush
            ? brush
            : null;
}

/// <summary>Maps the phase label shown in the header to a brush keyed by activity class.</summary>
public sealed class PhaseBrushConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ProcessingInput"] = "AlcolerPhaseBusy",
        ["AwaitingModel"] = "AlcolerPhaseBusy",
        ["Streaming"] = "AlcolerPhaseBusy",
        ["Completing"] = "AlcolerPhaseBusy",
        ["ExecutingTools"] = "AlcolerPhaseTools",
        ["AwaitingPermission"] = "AlcolerPhaseAttention",
        ["AwaitingInteraction"] = "AlcolerPhaseAttention",
        ["Error"] = "AlcolerPhaseError",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is string phase && Keys.TryGetValue(phase, out var resolved) ? resolved : "AlcolerPhaseIdle";
        return ShellBrushes.Resolve(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colors the context meter fill: green below 50 percent, amber below 80, red above.</summary>
public sealed class ContextUsageBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is int i ? i : 0;
        var key = percent switch
        {
            >= 80 => "AlcolerError",
            >= 50 => "AlcolerWarning",
            _ => "AlcolerSuccess",
        };
        return ShellBrushes.Resolve(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns a 0-100 percentage into a pixel width; ConverterParameter is the track width.</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var track = parameter is null ? 100.0 : System.Convert.ToDouble(parameter, CultureInfo.InvariantCulture);
        return track * percent / 100.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Picks one of two brushes by boolean value; ConverterParameter is "trueKey|falseKey".</summary>
public sealed class BoolBrushConverter : IValueConverter
{
    private const char Separator = '|';

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var keys = (parameter?.ToString() ?? string.Empty).Split(Separator, 2);
        var key = value is true && keys.Length == 2 ? keys[0] : keys.Length == 2 ? keys[1] : string.Empty;
        return key.Length > 0 ? ShellBrushes.Resolve(key) : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when a count is zero; used to show empty-state placeholders.</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count == 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
