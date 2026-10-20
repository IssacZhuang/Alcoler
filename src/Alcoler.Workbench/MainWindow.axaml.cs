using System.ComponentModel;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Alcoler.Workbench;

public partial class MainWindow : Window
{
    private const double DefaultSidePaneWidth = 380;
    private const double DefaultTerminalHeight = 240;

    private double _sidePaneWidth = DefaultSidePaneWidth;
    private double _terminalHeight = DefaultTerminalHeight;
    private WorkbenchViewModel? _viewModel;

    private ColumnDefinition SidePaneColumn => ShellGrid.ColumnDefinitions[^1];

    private RowDefinition TerminalSplitterRow => MainCard.RowDefinitions[^2];

    private RowDefinition TerminalRow => MainCard.RowDefinitions[^1];

    public MainWindow()
    {
        InitializeComponent(true);
        WindowChrome.Attach(this);
    }

    public MainWindow(WorkbenchViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

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
        if (e.PropertyName is nameof(WorkbenchViewModel.IsSidePaneVisible) or nameof(WorkbenchViewModel.IsTerminalVisible))
        {
            ApplyPaneVisibility();
        }
    }

    private void ApplyPaneVisibility()
    {
        var sidePaneOpen = _viewModel?.IsSidePaneVisible == true;
        if (!sidePaneOpen && SidePaneColumn.Width.Value > 0)
        {
            _sidePaneWidth = SidePaneColumn.Width.Value;
        }
        SidePaneColumn.Width = new GridLength(sidePaneOpen ? _sidePaneWidth : 0);

        var terminalOpen = _viewModel?.IsTerminalVisible == true;
        if (!terminalOpen && TerminalRow.Height.Value > 0)
        {
            _terminalHeight = TerminalRow.Height.Value;
        }
        TerminalSplitterRow.Height = new GridLength(terminalOpen ? 4 : 0);
        TerminalRow.Height = new GridLength(terminalOpen ? _terminalHeight : 0);
    }

    private void OnSidePaneSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        _sidePaneWidth = Math.Clamp(SidePaneColumn.Width.Value, 280, 640);
        SidePaneColumn.Width = new GridLength(_sidePaneWidth);
    }

    private void OnTerminalSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        _terminalHeight = Math.Clamp(TerminalRow.Height.Value, 120, 520);
        TerminalRow.Height = new GridLength(_terminalHeight);
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkbenchViewModel viewModel)
        {
            await viewModel.RefreshFromRuntimeAsync();
        }
    }

    private void OnDismissWorkbenchBannerClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkbenchViewModel viewModel)
        {
            viewModel.Banner = null;
        }
    }
}
