using System.Collections.Concurrent;
using System.Globalization;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Alcoler.Converters;

public static class ComposerConverters
{
    /// <summary>Two-way gate for segmented enum selectors; an unchecked toggle must never write back.</summary>
    public static readonly IValueConverter EnumEquals = new EnumEqualsConverter();

    public static readonly IValueConverter Not = new NotConverter();

    public static readonly IMultiValueConverter RiskLevelBrush = new RiskLevelBrushConverter();

    /// <summary>True when the first count (selected answers) reaches the second (questions).</summary>
    public static readonly IMultiValueConverter CountSatisfied = new CountSatisfiedConverter();

    private sealed class EnumEqualsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return parameter is string name
                && Enum.TryParse<PermissionMode>(name, out var mode)
                && value is PermissionMode current
                && current == mode;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not true
                || parameter is not string name
                || !Enum.TryParse<PermissionMode>(name, out var mode))
            {
                return BindingOperations.DoNothing;
            }
            return mode;
        }
    }

    private sealed class NotConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is not true;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }

    private sealed class RiskLevelBrushConverter : IMultiValueConverter
    {
        private static readonly ConcurrentDictionary<Color, IBrush> Brushes = new();

        private static readonly IReadOnlyDictionary<RiskLevel, (string Key, uint Fallback)> Palette =
            new Dictionary<RiskLevel, (string, uint)>
            {
                [RiskLevel.Low] = ("AlcolerSuccess", 0xFF3FB950),
                [RiskLevel.Medium] = ("AlcolerWarning", 0xFFC9A227),
                [RiskLevel.High] = ("AlcolerError", 0xFFD13438),
                [RiskLevel.Critical] = ("AlcolerError", 0xFFD13438),
            };

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count != 2 || values[0] is not RiskLevel level || !Palette.TryGetValue(level, out var entry)
                || values[1] is not ThemeVariant theme)
            {
                return null;
            }
            var app = Application.Current;
            if (app is not null && app.TryFindResource(entry.Key, theme, out var resource))
            {
                return resource switch
                {
                    IBrush brush => brush,
                    Color color => Brushes.GetOrAdd(color, static value => new ImmutableSolidColorBrush(value)),
                    _ => Brushes.GetOrAdd(Color.FromUInt32(entry.Fallback), static value => new ImmutableSolidColorBrush(value)),
                };
            }
            return Brushes.GetOrAdd(Color.FromUInt32(entry.Fallback), static value => new ImmutableSolidColorBrush(value));
        }
    }

    private sealed class CountSatisfiedConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count == 2 && values[0] is int selected && values[1] is int required)
            {
                return selected >= required;
            }
            return false;
        }
    }
}
