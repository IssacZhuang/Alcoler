using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Alcoler.Workbench.Views;

public partial class GitPaneView : UserControl
{
    public GitPaneView()
    {
        // The generated InitializeComponent(bool) overload wires x:Name fields; a manual parameterless overload would shadow it.
        InitializeComponent(true);
    }

    private GitPaneViewModel? ViewModel => DataContext as GitPaneViewModel;

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => _ = ViewModel?.LoadAsync();

    private void OnBranchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitBranchViewModel branch })
        {
            _ = ViewModel?.CheckoutAsync(branch.Name);
        }
    }

    private void OnFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GitFileChangeViewModel file }
            && ViewModel is { } git
            && TopLevel.GetTopLevel(this) is MainWindow { DataContext: WorkbenchViewModel workbench })
        {
            _ = workbench.OpenFileDocumentAsync(Path.Combine(git.FileRoot, file.Path));
            e.Handled = true;
        }
    }

    private void OnDismissBannerClick(object? sender, RoutedEventArgs e) => ViewModel?.ClearBanner();
}
