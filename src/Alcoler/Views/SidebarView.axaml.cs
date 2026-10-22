using System.Text.Json;
using Alcoler.Themes;
using Alcoler.ViewModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Alcoler.Views;

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

    private async void OnToggleArchivedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkbenchViewModel viewModel)
        {
            await viewModel.ToggleArchivedViewAsync();
        }
    }

    private async void OnArchiveSessionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionSummaryViewModel summary }
            && DataContext is WorkbenchViewModel viewModel)
        {
            e.Handled = true;
            await viewModel.SetSessionArchivedAsync(summary.Id, archived: true);
        }
    }

    private async void OnUnarchiveSessionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionSummaryViewModel summary }
            && DataContext is WorkbenchViewModel viewModel)
        {
            e.Handled = true;
            await viewModel.SetSessionArchivedAsync(summary.Id, archived: false);
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

    private void OnShowMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionGroupViewModel group })
        {
            group.ShowMore();
            e.Handled = true;
        }
    }

    private void OnCollapseSessionsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SessionGroupViewModel group })
        {
            group.Collapse();
            e.Handled = true;
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
        await viewModel.Sidebar.RefreshAsync();
        var mcpChanged = McpServersChanged(current, saved);
        var skillsChanged = SkillsChanged(current, saved);
        var searchChanged = !string.Equals(current.BraveApiKey, saved.BraveApiKey, StringComparison.Ordinal);
        if (searchChanged)
        {
            viewModel.Banner = "Web search settings saved. Restart Alcoler to apply them.";
        }
        else if (mcpChanged)
        {
            viewModel.Banner = "MCP settings saved. They apply to new sessions; open sessions keep their current tools.";
        }
        else if (skillsChanged)
        {
            viewModel.Banner = "Skill settings applied.";
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

    internal static bool SkillsChanged(WorkbenchConfig before, WorkbenchConfig after) =>
        SerializeSkillSettings(before) != SerializeSkillSettings(after);

    private static string SerializeProviderSettings(WorkbenchConfig config) =>
        JsonSerializer.Serialize(new WorkbenchConfig(config.Providers, config.DefaultModel), WorkbenchConfig.JsonOptions);

    private static string SerializeMcpSettings(WorkbenchConfig config) =>
        JsonSerializer.Serialize(new WorkbenchConfig([], McpServers: config.McpServers), WorkbenchConfig.JsonOptions);

    private static string SerializeSkillSettings(WorkbenchConfig config) =>
        string.Join('\n', (config.DisabledSkillPaths ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

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
