using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Input;

namespace Alcoler.Workbench.Views;

public partial class BrowserPaneView : UserControl
{
    public BrowserPaneView()
    {
        InitializeComponent(true);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BrowserPaneViewModel viewModel
            && viewModel.NavigateCommand.CanExecute(null))
        {
            _ = viewModel.NavigateCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }
}
