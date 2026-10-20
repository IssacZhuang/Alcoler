using System.ComponentModel;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Alcoler.Workbench;

public partial class MainWindow : Window
{
    private double _sidebarWidth = 264;
    private double _sidePaneWidth;
    private double _terminalHeight;
    private WorkbenchViewModel? _viewModel;

    private ColumnDefinition SidePaneColumn => ShellGrid.ColumnDefinitions[^1];
    private RowDefinition TerminalSplitterRow => MainCard.RowDefinitions[^2];
    private RowDefinition TerminalRow => MainCard.RowDefinitions[^1];

    public MainWindow()
    {
        InitializeComponent(true);
        WindowChrome.Attach(this);
        AddHandler(KeyDownEvent, OnShellKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => ApplyPaneVisibility();
    }

    public MainWindow(WorkbenchViewModel viewModel) : this() => DataContext = viewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = DataContext as WorkbenchViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        ApplyPaneVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchViewModel.IsSidePaneVisible)
            or nameof(WorkbenchViewModel.IsTerminalVisible) or nameof(WorkbenchViewModel.IsSidebarVisible))
        {
            ApplyPaneVisibility();
        }
    }

    private void ApplyPaneVisibility()
    {
        var sidebarOpen = _viewModel?.IsSidebarVisible != false;
        var sidebarColumn = ShellGrid.ColumnDefinitions[0];
        Sidebar.IsVisible = sidebarOpen;
        SidebarSplitter.IsVisible = sidebarOpen;
        sidebarColumn.MinWidth = sidebarOpen ? 264 : 0;
        sidebarColumn.Width = new GridLength(sidebarOpen ? Math.Clamp(_sidebarWidth, 264, Math.Max(264, Bounds.Width / 2)) : 0);
        ShellGrid.ColumnDefinitions[1].Width = new GridLength(sidebarOpen ? 4 : 0);

        var sidePaneOpen = _viewModel?.IsSidePaneVisible == true;
        var availableWidth = Math.Max(620, Bounds.Width - (sidebarOpen ? _sidebarWidth + 4 : 0) - 12);
        var maxPaneWidth = Math.Max(280, availableWidth - 340);
        SidePaneColumn.MinWidth = sidePaneOpen ? 280 : 0;
        SidePaneColumn.Width = new GridLength(sidePaneOpen
            ? Math.Clamp(_sidePaneWidth > 0 ? _sidePaneWidth : availableWidth * 0.45, 280, maxPaneWidth)
            : 0);

        var terminalOpen = _viewModel?.IsTerminalVisible == true;
        var maxTerminalHeight = Math.Max(120, Bounds.Height - 320);
        TerminalSplitterRow.Height = new GridLength(terminalOpen ? 4 : 0);
        TerminalRow.Height = new GridLength(terminalOpen
            ? Math.Clamp(_terminalHeight > 0 ? _terminalHeight : Bounds.Height * 0.3, 120, maxTerminalHeight)
            : 0);
    }

    private void OnSidebarSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        _sidebarWidth = Math.Clamp(ShellGrid.ColumnDefinitions[0].Width.Value, 264, Math.Max(264, Bounds.Width / 2));
        ApplyPaneVisibility();
    }

    private void OnSidePaneSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        _sidePaneWidth = SidePaneColumn.Width.Value;
        ApplyPaneVisibility();
    }

    private void OnTerminalSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        _terminalHeight = TerminalRow.Height.Value;
        ApplyPaneVisibility();
    }

    internal void DragTitleBar(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any()))
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else
        {
            BeginMoveDrag(e);
        }
        e.Handled = true;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) => DragTitleBar(e);
    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private async void OnNewSessionClick(object? sender, RoutedEventArgs e) => await Sidebar.ShowNewSessionAsync();

    private async void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }
        switch (e.Key)
        {
            case Key.N:
                e.Handled = true;
                await Sidebar.ShowNewSessionAsync();
                break;
            case Key.OemComma:
                e.Handled = true;
                await Sidebar.ShowSettingsAsync();
                break;
            case Key.B:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                {
                    _viewModel.ToggleSidePaneCommand.Execute(null);
                }
                else
                {
                    _viewModel.ToggleSidebarCommand.Execute(null);
                }
                e.Handled = true;
                break;
            case Key.J:
                await _viewModel.ToggleTerminalCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
        }
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkbenchViewModel viewModel)
        {
            await viewModel.Sidebar.RefreshAsync();
        }
    }

    private void OnDismissWorkbenchBannerClick(object? sender, RoutedEventArgs e) => _viewModel?.ClearBanner();
}
