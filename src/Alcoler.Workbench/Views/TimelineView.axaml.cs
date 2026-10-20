using System.Globalization;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Alcoler.Workbench.Views;

public partial class TimelineView : UserControl
{
    private const double BottomStickThreshold = 80;

    private ConversationViewModel? _conversation;
    private bool _pendingScrollToEnd;
    private bool _awaitingLayout;

    // InitializeComponent is supplied by the Avalonia name generator; a manual
    // AvaloniaXamlLoader.Load overload here would leave x:Name fields unassigned.
    public TimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        TimelineScroll.ScrollChanged += OnTimelineScrollChanged;
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
        // The near-bottom check runs against the pre-append extent: only users already
        // reading the tail of the conversation get pinned to it.
        if (!IsNearBottom())
        {
            return;
        }
        AutoScrollCount++;
        RequestScrollToEndAfterLayout();
    }

    private void OnTimelineScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Streaming upserts grow the extent without appending rows; keep pinning while the
        // user stays near the bottom. Offset-only changes must not re-trigger (feedback loop).
        if (e.ExtentDelta == default || !IsNearBottom())
        {
            return;
        }
        TimelineScroll.ScrollToEnd();
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
        TimelineScroll.ScrollToEnd();
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
