using Alcoler.Ui;
using Alcoler.ViewModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;

namespace Alcoler.Tests.Views;

public sealed class WorkspaceChooserDiagTests
{
    private readonly ITestOutputHelper _output;

    public WorkspaceChooserDiagTests(ITestOutputHelper output) => _output = output;

    [AvaloniaFact]
    public void Chooser_ShowsWorkspaceName_ForAsciiAndCjkRoots()
    {
        var asciiRoot = Path.Combine(Path.GetTempPath(), "alcoler-chooser-ascii");
        var cjkRoot = Path.Combine(Path.GetTempPath(), "alcoler-测试工作区");
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));

        workbench.ShowNewTask(asciiRoot);
        var window = new MainWindow(workbench);
        window.Show();
        try
        {
            Dump(window, "ascii");

            workbench.ShowNewTask(cjkRoot);
            Dump(window, "cjk");
        }
        finally
        {
            window.Close();
        }
    }

    private void Dump(MainWindow window, string label)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var chooser = window.FindControl<Button>("WorkspaceChooser")!;
        _output.WriteLine($"[{label}] draft root={((WorkbenchViewModel)window.DataContext!).Draft?.WorkspaceRoot}");
        _output.WriteLine($"[{label}] draft name={((WorkbenchViewModel)window.DataContext!).Draft?.WorkspaceName}");
        foreach (var text in chooser.GetVisualDescendants().OfType<TextBlock>())
        {
            _output.WriteLine($"[{label}] text=[{text.Text}] bounds={text.Bounds} visible={text.IsVisible} foreground={text.Foreground}");
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        var path = Path.Combine(@"C:\Projects\Alcoler\artifacts", $"chooser-{label}.png");
        frame?.Save(path);
        _output.WriteLine($"[{label}] frame={path} exists={File.Exists(path)}");
    }
}
