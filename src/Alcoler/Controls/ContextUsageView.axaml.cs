using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Controls;

public partial class ContextUsageView : UserControl
{
    public static readonly StyledProperty<object?> QuotaContentProperty =
        AvaloniaProperty.Register<ContextUsageView, object?>(nameof(QuotaContent));

    private TopLevel? _owner;

    public ContextUsageView()
    {
        InitializeComponent(true);
        ContextTrigger.PointerEntered += (_, _) => ShowPopover();
        ContextTrigger.PointerExited += (_, _) => QueueClose();
        ContextTrigger.GotFocus += (_, _) => ShowPopover();
        ContextTrigger.LostFocus += (_, _) => QueueClose();
        ContextTrigger.Click += (_, _) => ShowPopover();
        ContextHoverBridge.PointerExited += (_, _) => QueueClose();
        ContextPopover.LostFocus += (_, _) => QueueClose();
        ContextPopover.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ContextPopup.Opened += (_, _) => PopoverOpened?.Invoke(this, EventArgs.Empty);
        ContextPopup.Closed += (_, _) => PopoverClosed?.Invoke(this, EventArgs.Empty);
        DataContextChanged += (_, _) =>
        {
            ContextPopover.DataContext = DataContext;
            ContextRingFill.Data = DataContext is ContextUsageViewModel usage ? Geometry.Parse(usage.RingPath) : null;
            if (DataContext is not ContextUsageViewModel { IsVisible: true })
                ContextPopup.IsOpen = false;
        };
    }

    public object? QuotaContent
    {
        get => GetValue(QuotaContentProperty);
        set => SetValue(QuotaContentProperty, value);
    }

    public event EventHandler? PopoverOpened;
    public event EventHandler? PopoverClosed;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _owner = TopLevel.GetTopLevel(this);
        if (_owner is not null)
        {
            _owner.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _owner.AddHandler(PointerPressedEvent, OnOwnerPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (_owner is Window window)
                window.Deactivated += OnOwnerDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ContextPopup.IsOpen = false;
        if (_owner is not null)
        {
            _owner.RemoveHandler(KeyDownEvent, OnKeyDown);
            _owner.RemoveHandler(PointerPressedEvent, OnOwnerPointerPressed);
            if (_owner is Window window)
                window.Deactivated -= OnOwnerDeactivated;
            _owner = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ContextPopup.IsOpen)
        {
            ContextPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnOwnerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ContextPopup.IsOpen && e.Source is Visual source
            && source != ContextTrigger && !ContextTrigger.IsVisualAncestorOf(source)
            && source != ContextHoverBridge && !ContextHoverBridge.IsVisualAncestorOf(source))
            ContextPopup.IsOpen = false;
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e) => ContextPopup.IsOpen = false;

    private void ShowPopover()
    {
        if (DataContext is ContextUsageViewModel { IsVisible: true } && this.GetVisualRoot() is not null)
            ContextPopup.IsOpen = true;
    }

    private void QueueClose() => Dispatcher.UIThread.Post(() =>
    {
        if (!ContextTrigger.IsPointerOver && !ContextHoverBridge.IsPointerOver && !ContextTrigger.IsFocused && !ContextPopover.IsKeyboardFocusWithin)
            ContextPopup.IsOpen = false;
    }, DispatcherPriority.Background);
}

public sealed class ContextUsageProgress : Control
{
    public static readonly StyledProperty<ContextUsageViewModel?> UsageProperty =
        AvaloniaProperty.Register<ContextUsageProgress, ContextUsageViewModel?>(nameof(Usage));

    static ContextUsageProgress() => AffectsRender<ContextUsageProgress>(UsageProperty);

    public ContextUsageProgress() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public ContextUsageViewModel? Usage
    {
        get => GetValue(UsageProperty);
        set => SetValue(UsageProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var surface = Resource("AlcolerSurface", Brushes.Transparent);
        var color = Resource("AlcolerInfo", Brushes.DodgerBlue);
        var radius = (float)(bounds.Height / 2);
        context.FillRectangle(surface, bounds, radius);
        if (Usage is not { Fraction: > 0 } usage)
            return;
        var filledWidth = Math.Min(bounds.Width, Math.Max(8, bounds.Width * usage.Fraction));
        using var clip = context.PushClip(new RoundedRect(new Rect(0, 0, filledWidth, bounds.Height), radius));
        if (!usage.HasBreakdown)
        {
            context.FillRectangle(color, new Rect(0, 0, filledWidth, bounds.Height));
            return;
        }
        var offset = 0d;
        foreach (var segment in usage.Segments)
        {
            var width = filledWidth * segment.Fraction;
            context.FillRectangle(surface, new Rect(offset, 0, width, bounds.Height));
            using (context.PushOpacity(segment.ToneOpacity))
                context.FillRectangle(color, new Rect(offset, 0, width, bounds.Height));
            offset += width;
        }
    }

    private IBrush Resource(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
