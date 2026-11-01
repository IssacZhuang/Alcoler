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
    public async Task OpenMarkdownLink_ResolvesRelativePathInExplicitWorkspace(string target, string relativePath)
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alcoler-link-origin"));
        workbench.ShowNewTask(Path.Combine(root, "other-workspace"));

        var images = new List<(string Path, string MimeType)>();
        await workbench.OpenMarkdownLinkAsync(target, root, openImage: (path, mimeType) =>
        {
            images.Add((path, mimeType));
            return Task.FromResult(true);
        });

        Assert.Equal((Path.GetFullPath(relativePath, root), "image/png"), Assert.Single(images));
        Assert.Empty(launcher.DefaultOpened);
    }

    [Fact]
    public async Task OpenMarkdownLink_KeepsAbsolutePathsAndDecodesFileUris()
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "alcoler link 四朝向.png"));

        var images = new List<string>();
        Task<bool> OpenImage(string imagePath, string mimeType)
        {
            Assert.Equal("image/png", mimeType);
            images.Add(imagePath);
            return Task.FromResult(true);
        }
        await workbench.OpenMarkdownLinkAsync(path, openImage: OpenImage);
        await workbench.OpenMarkdownLinkAsync(new Uri(path).AbsoluteUri, openImage: OpenImage);

        Assert.Equal(new[] { path, path }, images);
        Assert.Empty(launcher.DefaultOpened);
    }

    [Theory]
    [InlineData("preview.PNG", "image/png")]
    [InlineData("preview.jpg", "image/jpeg")]
    [InlineData("preview.JPEG", "image/jpeg")]
    [InlineData("preview.gif", "image/gif")]
    [InlineData("preview.webp", "image/webp")]
    [InlineData("preview.bmp", "image/bmp")]
    public async Task OpenMarkdownLink_SupportedImageFormatsUseInternalViewer(string name, string expectedMimeType)
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);
        var images = new List<(string Path, string MimeType)>();

        await workbench.OpenMarkdownLinkAsync(name, openImage: (path, mimeType) =>
        {
            images.Add((path, mimeType));
            return Task.FromResult(true);
        });

        Assert.Equal((Path.GetFullPath(name, workbench.CurrentWorkspaceRoot), expectedMimeType), Assert.Single(images));
        Assert.Empty(launcher.DefaultOpened);
        Assert.DoesNotContain(workbench.Documents.Items, document => document.Kind == "file");
    }

    [Fact]
    public async Task OpenMarkdownLink_ImageRejectedByViewerUsesDefaultApplication()
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);

        await workbench.OpenMarkdownLinkAsync("unsupported.png", openImage: (_, _) => Task.FromResult(false));

        Assert.Equal(Path.GetFullPath("unsupported.png", workbench.CurrentWorkspaceRoot), Assert.Single(launcher.DefaultOpened));
        Assert.Null(workbench.Banner);
    }

    [Fact]
    public async Task OpenMarkdownLink_ImageWithoutViewerUsesDefaultApplication()
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);

        await workbench.OpenMarkdownLinkAsync("preview.png");

        Assert.Equal(Path.GetFullPath("preview.png", workbench.CurrentWorkspaceRoot), Assert.Single(launcher.DefaultOpened));
    }

    [Theory]
    [InlineData("configuration.json")]
    [InlineData("script.cs")]
    [InlineData("README.md")]
    [InlineData("configuration.cfg")]
    [InlineData("README")]
    public async Task OpenMarkdownLink_ReadableTextUsesInternalFileDocument(string name)
    {
        var root = Directory.CreateTempSubdirectory("alcoler-link-text-").FullName;
        try
        {
            var path = Path.Combine(root, name);
            const string content = "Readable configuration and source: 中文";
            await File.WriteAllTextAsync(path, content);
            var launcher = new RecordingExternalEditorLauncher();
            using var workbench = CreateWorkbench(launcher);
            workbench.ShowNewTask(root);
            var ownerId = workbench.CurrentDocumentOwnerId;

            await workbench.OpenMarkdownLinkAsync(name);

            var document = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
            var preview = Assert.IsType<TextDocumentViewModel>(document.Content);
            Assert.Equal(path, preview.FilePath);
            Assert.Equal(content, preview.Text);
            Assert.Null(preview.Error);
            Assert.Equal(root, document.WorkspaceRoot);
            Assert.Equal(ownerId, document.OwnerId);
            Assert.Empty(launcher.DefaultOpened);

            await workbench.OpenMarkdownLinkAsync(new Uri(path).AbsoluteUri);

            Assert.Same(document, workbench.Documents.ActiveDocument);
            Assert.Single(workbench.Documents.Items, item => item.Kind == "file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenMarkdownLink_TextKeepsOriginWorkspaceAndOwnerWithoutRedirectingCurrentContext()
    {
        var root = Directory.CreateTempSubdirectory("alcoler-link-owner-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "configuration.txt"), "original workspace");
            var launcher = new RecordingExternalEditorLauncher();
            using var workbench = CreateWorkbench(launcher);
            workbench.ShowNewTask(root);
            var ownerId = workbench.CurrentDocumentOwnerId;
            var otherRoot = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
            workbench.ShowNewTask(otherRoot);
            var active = workbench.OpenTextDocument("other", "Other", "leave selected");

            await workbench.OpenMarkdownLinkAsync("configuration.txt", root, ownerId);

            var document = Assert.Single(workbench.Documents.Items, item => item.Kind == "file");
            Assert.Equal(root, document.WorkspaceRoot);
            Assert.Equal(ownerId, document.OwnerId);
            Assert.Equal("original workspace", Assert.IsType<TextDocumentViewModel>(document.Content).Text);
            Assert.Same(active, workbench.Documents.ActiveDocument);
            Assert.DoesNotContain(document, workbench.Documents.VisibleItems);
            Assert.Equal(otherRoot, workbench.CurrentWorkspaceRoot);
            Assert.Empty(launcher.DefaultOpened);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("binary.kra")]
    [InlineData("invalid-encoding.txt")]
    [InlineData("large.txt")]
    [InlineData("missing.txt")]
    [InlineData("directory")]
    public async Task OpenMarkdownLink_UnsupportedFilesUseDefaultApplicationWithoutCreatingPreviewTabs(string name)
    {
        var root = Directory.CreateTempSubdirectory("alcoler-link-unsupported-").FullName;
        try
        {
            var path = Path.Combine(root, name);
            if (name == "binary.kra")
                await File.WriteAllBytesAsync(path, [0x50, 0x4b, 3, 4, 0, 0]);
            else if (name == "invalid-encoding.txt")
                await File.WriteAllBytesAsync(path, [0xff, 0xff]);
            else if (name == "large.txt")
                await File.WriteAllTextAsync(path, new string('a', TextDocumentViewModel.MaxFileBytes + 1));
            else if (name == "directory")
                Directory.CreateDirectory(path);
            var launcher = new RecordingExternalEditorLauncher();
            using var workbench = CreateWorkbench(launcher);
            workbench.ShowNewTask(root);

            await workbench.OpenMarkdownLinkAsync(name);

            Assert.Equal(path, Assert.Single(launcher.DefaultOpened));
            Assert.DoesNotContain(workbench.Documents.Items, document => document.Kind == "file");
            Assert.Null(workbench.Banner);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://example.com/preview?q=four%20facings#daylight")]
    [InlineData("http://localhost:42101/preview")]
    [InlineData("mailto:example@example.com")]
    public async Task OpenMarkdownLink_KeepsNonFileUris(string target)
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);

        await workbench.OpenMarkdownLinkAsync(target);

        Assert.Equal(target, Assert.Single(launcher.DefaultOpened));
    }

    [Fact]
    public async Task OpenMarkdownLink_ReportsUrlLaunchFailureInBanner()
    {
        var launcher = new RecordingExternalEditorLauncher { OpenError = new System.ComponentModel.Win32Exception(2) };
        using var workbench = CreateWorkbench(launcher);
        const string target = "https://example.com/preview";

        await workbench.OpenMarkdownLinkAsync(target);

        Assert.Empty(launcher.DefaultOpened);
        Assert.Contains(target, workbench.Banner);
        Assert.Contains(launcher.OpenError.Message, workbench.Banner);
    }

    [Fact]
    public async Task OpenMarkdownLink_ReportsInvalidPathInBanner()
    {
        var launcher = new RecordingExternalEditorLauncher();
        using var workbench = CreateWorkbench(launcher);

        await workbench.OpenMarkdownLinkAsync("Artwork/invalid\0.png");

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
