using System.ComponentModel;
using Alcoler.Contracts.Permissions;
using Alcoler.ViewModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Alcoler.Views;

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
        _ = ViewModel?.ResolvePermissionAsync(DenyDecision());

    private PermissionDecision DenyDecision()
    {
        var feedback = ViewModel?.PendingInteraction?.Feedback;
        return string.IsNullOrWhiteSpace(feedback)
            ? PermissionDecision.Deny
            : new PermissionDecision(PermissionOutcome.Denied, Feedback: feedback.Trim());
    }

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
            Key.Enter when e.Source is not TextBox => PermissionDecision.Allow,
            Key.Escape => DenyDecision(),
            _ => null,
        };
        if (decision is { } resolved && ViewModel is { } viewModel)
        {
            e.Handled = true;
            _ = viewModel.ResolvePermissionAsync(resolved);
        }
    }
}
