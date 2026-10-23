using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

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
    public void PendingPlan_UsesToolCallIdAndSharesTabWithStatusPlan()
    {
        var runtime = new FakeAgentRuntime();
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        var toolCallId = StrongId.NewToolCallId();
        var pending = PendingInteractionViewModel.FromPermission(new PermissionRequest(
            StrongId.NewInteractionId(),
            new SessionId("sess_pending"),
            "ExitPlanMode",
            JsonSerializer.SerializeToElement(new
            {
                summary = "Pending plan",
                plan = "# Pending plan\n\nBody",
            }),
            RiskLevel.Medium,
            "tool requires user interaction",
            AllowsFeedback: true,
            ToolCallId: toolCallId));

        workbench.OpenPendingPlan(pending);
        var document = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        Assert.Equal($"plan:{toolCallId.Value}", document.Id);
        Assert.Equal("plan", document.Kind);
        Assert.Equal("Pending plan", document.Title);
        var content = Assert.IsType<PlanDocumentViewModel>(document.Content);
        Assert.Equal("# Pending plan\n\nBody", content.Markdown);

        var status = new StatusPlan
        {
            RowId = toolCallId.Value,
            Title = "Status title",
            Markdown = "# Status title\n\nUpdated body",
        };
        workbench.OpenStatusPlan(status);
        Assert.Same(document, workbench.Documents.ActiveDocument);
        Assert.Equal("Status title", document.Title);
        Assert.Same(content, document.Content);
        Assert.Equal(status.Markdown, content.Markdown);
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
        Assert.Equal("plan:plan", document.Id);
        Assert.Equal("plan", document.Kind);
        var planDocument = Assert.IsType<PlanDocumentViewModel>(document.Content);
        Assert.Equal(plan.Title, planDocument.Title);
        Assert.Equal(plan.Markdown, planDocument.Markdown);
        Assert.Equal(parentId.Value, document.OwnerId);
        workbench.OpenStatusPlan(plan);
        Assert.Same(document, workbench.Documents.ActiveDocument);

        await workbench.OpenStatusActivityAsync(new StatusActivity { Id = "agent", Title = "Review", ChildSessionId = childId });
        var agentDocument = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var transcript = Assert.IsType<SubagentDocumentViewModel>(agentDocument.Content).Conversation;
        Assert.Equal("subagent", agentDocument.Kind);
        Assert.Equal($"agent:{childId.Value}", agentDocument.Id);
        Assert.Equal(childId, transcript.SessionId);
        Assert.Collection(transcript.Items,
            item => Assert.Equal("Review this change", Assert.IsType<UserMessageItemViewModel>(item).Text),
            item => Assert.Equal("The change is safe.", Assert.IsType<AssistantMessageItemViewModel>(item).MarkdownText));
        Assert.Equal(parentId, workbench.Active!.SessionId);
        Assert.Equal(parentId.Value, agentDocument.OwnerId);
        Assert.Equal(Path.GetFullPath(_root), agentDocument.WorkspaceRoot);

        var outputPath = Path.Combine(_root, "job-output.txt");
        await File.WriteAllTextAsync(outputPath, "actual detached output");
        await workbench.OpenStatusActivityAsync(new StatusActivity { Id = "job", Title = "Build", OutputPath = outputPath });
        Assert.Equal("actual detached output", Assert.IsType<TextDocumentViewModel>(workbench.Documents.ActiveDocument!.Content).Text);
        Assert.Empty(runtime.Submitted);
    }

    [Fact]
    public async Task StatusActivity_SubagentPreservesRichRowsAndLiveDisclosureStateWithoutDuplicateObservations()
    {
        var runtime = new ParentScopedRuntime();
        var parentId = StrongId.NewSessionId();
        var childId = StrongId.NewSessionId();
        runtime.RegisterParent(parentId);
        var assistantRow = new AssistantMessageRow("assistant", StrongId.NewMessageId(),
            [new ReasoningPart(StrongId.NewPartId(), "Check ownership before updating."),
                new TextPart(StrongId.NewPartId(), "Review in progress")], true, DateTimeOffset.UtcNow);
        var toolRow = new ToolCallRow("tool", StrongId.NewToolCallId(), "Bash",
            JsonSerializer.SerializeToElement(new { command = "git status", timeout = 1200 }),
            ToolCallStatus.Running, [new ToolResultText("partial output")], false, null, DateTimeOffset.UtcNow);
        var source = runtime.RegisterChild(parentId, childId, new ConversationSnapshot(0, 1,
        [
            new UserMessageRow("user", "Review the patch", DateTimeOffset.UtcNow),
            assistantRow,
            toolRow,
            new SystemNoticeRow("notice", "compact", "Earlier context was compacted."),
        ], TestSnapshots.State(TurnPhase.ExecutingTools)));
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenSessionAsync(parentId);
        var activity = new StatusActivity { Id = "running-task", Title = "Review", ChildSessionId = childId, CanStop = true };

        await workbench.OpenStatusActivityAsync(activity);

        var document = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var content = Assert.IsType<SubagentDocumentViewModel>(document.Content);
        var conversation = content.Conversation;
        Assert.Equal("subagent", document.Kind);
        Assert.Equal($"agent:{childId.Value}", document.Id);
        Assert.Equal(parentId.Value, document.OwnerId);
        Assert.Equal(Path.GetFullPath(_root), document.WorkspaceRoot);
        Assert.Equal(childId, conversation.SessionId);
        Assert.False(content.IsDisposed);
        Assert.Equal(4, conversation.Items.Count);
        Assert.Equal("Review the patch", Assert.IsType<UserMessageItemViewModel>(conversation.Items[0]).Text);
        var assistant = Assert.IsType<AssistantMessageItemViewModel>(conversation.Items[1]);
        Assert.Equal("Review in progress", assistant.MarkdownText);
        Assert.Equal("Check ownership before updating.", Assert.Single(assistant.ReasoningBlocks));
        Assert.True(assistant.IsStreaming);
        var tool = Assert.IsType<ToolCallItemViewModel>(conversation.Items[2]);
        using (var input = JsonDocument.Parse(tool.InputJson))
        {
            Assert.Equal("git status", input.RootElement.GetProperty("command").GetString());
            Assert.Equal(1200, input.RootElement.GetProperty("timeout").GetInt32());
        }
        Assert.Equal("partial output", tool.OutputText);
        Assert.Equal(ToolCallStatus.Running, tool.Status);
        var notice = Assert.IsType<SystemNoticeItemViewModel>(conversation.Items[3]);
        Assert.Equal("compact", notice.Kind);
        Assert.Equal("Earlier context was compacted.", notice.Text);
        Assert.Equal(TurnPhase.ExecutingTools.ToString(), conversation.PhaseText);
        assistant.IsReasoningExpanded = true;
        tool.IsExpanded = true;
        var observation = Assert.Single(source.Observations);
        await observation.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.RunPending();

        workbench.OpenTextDocument("other", "Other output", "not the agent");
        await workbench.OpenStatusActivityAsync(activity);
        await workbench.OpenStatusActivityAsync(activity with { Id = "completion-notice", Title = "Review complete" });

        Assert.Same(document, workbench.Documents.ActiveDocument);
        Assert.Same(content, document.Content);
        Assert.Single(workbench.Documents.Items, item => item.Kind == "subagent");
        Assert.Single(source.Observations);
        Assert.Single(runtime.ScopedObservations);
        Assert.DoesNotContain(childId, runtime.GenericObservations);
        Assert.DoesNotContain(childId, runtime.Subscriptions);
        Assert.False(observation.IsDisposed);
        source.Projection.Emit(new ConversationDelta(2,
        [
            new RowUpserted(assistantRow with
            {
                Parts = [new ReasoningPart(StrongId.NewPartId(), "Ownership is preserved."),
                    new TextPart(StrongId.NewPartId(), "Review complete")],
                IsStreaming = false,
            }),
            new RowUpserted(toolRow with
            {
                Input = JsonSerializer.SerializeToElement(new { command = "git diff", timeout = 2400 }),
                Status = ToolCallStatus.Failed,
                Output = [new ToolResultText("first result"), new ToolResultText("second result")],
                IsError = true,
                Duration = TimeSpan.FromMilliseconds(125),
            }),
            new RowAppended(new UserMessageRow("follow-up", "Check the tests too", DateTimeOffset.UtcNow)),
            new StatePatched(TestSnapshots.State(TurnPhase.Idle)),
        ]));
        await DeliverUntilAsync(scheduler, () => conversation.Items.Count == 5);

        Assert.Same(assistant, conversation.Items[1]);
        Assert.Same(tool, conversation.Items[2]);
        Assert.Same(notice, conversation.Items[3]);
        Assert.True(assistant.IsReasoningExpanded);
        Assert.True(tool.IsExpanded);
        Assert.Equal("Ownership is preserved.", Assert.Single(assistant.ReasoningBlocks));
        Assert.Equal("Review complete", assistant.MarkdownText);
        Assert.False(assistant.IsStreaming);
        using (var input = JsonDocument.Parse(tool.InputJson))
        {
            Assert.Equal("git diff", input.RootElement.GetProperty("command").GetString());
            Assert.Equal(2400, input.RootElement.GetProperty("timeout").GetInt32());
        }
        Assert.Equal("first result\nsecond result", tool.OutputText);
        Assert.Equal(ToolCallStatus.Failed, tool.Status);
        Assert.True(tool.IsError);
        Assert.Equal("125ms", tool.DurationText);
        Assert.Equal("Check the tests too", Assert.IsType<UserMessageItemViewModel>(conversation.Items[4]).Text);
        Assert.Equal(TurnPhase.Idle.ToString(), conversation.PhaseText);
        Assert.Equal(parentId, workbench.Active!.SessionId);
        Assert.Single(source.Observations);
        AssertNoCommands(runtime);
    }

    [Fact]
    public async Task StatusActivity_EmptyAvailableSubagentRemainsLiveForFirstOutput()
    {
        var runtime = new ParentScopedRuntime();
        var parentId = StrongId.NewSessionId();
        var childId = StrongId.NewSessionId();
        runtime.RegisterParent(parentId);
        var source = runtime.RegisterChild(parentId, childId,
            new ConversationSnapshot(0, 1, [], TestSnapshots.State(TurnPhase.Streaming)));
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenSessionAsync(parentId);

        await workbench.OpenStatusActivityAsync(new StatusActivity { Id = "early", Title = "Starting", ChildSessionId = childId });

        var document = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var content = Assert.IsType<SubagentDocumentViewModel>(document.Content);
        Assert.Empty(content.Conversation.Items);
        Assert.Equal(TurnPhase.Streaming.ToString(), content.Conversation.PhaseText);
        var observation = Assert.Single(source.Observations);
        await observation.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Projection.Emit(new ConversationDelta(2,
            [new RowAppended(new UserMessageRow("first", "First child output", DateTimeOffset.UtcNow))]));
        await DeliverUntilAsync(scheduler, () => content.Conversation.Items.Count == 1);

        Assert.Equal("First child output", Assert.IsType<UserMessageItemViewModel>(Assert.Single(content.Conversation.Items)).Text);
        Assert.Same(document, workbench.Documents.ActiveDocument);
        Assert.False(content.IsDisposed);
        Assert.False(observation.IsDisposed);
        Assert.Single(source.Observations);
        AssertNoCommands(runtime);
    }

    [Fact]
    public async Task StatusActivity_SubagentIsolationAndGapResubscribeKeepCapturedParentAfterNavigation()
    {
        var runtime = new ParentScopedRuntime();
        var firstParentId = StrongId.NewSessionId();
        var secondParentId = StrongId.NewSessionId();
        var childId = StrongId.NewSessionId();
        runtime.RegisterParent(firstParentId);
        runtime.RegisterParent(secondParentId);
        var firstSource = runtime.RegisterChild(firstParentId, childId, new ConversationSnapshot(0, 1,
            [new UserMessageRow("first", "First parent child", DateTimeOffset.UtcNow)], TestSnapshots.State()));
        var secondSource = runtime.RegisterChild(secondParentId, childId, new ConversationSnapshot(0, 1,
            [new UserMessageRow("second", "Second parent child", DateTimeOffset.UtcNow)], TestSnapshots.State()));
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenSessionAsync(firstParentId);
        var activity = new StatusActivity { Id = "shared-activity", Title = "Review", ChildSessionId = childId };
        await workbench.OpenStatusActivityAsync(activity);
        var first = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var firstContent = Assert.IsType<SubagentDocumentViewModel>(first.Content);
        var firstObservation = Assert.Single(firstSource.Observations);
        await firstObservation.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.RunPending();

        await workbench.OpenSessionAsync(secondParentId);
        await workbench.OpenStatusActivityAsync(activity);
        var second = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var secondContent = Assert.IsType<SubagentDocumentViewModel>(second.Content);
        var secondObservation = Assert.Single(secondSource.Observations);
        await secondObservation.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.RunPending();

        Assert.NotSame(first, second);
        Assert.NotSame(firstContent.Conversation, secondContent.Conversation);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(firstParentId.Value, first.OwnerId);
        Assert.Equal(secondParentId.Value, second.OwnerId);
        Assert.DoesNotContain(first, workbench.Documents.VisibleItems);
        Assert.Contains(second, workbench.Documents.VisibleItems);
        Assert.False(firstContent.IsDisposed);
        Assert.False(firstObservation.IsDisposed);
        Assert.Equal("Second parent child", Assert.IsType<UserMessageItemViewModel>(Assert.Single(secondContent.Conversation.Items)).Text);
        firstSource.Projection.SetSnapshot(new ConversationSnapshot(0, 4,
        [
            new UserMessageRow("first", "First parent child", DateTimeOffset.UtcNow),
            new SystemNoticeRow("recovered", "compact", "Recovered from first parent"),
        ], TestSnapshots.State()));
        firstSource.Projection.Emit(new ConversationDelta(4,
            [new RowAppended(new SystemNoticeRow("gap-only", "turn_error", "Must not apply the gap delta"))]));
        await DeliverUntilAsync(scheduler, () => firstContent.Conversation.Items.Count == 2);

        Assert.True(firstObservation.IsDisposed);
        Assert.Equal(2, firstSource.Observations.Length);
        Assert.Single(secondSource.Observations);
        Assert.Equal(new[] { firstParentId, secondParentId, firstParentId },
            runtime.ScopedObservations.Select(request => request.Parent));
        Assert.DoesNotContain(firstContent.Conversation.Items, item => item.RowId == "gap-only");
        Assert.Equal("Recovered from first parent", Assert.IsType<SystemNoticeItemViewModel>(firstContent.Conversation.Items[1]).Text);
        Assert.Single(secondContent.Conversation.Items);
        Assert.Same(second, workbench.Documents.ActiveDocument);
        Assert.Equal(secondParentId, workbench.Active!.SessionId);
        Assert.Equal(Path.GetFullPath(_root), first.WorkspaceRoot);
        Assert.Equal(Path.GetFullPath(_root), second.WorkspaceRoot);
        await workbench.OpenSessionAsync(firstParentId);
        Assert.Contains(first, workbench.Documents.VisibleItems);
        Assert.DoesNotContain(second, workbench.Documents.VisibleItems);
        await workbench.OpenStatusActivityAsync(activity with { Id = "different-notice" });
        Assert.Same(first, workbench.Documents.ActiveDocument);
        Assert.Equal(2, firstSource.Observations.Length);
        Assert.DoesNotContain(childId, runtime.GenericObservations);
        AssertNoCommands(runtime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusActivity_CloseReopenAndWorkbenchDisposalReleaseObservationsWithoutStoppingAgent(bool closeImmediately)
    {
        var runtime = new ParentScopedRuntime();
        var parentId = StrongId.NewSessionId();
        var childId = StrongId.NewSessionId();
        runtime.RegisterParent(parentId);
        var source = runtime.RegisterChild(parentId, childId, new ConversationSnapshot(0, 1,
            [new UserMessageRow("initial", "Initial child row", DateTimeOffset.UtcNow)], TestSnapshots.State()));
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenSessionAsync(parentId);
        var activity = new StatusActivity { Id = "running", Title = "Review", ChildSessionId = childId, CanStop = true };
        await workbench.OpenStatusActivityAsync(activity);
        var document = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var content = Assert.IsType<SubagentDocumentViewModel>(document.Content);
        var observation = Assert.Single(source.Observations);
        if (!closeImmediately)
        {
            await observation.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scheduler.RunPending();
            source.Projection.Emit(new ConversationDelta(2,
                [new RowAppended(new UserMessageRow("queued", "Queued before close", DateTimeOffset.UtcNow))]));
            await ProjectionStoreTests.WaitUntil(() => observation.LastDeltaSequence == 2 && scheduler.PendingCount > 0);
        }

        workbench.Documents.Close(document);

        Assert.True(content.IsDisposed);
        Assert.True(observation.IsDisposed);
        if (observation.Subscribed.Task.IsCompleted)
            await observation.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.RunPending();
        Assert.Single(content.Conversation.Items);
        Assert.DoesNotContain(document, workbench.Documents.Items);
        Assert.Single(source.Observations);
        AssertNoCommands(runtime);
        source.Projection.SetSnapshot(new ConversationSnapshot(0, 2,
            [new UserMessageRow("reopened", "Fresh snapshot after reopening", DateTimeOffset.UtcNow)], TestSnapshots.State()));

        await workbench.OpenStatusActivityAsync(activity);

        var reopened = Assert.IsType<DocumentViewModel>(workbench.Documents.ActiveDocument);
        var reopenedContent = Assert.IsType<SubagentDocumentViewModel>(reopened.Content);
        Assert.NotSame(document, reopened);
        Assert.NotSame(content, reopenedContent);
        Assert.NotSame(content.Conversation, reopenedContent.Conversation);
        Assert.False(reopenedContent.IsDisposed);
        Assert.Equal("Fresh snapshot after reopening", Assert.IsType<UserMessageItemViewModel>(Assert.Single(reopenedContent.Conversation.Items)).Text);
        var reopenedObservation = source.Observations[1];
        Assert.Equal(2, source.Observations.Length);
        await reopenedObservation.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.RunPending();
        source.Projection.Emit(new ConversationDelta(3,
            [new RowAppended(new UserMessageRow("late", "Queued before workbench disposal", DateTimeOffset.UtcNow))]));
        await ProjectionStoreTests.WaitUntil(() => reopenedObservation.LastDeltaSequence == 3 && scheduler.PendingCount > 0);

        workbench.Dispose();
        await reopenedObservation.Ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.RunPending();

        Assert.True(reopenedContent.IsDisposed);
        Assert.True(reopenedObservation.IsDisposed);
        Assert.All(source.Observations, item => Assert.True(item.IsDisposed));
        Assert.Equal(2, source.Observations.Length);
        Assert.Single(reopenedContent.Conversation.Items);
        Assert.Empty(workbench.Documents.Items);
        AssertNoCommands(runtime);
    }

    [Fact]
    public async Task StatusActivity_UnavailableAgentUsesSummaryAndForegroundUsesOutput()
    {
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        await workbench.OpenStatusActivityAsync(new StatusActivity
        {
            Id = "ended",
            Title = "Ended agent",
            ChildSessionId = StrongId.NewSessionId(),
            OutputText = "Review complete",
        });
        Assert.Equal("Review complete", Assert.IsType<TextDocumentViewModel>(workbench.Documents.ActiveDocument!.Content).Text);
        await workbench.OpenStatusActivityAsync(new StatusActivity { Id = "shell", Title = "Build", OutputText = "partial output" });
        Assert.Equal("partial output", Assert.IsType<TextDocumentViewModel>(workbench.Documents.ActiveDocument!.Content).Text);
    }

    private static async Task DeliverUntilAsync(QueuedScheduler scheduler, Func<bool> condition)
    {
        await ProjectionStoreTests.WaitUntil(() =>
        {
            scheduler.RunPending();
            return condition();
        });
    }

    private static void AssertNoCommands(ParentScopedRuntime runtime)
    {
        Assert.Empty(runtime.Parents.Submitted);
        Assert.Empty(runtime.StoppedSubagents);
        Assert.Empty(runtime.StoppedBackgroundJobs);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ParentScopedRuntime : IAgentRuntime, ISessionActivityRuntime
    {
        private readonly Dictionary<(SessionId Parent, SessionId Child), ChildProjectionSource> _children = [];

        public FakeAgentRuntime Parents { get; } = new();
        public ConcurrentQueue<(SessionId Parent, SessionId Child)> ScopedObservations { get; } = new();
        public ConcurrentQueue<SessionId> GenericObservations { get; } = new();
        public ConcurrentQueue<SessionId> Subscriptions { get; } = new();
        public ConcurrentQueue<(SessionId Parent, string TaskId)> StoppedSubagents { get; } = new();
        public ConcurrentQueue<(SessionId Parent, BackgroundJobId JobId)> StoppedBackgroundJobs { get; } = new();

        public void RegisterParent(SessionId sessionId) =>
            Parents.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));

        public ChildProjectionSource RegisterChild(SessionId parentId, SessionId childId, ConversationSnapshot snapshot)
        {
            var source = new ChildProjectionSource(snapshot);
            _children.Add((parentId, childId), source);
            return source;
        }

        public IConversationProjection? TryObserveSubagent(SessionId parentSessionId, SessionId childSessionId)
        {
            ScopedObservations.Enqueue((parentSessionId, childSessionId));
            return _children.TryGetValue((parentSessionId, childSessionId), out var source) ? source.Observe() : null;
        }

        public IConversationProjection? TryObserve(SessionId sessionId)
        {
            GenericObservations.Enqueue(sessionId);
            return Parents.TryObserve(sessionId);
        }

        public IConversationProjection Subscribe(SessionId sessionId)
        {
            Subscriptions.Enqueue(sessionId);
            return Parents.Subscribe(sessionId);
        }

        public IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId) => [];

        public bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId)
        {
            StoppedBackgroundJobs.Enqueue((sessionId, jobId));
            return true;
        }

        public ValueTask<bool> TryStopSubagentAsync(SessionId sessionId, string taskId)
        {
            StoppedSubagents.Enqueue((sessionId, taskId));
            return ValueTask.FromResult(true);
        }

        public ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken) =>
            Parents.CreateSessionAsync(options, cancellationToken);

        public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            Parents.ResumeSessionAsync(sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken) =>
            Parents.ListSessionsAsync(workspaceRoot, limit, cancellationToken);

        public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken) =>
            Parents.SubmitAsync(command, cancellationToken);

        public ValueTask DisposeAsync() => Parents.DisposeAsync();
    }

    private sealed class ChildProjectionSource(ConversationSnapshot snapshot)
    {
        private readonly ConcurrentQueue<TrackedObservation> _observations = new();

        public FakeConversationProjection Projection { get; } = new(snapshot);
        public TrackedObservation[] Observations => _observations.ToArray();

        public TrackedObservation Observe()
        {
            var observation = new TrackedObservation(Projection);
            _observations.Enqueue(observation);
            return observation;
        }
    }

    private sealed class TrackedObservation(FakeConversationProjection projection) : IConversationProjection
    {
        private int _disposed;
        private long _lastDeltaSequence;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public long LastDeltaSequence => Volatile.Read(ref _lastDeltaSequence);
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConversationSnapshot Snapshot() => projection.Snapshot();

        public async IAsyncEnumerable<ConversationDelta> Deltas([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Subscribed.TrySetResult();
            try
            {
                await foreach (var delta in projection.Deltas(cancellationToken))
                {
                    yield return delta;
                    Interlocked.Exchange(ref _lastDeltaSequence, delta.Sequence);
                }
            }
            finally
            {
                Ended.TrySetResult();
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class QueuedScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> _actions = new();

        public int PendingCount => _actions.Count;

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
