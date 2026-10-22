using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Alcoler.Controls;

public sealed class Icon : Control
{
    private const double ViewportSize = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Icon, IBrush?>(nameof(Stroke), Brushes.Black);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Icon, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.5,
            validate: value => double.IsFinite(value) && value >= 0);

    static Icon() => AffectsRender<Icon>(DataProperty, StrokeProperty, FillProperty, StrokeThicknessProperty);

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(ViewportSize, ViewportSize);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data)
        {
            return;
        }

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (!double.IsFinite(size) || size <= 0)
        {
            return;
        }

        // Geometry and stroke share Lucide's fixed viewport; geometry bounds must not affect scale.
        var scale = size / ViewportSize;
        var transform = new Matrix(scale, 0, 0, scale, (Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        var pen = Stroke is { } stroke && StrokeThickness > 0
            ? new Pen(stroke, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : null;
        using (context.PushTransform(transform))
        {
            context.DrawGeometry(Fill, pen, data);
        }
    }
}
