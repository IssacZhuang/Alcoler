using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class ProviderUsageView : UserControl
{
    private readonly List<Visual> _visibilityAncestors = [];
    private bool _attached;
    private bool _open;

    public ProviderUsageView() => InitializeComponent(true);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        foreach (var visual in this.GetVisualAncestors().Prepend(this))
        {
            _visibilityAncestors.Add(visual);
            visual.PropertyChanged += OnVisibilityChanged;
        }
        Dispatcher.UIThread.Post(UpdateOpenState);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _open = false;
        foreach (var visual in _visibilityAncestors)
        {
            visual.PropertyChanged -= OnVisibilityChanged;
        }
        _visibilityAncestors.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached && _open && DataContext is ProviderUsageViewModel viewModel)
        {
            _ = viewModel.RefreshAsync();
        }
    }

    private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            Dispatcher.UIThread.Post(UpdateOpenState);
        }
    }

    private void UpdateOpenState()
    {
        var open = _attached && IsEffectivelyVisible;
        if (open && !_open && DataContext is ProviderUsageViewModel viewModel)
        {
            _open = true;
            _ = viewModel.RefreshAsync();
        }
        else
        {
            _open = open;
        }
    }
}
