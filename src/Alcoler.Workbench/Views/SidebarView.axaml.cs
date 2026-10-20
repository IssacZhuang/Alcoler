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

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
