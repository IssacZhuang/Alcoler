using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Alcoler.Tests.Views;

public class ToastRenderProbe
{
    [AvaloniaFact]
    public async Task RenderWorkbenchToast()
    {
        using var viewModel = ShellSmokeTests.CreateWorkbench();
        var window = new MainWindow(viewModel) { Width = 1440, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        viewModel.Banner = "MCP settings saved. They apply to new sessions; open sessions keep their current tools.";
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
        window.UpdateLayout();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(@"C:\Projects\Alcoler\artifacts\toast-probe.png");
        window.Close();
    }
}
