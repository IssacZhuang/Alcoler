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

    [Theory]
    [InlineData("Artwork/SpriteModels/.tmp/street-trash-bin-update/game-four-facings-daylight.png",
        "Artwork/SpriteModels/.tmp/street-trash-bin-update/game-four-facings-daylight.png")]
    [InlineData("Artwork/game%20four%20facings.png", "Artwork/game four facings.png")]
    [InlineData("Artwork/%E5%9B%9B%E6%9C%9D%E5%90%91.png", "Artwork/四朝向.png")]
    public void OpenMarkdownLink_ResolvesRelativePathInExplicitWorkspace(string target, string relativePath)
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alcoler-link-origin"));
        workbench.ShowNewTask(Path.Combine(root, "other-workspace"));

        workbench.OpenMarkdownLink(target, root);

        Assert.Equal(Path.GetFullPath(relativePath, root), Assert.Single(launcher.DefaultOpened));
    }

    [Fact]
    public void OpenMarkdownLink_KeepsAbsolutePathsAndDecodesFileUris()
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alcoler link 四朝向.png"));

        workbench.OpenMarkdownLink(path);
        workbench.OpenMarkdownLink(new Uri(path).AbsoluteUri);

        Assert.Equal(new[] { path, path }, launcher.DefaultOpened);
    }

    [Theory]
    [InlineData("https://example.com/preview?q=four%20facings#daylight")]
    [InlineData("http://localhost:42101/preview")]
    [InlineData("mailto:example@example.com")]
    public void OpenMarkdownLink_KeepsNonFileUris(string target)
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);

        workbench.OpenMarkdownLink(target);

        Assert.Equal(target, Assert.Single(launcher.DefaultOpened));
    }

    [Fact]
    public void OpenMarkdownLink_ReportsUrlLaunchFailureInBanner()
    {
        var launcher = new RecordingExternalEditorLauncher { OpenError = new System.ComponentModel.Win32Exception(2) };
        using var workbench = CreateWorkbench(launcher);
        const string target = "https://example.com/preview";

        workbench.OpenMarkdownLink(target);

        Assert.Empty(launcher.DefaultOpened);
        Assert.Contains(target, workbench.Banner);
        Assert.Contains(launcher.OpenError.Message, workbench.Banner);
    }

    [Fact]
    public void OpenMarkdownLink_ReportsInvalidPathInBanner()
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);

        workbench.OpenMarkdownLink("Artwork/invalid\0.png");

        Assert.Empty(launcher.DefaultOpened);
        Assert.False(string.IsNullOrWhiteSpace(workbench.Banner));
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

        public void OpenDefault(string path)
        {
            if (OpenError is not null)
            {
                throw OpenError;
            }
            DefaultOpened.Add(path);
        }

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
