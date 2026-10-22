using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Alcoler.Browser.IntegrationTests;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("FAIL Native integration requires Windows and Microsoft Edge WebView2 Runtime.");
            return 1;
        }
        try
        {
            return AppBuilder.Configure<IntegrationApp>().UsePlatformDetect()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL Native desktop startup{Environment.NewLine}{ex}");
            return 1;
        }
    }
}

public sealed class IntegrationApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new InvalidOperationException("A native classic desktop lifetime is required.");

        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new Window
        {
            Title = "Alcoler native browser integration tests",
            Width = 1100,
            Height = 820,
            Content = new TextBlock { Text = "Running real WebView2 integration assertions...", Margin = new Thickness(16) }
        };
        desktop.MainWindow = window;
        var finished = false;
        window.Closing += (_, closing) => closing.Cancel = !finished;
        window.Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            var exitCode = 1;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                Exception? dispatcherError = null;
                DispatcherUnhandledExceptionEventHandler onError = (_, error) =>
                {
                    dispatcherError ??= error.Exception;
                    error.Handled = true;
                    deadline.Cancel();
                };
                Dispatcher.UIThread.UnhandledException += onError;
                try
                {
                    await NativeBrowserSuite.RunAsync(window, deadline.Token);
                    if (dispatcherError is not null) throw new InvalidOperationException("Native dispatcher failed.", dispatcherError);
                    exitCode = 0;
                }
                finally
                {
                    Dispatcher.UIThread.UnhandledException -= onError;
                    if (dispatcherError is not null) Console.Error.WriteLine($"FAIL Native dispatcher{Environment.NewLine}{dispatcherError}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL Native browser integration{Environment.NewLine}{ex}");
            }
            finally
            {
                finished = true;
                Environment.ExitCode = exitCode;
                desktop.Shutdown(exitCode);
            }
        });
        base.OnFrameworkInitializationCompleted();
    }
}
