using System.Collections.Specialized;
using System.ComponentModel;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Alcoler.Controls;

public sealed class ConversationPanel : VirtualizingPanel
{
    private const double Overscan = 400;
    private static readonly object NullItem = new();
    private readonly Dictionary<object, double> _heights = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, int> _indices = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, RealizedRow> _realized = [];
    private readonly HashSet<Control> _ownContainers = [];
    private readonly HashSet<int> _keepIndices = [];
    private readonly List<int> _releaseIndices = [];
    private readonly List<object> _heightKeys = [];
    private readonly List<(int Index, RealizedRow Row)> _realizedSnapshot = [];
    private readonly List<RealizedRow> _realizedValues = [];
    private readonly List<Control> _ownContainerSnapshot = [];
    private readonly List<Control> _realizedContainers = [];
    private readonly List<Control> _orderedChildren = [];
    private readonly Comparison<Control> _childComparison;
    private double[] _offsets = [0];
    private double _width = double.NaN;
    private Rect _viewport;
    private IScrollAnchorProvider? _anchors;
    private ScrollViewer? _scrollOwner;
    private int _tailIndex = -1;
    private int _scrollToIndex = -1;
    private bool _inLayout;

    public ConversationPanel()
    {
        _childComparison = CompareChildren;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    public int FirstRealizedIndex => _realized.Count == 0 ? -1 : _realized.Keys.Min();
    public int LastRealizedIndex => _realized.Count == 0 ? -1 : _realized.Keys.Max();

    protected override Size MeasureOverride(Size availableSize)
    {
        _inLayout = true;
        try
        {
            if (_width != availableSize.Width)
            {
                _width = availableSize.Width;
                _heights.Clear();
            }
            if (_offsets.Length != Items.Count + 1)
                _offsets = new double[Items.Count + 1];

            var constraint = new Size(availableSize.Width, double.PositiveInfinity);
            foreach (var row in _realized.Values)
                MeasureRow(row, constraint);

            var viewport = _viewport;
            if (_scrollOwner is { Viewport.Height: > 0 } owner && this.TranslatePoint(default, owner) is { } origin)
                viewport = new Rect(0, Math.Max(0, -origin.Y), availableSize.Width, owner.Viewport.Height);
            var start = Math.Max(0, viewport.Top - Overscan);
            var end = Math.Max(start, viewport.Bottom) + Overscan;
            _keepIndices.Clear();
            var width = 0d;
            _offsets[0] = 0;
            for (var i = 0; i < Items.Count; i++)
            {
                var key = Key(Items[i]);
                var height = _heights.GetValueOrDefault(key, EstimateHeight(Items[i]));
                var retain = i == _tailIndex || i == _scrollToIndex ||
                    (_realized.TryGetValue(i, out var existing) && existing.Container.IsKeyboardFocusWithin);
                if (retain || (height > 0 && _offsets[i] + height >= start && _offsets[i] <= end))
                {
                    var row = Realize(i);
                    height = MeasureRow(row, constraint);
                    width = Math.Max(width, row.Container.DesiredSize.Width);
                    if (height > 0 || retain)
                        _keepIndices.Add(i);
                    else
                        Release(i);
                }
                _offsets[i + 1] = _offsets[i] + height;
            }
            _releaseIndices.Clear();
            foreach (var index in _realized.Keys)
            {
                if (!_keepIndices.Contains(index))
                    _releaseIndices.Add(index);
            }
            foreach (var index in _releaseIndices)
                Release(index);

            return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : width, _offsets[^1]);
        }
        finally
        {
            _inLayout = false;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _inLayout = true;
        try
        {
            foreach (var (index, row) in _realized)
            {
                var rect = new Rect(0, _offsets[index], finalSize.Width, _offsets[index + 1] - _offsets[index]);
                row.Container.Arrange(rect);
                if (_viewport.Intersects(rect))
                    _anchors?.RegisterAnchorCandidate(row.Container);
                else
                    _anchors?.UnregisterAnchorCandidate(row.Container);
            }
            return finalSize;
        }
        finally
        {
            _inLayout = false;
        }
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in _indices.Keys.OfType<INotifyPropertyChanged>())
            item.PropertyChanged -= OnItemPropertyChanged;
        _indices.Clear();
        _tailIndex = -1;
        _scrollToIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            _indices[Key(items[i])] = i;
            if (items[i] is AssistantMessageItemViewModel)
                _tailIndex = i;
        }
        foreach (var item in _indices.Keys.OfType<INotifyPropertyChanged>())
            item.PropertyChanged += OnItemPropertyChanged;
        _heightKeys.Clear();
        foreach (var key in _heights.Keys)
            _heightKeys.Add(key);
        foreach (var key in _heightKeys)
            if (!_indices.ContainsKey(key))
                _heights.Remove(key);

        _realizedSnapshot.Clear();
        foreach (var (index, row) in _realized)
            _realizedSnapshot.Add((index, row));
        _realized.Clear();
        foreach (var (oldIndex, row) in _realizedSnapshot)
        {
            if (e.Action != NotifyCollectionChangedAction.Reset && _indices.TryGetValue(row.Key, out var index))
            {
                _realized.Add(index, row);
                if (index != oldIndex)
                    ItemContainerGenerator!.ItemContainerIndexChanged(row.Container, oldIndex, index);
            }
            else
                Release(row);
        }
        _ownContainerSnapshot.Clear();
        foreach (var container in _ownContainers)
            _ownContainerSnapshot.Add(container);
        foreach (var container in _ownContainerSnapshot)
            if (!_indices.ContainsKey(container))
            {
                _ownContainers.Remove(container);
                RemoveInternalChild(container);
            }
        OrderChildren();
        if (e.Action == NotifyCollectionChangedAction.Reset)
            _heights.Clear();
        InvalidateMeasure();
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        if (oldValue is not null)
        {
            _realizedValues.Clear();
            foreach (var row in _realized.Values)
                _realizedValues.Add(row);
            _realized.Clear();
            foreach (var row in _realizedValues)
            {
                _anchors?.UnregisterAnchorCandidate(row.Container);
                if (row.Generated)
                    oldValue.ItemContainerGenerator.ClearItemContainer(row.Container);
            }
            var logicalChildren = (IAvaloniaList<ILogical>)((ILogical)oldValue).LogicalChildren;
            _orderedChildren.Clear();
            foreach (var child in Children)
                _orderedChildren.Add(child);
            logicalChildren.RemoveAll(_orderedChildren);
            Children.Clear();
            foreach (var item in _indices.Keys.OfType<INotifyPropertyChanged>())
                item.PropertyChanged -= OnItemPropertyChanged;
            _indices.Clear();
            _heights.Clear();
            _ownContainers.Clear();
            _offsets = [0];
            _tailIndex = _scrollToIndex = -1;
        }
        base.OnItemsControlChanged(oldValue);
        if (ItemsControl is not null)
            OnItemsChanged(Items, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override Control? ContainerFromIndex(int index) =>
        _realized.TryGetValue(index, out var row) ? row.Container :
        index >= 0 && index < Items.Count && Items[index] is Control c && _ownContainers.Contains(c) ? c : null;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, row) in _realized)
            if (ReferenceEquals(row.Container, container))
                return index;
        return _ownContainers.Contains(container) && _indices.TryGetValue(container, out var ownIndex) ? ownIndex : -1;
    }

    protected override IEnumerable<Control> GetRealizedContainers()
    {
        _realizedContainers.Clear();
        foreach (var row in _realized.Values)
            _realizedContainers.Add(row.Container);
        return _realizedContainers;
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (_inLayout || index < 0 || index >= Items.Count || !IsEffectivelyVisible ||
            this.GetVisualRoot() is not Control root)
            return null;
        _scrollToIndex = index;
        try
        {
            var container = Realize(index).Container;
            InvalidateMeasure();
            root.UpdateLayout();
            container.BringIntoView();
            root.UpdateLayout();
            container.BringIntoView();
            return container;
        }
        finally
        {
            _scrollToIndex = -1;
            InvalidateMeasure();
        }
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var index = from is Control control ? IndexFromContainer(control) : -1;
        if (index < 0 && from is Visual visual)
            foreach (var ancestor in visual.GetVisualAncestors().OfType<Control>())
                if ((index = IndexFromContainer(ancestor)) >= 0)
                    break;
        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Items.Count - 1,
            NavigationDirection.Next or NavigationDirection.Down when index >= 0 => index + 1,
            NavigationDirection.Previous or NavigationDirection.Up when index >= 0 => index - 1,
            _ => -1,
        };
        if (Items.Count == 0 || (target == -1 && index < 0) ||
            direction is not (NavigationDirection.First or NavigationDirection.Last or NavigationDirection.Next or
                NavigationDirection.Down or NavigationDirection.Previous or NavigationDirection.Up))
            return null;
        if (wrap)
            target = (target + Items.Count) % Items.Count;
        return ScrollIntoView(target);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _anchors = this.FindAncestorOfType<IScrollAnchorProvider>();
        _scrollOwner = this.FindAncestorOfType<ScrollViewer>();
        if (_scrollOwner is not null)
            _scrollOwner.ScrollChanged += OnOwnerScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var row in _realized.Values)
            _anchors?.UnregisterAnchorCandidate(row.Container);
        _anchors = null;
        if (_scrollOwner is not null)
            _scrollOwner.ScrollChanged -= OnOwnerScrollChanged;
        _scrollOwner = null;
        base.OnDetachedFromVisualTree(e);
    }

    private RealizedRow Realize(int index)
    {
        if (_realized.TryGetValue(index, out var row))
            return row;
        var item = Items[index];
        var generator = ItemContainerGenerator!;
        var generated = generator.NeedsContainer(item, index, out var recycleKey);
        var container = generated ? generator.CreateContainer(item, index, recycleKey) : (Control)item!;
        row = new RealizedRow(Key(item), container, generated);
        _realized.Add(index, row);
        if (generated || _ownContainers.Add(container))
        {
            generator.PrepareItemContainer(container, item, index);
            var childIndex = 0;
            foreach (var child in Children)
            {
                if (IndexFromContainer(child) < index)
                    childIndex++;
            }
            InsertInternalChild(childIndex, container);
            generator.ItemContainerPrepared(container, item, index);
        }
        else
            container.SetCurrentValue(IsVisibleProperty, true);
        return row;
    }

    private void OrderChildren()
    {
        _orderedChildren.Clear();
        foreach (var child in Children)
            _orderedChildren.Add(child);
        _orderedChildren.Sort(_childComparison);
        for (var i = 0; i < _orderedChildren.Count; i++)
        {
            if (!ReferenceEquals(Children[i], _orderedChildren[i]))
                Children.Move(Children.IndexOf(_orderedChildren[i]), i);
        }
    }

    private int CompareChildren(Control? left, Control? right) =>
        IndexFromContainer(left!).CompareTo(IndexFromContainer(right!));

    private double MeasureRow(RealizedRow row, Size constraint)
    {
        row.Container.Measure(constraint);
        var height = row.Container.DesiredSize.Height;
        _heights[row.Key] = height;
        return height;
    }

    private void Release(int index)
    {
        var row = _realized[index];
        _realized.Remove(index);
        Release(row);
    }

    private void Release(RealizedRow row)
    {
        _anchors?.UnregisterAnchorCandidate(row.Container);
        if (row.Generated)
        {
            ItemContainerGenerator!.ClearItemContainer(row.Container);
            RemoveInternalChild(row.Container);
        }
        else
            row.Container.SetCurrentValue(IsVisibleProperty, false);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is null || !_indices.TryGetValue(sender, out var index))
            return;
        if (_realized.TryGetValue(index, out var row))
            row.Container.InvalidateMeasure();
        else if (AffectsHeight(sender, e.PropertyName))
            _heights.Remove(sender);
        else
            return;
        InvalidateMeasure();
    }

    private static bool AffectsHeight(object item, string? propertyName) => propertyName is null or "" || item switch
    {
        AssistantMessageItemViewModel => propertyName is nameof(AssistantMessageItemViewModel.MarkdownText)
            or nameof(AssistantMessageItemViewModel.ReasoningBlocks)
            or nameof(AssistantMessageItemViewModel.IsStreaming)
            or nameof(AssistantMessageItemViewModel.IsReasoningExpanded)
            or nameof(AssistantMessageItemViewModel.StreamingTail)
            or nameof(AssistantMessageItemViewModel.StreamingTailIsCode)
            or nameof(AssistantMessageItemViewModel.FinalMarkdown),
        ToolCallItemViewModel tool => propertyName is nameof(ToolCallItemViewModel.IsExpanded) or nameof(ToolCallItemViewModel.ImageMedia) ||
            tool.IsExpanded && propertyName is nameof(ToolCallItemViewModel.InputJson) or nameof(ToolCallItemViewModel.OutputText),
        _ => true,
    };

    private void OnOwnerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, _scrollOwner) && (e.OffsetDelta != default || e.ViewportDelta != default))
            InvalidateMeasure();
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var viewport = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
        if (_viewport == viewport)
            return;
        _viewport = viewport;
        InvalidateMeasure();
    }

    private double EstimateHeight(object? item) => item switch
    {
        AgentCallItemViewModel => 32,
        ToolCallItemViewModel tool => (tool.IsExpanded ? 48 : 32) + EstimateImageHeight(tool.ImageMedia.Count),
        AssistantMessageItemViewModel { IsReasoningExpanded: false, MarkdownText.Length: 0 } => 32,
        _ => 48,
    };

    private double EstimateImageHeight(int imageCount)
    {
        if (imageCount == 0)
            return 0;
        const double stripLeftMargin = 24;
        const double stripVerticalMargin = 12;
        const double thumbnailWidthWithMargin = 134;
        const double thumbnailHeightWithMargin = 100;
        var imagesPerRow = double.IsFinite(_width)
            ? Math.Max(1, Math.Floor(Math.Max(0, _width - stripLeftMargin) / thumbnailWidthWithMargin))
            : imageCount;
        return stripVerticalMargin + Math.Ceiling(imageCount / imagesPerRow) * thumbnailHeightWithMargin;
    }

    private static object Key(object? item) => item ?? NullItem;
    private sealed record RealizedRow(object Key, Control Container, bool Generated);
}
