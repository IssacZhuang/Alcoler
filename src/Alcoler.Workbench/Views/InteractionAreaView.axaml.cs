using System.ComponentModel;
using Alcoler.Contracts.Permissions;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class InteractionAreaView : UserControl
{
    private ConversationViewModel? _observed;

    public InteractionAreaView()
    {
        // The generated InitializeComponent(bool) overload wires x:Name fields; a manual parameterless overload would shadow it.
        InitializeComponent(true);
        DataContextChanged += OnDataContextChanged;
    }

    private ConversationViewModel? ViewModel => DataContext as ConversationViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _observed = ViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
        }
        RefreshBanner();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationViewModel.Banner))
        {
            RefreshBanner();
        }
    }

    private void RefreshBanner() => BannerStrip.IsVisible = ViewModel?.Banner is { Length: > 0 };

    private void OnDismissBanner(object? sender, RoutedEventArgs e) => ViewModel?.ClearBanner();

    private void OnAllowClick(object? sender, RoutedEventArgs e) =>
        _ = ViewModel?.ResolvePermissionAsync(PermissionDecision.Allow);

    private void OnDenyClick(object? sender, RoutedEventArgs e) =>
        _ = ViewModel?.ResolvePermissionAsync(PermissionDecision.Deny);

    private void OnSubmitAnswersClick(object? sender, EventArgs e) =>
        _ = ViewModel?.SubmitInteractionAnswersAsync();

    private void OnDismissQuestion(object? sender, EventArgs e) =>
        _ = ViewModel?.DismissInteractionAsync();

    private void OnPermissionCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is Button or ToggleButton)
        {
            return;
        }
        var decision = e.Key switch
        {
            Key.Enter => PermissionDecision.Allow,
            Key.Escape => PermissionDecision.Deny,
            _ => null,
        };
        if (decision is { } resolved && ViewModel is { } viewModel)
        {
            e.Handled = true;
            _ = viewModel.ResolvePermissionAsync(resolved);
        }
    }
}
