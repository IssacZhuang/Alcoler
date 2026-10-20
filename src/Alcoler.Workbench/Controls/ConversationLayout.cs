using Avalonia;
using Avalonia.Controls;

namespace Alcoler.Workbench.Controls;

public sealed class ConversationLayout : Decorator
{
    public static readonly StyledProperty<bool> UseResponsiveHorizontalPaddingProperty =
        AvaloniaProperty.Register<ConversationLayout, bool>(nameof(UseResponsiveHorizontalPadding));

    public static readonly AttachedProperty<bool> ReserveStatusPanelSpaceProperty =
        AvaloniaProperty.RegisterAttached<ConversationLayout, Control, bool>("ReserveStatusPanelSpace", inherits: true);

    static ConversationLayout()
    {
        AffectsMeasure<ConversationLayout>(UseResponsiveHorizontalPaddingProperty);
        AffectsArrange<ConversationLayout>(ReserveStatusPanelSpaceProperty);
    }

    public static bool GetReserveStatusPanelSpace(Control control) => control.GetValue(ReserveStatusPanelSpaceProperty);

    public static void SetReserveStatusPanelSpace(Control control, bool value) => control.SetValue(ReserveStatusPanelSpaceProperty, value);

    public bool UseResponsiveHorizontalPadding
    {
        get => GetValue(UseResponsiveHorizontalPaddingProperty);
        set => SetValue(UseResponsiveHorizontalPaddingProperty, value);
    }

    internal static double CalculateSectionWidth(double paneWidth) => paneWidth switch
    {
        >= 1280 => Math.Min(paneWidth - 384, 1152),
        >= 864 => Math.Min(paneWidth - 96, 896),
        _ => Math.Max(0, paneWidth),
    };

    internal Thickness CalculatePadding(double paneWidth) => UseResponsiveHorizontalPadding && paneWidth >= 448
        ? new Thickness(24, Padding.Top, 24, Padding.Bottom)
        : Padding;

    protected override Size MeasureOverride(Size availableSize)
    {
        var padding = CalculatePadding(availableSize.Width);
        var sectionWidth = CalculateSectionWidth(availableSize.Width);
        Child?.Measure(new Size(
            Math.Max(0, sectionWidth - padding.Left - padding.Right),
            Math.Max(0, availableSize.Height - padding.Top - padding.Bottom)));
        var desiredSize = Child?.DesiredSize ?? default;
        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : desiredSize.Width + padding.Left + padding.Right,
            desiredSize.Height + padding.Top + padding.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var sectionWidth = CalculateSectionWidth(finalSize.Width);
        var padding = CalculatePadding(finalSize.Width);
        Child?.Arrange(new Rect(
            (finalSize.Width - sectionWidth) / 2 + padding.Left -
                (GetReserveStatusPanelSpace(this) ? 168 : 0),
            padding.Top,
            Math.Max(0, sectionWidth - padding.Left - padding.Right),
            Math.Max(0, finalSize.Height - padding.Top - padding.Bottom)));
        return finalSize;
    }
}
