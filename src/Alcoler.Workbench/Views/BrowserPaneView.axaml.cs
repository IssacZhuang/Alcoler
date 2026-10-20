using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Alcoler.Workbench.Views;

public partial class BrowserPaneView : UserControl
{
    public BrowserPaneView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
