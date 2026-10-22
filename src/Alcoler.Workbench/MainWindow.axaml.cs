using System.ComponentModel;
using System.Diagnostics;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.Controls;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LayoutHorizontalAlignment = Avalonia.Layout.HorizontalAlignment;

namespace Alcoler.Workbench;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PaneAnimationDuration = TimeSpan.FromMilliseconds(180);
    private readonly Dictionary<ColumnDefinition, PaneAnimation> _columnAnimations = [];
    private double _sidebarWidth = 264;
    private double _sidePaneWidth;
    private double _terminalHeight;
    private bool? _sidebarOpenState;
    private bool? _sidePaneOpenState;
    private bool _paneStateInitialized;
    private WorkbenchViewModel? _viewModel;
    private ConversationViewModel? _conversation;

    private ColumnDefinition SidePaneColumn => ShellGrid.ColumnDefinitions[^1];
    private RowDefinition TerminalSplitterRow => MainCard.RowDefinitions[^2];
    private RowDefinition TerminalRow => MainCard.RowDefinitions[^1];

    public MainWindow()
    {
        InitializeComponent(true);
        WindowChrome.Attach(this);
        AddHandler(KeyDownEvent, OnShellKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) =>
        {
            ApplyPaneVisibility();
            ApplyConversationOverlays();
        };
        ConversationArea.SizeChanged += (_, _) => ApplyConversationOverlays();
        SessionStatus.DisplayModeChanged += (_, _) => ApplyConversationOverlays();
        MainComposer.StatusRequested += () => SessionStatus.ExpandAndOpenRunningSections();
        SessionStatus.ReviewRequested += () => _viewModel?.ShowGitPaneCommand.Execute(null);
        SessionStatus.PlanRequested += plan => _viewModel?.OpenStatusPlan(plan);
        SessionStatus.ActivityRequested += async activity =>
        {
            if (_viewModel is { } viewModel)
            {
                await viewModel.OpenStatusActivityAsync(activity);
            }
        };
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
        ObserveConversation();
        ApplyPaneVisibility();
        ApplyConversationLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelColumnAnimation(ShellGrid.ColumnDefinitions[0]);
        CancelColumnAnimation(SidePaneColumn);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        if (_conversation is not null)
        {
            _conversation.PropertyChanged -= OnConversationPropertyChanged;
            _conversation.Status.PropertyChanged -= OnStatusPropertyChanged;
        }
        base.OnClosed(e);
    }

    private void ObserveConversation()
    {
        if (_conversation is not null)
        {
            _conversation.PropertyChanged -= OnConversationPropertyChanged;
            _conversation.Status.PropertyChanged -= OnStatusPropertyChanged;
        }
        _conversation = _viewModel?.Active;
        if (_conversation is not null)
        {
            _conversation.PropertyChanged += OnConversationPropertyChanged;
            _conversation.Status.PropertyChanged += OnStatusPropertyChanged;
        }
    }

    private void OnConversationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConversationViewModel.HasPendingQuestion)
            or nameof(ConversationViewModel.HasPendingInteraction) or nameof(ConversationViewModel.Banner))
        {
            var restoreFocus = !MainComposer.IsVisible && _conversation?.HasPendingQuestion != true;
            ApplyConversationOverlays();
            if (restoreFocus)
            {
                Dispatcher.UIThread.Post(() => MainComposer.FocusInput(), DispatcherPriority.Input);
            }
        }
    }

    private void OnStatusPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationStatusViewModel.HasContent))
        {
            ApplyConversationOverlays();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkbenchViewModel.IsSidePaneVisible)
            or nameof(WorkbenchViewModel.IsTerminalVisible) or nameof(WorkbenchViewModel.IsSidebarVisible))
        {
            ApplyPaneVisibility();
        }
        if (e.PropertyName == nameof(WorkbenchViewModel.Active))
        {
            ObserveConversation();
        }
        if (e.PropertyName is nameof(WorkbenchViewModel.IsDraft) or nameof(WorkbenchViewModel.Draft)
            or nameof(WorkbenchViewModel.Active))
        {
            ApplyConversationLayout();
        }
    }

    private void ApplyConversationLayout()
    {
        var isDraft = _viewModel?.IsDraft != false;
        ConversationArea.RowDefinitions[2].Height = isDraft ? GridLength.Star : new GridLength(0);
        ApplyConversationOverlays();
        if (isDraft)
        {
            Dispatcher.UIThread.Post(() => MainComposer.FocusInput(), DispatcherPriority.Input);
        }
    }

    private void ApplyConversationOverlays()
    {
        MainComposer.IsVisible = _conversation?.HasPendingQuestion != true;
        InteractionArea.IsVisible = _conversation is { } conversation
            && (conversation.HasPendingInteraction || !string.IsNullOrEmpty(conversation.Banner));
        InteractionArea.MaxHeight = _conversation?.HasPendingQuestion == true
            ? Math.Max(120, Math.Min(672, Math.Min(Bounds.Height * .72, ConversationArea.Bounds.Height - 48)))
            : double.PositiveInfinity;
        SessionStatus.MaxHeight = Math.Max(80, Math.Min(512, Math.Min(Bounds.Height * .64, ConversationArea.Bounds.Height - 32)));
        SessionStatus.SetConversationWidth(ConversationArea.Bounds.Width);
        ConversationLayout.SetReserveStatusPanelSpace(ConversationArea,
            _conversation?.Status.HasContent == true && SessionStatus.IsExpanded && ConversationArea.Bounds.Width >= 1280);
    }

    internal void FocusComposer() => MainComposer.FocusInput();

    internal bool IsPaneAnimationActive => _columnAnimations.Count > 0;

    private void ApplyPaneVisibility()
    {
        var sidebarOpen = _viewModel?.IsSidebarVisible != false;
        var sidePaneOpen = _viewModel?.IsSidePaneVisible == true;

        if (!_paneStateInitialized)
        {
            _paneStateInitialized = true;
            _sidebarOpenState = sidebarOpen;
            _sidePaneOpenState = sidePaneOpen;
            ApplySidebarLayout(sidebarOpen);
            ApplySidePaneLayout(sidePaneOpen);
        }
        else
        {
            if (_sidebarOpenState != sidebarOpen)
            {
                _sidebarOpenState = sidebarOpen;
                AnimateSidebar(sidebarOpen);
            }
            else if (!_columnAnimations.ContainsKey(ShellGrid.ColumnDefinitions[0]))
            {
                ApplySidebarLayout(sidebarOpen);
            }

            if (_sidePaneOpenState != sidePaneOpen)
            {
                _sidePaneOpenState = sidePaneOpen;
                AnimateSidePane(sidePaneOpen);
            }
            else if (!_columnAnimations.ContainsKey(SidePaneColumn))
            {
                ApplySidePaneLayout(sidePaneOpen);
            }
        }

        var terminalOpen = _viewModel?.IsTerminalVisible == true;
        var maxTerminalHeight = Math.Max(120, Bounds.Height - 320);
        TerminalSplitterRow.Height = new GridLength(terminalOpen ? 4 : 0);
        TerminalRow.Height = new GridLength(terminalOpen
            ? Math.Clamp(_terminalHeight > 0 ? _terminalHeight : Bounds.Height * 0.3, 120, maxTerminalHeight)
            : 0);
    }

    private void ApplySidebarLayout(bool open)
    {
        var sidebarColumn = ShellGrid.ColumnDefinitions[0];
        sidebarColumn.MinWidth = open ? 264 : 0;
        sidebarColumn.Width = new GridLength(open ? SidebarTargetWidth() : 0);
        ShellGrid.ColumnDefinitions[1].Width = new GridLength(open ? 4 : 0);
        SidebarHost.IsVisible = open;
        Sidebar.IsVisible = open;
        Sidebar.Width = double.NaN;
        Sidebar.HorizontalAlignment = LayoutHorizontalAlignment.Stretch;
        SidebarSplitter.IsVisible = open;
        SidebarSplitter.IsHitTestVisible = open;
    }

    private void AnimateSidebar(bool open)
    {
        var sidebarColumn = ShellGrid.ColumnDefinitions[0];
        var current = sidebarColumn.Width.IsAbsolute ? sidebarColumn.Width.Value : SidebarHost.Bounds.Width;
        var target = open ? SidebarTargetWidth() : 0;
        SidebarHost.IsVisible = true;
        Sidebar.IsVisible = true;
        SidebarSplitter.IsVisible = true;
        SidebarSplitter.IsHitTestVisible = false;
        sidebarColumn.MinWidth = 0;
        ShellGrid.ColumnDefinitions[1].Width = new GridLength(4);
        if (open || double.IsNaN(Sidebar.Width))
        {
            Sidebar.Width = open ? target : Math.Max(0, current);
            Sidebar.HorizontalAlignment = LayoutHorizontalAlignment.Left;
        }
        AnimateColumn(sidebarColumn, Math.Max(0, current), target, ApplyPaneVisibility);
    }

    private double SidebarTargetWidth() =>
        Math.Clamp(_sidebarWidth, 264, Math.Max(264, Bounds.Width / 2));

    private (double Target, double Max) SidePaneMetrics()
    {
        var sidebarOpen = _viewModel?.IsSidebarVisible != false;
        var availableWidth = Math.Max(620, Bounds.Width - (sidebarOpen ? _sidebarWidth + 4 : 0) - 12);
        var maxPaneWidth = Math.Max(240, Math.Min(availableWidth * 0.65, availableWidth - 340));
        var target = Math.Clamp(_sidePaneWidth > 0 ? _sidePaneWidth : availableWidth * 0.45, 240, maxPaneWidth);
        return (target, maxPaneWidth);
    }

    private void ApplySidePaneLayout(bool open)
    {
        var (target, maxPaneWidth) = SidePaneMetrics();
        SidePaneColumn.MinWidth = open ? 240 : 0;
        SidePaneColumn.MaxWidth = open ? maxPaneWidth : double.PositiveInfinity;
        SidePaneColumn.Width = new GridLength(open ? target : 0);
        SidePaneDivider.IsVisible = open;
        SidePaneSplitter.IsVisible = open;
        SidePaneSplitter.IsHitTestVisible = open;
        DocumentsHost.IsVisible = open;
        DocumentsView.Width = double.NaN;
        DocumentsView.HorizontalAlignment = LayoutHorizontalAlignment.Stretch;
    }

    private void AnimateSidePane(bool open)
    {
        var current = SidePaneColumn.Width.IsAbsolute ? SidePaneColumn.Width.Value : DocumentsHost.Bounds.Width;
        var (target, maxPaneWidth) = SidePaneMetrics();
        if (!open)
        {
            target = 0;
        }
        DocumentsHost.IsVisible = true;
        SidePaneDivider.IsVisible = true;
        SidePaneSplitter.IsVisible = true;
        SidePaneSplitter.IsHitTestVisible = false;
        SidePaneColumn.MinWidth = 0;
        if (open)
        {
            SidePaneColumn.MaxWidth = maxPaneWidth;
        }
        AnimateColumn(SidePaneColumn, Math.Max(0, current), target, ApplyPaneVisibility);
    }

    private void AnimateColumn(ColumnDefinition column, double from, double to, Action completed)
    {
        CancelColumnAnimation(column);
        if (Math.Abs(to - from) < 0.5)
        {
            column.Width = new GridLength(Math.Max(0, to));
            completed();
            return;
        }
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        void Step(object? sender, EventArgs e)
        {
            var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / PaneAnimationDuration.TotalMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            column.Width = new GridLength(Math.Max(0, from + (to - from) * eased));
            if (progress < 1)
            {
                return;
            }
            CancelColumnAnimation(column);
            completed();
        }
        timer.Tick += Step;
        _columnAnimations[column] = new PaneAnimation(timer);
        timer.Start();
    }

    private void CancelColumnAnimation(ColumnDefinition column)
    {
        if (_columnAnimations.Remove(column, out var animation))
        {
            animation.Timer.Stop();
        }
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
    private void OnWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionGroupViewModel group })
        {
            WorkspaceChooser.Flyout?.Hide();
            _viewModel?.ShowNewTask(group.WorkspaceRoot);
            MainComposer.FocusInput();
        }
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        WorkspaceChooser.Flyout?.Hide();
        if (_viewModel is null || !StorageProvider.CanPickFolder)
        {
            return;
        }
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open workspace folder",
                AllowMultiple = false,
            });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                _viewModel.ShowNewTask(path);
            }
            MainComposer.FocusInput();
        }
        catch (Exception exception)
        {
            _viewModel.Banner = $"Could not open the folder picker. {exception.Message}";
        }
    }

    private void OnOutsideProjectClick(object? sender, RoutedEventArgs e)
    {
        WorkspaceChooser.Flyout?.Hide();
        try
        {
            _viewModel?.WorkOutsideProject();
            MainComposer.FocusInput();
        }
        catch (Exception exception)
        {
            if (_viewModel is not null)
            {
                _viewModel.Banner = $"Could not open the default workspace. {exception.Message}";
            }
        }
    }

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
            case Key.W when _viewModel.Documents.IsVisible && _viewModel.Documents.ActiveDocument is { } document:
                _viewModel.Documents.Close(document);
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

    private sealed record PaneAnimation(DispatcherTimer Timer);
}
