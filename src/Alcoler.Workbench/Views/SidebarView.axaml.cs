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
        if (await SettingsDialog.ShowAsync(owner, current, viewModel.ChatGptAccount) is not { } saved)
        {
            return;
        }
        ThemeSelector.Apply(saved.Theme);
        viewModel.ApplySettings(saved);
        var providersChanged = ProvidersChanged(current, saved);
        var mcpChanged = McpServersChanged(current, saved);
        var searchChanged = !string.Equals(current.BraveApiKey, saved.BraveApiKey, StringComparison.Ordinal);
        if (searchChanged)
        {
            viewModel.Banner = "Web search settings saved. Restart Alcoler to apply them.";
        }
        else if (providersChanged && mcpChanged)
        {
            viewModel.Banner = "Provider settings applied. MCP settings apply to new sessions; open sessions keep their current tools.";
        }
        else if (providersChanged)
        {
            viewModel.Banner = "Provider settings applied.";
        }
        else if (mcpChanged)
        {
            viewModel.Banner = "MCP settings saved. They apply to new sessions; open sessions keep their current tools.";
        }
        else if (current.IntegratedTerminalShell != saved.IntegratedTerminalShell)
        {
            viewModel.Banner = "Shell preference saved. It applies to new sessions; existing sessions keep their shell.";
        }
    }

    internal static bool ProvidersChanged(WorkbenchConfig before, WorkbenchConfig after) =>
        SerializeProviderSettings(before) != SerializeProviderSettings(after);

    internal static bool McpServersChanged(WorkbenchConfig before, WorkbenchConfig after) =>
        SerializeMcpSettings(before) != SerializeMcpSettings(after);

    private static string SerializeProviderSettings(WorkbenchConfig config) =>
        JsonSerializer.Serialize(new WorkbenchConfig(config.Providers, config.DefaultModel), WorkbenchConfig.JsonOptions);

    private static string SerializeMcpSettings(WorkbenchConfig config) =>
        JsonSerializer.Serialize(new WorkbenchConfig([], McpServers: config.McpServers), WorkbenchConfig.JsonOptions);

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
