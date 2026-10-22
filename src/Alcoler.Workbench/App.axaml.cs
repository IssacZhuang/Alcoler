using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Alcoler.Workbench;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            _ = RunAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var config = WorkbenchConfig.LoadOrDefault();
        Themes.ThemeSelector.Apply(config.Theme);
        var viewModel = await Composition.BuildWorkbenchAsync(config, CancellationToken.None);
        desktop.MainWindow = new MainWindow(viewModel);
        desktop.Exit += (_, _) => viewModel.Dispose();
    }
}
