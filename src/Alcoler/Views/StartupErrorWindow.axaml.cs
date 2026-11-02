using System.Diagnostics;
using Alcoler.Localization;
using Alcoler.Logging;
using Alcoler.Ui;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;

namespace Alcoler.Views;

public partial class StartupErrorWindow : Window
{
    private readonly string _logDirectory;

    public StartupErrorWindow()
    {
        InitializeComponent(true);
        _logDirectory = AppLog.DirectoryPath;
    }

    internal StartupErrorWindow(string message, string logDirectory) : this()
    {
        WindowChrome.Attach(this);
        _logDirectory = logDirectory;
        MessageText.Text = message;
        LogPathText.Text = Loc.Get("error_log_location", logDirectory);
    }

    private void OnOpenLogsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLog.CreateLogger(nameof(StartupErrorWindow))
                .LogError(exception, "Opening the log directory failed.");
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
