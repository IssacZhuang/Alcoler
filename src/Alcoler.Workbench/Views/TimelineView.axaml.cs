using System.Globalization;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class TimelineView : UserControl
{
    private const double BottomStickThreshold = 48;

    public static readonly StyledProperty<string> EmptyTitleProperty =
        AvaloniaProperty.Register<TimelineView, string>(nameof(EmptyTitle), "What can I help you with?");

    public static readonly StyledProperty<string> EmptyDescriptionProperty =
        AvaloniaProperty.Register<TimelineView, string>(nameof(EmptyDescription), "Ask a question, plan a change, or describe a task.");

    public string EmptyTitle
    {
        get => GetValue(EmptyTitleProperty);
        set => SetValue(EmptyTitleProperty, value);
    }

    public string EmptyDescription
    {
        get => GetValue(EmptyDescriptionProperty);
        set => SetValue(EmptyDescriptionProperty, value);
    }

    private ConversationViewModel? _conversation;
    private bool _pendingScrollToEnd;
    private bool _awaitingLayout;
    private bool _followingTail = true;
    private bool _scrollBarInteracting;
    private bool _scrollingToTail;

    // InitializeComponent is supplied by the Avalonia name generator; a manual
    // AvaloniaXamlLoader.Load overload here would leave x:Name fields unassigned.
    public TimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        TimelineScroll.ScrollChanged += OnTimelineScrollChanged;
        TimelineScroll.PropertyChanged += OnScrollPropertyChanged;
        TimelineScroll.AddHandler(PointerPressedEvent, OnTimelinePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TimelineScroll.AddHandler(PointerReleasedEvent, OnTimelinePointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        TimelineScroll.AddHandler(Thumb.DragStartedEvent, OnTimelineThumbDragStarted, RoutingStrategies.Bubble, handledEventsToo: true);
        TimelineScroll.AddHandler(Thumb.DragCompletedEvent, OnTimelineThumbDragCompleted, RoutingStrategies.Bubble, handledEventsToo: true);
        TimelineScroll.AddHandler(PointerWheelChangedEvent, OnTimelinePointerWheelChanged, RoutingStrategies.Tunnel);
        TimelineScroll.AddHandler(KeyDownEvent, OnTimelineKeyDown, RoutingStrategies.Tunnel);
        TimelineScroll.AddHandler(Gestures.ScrollGestureEvent, OnTimelineScrollGesture, RoutingStrategies.Tunnel);
        TimelineLayout.SizeChanged += (_, _) => TimelineItems.Classes.Set("wideUserMessages", TimelineLayout.Bounds.Width >= 624);
    }

    /// <summary>Number of append notifications that caused a scroll to the bottom; test hook.</summary>
    internal int AutoScrollCount { get; private set; }

    internal ScrollViewer Scroller => TimelineScroll;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_conversation is not null)
        {
            _conversation.TimelineAppended -= OnTimelineAppended;
        }
        _conversation = DataContext as ConversationViewModel;
        _pendingScrollToEnd = false;
        _scrollBarInteracting = false;
        _followingTail = true;
        if (_conversation is null)
        {
            return;
        }
        _conversation.TimelineAppended += OnTimelineAppended;
        if (_conversation.Items.Count > 0)
        {
            RequestScrollToEndAfterLayout();
        }
    }

    private void OnTimelineAppended()
    {
        if (!_followingTail || _scrollBarInteracting || !IsNearBottom())
        {
            return;
        }
        AutoScrollCount++;
        RequestScrollToEndAfterLayout();
    }

    private void OnTimelineScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollBarInteracting || !ReferenceEquals(e.Source, TimelineScroll))
        {
            return;
        }
        if (e.ExtentDelta != default || e.ViewportDelta != default)
        {
            if (_followingTail)
            {
                _scrollingToTail = true;
                try
                {
                    TimelineScroll.ScrollToEnd();
                }
                finally
                {
                    _scrollingToTail = false;
                }
            }
        }
        else if (e.OffsetDelta != default)
        {
            _followingTail = e.OffsetDelta.Y > 0 && IsNearBottom();
            if (!_followingTail)
                _pendingScrollToEnd = false;
        }
    }

    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty && !_scrollingToTail && !_scrollBarInteracting &&
            e.GetNewValue<Vector>().Y < e.GetOldValue<Vector>().Y && !IsNearBottom())
        {
            _followingTail = false;
            _pendingScrollToEnd = false;
        }
    }

    private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.GetVisualAncestors().OfType<ScrollBar>()
                .Any(bar => ReferenceEquals(bar.TemplatedParent, TimelineScroll)) &&
            e.GetCurrentPoint(TimelineScroll).Properties.IsLeftButtonPressed)
        {
            _scrollBarInteracting = true;
            PauseFollowing();
        }
    }

    private void OnTimelinePointerReleased(object? sender, PointerReleasedEventArgs e) => EndScrollBarInteraction();

    private void OnTimelineThumbDragStarted(object? sender, VectorEventArgs e)
    {
        if (e.Source is Visual source && source.GetVisualAncestors().OfType<ScrollBar>()
                .Any(bar => ReferenceEquals(bar.TemplatedParent, TimelineScroll)))
        {
            _scrollBarInteracting = true;
            PauseFollowing();
        }
    }

    private void OnTimelineThumbDragCompleted(object? sender, VectorEventArgs e) => EndScrollBarInteraction();

    private void EndScrollBarInteraction()
    {
        if (!_scrollBarInteracting)
            return;
        _scrollBarInteracting = false;
        _followingTail = IsNearBottom();
    }

    private void OnTimelinePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0)
            PauseFollowing();
    }

    private void OnTimelineKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.PageUp or Key.Home)
            PauseFollowing();
    }

    private void OnTimelineScrollGesture(object? sender, ScrollGestureEventArgs e)
    {
        if (e.Delta.Y < 0)
            PauseFollowing();
    }

    private void PauseFollowing()
    {
        _followingTail = false;
        _pendingScrollToEnd = false;
        // ScrollToEnd retains an infinite base value that re-coerces to the new tail as content grows.
        TimelineScroll.SetCurrentValue(ScrollViewer.OffsetProperty, TimelineScroll.Offset);
    }

    private bool IsNearBottom() =>
        TimelineScroll.Extent.Height - TimelineScroll.Viewport.Height - TimelineScroll.Offset.Y
            <= BottomStickThreshold;

    private void RequestScrollToEndAfterLayout()
    {
        _pendingScrollToEnd = true;
        if (_awaitingLayout)
        {
            return;
        }
        _awaitingLayout = true;
        TimelineScroll.LayoutUpdated += OnLayoutUpdatedForScroll;
    }

    private void OnLayoutUpdatedForScroll(object? sender, EventArgs e)
    {
        TimelineScroll.LayoutUpdated -= OnLayoutUpdatedForScroll;
        _awaitingLayout = false;
        if (!_pendingScrollToEnd)
        {
            return;
        }
        _pendingScrollToEnd = false;
        if (_followingTail && !_scrollBarInteracting)
        {
            _scrollingToTail = true;
            try
            {
                if (TimelineItems.ItemCount > 0)
                    TimelineItems.ScrollIntoView(TimelineItems.ItemCount - 1);
                TimelineScroll.ScrollToEnd();
            }
            finally
            {
                _scrollingToTail = false;
            }
        }
    }

    private void OnOpenToolOutputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolCallItemViewModel item }
            && TopLevel.GetTopLevel(this) is MainWindow { DataContext: WorkbenchViewModel workbench })
        {
            workbench.OpenTextDocument($"tool-output:{item.RowId}", $"{item.ToolName} output", item.OutputText);
            e.Handled = true;
        }
    }

    private void OnToolRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolCallItemViewModel item })
        {
            item.IsExpanded = !item.IsExpanded;
            e.Handled = true;
        }
    }
}

public sealed class ConversationIconConverter : IValueConverter
{
    private readonly Dictionary<string, Geometry> _geometries = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string { Length: > 0 } path)
        {
            return null;
        }
        if (!_geometries.TryGetValue(path, out var geometry))
        {
            geometry = Geometry.Parse(path);
            _geometries.Add(path, geometry);
        }
        return geometry;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
