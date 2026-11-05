using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Alcoler.Converters;

public static class TimelineConverters
{
    public static readonly NonEmptyConverter NonEmpty = new();
    public static readonly JoinLinesConverter JoinLines = new();
    public static readonly TerminalToolConverter TerminalTool = new();
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
