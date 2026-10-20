using System.ComponentModel;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Alcoler.Workbench.Views;

public partial class ComposerView : UserControl
{
    private IComposerHost? _observed;
    private WorkbenchViewModel? _workbench;
    private Window? _owner;
    private readonly ProviderUsageViewModel _providerUsage = new();
    private readonly ProviderUsageView _providerUsageView;
    private bool _synchronizingModel;

    public ComposerView()
    {
        // The generated InitializeComponent(bool) overload wires x:Name fields; a manual parameterless overload would shadow it.
        InitializeComponent(true);
        _providerUsageView = new ProviderUsageView { DataContext = _providerUsage };
        ContextUsageHost.QuotaContent = _providerUsageView;
        ContextUsageHost.PopoverOpened += (_, _) => _ = _providerUsage.RefreshAsync();
        // Tunneling is required to observe plain Enter before the TextBox consumes it as a newline.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => ObserveConversation();
        SizeChanged += (_, _) => Classes.Set("compact", Bounds.Width < 560);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveConversation();
        if (e.Root is Window owner)
        {
            if (_owner != owner)
            {
                if (_owner is not null)
                    _owner.Closed -= OnOwnerClosed;
                _owner = owner;
                owner.Closed += OnOwnerClosed;
            }
            if (owner.DataContext is WorkbenchViewModel workbench)
            {
                _workbench = workbench;
                ModelSelector.ItemsSource = workbench.ModelChoices;
                SynchronizeModel();
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnConversationPropertyChanged;
            _observed.Composer.PropertyChanged -= OnComposerPropertyChanged;
            _observed = null;
        }
        if (_workbench is not null)
        {
            _workbench = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnOwnerClosed(object? sender, EventArgs e)
    {
        _providerUsage.Dispose();
        if (_owner is not null)
            _owner.Closed -= OnOwnerClosed;
        _owner = null;
    }

    private void ObserveConversation()
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnConversationPropertyChanged;
            _observed.Composer.PropertyChanged -= OnComposerPropertyChanged;
        }
        _observed = ViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnConversationPropertyChanged;
            _observed.Composer.PropertyChanged += OnComposerPropertyChanged;
        }
        SynchronizeModel();
    }

    private void OnConversationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationViewModel.ModelText))
        {
            SynchronizeModel();
        }
    }

    private void OnComposerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComposerViewModel.Mode))
        {
            ModeSelector.Flyout?.Hide();
            InputBox.Focus();
        }
    }

    private void SynchronizeModel()
    {
        _synchronizingModel = true;
        try
        {
            var choice = _workbench?.ModelChoices.FirstOrDefault(choice =>
                $"{choice.ProviderId}/{choice.ModelId}" == ViewModel?.ModelText);
            ModelSelector.SelectedItem = choice;
            if (choice is not null)
                ViewModel?.ConfigureModel(choice);
        }
        finally
        {
            _synchronizingModel = false;
        }
        var provider = ModelSelector.SelectedItem is ModelChoice selected ? _workbench?.FindProvider(selected.ProviderId) : null;
        _providerUsage.Configure(provider?.EffectiveId, provider?.BaseUrl, provider?.ApiKey);
    }

    private IComposerHost? ViewModel => DataContext as IComposerHost;

    public void FocusInput() => InputBox.Focus();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }
        e.Handled = true;
        if (ViewModel is { Composer.CanSend: true } viewModel)
        {
            _ = viewModel.SendAsync();
        }
    }

    private void OnSendClick(object? sender, RoutedEventArgs e) => _ = ViewModel?.SendAsync();

    private void OnStopClick(object? sender, RoutedEventArgs e) => _ = ViewModel?.StopAsync();

    private void OnCompactClick(object? sender, RoutedEventArgs e) => _ = ViewModel?.CompactAsync();

    private void OnModelSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizingModel && e.AddedItems.OfType<ModelChoice>().FirstOrDefault() is { } choice
            && ViewModel is { } viewModel)
        {
            viewModel.ConfigureModel(choice, useDefaultReasoning: true);
            _ = viewModel.SwitchModelAsync(viewModel.ApplyThoughtLevel(choice.Selection));
        }
    }
}
