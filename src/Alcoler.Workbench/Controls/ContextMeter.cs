using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Alcoler.Workbench.Controls;

/// <summary>
/// Slim context-usage bar. Fill color switches at 50/80 percent; palette keys resolve at
/// render time so the meter stays legible even when theme resources are not yet defined.
/// </summary>
public sealed class ContextMeter : Control
{
    private const double BarHeight = 4;

    public static readonly StyledProperty<int> PercentProperty =
        AvaloniaProperty.Register<ContextMeter, int>(nameof(Percent));

    static ContextMeter() => AffectsRender<ContextMeter>(PercentProperty);

    public int Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 60;
        return new Size(Math.Max(width, 24), BarHeight);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= 0)
        {
            return;
        }

        var percent = Math.Clamp(Percent, 0, 100);
        var fill = percent switch
        {
            >= 80 => ResolveBrush("AlcolerError", 0xFFD13438),
            >= 50 => ResolveBrush("AlcolerWarning", 0xFFC9A227),
            _ => ResolveBrush("AlcolerSuccess", 0xFF3FB950),
        };
        var radius = (float)(BarHeight / 2);
        context.FillRectangle(ResolveBrush("AlcolerBorder", 0xFF3A3A3C), new Rect(0, 0, width, BarHeight), radius);
        if (percent > 0)
        {
            var fillWidth = Math.Max(BarHeight, width * percent / 100.0);
            context.FillRectangle(fill, new Rect(0, 0, fillWidth, BarHeight), radius);
        }
    }

    private IBrush ResolveBrush(string key, uint fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }
        return new ImmutableSolidColorBrush(fallback);
    }
}
