using System;
using System.Threading.Tasks;
using Alcoler.Logging;
using Avalonia;

namespace Alcoler;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            AppLog.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.LogUnhandled(
            "AppDomain",
            e.ExceptionObject as Exception
                ?? new InvalidOperationException("An unknown unhandled exception terminated the process."));
        AppLog.Flush(TimeSpan.FromSeconds(2));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.LogUnhandled("TaskScheduler", e.Exception);
        e.SetObserved();
    }
}
