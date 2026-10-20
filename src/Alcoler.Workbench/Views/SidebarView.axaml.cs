using System.Text.Json;
using Alcoler.Workbench.Themes;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Alcoler.Workbench.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    private async void OnNewSessionClick(object? sender, RoutedEventArgs e) => await ShowNewSessionAsync();

    public Task ShowNewSessionAsync()
    {
        if (DataContext is WorkbenchViewModel viewModel)
        {
            viewModel.ShowNewTask();
            (TopLevel.GetTopLevel(this) as MainWindow)?.FocusComposer();
        }
        return Task.CompletedTask;
    }

    private void OnProjectNewTaskClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionGroupViewModel group }
            && DataContext is WorkbenchViewModel viewModel)
        {
            viewModel.ShowNewTask(group.WorkspaceRoot);
            (TopLevel.GetTopLevel(this) as MainWindow)?.FocusComposer();
            e.Handled = true;
        }
    }

    private async void OnSessionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionSummaryViewModel summary }
            && DataContext is WorkbenchViewModel viewModel)
        {
            await viewModel.OpenSessionAsync(summary.Id);
        }
    }

    private void OnGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionGroupViewModel group })
        {
            (DataContext as WorkbenchViewModel)?.FocusWorkspace(group.WorkspaceRoot);
            group.IsExpanded = !group.IsExpanded;
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e) => await ShowSettingsAsync();

    public async Task ShowSettingsAsync()
    {
        if (DataContext is not WorkbenchViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        var current = WorkbenchConfig.LoadOrDefault();
        if (await SettingsDialog.ShowAsync(owner, current) is not { } saved)
        {
            return;
        }
        ThemeSelector.Apply(saved.Theme);
        if (ProvidersChanged(current, saved))
        {
            viewModel.Banner = "Settings saved. Restart Alcoler to apply provider and MCP changes.";
        }
    }

    private static bool ProvidersChanged(WorkbenchConfig before, WorkbenchConfig after) =>
        JsonSerializer.Serialize(before with { Theme = "dark" }, WorkbenchConfig.JsonOptions)
        != JsonSerializer.Serialize(after with { Theme = "dark" }, WorkbenchConfig.JsonOptions);

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkbenchViewModel viewModel)
        {
            await viewModel.Sidebar.RefreshAsync();
        }
    }

    private void OnTitleBarPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow owner)
        {
            owner.DragTitleBar(e);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
