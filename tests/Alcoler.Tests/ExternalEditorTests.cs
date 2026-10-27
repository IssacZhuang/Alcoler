using Alcoler.Ui;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class ExternalEditorTests
{
    private static readonly ExternalEditor Editor = new("vscode", "VS Code", "Code.exe");

    [Fact]
    public void OpenFileExternally_ResolvesWorkspaceRelativePathAndUsesSelectedEditor()
    {
        var root = Directory.CreateTempSubdirectory("alcoler-external-").FullName;
        try
        {
            var launcher = new RecordingExternalEditorLauncher();
            using var workbench = CreateWorkbench(launcher);
            workbench.ShowNewTask(root);

            workbench.OpenFileExternally(Path.Combine("src", "AgentRuntime.cs"), Editor);

            var opened = Assert.Single(launcher.Opened);
            Assert.Same(Editor, opened.Editor);
            Assert.Equal(Path.Combine(root, "src", "AgentRuntime.cs"), opened.Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenFileExternally_KeepsAbsolutePath()
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alcoler-absolute.cs"));

        workbench.OpenFileExternally(path, Editor);

        Assert.Equal(path, Assert.Single(launcher.Opened).Path);
    }

    [Fact]
    public void OpenFileWithDefaultApp_DelegatesResolvedPath()
    {
        var root = Directory.CreateTempSubdirectory("alcoler-external-").FullName;
        try
        {
            var launcher = new RecordingExternalEditorLauncher();
            using var workbench = CreateWorkbench(launcher);
            workbench.ShowNewTask(root);

            workbench.OpenFileWithDefaultApp("README.md");

            Assert.Equal(Path.Combine(root, "README.md"), Assert.Single(launcher.DefaultOpened));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenFileExternally_ReportsLaunchFailureInBanner()
    {
        var launcher = new RecordingExternalEditorLauncher
        {
            OpenError = new InvalidOperationException("launch failed"),
        };
        using var workbench = CreateWorkbench(launcher);

        workbench.OpenFileExternally("AgentRuntime.cs", Editor);

        Assert.Contains("AgentRuntime.cs", workbench.Banner);
        Assert.Contains("VS Code", workbench.Banner);
        Assert.Contains("launch failed", workbench.Banner);
    }

    [Fact]
    public void ExternalEditors_ExposeDetectedCatalog()
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);

        Assert.Equal(launcher.Editors, workbench.ExternalEditors);
    }

    private static WorkbenchViewModel CreateWorkbench(IExternalEditorLauncher launcher) =>
        new(new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]),
            externalEditorLauncher: launcher);

    private sealed class RecordingExternalEditorLauncher : IExternalEditorLauncher
    {
        public IReadOnlyList<ExternalEditor> Editors { get; } = [Editor];

        public List<(ExternalEditor Editor, string Path)> Opened { get; } = [];

        public List<string> DefaultOpened { get; } = [];

        public Exception? OpenError { get; init; }

        public void OpenDefault(string path) => DefaultOpened.Add(path);

        public void Open(ExternalEditor editor, string path)
        {
            if (OpenError is not null)
            {
                throw OpenError;
            }
            Opened.Add((editor, path));
        }
    }
}
