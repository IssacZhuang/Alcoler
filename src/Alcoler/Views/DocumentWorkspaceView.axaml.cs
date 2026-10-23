using System.Collections.Specialized;
using System.ComponentModel;
using Alcoler.Localization;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Views;

public partial class DocumentWorkspaceView : UserControl
{
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    private DocumentWorkspaceViewModel? _workspace;
    private DocumentViewModel? _dragDocument;
    private Point _dragStart;
    private readonly List<DocumentViewModel> _searchMatchesA = [];
    private readonly List<DocumentViewModel> _searchMatchesB = [];
    private bool _useSearchMatchesB;

    public DocumentWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindWorkspace();
        DocumentTabsScroll.SizeChanged += (_, _) => UpdateTabWidths();
        AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Bubble);
    }

    private WorkbenchViewModel? Workbench => DataContext as WorkbenchViewModel;

    private void BindWorkspace()
    {
        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= OnWorkspaceChanged;
            ((INotifyCollectionChanged)_workspace.VisibleItems).CollectionChanged -= OnVisibleTabsChanged;
        }
        _workspace = Workbench?.Documents;
        if (_workspace is not null)
        {
            _workspace.PropertyChanged += OnWorkspaceChanged;
            ((INotifyCollectionChanged)_workspace.VisibleItems).CollectionChanged += OnVisibleTabsChanged;
        }
        UpdateTabWidths();
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentWorkspaceViewModel.ActiveDocument))
        {
            Dispatcher.UIThread.Post(() =>
            {
                var tab = DocumentTabs.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(border => border.Classes.Contains("documentTab") && ReferenceEquals(border.DataContext, _workspace?.ActiveDocument));
                tab?.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnVisibleTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ApplySearch();
        Dispatcher.UIThread.Post(UpdateTabWidths, DispatcherPriority.Loaded);
    }

    private void UpdateTabWidths()
    {
        foreach (var panel in DocumentTabs.GetVisualDescendants().OfType<DocumentTabPanel>())
            panel.ViewportWidth = DocumentTabsScroll.Bounds.Width;
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

    private void ApplySearch()
    {
        if (_workspace is null)
            return;
        var query = DocumentSearch.Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            DocumentOverviewItems.ItemsSource = _workspace.VisibleItems;
            return;
        }
        var matches = _useSearchMatchesB ? _searchMatchesB : _searchMatchesA;
        _useSearchMatchesB = !_useSearchMatchesB;
        matches.Clear();
        foreach (var document in _workspace.VisibleItems)
        {
            if (document.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                matches.Add(document);
        }
        DocumentOverviewItems.ItemsSource = matches;
    }

    private void OnOverviewDocumentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentViewModel document })
            _workspace?.Activate(document);
        DocumentOverviewButton.Flyout?.Hide();
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: DocumentViewModel document } tab
            || e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any())
            return;
        var properties = e.GetCurrentPoint(tab).Properties;
        if (properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            _workspace?.Close(document);
            e.Handled = true;
        }
        else if (properties.IsLeftButtonPressed)
        {
            _workspace?.Activate(document);
            tab.Focus();
            _dragDocument = document;
            _dragStart = e.GetPosition(DocumentTabs);
            e.Pointer.Capture(tab);
            e.Handled = true;
        }
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragDocument is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (Math.Abs(e.GetPosition(DocumentTabs).X - _dragStart.X) >= 4)
            Cursor = ResizeCursor;
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragDocument is not { } document)
            return;
        var point = e.GetPosition(DocumentTabs);
        if (Math.Abs(point.X - _dragStart.X) >= 4 && _workspace is { } workspace)
        {
            var tabs = DocumentTabs.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("documentTab")).ToArray();
            var target = tabs.FirstOrDefault(tab =>
            {
                var origin = tab.TranslatePoint(default, DocumentTabs);
                return origin is { } position && point.X < position.X + tab.Bounds.Width;
            })?.DataContext as DocumentViewModel ?? workspace.VisibleItems.LastOrDefault();
            if (target is not null)
                workspace.Move(document, workspace.VisibleItems.IndexOf(target));
        }
        _dragDocument = null;
        Cursor = null;
        e.Pointer.Capture(null);
    }

    private void OnTabKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: DocumentViewModel document } || _workspace is null)
            return;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
                _workspace.SelectNext(e.Key == Key.Left ? -1 : 1);
                DocumentTabs.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(tab => tab.Classes.Contains("documentTab") && ReferenceEquals(tab.DataContext, _workspace.ActiveDocument))?.Focus();
                e.Handled = true;
                break;
            case Key.Delete:
                _workspace.Close(document);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                _workspace.Activate(document);
                e.Handled = true;
                break;
        }
    }

    private void OnCloseDocumentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentViewModel document })
            _workspace?.Close(document);
        e.Handled = true;
    }

    private void OnCloseOthersClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DocumentViewModel document })
            _workspace?.CloseOthers(document);
    }

    private void OnCloseAllClick(object? sender, RoutedEventArgs e) => _workspace?.CloseAll();

    private void OnMoveLeftClick(object? sender, RoutedEventArgs e) => MoveTab(sender, -1);
    private void OnMoveRightClick(object? sender, RoutedEventArgs e) => MoveTab(sender, 1);

    private void MoveTab(object? sender, int offset)
    {
        if (sender is Control { DataContext: DocumentViewModel document } && _workspace is { } workspace)
            workspace.Move(document, workspace.VisibleItems.IndexOf(document) + offset);
    }

    private void OnOpenTabClick(object? sender, RoutedEventArgs e) => AddDocumentButton.Flyout?.ShowAt(AddDocumentButton);
    private void OnOpenChangesClick(object? sender, RoutedEventArgs e)
    {
        Workbench?.ShowGitPaneCommand.Execute(null);
        AddDocumentButton.Flyout?.Hide();
    }

    private void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        Workbench?.ShowBrowserPaneCommand.Execute(null);
        AddDocumentButton.Flyout?.Hide();
    }

    private void OnOpenTerminalClick(object? sender, RoutedEventArgs e)
    {
        Workbench?.OpenTerminalDocumentCommand.Execute(null);
        AddDocumentButton.Flyout?.Hide();
    }

    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        AddDocumentButton.Flyout?.Hide();
        if (Workbench is not { } workbench || TopLevel.GetTopLevel(this) is not { } topLevel || !topLevel.StorageProvider.CanOpen)
            return;
        var root = workbench.CurrentWorkspaceRoot;
        var owner = workbench.CurrentDocumentOwnerId;
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.Get("docs_open_document_title"), AllowMultiple = true });
            foreach (var file in files)
                if (file.TryGetLocalPath() is { } path)
                    await workbench.OpenFileDocumentAsync(path, root, owner);
        }
        catch (Exception exception)
        {
            workbench.Banner = Loc.Get("docs_file_picker_failed", exception.Message);
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Border>().Any(border => border.Classes.Contains("documentTab")))
            return;
        (TopLevel.GetTopLevel(this) as MainWindow)?.DragTitleBar(e);
    }
}

public sealed class DocumentTabPanel : Panel
{
    private double _viewportWidth;

    public double ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            if (Math.Abs(_viewportWidth - value) < .1)
                return;
            _viewportWidth = value;
            InvalidateMeasure();
        }
    }

    private double TabWidth => Children.Count == 0 ? 156 : Math.Clamp(ViewportWidth / Children.Count, 60, 156);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = TabWidth;
        foreach (var child in Children)
            child.Measure(new Size(width, 28));
        return new Size(width * Children.Count, 28);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = TabWidth;
        for (var index = 0; index < Children.Count; index++)
            Children[index].Arrange(new Rect(index * width, 0, width, 28));
        return finalSize;
    }
}
