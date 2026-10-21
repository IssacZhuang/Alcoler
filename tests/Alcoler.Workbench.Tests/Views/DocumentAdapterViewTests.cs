using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;
using Alcoler.Workbench.Controls;
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

    [AvaloniaFact]
    public async Task AgentActivityClick_OpensLiveChildTimelineWithDisclosuresAndDisposesOnClose()
    {
        EnsureTheme();
        var runtime = new FakeAgentRuntime();
        var parentId = StrongId.NewSessionId();
        var childId = StrongId.NewSessionId();
        var startedAt = DateTimeOffset.UtcNow;
        var activity = AgentActivityRow(childId, "Inspect child transcript", startedAt);
        runtime.RegisterSession(parentId, new ConversationSnapshot(0, 1,
            [new UserMessageRow("parent-user", "Keep the parent conversation active", startedAt), activity],
            TestSnapshots.State() with { Title = "Parent conversation" }));
        var reasoningPart = new ReasoningPart(StrongId.NewPartId(), "Inspect the child workspace first.");
        var textPart = new TextPart(StrongId.NewPartId(), "The child is reviewing the change.");
        var assistant = new AssistantMessageRow("child-assistant", StrongId.NewMessageId(),
            [reasoningPart, textPart], true, startedAt);
        var tool = new ToolCallRow("child-tool", StrongId.NewToolCallId(), "Bash",
            JsonSerializer.SerializeToElement(new { command = "inspect-child", description = "Inspect child files" }),
            ToolCallStatus.Failed, [new ToolResultText("Child tool output: retry needed.")],
            true, TimeSpan.FromMilliseconds(420), startedAt);
        var childProjection = runtime.RegisterSession(childId, new ConversationSnapshot(0, 1,
            [assistant, tool], TestSnapshots.State(TurnPhase.Streaming) with { Title = "Child conversation" }));
        using var workbench = new WorkbenchViewModel(runtime, AvaloniaUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenSessionAsync(parentId);
        var parent = Assert.IsType<ConversationViewModel>(workbench.Active);
        var window = new MainWindow(workbench) { Width = 1400, Height = 900 };
        window.Show();
        try
        {
            await WaitUntilAsync(window, () => parent.Status.Agents.Count == 1 && parent.Items.Count == 2);
            var parentTimeline = Assert.Single(window.GetVisualDescendants().OfType<TimelineView>());
            Assert.Same(parent, parentTimeline.DataContext);
            var status = window.FindControl<ConversationStatusView>("SessionStatus")!;
            var conversationArea = window.FindControl<Grid>("ConversationArea")!;
            var statusOrigin = status.TranslatePoint(default, conversationArea)!.Value;
            Assert.True(status.IsEffectivelyVisible);
            Assert.InRange(statusOrigin.Y, 15, 17);
            Assert.InRange(statusOrigin.X + status.Bounds.Width, conversationArea.Bounds.Width - 17, conversationArea.Bounds.Width - 15);
            await ExpandAgentsAsync(window, status);
            await ClickAsync(window, AgentActivityButton(status, childId));
            await WaitUntilAsync(window, () => workbench.Documents.ActiveDocument?.Content is SubagentDocumentViewModel
                && childProjection.SubscribeCount == 1
                && window.GetVisualDescendants().OfType<TimelineView>().Count() == 2);

            var document = Assert.Single(workbench.Documents.Items);
            Assert.Same(document, workbench.Documents.ActiveDocument);
            Assert.Equal("subagent", document.Kind);
            Assert.Equal($"agent:{childId.Value}", document.Id);
            Assert.Equal("Inspect child transcript", document.Title);
            Assert.Equal(parentId.Value, document.OwnerId);
            Assert.Equal(_root, document.WorkspaceRoot);
            var transcript = Assert.IsType<SubagentDocumentViewModel>(document.Content);
            Assert.False(transcript.IsDisposed);
            Assert.Equal(childId, transcript.Conversation.SessionId);
            Assert.NotSame(parent, transcript.Conversation);
            Assert.Same(parent, workbench.Active);
            Assert.Equal(parentId, workbench.Active!.SessionId);
            var documentsView = Assert.Single(window.GetVisualDescendants().OfType<DocumentWorkspaceView>());
            var childTimeline = Assert.Single(documentsView.GetVisualDescendants().OfType<TimelineView>());
            Assert.True(workbench.IsSidePaneVisible);
            Assert.True(documentsView.IsEffectivelyVisible);
            Assert.True(childTimeline.IsEffectivelyVisible);
            Assert.Same(transcript.Conversation, childTimeline.DataContext);
            Assert.Same(parent, parentTimeline.DataContext);
            Assert.Equal(2, window.GetVisualDescendants().OfType<TimelineView>().Count());
            var childOrigin = childTimeline.TranslatePoint(default, window)!.Value;
            var parentOrigin = parentTimeline.TranslatePoint(default, window)!.Value;
            Assert.True(childOrigin.X >= parentOrigin.X + parentTimeline.Bounds.Width);
            Assert.True(childTimeline.Bounds.Width > 0 && childTimeline.Bounds.Height > 0);
            Assert.Empty(documentsView.GetVisualDescendants().OfType<FileDocumentView>());
            Assert.Empty(documentsView.GetVisualDescendants().OfType<ComposerView>());
            Assert.Empty(childTimeline.GetVisualDescendants().OfType<TextBox>());
            Assert.Same(parent, Assert.Single(window.GetVisualDescendants().OfType<ComposerView>()).DataContext);
            Assert.Empty(runtime.Submitted);

            var assistantItem = Assert.Single(transcript.Conversation.Items.OfType<AssistantMessageItemViewModel>());
            var toolItem = Assert.Single(transcript.Conversation.Items.OfType<ToolCallItemViewModel>());
            var markdown = Assert.Single(childTimeline.GetVisualDescendants().OfType<ConversationMarkdown>());
            Assert.True(markdown.IsEffectivelyVisible);
            Assert.Equal(textPart.Text, markdown.Markdown);
            Assert.Contains(markdown.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == textPart.Text);
            var thinking = Assert.Single(childTimeline.GetVisualDescendants().OfType<Expander>(),
                expander => expander.Classes.Contains("thinkingBlock"));
            var thinkingToggle = Assert.Single(thinking.GetVisualDescendants().OfType<ToggleButton>(),
                button => button.Classes.Contains("thinkingToggle"));
            Assert.False(thinking.IsExpanded);
            Assert.False(assistantItem.IsReasoningExpanded);
            await ClickAsync(window, thinkingToggle);
            Pump(window);
            Assert.True(thinking.IsExpanded);
            Assert.True(assistantItem.IsReasoningExpanded);
            AssertVisibleText(childTimeline, reasoningPart.Text);

            var toolButton = Assert.Single(childTimeline.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("toolRow") && ReferenceEquals(button.DataContext, toolItem));
            Assert.False(toolItem.IsExpanded);
            AssertVisibleText(toolButton, "Error");
            AssertVisibleText(toolButton, "420ms");
            await ClickAsync(window, toolButton);
            Pump(window);
            Assert.True(toolItem.IsExpanded);
            var toolDetails = Assert.Single(childTimeline.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("toolDetails") && ReferenceEquals(border.DataContext, toolItem));
            Assert.True(toolDetails.IsEffectivelyVisible);
            AssertVisibleText(toolDetails, "Input");
            AssertVisibleText(toolDetails, "Output");
            AssertVisibleText(toolDetails, toolItem.InputJson);
            Assert.Contains("inspect-child", toolItem.InputJson);
            AssertVisibleText(toolDetails, "Child tool output: retry needed.");

            var updatedReasoning = reasoningPart with { Text = "The child found the cause and is checking the result." };
            var updatedText = textPart with { Text = "The child review now includes a live update." };
            childProjection.Emit(new ConversationDelta(2,
                [new RowUpserted(assistant with { Parts = [updatedReasoning, updatedText] })]));
            await WaitUntilAsync(window, () => markdown.Markdown == updatedText.Text
                && childTimeline.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Any(block => block.IsEffectivelyVisible && block.Text == updatedReasoning.Text));
            Assert.True(thinking.IsExpanded);
            Assert.True(assistantItem.IsReasoningExpanded);
            Assert.True(toolItem.IsExpanded);
            Assert.Contains(markdown.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == updatedText.Text);

            childProjection.Emit(new ConversationDelta(3,
                [new RowUpserted(tool with
                {
                    Output = [new ToolResultText("Live child tool output after retry.")],
                    Duration = TimeSpan.FromMilliseconds(840),
                })]));
            await WaitUntilAsync(window, () => toolItem.OutputText == "Live child tool output after retry."
                && toolItem.DurationText == "840ms");
            Assert.Same(assistantItem, Assert.Single(transcript.Conversation.Items.OfType<AssistantMessageItemViewModel>()));
            Assert.Same(toolItem, Assert.Single(transcript.Conversation.Items.OfType<ToolCallItemViewModel>()));
            Assert.True(thinking.IsExpanded);
            Assert.True(toolItem.IsExpanded);
            Assert.True(toolDetails.IsEffectivelyVisible);
            AssertVisibleText(childTimeline, updatedReasoning.Text);
            AssertVisibleText(toolDetails, "Live child tool output after retry.");
            AssertVisibleText(toolButton, "Error");
            AssertVisibleText(toolButton, "840ms");
            Assert.Same(parent, workbench.Active);
            Assert.DoesNotContain(parent.Items, item => item.RowId is "child-assistant" or "child-tool");

            await ClickAsync(window, AgentActivityButton(status, childId));
            Pump(window);
            Assert.Same(document, workbench.Documents.ActiveDocument);
            Assert.Same(document, Assert.Single(workbench.Documents.Items));
            Assert.Same(childTimeline, Assert.Single(documentsView.GetVisualDescendants().OfType<TimelineView>()));
            Assert.Equal(1, childProjection.SubscribeCount);
            Assert.True(thinking.IsExpanded);
            Assert.True(toolItem.IsExpanded);
            Assert.False(transcript.IsDisposed);
            Assert.Empty(runtime.Submitted);

            var close = Assert.Single(documentsView.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("documentClose") && ReferenceEquals(button.DataContext, document));
            await ClickAsync(window, close);
            await WaitUntilAsync(window, () => transcript.IsDisposed && workbench.Documents.Items.Count == 0);
            Assert.Null(workbench.Documents.ActiveDocument);
            Assert.False(workbench.IsSidePaneVisible);
            Assert.Single(window.GetVisualDescendants().OfType<TimelineView>());
            Assert.Same(parent, workbench.Active);
            Assert.Same(parent, parentTimeline.DataContext);
            Assert.Empty(runtime.Submitted);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EmptyAgentActivityClick_ShowsAgentPlaceholderUntilFirstLiveMessage()
    {
        EnsureTheme();
        var runtime = new FakeAgentRuntime();
        var parentId = StrongId.NewSessionId();
        var childId = StrongId.NewSessionId();
        var startedAt = DateTimeOffset.UtcNow;
        runtime.RegisterSession(parentId, new ConversationSnapshot(0, 1,
            [AgentActivityRow(childId, "Waiting child", startedAt)], TestSnapshots.State()));
        var childProjection = runtime.RegisterSession(childId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        using var workbench = new WorkbenchViewModel(runtime, AvaloniaUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenSessionAsync(parentId);
        var parent = Assert.IsType<ConversationViewModel>(workbench.Active);
        var window = new MainWindow(workbench) { Width = 1400, Height = 900 };
        window.Show();
        try
        {
            await WaitUntilAsync(window, () => parent.Status.Agents.Count == 1);
            var status = window.FindControl<ConversationStatusView>("SessionStatus")!;
            await ExpandAgentsAsync(window, status);
            await ClickAsync(window, AgentActivityButton(status, childId));
            await WaitUntilAsync(window, () => workbench.Documents.ActiveDocument?.Content is SubagentDocumentViewModel
                && childProjection.SubscribeCount == 1);
            var document = Assert.Single(workbench.Documents.Items);
            var transcript = Assert.IsType<SubagentDocumentViewModel>(document.Content);
            var documentsView = Assert.Single(window.GetVisualDescendants().OfType<DocumentWorkspaceView>());
            var timeline = Assert.Single(documentsView.GetVisualDescendants().OfType<TimelineView>());
            Assert.Same(transcript.Conversation, timeline.DataContext);
            Assert.Equal(childId, transcript.Conversation.SessionId);
            Assert.Empty(transcript.Conversation.Items);
            AssertVisibleText(timeline, "No agent output yet");
            AssertVisibleText(timeline, "This agent's messages, thinking and tool calls will appear here.");
            Assert.DoesNotContain(timeline.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "What can I help you with?");
            Assert.Empty(documentsView.GetVisualDescendants().OfType<FileDocumentView>());
            Assert.Empty(documentsView.GetVisualDescendants().OfType<ComposerView>());
            Assert.Empty(timeline.GetVisualDescendants().OfType<TextBox>());
            Assert.Same(parent, workbench.Active);
            Assert.Empty(runtime.Submitted);

            childProjection.Emit(new ConversationDelta(2,
                [new RowAppended(new AssistantMessageRow("first-child-message", StrongId.NewMessageId(),
                    [new TextPart(StrongId.NewPartId(), "The first child output has arrived.")], false, startedAt))]));
            await WaitUntilAsync(window, () => timeline.GetVisualDescendants().OfType<ConversationMarkdown>()
                .Any(markdown => markdown.IsEffectivelyVisible && markdown.Markdown == "The first child output has arrived."));
            Assert.DoesNotContain(timeline.GetVisualDescendants().OfType<TextBlock>(),
                block => block.IsEffectivelyVisible && block.Text == "No agent output yet");
            Assert.Same(timeline, Assert.Single(documentsView.GetVisualDescendants().OfType<TimelineView>()));
            Assert.Same(parent, workbench.Active);
            Assert.Empty(runtime.Submitted);
        }
        finally
        {
            window.Close();
        }
    }

    private static SystemNoticeRow AgentActivityRow(SessionId childId, string title, DateTimeOffset startedAt) =>
        new("child-activity", "subagent", "Child agent started", new SubagentState(StrongId.NewSubagentId(),
            childId, title, SubagentStatus.Running, null, startedAt, null));

    private static Button AgentActivityButton(ConversationStatusView status, SessionId childId) =>
        Assert.Single(status.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("activityRow")
            && button.DataContext is StatusActivity { ChildSessionId: var id } && id == childId);

    private static async Task ExpandAgentsAsync(Window window, ConversationStatusView status)
    {
        if (!status.IsExpanded)
        {
            await ClickAsync(window, status.FindControl<Button>("SummaryButton")!);
            Pump(window);
        }
        var agents = status.FindControl<Expander>("AgentsSection")!;
        Assert.False(agents.IsExpanded);
        await ClickAsync(window, Assert.Single(agents.GetVisualDescendants().OfType<ToggleButton>(),
            button => button.Classes.Contains("sectionHeader")));
        Pump(window);
        Assert.True(status.IsExpanded);
        Assert.True(agents.IsExpanded);
    }

    private static void AssertVisibleText(Control root, string expected) =>
        Assert.Contains(root.GetVisualDescendants().OfType<TextBlock>(),
            block => block.IsEffectivelyVisible && block.Text == expected);

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
