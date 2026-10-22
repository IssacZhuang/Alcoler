using Alcoler.Contracts.Browser;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Views;

public partial class BrowserPaneView : UserControl
{
    private BrowserPaneViewModel? _attachedViewModel;
    private bool _attached;
    private int _attachmentVersion;

    public BrowserPaneView()
    {
        InitializeComponent(true);
        DataContextChanged += (_, _) => AttachViewModel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        AttachViewModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        AttachViewModel();
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachViewModel()
    {
        var version = ++_attachmentVersion;
        var viewModel = _attached ? DataContext as BrowserPaneViewModel : null;
        if (ReferenceEquals(_attachedViewModel, viewModel))
        {
            return;
        }
        _attachedViewModel?.DetachView();
        _attachedViewModel = null;
        if (viewModel is null)
        {
            return;
        }
        // Presenter bindings must not mutate descendants during the parent's visual attach traversal.
        Dispatcher.UIThread.Post(() =>
        {
            if (version == _attachmentVersion && _attached && ReferenceEquals(DataContext, viewModel)
                && TopLevel.GetTopLevel(this) is not null)
            {
                _attachedViewModel = viewModel;
                viewModel.AttachView();
            }
        }, DispatcherPriority.Loaded);
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

    private void OnSelectTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BrowserTabStatus tab }
            && DataContext is BrowserPaneViewModel viewModel && viewModel.SelectTabCommand.CanExecute(tab))
        {
            _ = viewModel.SelectTabCommand.ExecuteAsync(tab);
        }
        e.Handled = true;
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BrowserTabStatus tab }
            && DataContext is BrowserPaneViewModel viewModel && viewModel.CloseTabCommand.CanExecute(tab))
        {
            _ = viewModel.CloseTabCommand.ExecuteAsync(tab);
        }
        e.Handled = true;
    }
}
