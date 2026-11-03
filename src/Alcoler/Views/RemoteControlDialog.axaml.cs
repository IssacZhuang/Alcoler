using Alcoler.Localization;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Alcoler.Views;

public partial class RemoteControlDialog : Window
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RemoteSettingsViewModel? _viewModel;
    private bool _closeApproved;
    private bool _isClosing;

    public RemoteControlDialog()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => _viewModel?.RemoteAccess.RefreshState();
        Opened += (_, _) =>
        {
            _viewModel?.RemoteAccess.RefreshState();
            _statusTimer.Start();
        };
        Closing += OnClosing;
        Closed += (_, _) => _statusTimer.Stop();
        WindowChrome.Attach(this);
    }

    internal RemoteControlDialog(WorkbenchConfig config, RemoteAccessViewModel remoteAccess, string? configPath = null)
        : this()
    {
        _viewModel = new RemoteSettingsViewModel(config, remoteAccess, configPath);
        DataContext = _viewModel;
    }

    public static Task ShowAsync(Window owner, WorkbenchConfig config, RemoteAccessViewModel remoteAccess) =>
        new RemoteControlDialog(config, remoteAccess).ShowDialog(owner);

    private async void OnRemoteEnabledChanged(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsApplying
            || RemoteEnabledToggle.IsChecked == _viewModel.Enabled)
        {
            return;
        }
        await _viewModel.SetEnabledAsync(RemoteEnabledToggle.IsChecked == true);
        RemoteEnabledToggle.SetCurrentValue(ToggleSwitch.IsCheckedProperty, _viewModel.Enabled);
    }

    private async void OnConfigurationLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is { CanEdit: true })
        {
            await _viewModel.SaveEditsAsync();
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || _viewModel is null)
        {
            return;
        }
        e.Cancel = true;
        if (_isClosing || _viewModel.IsApplying)
        {
            return;
        }
        _isClosing = true;
        if (await _viewModel.SaveEditsAsync())
        {
            _closeApproved = true;
            Dispatcher.UIThread.Post(Close);
        }
        _isClosing = false;
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private async void OnCopyPairingUrlClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.RemoteAccess.PairingUrl is not { Length: > 0 } url || Clipboard is not { } clipboard)
        {
            return;
        }
        try
        {
            await clipboard.SetTextAsync(url);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            _viewModel.Error = Loc.Get("settings_remote_copy_error", exception.Message);
        }
    }
}
