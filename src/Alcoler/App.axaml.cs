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
            var window = new MainWindow(viewModel);
            window.Closing += OnMainWindowClosing;
            _mainWindow = window;
            desktop.MainWindow = window;
            _trayIcon = CreateTrayIcon(desktop);
            desktop.Exit += (_, _) =>
            {
                // An exception in this handler would abort the dispatcher exit sequence
                // and leave a headless process behind, so disposal is guarded.
                try
                {
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
        catch (Exception exception)
        {
            AppLog.LogUnhandled("Startup", exception);
            ShowStartupFailure(desktop, exception);
        }
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

    private TrayIcon CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var openItem = new NativeMenuItem(Loc.Get("tray_open"));
        openItem.Click += (_, _) => ShowMainWindow();
        var exitItem = new NativeMenuItem(Loc.Get("tray_exit"));
        exitItem.Click += (_, _) => Exit(desktop);
        var menu = new NativeMenu();
        menu.Items.Add(openItem);
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
        if (!window.IsVisible)
        {
            window.Show();
        }
        window.WindowState = WindowState.Normal;
        window.Activate();
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
        desktop.Shutdown();
    }
}
