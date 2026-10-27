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
    // Lowest row index whose trailing offset is stale; int.MaxValue when all
    // offsets computed by the last measure pass are still valid.
    private int _firstDirtyOffset;
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
                _firstDirtyOffset = 0;
            }
            if (_offsets.Length != Items.Count + 1)
            {
                _offsets = new double[Items.Count + 1];
                _firstDirtyOffset = 0;
            }

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
            var firstDirty = Math.Min(_firstDirtyOffset, Items.Count);
            if (firstDirty > 0)
                firstDirty = ReconcileCleanPrefix(start, end, firstDirty, constraint, ref width);
            for (var i = firstDirty; i < Items.Count; i++)
            {
                ProcessMeasure(i, start, end, constraint, ref width);
            }
            _releaseIndices.Clear();
            foreach (var index in _realized.Keys)
            {
                if (!_keepIndices.Contains(index))
                    _releaseIndices.Add(index);
            }
            foreach (var index in _releaseIndices)
                Release(index);

            _firstDirtyOffset = int.MaxValue;
            return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : width, _offsets[^1]);
        }
        finally
        {
            _inLayout = false;
        }
    }

    /// <summary>
    /// Reconciles realization for rows before <paramref name="firstDirty"/>, whose offsets are
    /// unchanged since the previous pass. Only rows intersecting the viewport and retained rows
    /// need work; every other clean-prefix row resolves exactly as the full walk would leave it.
    /// Returns the possibly lowered dirty index, because a measured height change inside the
    /// prefix dirties every offset after that row.
    /// </summary>
    private int ReconcileCleanPrefix(double start, double end, int firstDirty, Size constraint, ref double width)
    {
        var windowStart = OffsetLowerBound(1, firstDirty + 1, start) - 1;
        var windowEnd = OffsetUpperBound(0, firstDirty, end);
        for (var i = Math.Max(0, windowStart); i < windowEnd && i < firstDirty; i++)
        {
            ProcessMeasure(i, start, end, constraint, ref width);
        }
        if (_tailIndex >= 0 && _tailIndex < firstDirty)
            ProcessMeasure(_tailIndex, start, end, constraint, ref width);
        if (_scrollToIndex >= 0 && _scrollToIndex < firstDirty)
            ProcessMeasure(_scrollToIndex, start, end, constraint, ref width);
        _realizedSnapshot.Clear();
        foreach (var pair in _realized)
        {
            if (pair.Key < firstDirty && pair.Value.Container.IsKeyboardFocusWithin)
                _realizedSnapshot.Add((pair.Key, pair.Value));
        }
        foreach (var (index, _) in _realizedSnapshot)
            ProcessMeasure(index, start, end, constraint, ref width);
        return Math.Min(_firstDirtyOffset, firstDirty);
    }

    private int OffsetLowerBound(int from, int to, double value)
    {
        while (from < to)
        {
            var mid = from + (to - from) / 2;
            if (_offsets[mid] < value)
                from = mid + 1;
            else
                to = mid;
        }
        return from;
    }

    private int OffsetUpperBound(int from, int to, double value)
    {
        while (from < to)
        {
            var mid = from + (to - from) / 2;
            if (_offsets[mid] <= value)
                from = mid + 1;
            else
                to = mid;
        }
        return from;
    }

    private void ProcessMeasure(int i, double start, double end, Size constraint, ref double width)
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
        if (TryApplyIncrementalItemsChange(items, e))
        {
            InvalidateMeasure();
            return;
        }
        _firstDirtyOffset = 0;
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

    /// <summary>
    /// Applies a single-item Add/Remove/Replace/Move to the panel state without the full rebuild,
    /// producing exactly the indices, subscriptions, cached heights, realized rows and tail the
    /// rebuild path would produce. Returns false for anything else (multi-item batches, Reset,
    /// or args that fail the consistency checks) so the rebuild stays authoritative.
    /// </summary>
    private bool TryApplyIncrementalItemsChange(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is [var added]:
            {
                var index = e.NewStartingIndex;
                if (index < 0 || index >= items.Count || items.Count != _indices.Count + 1 ||
                    !ReferenceEquals(items[index], added) || _indices.ContainsKey(Key(added)))
                    return false;
                for (var i = items.Count - 1; i > index; i--)
                    _indices[Key(items[i])] = i;
                _indices[Key(added)] = index;
                if (added is INotifyPropertyChanged notify)
                    notify.PropertyChanged += OnItemPropertyChanged;
                var shiftedTail = _tailIndex >= index ? _tailIndex + 1 : _tailIndex;
                _tailIndex = added is AssistantMessageItemViewModel ? Math.Max(shiftedTail, index) : shiftedTail;
                _scrollToIndex = -1;
                ShiftRealizedIndices(index, 1);
                MarkOffsetsDirty(index);
                OrderChildren();
                return true;
            }
            case NotifyCollectionChangedAction.Remove when e.OldItems is [var removed]:
            {
                var index = e.OldStartingIndex;
                var key = Key(removed);
                if (index < 0 || items.Count != _indices.Count - 1 ||
                    !_indices.TryGetValue(key, out var at) || at != index)
                    return false;
                _indices.Remove(key);
                for (var i = index; i < items.Count; i++)
                    _indices[Key(items[i])] = i;
                if (removed is INotifyPropertyChanged notify)
                    notify.PropertyChanged -= OnItemPropertyChanged;
                _tailIndex = index < _tailIndex ? _tailIndex - 1 : index == _tailIndex ? FindTailIndex(items) : _tailIndex;
                _scrollToIndex = -1;
                _heights.Remove(key);
                if (_realized.Remove(index, out var row))
                    Release(row);
                ShiftRealizedIndices(index + 1, -1);
                if (removed is Control ownContainer && _ownContainers.Remove(ownContainer))
                    RemoveInternalChild(ownContainer);
                MarkOffsetsDirty(index);
                OrderChildren();
                return true;
            }
            case NotifyCollectionChangedAction.Replace when e.OldItems is [var oldItem] && e.NewItems is [var newItem]:
            {
                var index = e.OldStartingIndex;
                var oldKey = Key(oldItem);
                var newKey = Key(newItem);
                if (index < 0 || e.NewStartingIndex != index || index >= items.Count || items.Count != _indices.Count ||
                    !_indices.TryGetValue(oldKey, out var at) || at != index || !ReferenceEquals(items[index], newItem))
                    return false;
                _indices.Remove(oldKey);
                _indices[newKey] = index;
                if (oldItem is INotifyPropertyChanged oldNotify)
                    oldNotify.PropertyChanged -= OnItemPropertyChanged;
                if (newItem is INotifyPropertyChanged newNotify)
                    newNotify.PropertyChanged += OnItemPropertyChanged;
                _tailIndex = FindTailIndex(items);
                _scrollToIndex = -1;
                if (!ReferenceEquals(oldKey, newKey))
                {
                    _heights.Remove(oldKey);
                    if (_realized.Remove(index, out var row))
                        Release(row);
                    if (oldItem is Control ownContainer && _ownContainers.Remove(ownContainer))
                        RemoveInternalChild(ownContainer);
                }
                MarkOffsetsDirty(index);
                OrderChildren();
                return true;
            }
            case NotifyCollectionChangedAction.Move when e.OldItems is [var moved] && e.NewItems?.Count == 1:
            {
                var oldIndex = e.OldStartingIndex;
                var newIndex = e.NewStartingIndex;
                var key = Key(moved);
                if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex ||
                    oldIndex >= items.Count || newIndex >= items.Count || items.Count != _indices.Count ||
                    !_indices.TryGetValue(key, out var at) || at != oldIndex ||
                    !ReferenceEquals(items[newIndex], moved))
                    return false;
                if (oldIndex < newIndex)
                {
                    for (var i = oldIndex; i < newIndex; i++)
                        _indices[Key(items[i])] = i;
                }
                else
                {
                    for (var i = newIndex + 1; i <= oldIndex; i++)
                        _indices[Key(items[i])] = i;
                }
                _indices[key] = newIndex;
                _tailIndex = FindTailIndex(items);
                _scrollToIndex = -1;
                _realizedSnapshot.Clear();
                foreach (var pair in _realized)
                {
                    if (pair.Key >= Math.Min(oldIndex, newIndex) && pair.Key <= Math.Max(oldIndex, newIndex))
                        _realizedSnapshot.Add((pair.Key, pair.Value));
                }
                foreach (var (index, _) in _realizedSnapshot)
                    _realized.Remove(index);
                foreach (var (index, row) in _realizedSnapshot)
                {
                    var target = index == oldIndex ? newIndex : index + (oldIndex < newIndex ? -1 : 1);
                    _realized.Add(target, row);
                    if (target != index)
                        ItemContainerGenerator!.ItemContainerIndexChanged(row.Container, index, target);
                }
                MarkOffsetsDirty(Math.Min(oldIndex, newIndex));
                OrderChildren();
                return true;
            }
            default:
                return false;
        }
    }

    private static int FindTailIndex(IReadOnlyList<object?> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i] is AssistantMessageItemViewModel)
                return i;
        return -1;
    }

    private void ShiftRealizedIndices(int fromIndex, int delta)
    {
        _realizedSnapshot.Clear();
        foreach (var pair in _realized)
        {
            if (pair.Key >= fromIndex)
                _realizedSnapshot.Add((pair.Key, pair.Value));
        }
        foreach (var (oldIndex, _) in _realizedSnapshot)
            _realized.Remove(oldIndex);
        foreach (var (oldIndex, row) in _realizedSnapshot)
        {
            var newIndex = oldIndex + delta;
            _realized.Add(newIndex, row);
            ItemContainerGenerator!.ItemContainerIndexChanged(row.Container, oldIndex, newIndex);
        }
    }

    private void MarkOffsetsDirty(int index)
    {
        if (_offsets.Length != Items.Count + 1)
        {
            var resized = new double[Items.Count + 1];
            Array.Copy(_offsets, resized, Math.Min(_offsets.Length, index + 1));
            _offsets = resized;
        }
        if (index < _firstDirtyOffset)
            _firstDirtyOffset = index;
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
            _firstDirtyOffset = 0;
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
        if (!_heights.TryGetValue(row.Key, out var previous) || previous != height)
        {
            // A changed row height shifts every offset after that row's position.
            if (_indices.TryGetValue(row.Key, out var index) && index < _firstDirtyOffset)
                _firstDirtyOffset = index;
        }
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
        {
            row.Container.InvalidateMeasure();
        }
        else if (AffectsHeight(sender, e.PropertyName))
        {
            _heights.Remove(sender);
            if (index < _firstDirtyOffset)
                _firstDirtyOffset = index;
        }
        else
        {
            return;
        }
        InvalidateMeasure();
    }

    private static bool AffectsHeight(object item, string? propertyName) => propertyName is null or "" || item switch
    {
        AssistantMessageItemViewModel => propertyName is nameof(AssistantMessageItemViewModel.MarkdownText)
            or nameof(AssistantMessageItemViewModel.ReasoningBlocks)
            or nameof(AssistantMessageItemViewModel.IsStreaming)
            or nameof(AssistantMessageItemViewModel.IsReasoningExpanded)
            or nameof(AssistantMessageItemViewModel.IsReasoningSuppressed)
            or nameof(AssistantMessageItemViewModel.StreamingTail)
            or nameof(AssistantMessageItemViewModel.StreamingTailIsCode)
            or nameof(AssistantMessageItemViewModel.FinalMarkdown)
            or nameof(TimelineItemViewModel.IsFolded),
        ToolCallItemViewModel tool => propertyName is nameof(ToolCallItemViewModel.IsExpanded)
            or nameof(TimelineItemViewModel.IsFolded) or nameof(ToolCallItemViewModel.ImageMedia) ||
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
        TimelineItemViewModel { IsFolded: true } => 0,
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
