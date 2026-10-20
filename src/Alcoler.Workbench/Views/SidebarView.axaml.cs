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

    private async void OnNewSessionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkbenchViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        var defaultRoot = viewModel.Sidebar.Groups.FirstOrDefault()?.WorkspaceRoot ?? string.Empty;
        var request = await NewSessionDialog.ShowAsync(owner, defaultRoot);
        if (request is { } created)
        {
            await viewModel.NewSessionAsync(created.WorkspaceRoot, created.Title);
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
            group.IsExpanded = !group.IsExpanded;
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
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
        !string.Equals(before.DefaultModel?.ProviderId, after.DefaultModel?.ProviderId, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(before.DefaultModel?.ModelId, after.DefaultModel?.ModelId, StringComparison.OrdinalIgnoreCase)
        || before.Providers.Count != after.Providers.Count
        || before.McpServers?.Count != after.McpServers?.Count;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
