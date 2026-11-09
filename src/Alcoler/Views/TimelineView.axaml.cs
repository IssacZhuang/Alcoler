using System.Globalization;
using Alcoler.Controls;
using Alcoler.Localization;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Alcoler.Views;

public partial class TimelineView : UserControl
{
    private const double BottomStickThreshold = 48;

    public static readonly StyledProperty<string> EmptyTitleProperty =
        AvaloniaProperty.Register<TimelineView, string>(nameof(EmptyTitle), Loc.Get("timeline_empty_title"));

    public static readonly StyledProperty<string> EmptyDescriptionProperty =
        AvaloniaProperty.Register<TimelineView, string>(nameof(EmptyDescription), Loc.Get("timeline_empty_description"));

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
    private bool _followingTail = true;
    private bool _scrollBarInteracting;
    private bool _scrollingToTail;
    private bool _restoringAnchor;
    private TimelineScrollAnchor? _pendingAnchor;
    private int _anchorAttempts;

    private ConversationPanel? Panel => TimelineItems.ItemsPanelRoot as ConversationPanel;

    // InitializeComponent is supplied by the Avalonia name generator; a manual
    // AvaloniaXamlLoader.Load overload here would leave x:Name fields unassigned.
    public TimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        TimelineScroll.ScrollChanged += OnTimelineScrollChanged;
        TimelineScroll.PropertyChanged += OnScrollPropertyChanged;
        TimelineScroll.LayoutUpdated += OnLayoutUpdatedForScroll;
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

    /// <summary>Releases realized rows when the hosting surface is hidden so the
    /// visual tree is not retained while invisible; cached heights restore it on show.</summary>
    public void UnloadRealizedContent()
    {
        if (!IsEffectivelyVisible)
            Panel?.UnloadRealizedRows();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnsubscribeConversation();
        ClearPendingAnchor();
        _conversation = DataContext as ConversationViewModel;
        _pendingScrollToEnd = false;
        _scrollBarInteracting = false;
        _followingTail = _conversation?.ReadingState.FollowLatest ?? true;
        if (_conversation is null)
        {
            UpdateJumpToLatest();
            return;
        }
        _conversation.TimelineAppended += OnTimelineAppended;
        _conversation.TimelineChanging += PreserveReadingPosition;
        _conversation.ReadingStateCapturing += CaptureReadingPosition;
        if (_followingTail)
        {
            if (_conversation.Items.Count > 0)
                RequestScrollToEndAfterLayout();
        }
        else
        {
            TimelineScroll.SetCurrentValue(ScrollViewer.OffsetProperty, TimelineScroll.Offset);
            if (_conversation.ReadingState.Anchor is { } anchor)
                RequestAnchorRestore(anchor);
        }
        UpdateJumpToLatest();
    }

    private void UnsubscribeConversation()
    {
        if (_conversation is null)
            return;
        _conversation.TimelineAppended -= OnTimelineAppended;
        _conversation.TimelineChanging -= PreserveReadingPosition;
        _conversation.ReadingStateCapturing -= CaptureReadingPosition;
        _conversation = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_conversation is null)
            OnDataContextChanged(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CaptureReadingPosition();
        UnsubscribeConversation();
        ClearPendingAnchor();
        _pendingScrollToEnd = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void CaptureReadingPosition()
    {
        if (_conversation is null || _restoringAnchor)
            return;
        var anchor = _pendingAnchor ?? Panel?.CaptureReadingAnchor() ?? _conversation.ReadingState.Anchor;
        _conversation.SaveReadingPosition(anchor, _followingTail);
    }

    private void PreserveReadingPosition()
    {
        if (_followingTail || _scrollBarInteracting || _pendingAnchor is not null)
            return;
        if (Panel?.CaptureReadingAnchor() is { } anchor)
            RequestAnchorRestore(anchor);
    }

    private void RequestAnchorRestore(TimelineScrollAnchor anchor)
    {
        _pendingAnchor = anchor;
        _anchorAttempts = 0;
        Panel?.SetReadingAnchor(anchor.RowId);
        TimelineScroll.InvalidateMeasure();
    }

    private void ClearPendingAnchor()
    {
        _pendingAnchor = null;
        Panel?.SetReadingAnchor(null);
    }

    private void OnTimelineAppended()
    {
        if (!_followingTail || _scrollBarInteracting || !IsNearBottom())
        {
            UpdateJumpToLatest();
            return;
        }
        AutoScrollCount++;
        RequestScrollToEndAfterLayout();
        UpdateJumpToLatest();
    }

    private void OnTimelineScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollBarInteracting || _restoringAnchor || _pendingAnchor is not null ||
            !ReferenceEquals(e.Source, TimelineScroll))
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
        UpdateJumpToLatest();
        CaptureReadingPosition();
    }

    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty && !_scrollingToTail && !_restoringAnchor &&
            _pendingAnchor is null && !_scrollBarInteracting &&
            e.GetNewValue<Vector>().Y < e.GetOldValue<Vector>().Y && !IsNearBottom())
        {
            _followingTail = false;
            _pendingScrollToEnd = false;
        }
        UpdateJumpToLatest();
    }

    private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ConversationMarkdownFragment.ClearActiveSelection();
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
        UpdateJumpToLatest();
        CaptureReadingPosition();
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
        ClearPendingAnchor();
        // ScrollToEnd retains an infinite base value that re-coerces to the new tail as content grows.
        TimelineScroll.SetCurrentValue(ScrollViewer.OffsetProperty, TimelineScroll.Offset);
        UpdateJumpToLatest();
        CaptureReadingPosition();
    }

    private bool IsNearBottom() =>
        TimelineScroll.Extent.Height - TimelineScroll.Viewport.Height - TimelineScroll.Offset.Y
            <= BottomStickThreshold;

    private void RequestScrollToEndAfterLayout()
    {
        _pendingScrollToEnd = true;
        TimelineScroll.InvalidateMeasure();
    }

    private void OnLayoutUpdatedForScroll(object? sender, EventArgs e)
    {
        if (_conversation is null || _scrollingToTail || _restoringAnchor || _scrollBarInteracting)
            return;
        if (_pendingAnchor is { } anchor && Panel is { } panel)
        {
            var item = _conversation.Items.FirstOrDefault(item => item.RowId == anchor.RowId);
            if (item is null || item.IsFolded || _anchorAttempts++ >= 12)
            {
                ClearPendingAnchor();
            }
            else
            {
                panel.SetReadingAnchor(anchor.RowId);
                if (!panel.TryGetReadingAnchorPosition(out var position))
                {
                    TimelineScroll.InvalidateMeasure();
                    return;
                }
                var difference = position - anchor.RelativeOffset;
                if (Math.Abs(difference) > 0.5)
                {
                    _restoringAnchor = true;
                    try
                    {
                        var offset = TimelineScroll.Offset;
                        var target = Math.Clamp(offset.Y + difference, 0,
                            Math.Max(0, TimelineScroll.Extent.Height - TimelineScroll.Viewport.Height));
                        TimelineScroll.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(offset.X, target));
                        TimelineScroll.InvalidateMeasure();
                    }
                    finally
                    {
                        _restoringAnchor = false;
                    }
                    return;
                }
                ClearPendingAnchor();
            }
        }
        if (_pendingScrollToEnd)
        {
            _pendingScrollToEnd = false;
            if (_followingTail)
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
        UpdateJumpToLatest();
        CaptureReadingPosition();
    }

    private void UpdateJumpToLatest()
    {
        JumpToLatestButton.IsVisible = _conversation is not null && !IsNearBottom();
    }

    private void OnJumpToLatestClick(object? sender, RoutedEventArgs e)
    {
        _followingTail = true;
        _pendingScrollToEnd = false;
        ClearPendingAnchor();
        TimelineScroll.SetCurrentValue(ScrollViewer.OffsetProperty, TimelineScroll.Offset);
        RequestScrollToEndAfterLayout();
        UpdateJumpToLatest();
        e.Handled = true;
    }

    private void OnOpenPlanClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolCallItemViewModel item }
            && TopLevel.GetTopLevel(this)?.DataContext is WorkbenchViewModel workbench)
        {
            workbench.OpenToolPlan(item);
            e.Handled = true;
        }
    }

    private void OnOpenToolOutputClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolCallItemViewModel item }
            && TopLevel.GetTopLevel(this) is MainWindow { DataContext: WorkbenchViewModel workbench })
        {
            workbench.OpenTextDocument($"tool-output:{item.RowId}", Loc.Get("timeline_tool_output_title", item.ToolName), item.OutputText);
            e.Handled = true;
        }
    }

    private async void OnAgentRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgentCallItemViewModel { Activity: { } activity } }
            && TopLevel.GetTopLevel(this) is MainWindow { DataContext: WorkbenchViewModel workbench })
        {
            await workbench.OpenStatusActivityAsync(activity);
            e.Handled = true;
        }
    }

    private void BeginReadingInteraction()
    {
        PauseFollowing();
        PreserveReadingPosition();
    }

    private void OnReasoningToggleClick(object? sender, RoutedEventArgs e) => BeginReadingInteraction();

    private void OnToolRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolCallItemViewModel item })
        {
            BeginReadingInteraction();
            item.IsExpanded = !item.IsExpanded;
            e.Handled = true;
        }
    }

    private void OnTurnSummaryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: UserMessageItemViewModel item } && _conversation is not null)
        {
            BeginReadingInteraction();
            _conversation.ToggleTurnSummary(item);
            e.Handled = true;
        }
    }

    private void OnFileSummaryHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileChangeSummaryItemViewModel item })
        {
            BeginReadingInteraction();
            item.IsExpanded = !item.IsExpanded;
            e.Handled = true;
        }
    }

    private async void OnOpenChangedFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChangedFileItemViewModel file })
        {
            return;
        }
        e.Handled = true;
        if (TopLevel.GetTopLevel(this) is not MainWindow { DataContext: WorkbenchViewModel workbench })
        {
            return;
        }
        try
        {
            await workbench.OpenFileDocumentAsync(file.Path);
        }
        catch (Exception exception)
        {
            if (_conversation is not null)
            {
                _conversation.Banner = Loc.Get("timeline_open_failed", file.FileName, exception.Message);
            }
        }
    }

    private void OnOpenChangedFileMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChangedFileItemViewModel file } button)
        {
            return;
        }
        e.Handled = true;
        if (TopLevel.GetTopLevel(this) is not MainWindow { DataContext: WorkbenchViewModel workbench })
        {
            return;
        }

        var workspaceRoot = _conversation?.WorkspaceRoot;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var editor in workbench.ExternalEditors)
        {
            var item = new MenuItem { Header = editor.Name, MinWidth = 160 };
            item.Click += (_, _) =>
            {
                flyout.Hide();
                workbench.OpenFileExternally(file.Path, editor, workspaceRoot);
            };
            flyout.Items.Add(item);
        }
        if (flyout.Items.Count > 0)
        {
            flyout.Items.Add(new Separator());
        }

        var defaultItem = new MenuItem { Header = Loc.Get("timeline_open_with_default"), MinWidth = 160 };
        defaultItem.Click += (_, _) =>
        {
            flyout.Hide();
            workbench.OpenFileWithDefaultApp(file.Path, workspaceRoot);
        };
        flyout.Items.Add(defaultItem);
        flyout.ShowAt(button);
    }

    private async void OnCopyUserMessageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UserMessageItemViewModel item })
        {
            return;
        }
        e.Handled = true;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }
        try
        {
            await clipboard.SetTextAsync(item.Text);
        }
        catch (Exception exception)
        {
            if (_conversation is not null)
            {
                _conversation.Banner = Loc.Get("timeline_copy_failed", exception.Message);
            }
        }
    }

    private async void OnForkAssistantMessageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AssistantMessageItemViewModel item } || _conversation is null)
        {
            return;
        }
        e.Handled = true;
        await _conversation.ForkFromMessageAsync(item);
    }

    private async void OnRewindUserMessageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: UserMessageItemViewModel item } || _conversation is null)
        {
            return;
        }
        e.Handled = true;
        await _conversation.RewindToMessageAsync(item);
        if (TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.FocusComposer();
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
