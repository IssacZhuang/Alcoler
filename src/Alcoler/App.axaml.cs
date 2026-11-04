using System.ComponentModel;
using Alcoler.Localization;
using Alcoler.Logging;
using Alcoler.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Alcoler;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private TaskOverlayWindow? _taskOverlayWindow;
    private bool _isExiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
            _ = RunAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var config = WorkbenchConfig.LoadOrDefault();
            Themes.ThemeSelector.Apply(config.Theme);
            Loc.SetLanguage(Loc.ResolveLanguage(config.Language));
            var viewModel = await Composition.BuildWorkbenchAsync(config, CancellationToken.None);
            InitializeWorkbench(desktop, viewModel);
        }
        catch (Exception exception)
        {
            AppLog.LogUnhandled("Startup", exception);
            ShowStartupFailure(desktop, exception);
        }
    }

    internal void InitializeWorkbench(IClassicDesktopStyleApplicationLifetime desktop, ViewModel.WorkbenchViewModel viewModel)
    {
        var window = new MainWindow(viewModel);
        window.Closing += OnMainWindowClosing;
        _mainWindow = window;
        desktop.MainWindow = window;
        var overlay = new TaskOverlayWindow(viewModel.TaskOverlay);
        _taskOverlayWindow = overlay;
        overlay.MainWindowRequested += ShowMainWindow;
        overlay.SessionRequested += async sessionId =>
        {
            if (_isExiting)
                return;
            ShowMainWindow();
            try
            {
                await viewModel.OpenSessionAsync(sessionId);
            }
            catch (Exception exception)
            {
                if (!_isExiting)
                {
                    AppLog.LogUnhandled("TaskOverlayNavigation", exception);
                    window.ShowErrorTip(Describe(exception), AppLog.DirectoryPath);
                }
            }
        };
        void OnTaskOverlayVisibilityChanged(object? sender, PropertyChangedEventArgs change)
        {
            if (change.PropertyName != nameof(ViewModel.TaskOverlayViewModel.IsVisible)
                || _isExiting || overlay.IsVisible == viewModel.TaskOverlay.IsVisible)
                return;
            if (viewModel.TaskOverlay.IsVisible)
                overlay.ShowNear(window);
            else
                overlay.Hide();
        }
        viewModel.TaskOverlay.PropertyChanged += OnTaskOverlayVisibilityChanged;
        overlay.PropertyChanged += (_, change) =>
        {
            if (change.Property == Window.IsVisibleProperty)
                viewModel.TaskOverlay.IsVisible = overlay.IsVisible;
        };
        _trayIcon = CreateTrayIcon(desktop, viewModel.TaskOverlay);
        if (viewModel.TaskOverlay.IsVisible)
            overlay.ShowNear(window);
        desktop.Exit += (_, _) =>
        {
            _isExiting = true;
            viewModel.TaskOverlay.PropertyChanged -= OnTaskOverlayVisibilityChanged;
            // An exception in this handler would abort the dispatcher exit sequence
            // and leave a headless process behind, so disposal is guarded.
            try
            {
                _taskOverlayWindow?.Shutdown();
                _taskOverlayWindow = null;
                _trayIcon?.Dispose();
                _trayIcon = null;
                viewModel.Dispose();
            }
            catch (Exception exception)
            {
                AppLog.LogUnhandled("Shutdown", exception);
            }
            finally
            {
                Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandledException;
            }
        };
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        AppLog.LogUnhandled("Dispatcher", e.Exception);
        try
        {
            if (_mainWindow is not null)
            {
                ShowMainWindow();
                _mainWindow.ShowErrorTip(Describe(e.Exception), AppLog.DirectoryPath);
            }
        }
        catch (Exception exception)
        {
            AppLog.LogUnhandled("ErrorTip", exception);
        }
    }

    private void ShowStartupFailure(IClassicDesktopStyleApplicationLifetime desktop, Exception exception)
    {
        try
        {
            if (_mainWindow is not null)
            {
                ShowMainWindow();
                _mainWindow.ShowErrorTip(Describe(exception), AppLog.DirectoryPath);
                return;
            }

            var window = new StartupErrorWindow(Describe(exception), AppLog.DirectoryPath);
            window.Closed += (_, _) => desktop.Shutdown();
            desktop.MainWindow = window;
            window.Show();
        }
        catch (Exception failure)
        {
            AppLog.LogUnhandled("StartupFailurePresentation", failure);
            desktop.Shutdown();
        }
    }

    private static string Describe(Exception exception)
    {
        const int maxLength = 500;
        var message = exception.Message.Trim();
        if (message.Length > maxLength)
        {
            message = message[..maxLength] + "...";
        }
        return message.Length == 0 ? exception.GetType().Name : $"{exception.GetType().Name}: {message}";
    }

    private TrayIcon CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, ViewModel.TaskOverlayViewModel taskOverlay)
    {
        var openItem = new NativeMenuItem(Loc.Get("tray_open"));
        openItem.Click += (_, _) => ShowMainWindow();
        var overlayItem = new NativeMenuItem(Loc.Get("tray_task_overlay"))
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = taskOverlay.IsVisible,
        };
        overlayItem.Click += (_, _) =>
        {
            if (!_isExiting)
                taskOverlay.IsVisible = !taskOverlay.IsVisible;
        };
        if (_taskOverlayWindow is { } overlay)
        {
            overlay.PropertyChanged += (_, change) =>
            {
                if (change.Property == Window.IsVisibleProperty)
                    overlayItem.IsChecked = overlay.IsVisible;
            };
        }
        var exitItem = new NativeMenuItem(Loc.Get("tray_exit"));
        exitItem.Click += (_, _) => Exit(desktop);
        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(overlayItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);
        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Alcoler/Assets/Icons/Alcoler.ico"))),
            ToolTipText = "Alcoler",
            Menu = menu,
            IsVisible = true,
        };
        trayIcon.Clicked += (_, _) => ShowMainWindow();
        return trayIcon;
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }
        e.Cancel = true;
        _mainWindow?.Hide();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is not { } window)
        {
            return;
        }
        window.ShowAndActivate();
    }

    private void Exit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_isExiting)
        {
            return;
        }
        _isExiting = true;
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        // Watchdog: a hang or a swallowed exception in the shutdown sequence must not
        // be able to leave a headless process behind, so exit is forced after a grace
        // period that comfortably exceeds the normal shutdown duration.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            Environment.Exit(Environment.ExitCode);
        });
        _taskOverlayWindow?.Shutdown();
        _taskOverlayWindow = null;
        desktop.Shutdown();
    }
}
