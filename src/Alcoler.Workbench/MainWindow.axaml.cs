using Alcoler.Workbench.Themes;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Alcoler.Workbench;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(WorkbenchViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkbenchViewModel viewModel)
        {
            await viewModel.RefreshFromRuntimeAsync();
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkbenchViewModel viewModel)
        {
            return;
        }
        var current = WorkbenchConfig.LoadOrDefault();
        if (await Views.SettingsDialog.ShowAsync(this, current) is not { } saved)
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

    private void OnDismissWorkbenchBannerClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkbenchViewModel viewModel)
        {
            viewModel.Banner = null;
        }
    }

    private void OnDismissConversationBannerClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkbenchViewModel { Active: { } active })
        {
            active.Banner = null;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
