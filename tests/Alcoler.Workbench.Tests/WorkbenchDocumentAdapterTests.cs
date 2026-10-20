using System.Collections.Concurrent;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Xunit;

namespace Alcoler.Workbench.Tests;

public sealed class WorkbenchDocumentAdapterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("alcoler-document-adapters-").FullName;

    [Fact]
    public void OpenTextDocument_DeduplicatesSourceWithinOwnerAndKeepsOtherOwnersIsolated()
    {
        var runtime = new FakeAgentRuntime();
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        var firstOwner = workbench.CurrentDocumentOwnerId;
        var first = workbench.OpenTextDocument("tool-output:row-1", "Tool output", "first", "json");
        var duplicate = workbench.OpenTextDocument("tool-output:row-1", "Tool output", "updated", "json");

        Assert.Same(first, duplicate);
        Assert.Equal("updated", Assert.IsType<TextDocumentViewModel>(first.Content).Text);
        Assert.Equal(firstOwner, first.OwnerId);
        Assert.Equal(Path.GetFullPath(_root), first.WorkspaceRoot);
        Assert.Equal("text", first.Kind);
        Assert.True(workbench.Documents.IsVisible);

        var otherRoot = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        workbench.ShowNewTask(otherRoot);
        var other = workbench.OpenTextDocument("tool-output:row-1", "Tool output", "other workspace");
        Assert.NotSame(first, other);
        Assert.NotEqual(first.OwnerId, other.OwnerId);
        Assert.DoesNotContain(first, workbench.Documents.VisibleItems);
        Assert.Contains(other, workbench.Documents.VisibleItems);

        workbench.ShowNewTask(_root);
        Assert.Contains(first, workbench.Documents.VisibleItems);
        Assert.DoesNotContain(other, workbench.Documents.VisibleItems);
        Assert.Equal("updated", Assert.IsType<TextDocumentViewModel>(first.Content).Text);
    }

    [Fact]
    public async Task OpenTextDocument_SameWorkspaceDifferentSessionsDoNotShareOutput()
    {
        var runtime = new FakeAgentRuntime();
        var firstId = StrongId.NewSessionId();
        var secondId = StrongId.NewSessionId();
        runtime.RegisterSession(firstId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        runtime.RegisterSession(secondId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenSessionAsync(firstId);
        var first = workbench.OpenTextDocument("tool-output:same-row", "Result", "session one");

        await workbench.OpenSessionAsync(secondId);
        var second = workbench.OpenTextDocument("tool-output:same-row", "Result", "session two");

        Assert.NotSame(first, second);
        Assert.Equal(firstId.Value, first.OwnerId);
        Assert.Equal(secondId.Value, second.OwnerId);
        Assert.DoesNotContain(first, workbench.Documents.VisibleItems);
        Assert.Contains(second, workbench.Documents.VisibleItems);
        Assert.Empty(runtime.Submitted);
    }

    [Fact]
    public async Task OpenFileDocument_OpensLoadingImmediatelyAndDeduplicatesCanonicalPath()
    {
        var path = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(path, "read-only file contents");
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        var pending = workbench.OpenFileDocumentAsync(path);
        var document = Assert.Single(workbench.Documents.Items, item => item.Kind == "file");
        var preview = Assert.IsType<TextDocumentViewModel>(document.Content);
        Assert.True(preview.IsLoading);
        Assert.Same(document, workbench.Documents.ActiveDocument);
        Assert.Empty(preview.Text);

        await pending;
        scheduler.RunPending();
        var duplicate = await workbench.OpenFileDocumentAsync(Path.Combine(_root, ".", "FILE.txt"));

        Assert.Same(document, duplicate);
        Assert.Single(workbench.Documents.Items, item => item.Kind == "file");
        Assert.Equal("read-only file contents", preview.Text);
        Assert.False(preview.IsLoading);
        Assert.Null(preview.Error);
        Assert.Equal(path, preview.FilePath);
        Assert.Equal("file.txt", document.Title);
        Assert.Equal("read-only file contents", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OpenFileDocument_CompletionDoesNotRedirectCurrentWorkspaceOrOwner()
    {
        var path = Path.Combine(_root, "background.txt");
        await File.WriteAllTextAsync(path, "belongs to original draft");
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        var owner = workbench.CurrentDocumentOwnerId;
        var pending = workbench.OpenFileDocumentAsync("background.txt");
        var original = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var otherRoot = Directory.CreateDirectory(Path.Combine(_root, "navigation")).FullName;
        workbench.ShowNewTask(otherRoot);
        var active = workbench.OpenTextDocument("other", "Other workspace", "leave selected");

        await pending;
        scheduler.RunPending();

        Assert.Same(active, workbench.Documents.ActiveDocument);
        Assert.Equal(otherRoot, workbench.CurrentWorkspaceRoot);
        Assert.DoesNotContain(original, workbench.Documents.VisibleItems);
        Assert.Equal(owner, original.OwnerId);
        Assert.Equal(Path.GetFullPath(_root), original.WorkspaceRoot);
        Assert.Equal("belongs to original draft", Assert.IsType<TextDocumentViewModel>(original.Content).Text);
    }

    [Fact]
    public async Task OpenFileDocument_CloseBeforeCompletionDisposesAndDoesNotReopen()
    {
        var path = Path.Combine(_root, "closed.txt");
        await File.WriteAllTextAsync(path, "late completion");
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        var pending = workbench.OpenFileDocumentAsync(path);
        var document = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var preview = Assert.IsType<TextDocumentViewModel>(document.Content);

        workbench.Documents.Close(document);
        await pending;
        scheduler.RunPending();

        Assert.True(preview.IsDisposed);
        Assert.Empty(preview.Text);
        Assert.DoesNotContain(document, workbench.Documents.Items);
    }

    [Fact]
    public async Task OpenFileDocument_MissingAndInvalidPathsProduceVisibleErrorDocuments()
    {
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        var missing = await workbench.OpenFileDocumentAsync("missing.txt");
        Assert.Contains("Could not read", Assert.IsType<TextDocumentViewModel>(missing.Content).Error);
        Assert.Same(missing, workbench.Documents.ActiveDocument);

        var invalid = await workbench.OpenFileDocumentAsync("bad\0path");
        Assert.Contains("path cannot be opened", Assert.IsType<TextDocumentViewModel>(invalid.Content).Error);
        Assert.Same(invalid, workbench.Documents.ActiveDocument);
    }

    [Fact]
    public async Task StatusDocuments_OpenWithinParentWithoutSubmittingCommands()
    {
        var runtime = new FakeAgentRuntime();
        var parentId = StrongId.NewSessionId();
        var childId = StrongId.NewSessionId();
        runtime.RegisterSession(parentId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        runtime.RegisterSession(childId, new ConversationSnapshot(0, 1,
        [
            new UserMessageRow("user", "Review this change", DateTimeOffset.UtcNow),
            new AssistantMessageRow("assistant", StrongId.NewMessageId(),
                [new Alcoler.Contracts.Conversation.TextPart(StrongId.NewPartId(), "The change is safe.")], false, DateTimeOffset.UtcNow),
        ], TestSnapshots.State()));
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenSessionAsync(parentId);

        var plan = new StatusPlan { RowId = "plan", Title = "Implementation", Markdown = "# Implementation\n\nUse the existing API." };
        workbench.OpenStatusPlan(plan);
        var document = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        Assert.Equal(plan.Markdown, Assert.IsType<TextDocumentViewModel>(document.Content).Text);
        Assert.Equal(parentId.Value, document.OwnerId);
        workbench.OpenStatusPlan(plan);
        Assert.Same(document, workbench.Documents.ActiveDocument);

        await workbench.OpenStatusActivityAsync(new StatusActivity { Id = "agent", Title = "Review", ChildSessionId = childId });
        var transcript = Assert.IsType<TextDocumentViewModel>(workbench.Documents.ActiveDocument!.Content).Text;
        Assert.Contains("## User\n\nReview this change", transcript.Replace("\r\n", "\n"));
        Assert.Contains("The change is safe.", transcript);
        Assert.Equal(parentId, workbench.Active!.SessionId);
        Assert.Equal(parentId.Value, workbench.Documents.ActiveDocument.OwnerId);

        var outputPath = Path.Combine(_root, "job-output.txt");
        await File.WriteAllTextAsync(outputPath, "actual detached output");
        await workbench.OpenStatusActivityAsync(new StatusActivity { Id = "job", Title = "Build", OutputPath = outputPath });
        Assert.Equal("actual detached output", Assert.IsType<TextDocumentViewModel>(workbench.Documents.ActiveDocument!.Content).Text);
        Assert.Empty(runtime.Submitted);
    }

    [Fact]
    public async Task StatusActivity_UnavailableAgentUsesSummaryAndForegroundUsesOutput()
    {
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenStatusActivityAsync(new StatusActivity
        {
            Id = "ended", Title = "Ended agent", ChildSessionId = StrongId.NewSessionId(), OutputText = "Review complete",
        });
        Assert.Equal("Review complete", Assert.IsType<TextDocumentViewModel>(workbench.Documents.ActiveDocument!.Content).Text);
        await workbench.OpenStatusActivityAsync(new StatusActivity { Id = "shell", Title = "Build", OutputText = "partial output" });
        Assert.Equal("partial output", Assert.IsType<TextDocumentViewModel>(workbench.Documents.ActiveDocument!.Content).Text);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class QueuedScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> _actions = new();

        public void Post(Action action) => _actions.Enqueue(action);

        public void RunPending()
        {
            while (_actions.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
