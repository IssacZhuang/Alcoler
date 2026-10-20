using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Workbench.Git;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibGit2Sharp;
using Xunit;

namespace Alcoler.Workbench.Tests.Views;

public sealed class DocumentAdapterViewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("alcoler-document-views-").FullName;

    [AvaloniaFact]
    public async Task FileView_RendersActualTextPathReloadWrapAndErrorsReadOnly()
    {
        EnsureTheme();
        var path = Path.Combine(_root, "preview.txt");
        await File.WriteAllTextAsync(path, "first file content");
        using var preview = TextDocumentViewModel.ForFile(path, AvaloniaUiScheduler.Instance);
        await preview.ReloadAsync();
        var view = new FileDocumentView { DataContext = preview };
        var window = new Window { Width = 500, Height = 400, Content = view };
        window.Show();
        try
        {
            Pump(window);
            var text = view.FindControl<SelectableTextBlock>("PART_DocumentText")!;
            Assert.Equal("first file content", text.Text);
            Assert.Equal(path, view.FindControl<TextBlock>("PART_DocumentPath")!.Text);
            Assert.Empty(view.GetVisualDescendants().OfType<TextBox>());
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            view.FindControl<ToggleButton>("PART_DocumentWrap")!.IsChecked = false;
            Pump(window);
            Assert.False(preview.Wrap);
            Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
            Assert.Equal(ScrollBarVisibility.Auto,
                view.FindControl<ScrollViewer>("PART_DocumentScroll")!.HorizontalScrollBarVisibility);

            await File.WriteAllTextAsync(path, "reloaded file content");
            var reload = view.FindControl<Button>("PART_DocumentReload")!;
            var previousLoad = preview.LoadingTask;
            await ClickAsync(window, reload);
            Assert.NotSame(previousLoad, preview.LoadingTask);
            await preview.LoadingTask;
            Pump(window);
            Assert.Equal("reloaded file content", text.Text);
            Assert.Equal("reloaded file content", await File.ReadAllTextAsync(path));

            File.Delete(path);
            await preview.ReloadAsync();
            Pump(window);
            var error = view.FindControl<SelectableTextBlock>("PART_DocumentError")!;
            Assert.True(error.IsEffectivelyVisible);
            Assert.Contains("Could not read", error.Text);
            Assert.Empty(text.Text!);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task GitFileButton_OpensGenericFileFromRepositoryRootWithoutCheckingOut()
    {
        EnsureTheme();
        Repository.Init(_root);
        var nestedWorkspace = Directory.CreateDirectory(Path.Combine(_root, "nested")).FullName;
        var filePath = Path.Combine(_root, "scratch.txt");
        await File.WriteAllTextAsync(filePath, "opened from an existing git file row");
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), AvaloniaUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(nestedWorkspace);
        workbench.ShowGitPaneCommand.Execute(null);
        var gitDocument = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var git = Assert.IsType<GitPaneViewModel>(gitDocument.Content);
        var window = new MainWindow(workbench) { Width = 1200, Height = 800 };
        window.Show();
        try
        {
            await WaitUntilAsync(window, () => !git.IsLoading && git.Untracked.Any(file => file.Path == "scratch.txt"));
            var branch = git.CurrentBranch;
            var gitView = Assert.Single(window.GetVisualDescendants().OfType<GitPaneView>());
            var fileButton = Assert.Single(gitView.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("fileChangeRow")
                    && button.DataContext is GitFileChangeViewModel { Path: "scratch.txt" });
            await ClickAsync(window, fileButton);
            var fileDocument = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
            var preview = Assert.IsType<TextDocumentViewModel>(fileDocument.Content);
            await preview.LoadingTask;
            Pump(window);

            Assert.Equal("file", fileDocument.Kind);
            Assert.Equal(nestedWorkspace, fileDocument.WorkspaceRoot);
            Assert.Equal(filePath, preview.FilePath);
            Assert.Equal(workbench.CurrentDocumentOwnerId, fileDocument.OwnerId);
            var fileView = Assert.Single(window.GetVisualDescendants().OfType<FileDocumentView>());
            Assert.True(fileView.IsEffectivelyVisible);
            Assert.Equal("opened from an existing git file row",
                fileView.FindControl<SelectableTextBlock>("PART_DocumentText")!.Text);
            Assert.Equal(branch, git.CurrentBranch);
            Assert.Contains(gitDocument, workbench.Documents.Items);
            Assert.Empty(fileView.GetVisualDescendants().OfType<TextBox>());

            workbench.Documents.Activate(gitDocument);
            Pump(window);
            await ClickAsync(window, fileButton);
            Assert.Same(fileDocument, workbench.Documents.ActiveDocument);
            Assert.Single(workbench.Documents.Items, document => document.Kind == "file");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ToolOutputAction_OpensPlainTextDocumentWithoutExecutingToolAgain()
    {
        EnsureTheme();
        var runtime = new FakeAgentRuntime();
        var sessionId = StrongId.NewSessionId();
        var row = new ToolCallRow("stable-row", StrongId.NewToolCallId(), "Bash",
            JsonDocument.Parse("{\"command\":\"list-files\"}").RootElement,
            ToolCallStatus.Succeeded, [new ToolResultText("tool output text\nsecond line")],
            IsError: false, Duration: TimeSpan.FromMilliseconds(20), StartedAt: DateTimeOffset.UtcNow);
        runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [row], TestSnapshots.State()));
        using var workbench = new WorkbenchViewModel(runtime, AvaloniaUiScheduler.Instance, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(sessionId);
        var window = new MainWindow(workbench) { Width = 1200, Height = 800 };
        window.Show();
        try
        {
            Pump(window);
            var timeline = Assert.Single(window.GetVisualDescendants().OfType<TimelineView>());
            var rowButton = Assert.Single(timeline.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("toolRow"));
            await ClickAsync(window, rowButton);
            Pump(window);
            var outputButton = Assert.Single(timeline.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("openToolOutput"));
            Assert.True(outputButton.IsEffectivelyVisible);
            await ClickAsync(window, outputButton);
            Pump(window);

            var document = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
            Assert.Equal("text", document.Kind);
            Assert.Equal("Bash output", document.Title);
            Assert.Equal(sessionId.Value, document.OwnerId);
            var text = Assert.IsType<TextDocumentViewModel>(document.Content);
            Assert.Null(text.FilePath);
            Assert.Equal("tool output text\nsecond line", text.Text);
            var fileView = Assert.Single(window.GetVisualDescendants().OfType<FileDocumentView>());
            Assert.Equal(text.Text, fileView.FindControl<SelectableTextBlock>("PART_DocumentText")!.Text);
            Assert.False(fileView.FindControl<Button>("PART_DocumentReload")!.IsVisible);
            Assert.Empty(runtime.Submitted);
            await ClickAsync(window, outputButton);
            Assert.Same(document, workbench.Documents.ActiveDocument);
            Assert.Single(workbench.Documents.Items);
        }
        finally
        {
            window.Close();
        }
    }

    private static void EnsureTheme()
    {
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        if (!app.Styles.OfType<FluentTheme>().Any())
        {
            app.Styles.Add(new FluentTheme());
        }
    }

    private static async Task ClickAsync(Window window, Button button)
    {
        Pump(window);
        var visual = ElementComposition.GetElementVisual(window);
        Assert.NotNull(visual);
        var rendered = visual.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Assert.True(new Rect(window.ClientSize).Contains(point));
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button),
            $"Expected {button.Name} at {point}, hit {hit.GetType().Name}.");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static async Task WaitUntilAsync(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            Pump(window);
            if (condition())
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, "Document view update did not arrive within 10 seconds.");
            await Task.Delay(10);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
