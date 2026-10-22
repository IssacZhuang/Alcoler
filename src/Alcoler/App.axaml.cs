using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

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
            _ = RunAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var config = WorkbenchConfig.LoadOrDefault();
        Themes.ThemeSelector.Apply(config.Theme);
        var viewModel = await Composition.BuildWorkbenchAsync(config, CancellationToken.None);
        var window = new MainWindow(viewModel);
        window.Closing += OnMainWindowClosing;
        _mainWindow = window;
        desktop.MainWindow = window;
        _trayIcon = CreateTrayIcon(desktop);
        desktop.Exit += (_, _) =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            viewModel.Dispose();
        };
    }

    private TrayIcon CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var openItem = new NativeMenuItem("Open Alcoler");
        openItem.Click += (_, _) => ShowMainWindow();
        var exitItem = new NativeMenuItem("Exit");
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
        _isExiting = true;
        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        desktop.Shutdown();
    }
}
