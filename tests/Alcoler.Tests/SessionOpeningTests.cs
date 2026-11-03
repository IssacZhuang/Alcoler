using System.Collections.Concurrent;
using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Alcoler.Tests;

public sealed class SessionOpeningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("alcoler-session-opening-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_PublishesOneCoherentSnapshotAndReusesWarmStore(bool permission)
    {
        var runtime = new FakeAgentRuntime();
        var first = StrongId.NewSessionId();
        var second = StrongId.NewSessionId();
        var interactionId = StrongId.NewInteractionId();
        var state = TestSnapshots.State(permission ? TurnPhase.AwaitingPermission : TurnPhase.AwaitingInteraction) with
        {
            Mode = PermissionMode.Yolo,
            PlanEnabled = true,
            PendingPermission = permission ? new PermissionRequest(interactionId, first, "Write",
                JsonSerializer.SerializeToElement(new { file_path = "a.txt" }), RiskLevel.Medium, "Approval") : null,
            PendingInteraction = permission ? null : new InteractionRequest(interactionId, "Choose",
                [new InteractionQuestion("Choice", "Which?", [new InteractionOption("One")])]),
        };
        var projection = runtime.RegisterSession(first, new ConversationSnapshot(7, 12,
            [new UserMessageRow("u1", "Question", DateTimeOffset.UtcNow)], state), _root);
        runtime.RegisterSession(second, new ConversationSnapshot(0, 0, [], TestSnapshots.State()), _root);
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        var published = 0;
        workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(WorkbenchViewModel.Active) || workbench.Active?.SessionId != first)
                return;
            published++;
            Assert.Equal(state.Phase.ToString(), workbench.Active.PhaseText);
            Assert.Equal(state.Mode, workbench.Active.Composer.SessionMode);
            Assert.Equal(state.Mode, workbench.Active.Composer.Mode);
            Assert.True(workbench.Active.Composer.PlanEnabled);
            Assert.True(workbench.Active.Composer.IsTurnActive);
            Assert.Equal(interactionId, workbench.Active.PendingInteraction?.InteractionId);
            Assert.Equal(permission, workbench.Active.PendingInteraction?.IsPermission);
            Assert.Single(workbench.Active.Items);
        };

        await workbench.OpenSessionAsync(first);
        var original = workbench.Active!;
        var redundantResets = 0;
        original.TimelineAppended += () => redundantResets++;
        scheduler.RunPending();
        Assert.Equal(0, redundantResets);
        Assert.Equal(1, runtime.SubscribeCount);
        Assert.Equal(1, projection.SnapshotCount);
        Assert.Equal(0, runtime.ObserveCount);
        await original.CompactAsync();
        Assert.Equal(7, Assert.Single(runtime.Submitted).BaseRevision);

        await workbench.OpenSessionAsync(second);
        await workbench.OpenSessionAsync(first);
        Assert.NotSame(original, workbench.Active);
        Assert.Equal(2, published);
        Assert.Equal(2, runtime.SubscribeCount);
        Assert.Equal(1, projection.SnapshotCount);
        Assert.Equal(0, runtime.ObserveCount);
        await DrainGitAsync(workbench, scheduler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewConversation_UsesOnlyItsHubProjection(bool sendDraft)
    {
        var runtime = new FakeAgentRuntime();
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        if (sendDraft)
        {
            workbench.Draft!.Composer.Draft = "Start once";
            await workbench.Draft.SendAsync();
            Assert.Single(runtime.Submitted);
        }
        else
        {
            await workbench.NewSessionAsync(_root);
        }
        var active = Assert.IsType<ConversationViewModel>(workbench.Active);
        Assert.Equal(1, runtime.SubscribeCount);
        Assert.Equal(1, runtime.ProjectionFor(active.SessionId).SnapshotCount);
        Assert.Equal(0, runtime.ObserveCount);
        await DrainGitAsync(workbench, scheduler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftAcknowledgement_AdvancesTheActiveAndWarmStoreRevision(bool accepted)
    {
        var runtime = new FakeAgentRuntime();
        runtime.AckHandler = command => accepted
            ? CommandAck.AcceptedAt(command.CommandId, 19)
            : CommandAck.Rejected(command.CommandId, 19, "rejected for test");
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        workbench.Draft!.Composer.Draft = "First send";
        await workbench.Draft.SendAsync();
        var id = workbench.Active!.SessionId;
        Assert.Equal(0, Assert.Single(runtime.Submitted).BaseRevision);
        runtime.AckHandler = command => CommandAck.AcceptedAt(command.CommandId, 23);
        await workbench.Active.CompactAsync();
        Assert.Equal(19, runtime.Submitted[1].BaseRevision);
        workbench.ShowNewTask(_root);
        await workbench.OpenSessionAsync(id);
        await workbench.Active!.CompactAsync();
        Assert.Equal(23, runtime.Submitted[2].BaseRevision);
        Assert.Equal(1, runtime.SubscribeCount);
        Assert.Equal(1, runtime.ProjectionFor(id).SnapshotCount);
        Assert.Equal(0, runtime.ObserveCount);
        await DrainGitAsync(workbench, scheduler);
    }

    [AvaloniaFact]
    public async Task Open_DoesNotMaterializeTheProjectionOnTheUiThread()
    {
        var runtime = new FakeAgentRuntime();
        var id = StrongId.NewSessionId();
        var projection = runtime.RegisterSession(id, new ConversationSnapshot(0, 0, [], TestSnapshots.State()), _root);
        var snapshotOnUi = true;
        projection.BeforeSnapshot = () => snapshotOnUi = Dispatcher.UIThread.CheckAccess();
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));

        Assert.True(Dispatcher.UIThread.CheckAccess());
        await workbench.OpenSessionAsync(id);

        Assert.False(snapshotOnUi);
        Assert.Equal(1, projection.SnapshotCount);
        await DrainGitAsync(workbench, scheduler);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("other")]
    [InlineData("active")]
    [InlineData("dispose")]
    [InlineData("cancel")]
    public async Task Open_BlockedSnapshotCannotReplaceNewerNavigation(string destination)
    {
        var runtime = new FakeAgentRuntime();
        var originalId = StrongId.NewSessionId();
        var blockedId = StrongId.NewSessionId();
        var otherId = StrongId.NewSessionId();
        runtime.RegisterSession(originalId, new ConversationSnapshot(0, 0, [], TestSnapshots.State()), _root);
        runtime.RegisterSession(otherId, new ConversationSnapshot(0, 0, [], TestSnapshots.State()), _root);
        var blocked = runtime.RegisterSession(blockedId, new ConversationSnapshot(0, 0, [],
            TestSnapshots.State(TurnPhase.Streaming) with { PlanEnabled = true }), _root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        blocked.BeforeSnapshot = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Task.Wait(TimeSpan.FromSeconds(10)));
        };
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        using var cancellation = new CancellationTokenSource();
        await workbench.OpenSessionAsync(originalId);
        var original = workbench.Active;
        var opening = workbench.OpenSessionAsync(blockedId, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(opening.IsCompleted);
            Assert.Same(original, workbench.Active);
            switch (destination)
            {
                case "draft": workbench.ShowNewTask(_root); break;
                case "other": await workbench.OpenSessionAsync(otherId); break;
                case "active": await workbench.OpenSessionAsync(originalId); break;
                case "dispose": workbench.Dispose(); break;
                case "cancel": cancellation.Cancel(); break;
            }
            if (destination == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
            else
                await opening.WaitAsync(TimeSpan.FromSeconds(5));
            var winner = workbench.Active;
            release.TrySetResult();
            await Task.Delay(40);
            scheduler.RunPending();
            Assert.Same(winner, workbench.Active);
            Assert.NotEqual(blockedId, workbench.Active?.SessionId);
            if (destination is "draft" or "dispose")
                Assert.Null(workbench.Active);
            else
                Assert.Equal(destination == "other" ? otherId : originalId, workbench.Active?.SessionId);
            await DrainGitAsync(workbench, scheduler);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Open_UncooperativeResumeCannotAcquireAfterNavigationChanges()
    {
        var runtime = new FakeAgentRuntime();
        var id = StrongId.NewSessionId();
        runtime.RegisterSession(id, new ConversationSnapshot(0, 0, [], TestSnapshots.State()), _root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SessionHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ResumeHandler = (_, _) =>
        {
            entered.TrySetResult();
            return new ValueTask<SessionHandle>(release.Task);
        };
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        var opening = workbench.OpenSessionAsync(id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        workbench.ShowNewTask(_root);
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult(new SessionHandle(id, _root, "Late"));
        await Task.Delay(40);
        scheduler.RunPending();
        Assert.Null(workbench.Active);
        Assert.Equal(0, runtime.SubscribeCount);
        await DrainGitAsync(workbench, scheduler);
    }

    [Fact]
    public async Task Open_RestoresOnlyReadingMetadataIntoFreshViewModels()
    {
        var runtime = new FakeAgentRuntime();
        var first = StrongId.NewSessionId();
        var second = StrongId.NewSessionId();
        var rows = CompletedRows();
        var projection = runtime.RegisterSession(first, new ConversationSnapshot(0, 1, rows, TestSnapshots.State()), _root);
        runtime.RegisterSession(second, new ConversationSnapshot(0, 0, [], TestSnapshots.State()), _root);
        var scheduler = new QueuedScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(first);
        var original = workbench.Active!;
        original.ToggleTurnSummary(Assert.IsType<UserMessageItemViewModel>(original.Items[0]));
        Assert.IsType<ToolCallItemViewModel>(original.Items[1]).IsExpanded = true;
        Assert.IsType<AssistantMessageItemViewModel>(original.Items[2]).IsReasoningExpanded = true;
        Assert.IsType<FileChangeSummaryItemViewModel>(original.Items[3]).IsExpanded = true;
        original.SaveReadingPosition(new TimelineScrollAnchor("t1", -23), followLatest: false);
        await workbench.OpenSessionAsync(second);
        await workbench.OpenSessionAsync(first);

        var restored = workbench.Active!;
        Assert.NotSame(original, restored);
        Assert.NotSame(original.Items[1], restored.Items[1]);
        Assert.True(Assert.IsType<UserMessageItemViewModel>(restored.Items[0]).IsTurnExpanded);
        Assert.True(Assert.IsType<ToolCallItemViewModel>(restored.Items[1]).IsExpanded);
        Assert.True(Assert.IsType<AssistantMessageItemViewModel>(restored.Items[2]).IsReasoningExpanded);
        Assert.True(Assert.IsType<FileChangeSummaryItemViewModel>(restored.Items[3]).IsExpanded);
        Assert.False(restored.Items[1].IsFolded);
        Assert.Equal(new TimelineScrollAnchor("t1", -23), restored.ReadingState.Anchor);
        Assert.False(restored.ReadingState.FollowLatest);
        Assert.Equal(1, projection.SnapshotCount);
        await DrainGitAsync(workbench, scheduler);
    }

    [Fact]
    public void ReadingStateCapture_PreservesTheAnchorTurnWhenExpansionMetadataReachesItsCap()
    {
        var at = DateTimeOffset.UtcNow;
        var input = JsonSerializer.SerializeToElement(new { path = "file.txt" });
        var rows = Enumerable.Range(0, 300).SelectMany(index => new ConversationRow[]
        {
            new UserMessageRow($"u{index}", "Work", at),
            new ToolCallRow($"t{index}", StrongId.NewToolCallId(), "Read", input,
                ToolCallStatus.Succeeded, null, false, null, at),
            new AssistantMessageRow($"a{index}", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), "Done")], false, at),
        }).ToArray();
        using var store = new ProjectionStore(() => throw new NotSupportedException(), StrongId.NewSessionId(),
            InlineUiScheduler.Instance, TestSnapshots.State());
        using var conversation = new ConversationViewModel(new FakeAgentRuntime(), store);
        conversation.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), [], Reset: true, ResetRows: rows));
        foreach (var user in conversation.Items.OfType<UserMessageItemViewModel>())
            conversation.ToggleTurnSummary(user);
        foreach (var tool in conversation.Items.OfType<ToolCallItemViewModel>())
            tool.IsExpanded = true;
        conversation.SaveReadingPosition(new TimelineScrollAnchor("t0", -10), followLatest: false);

        var saved = conversation.CaptureReadingState();

        Assert.Equal(TimelineReadingState.MaxExpandedRows, saved.ExpandedRows.Count);
        Assert.Contains(saved.ExpandedRows, row => row.RowId == "u0" && row.Expansion == TimelineExpansion.Turn);
        Assert.Contains(saved.ExpandedRows, row => row.RowId == "t0" && row.Expansion == TimelineExpansion.Tool);
        using var restored = new ConversationViewModel(new FakeAgentRuntime(), store, readingState: saved);
        restored.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), [], Reset: true, ResetRows: rows));
        Assert.True(Assert.IsType<UserMessageItemViewModel>(restored.Items[0]).IsTurnExpanded);
        Assert.True(Assert.IsType<ToolCallItemViewModel>(restored.Items[1]).IsExpanded);
        Assert.False(restored.Items[1].IsFolded);
    }

    [Fact]
    public void ReadingStateCache_IsLruAndBoundsMetadataPerSession()
    {
        var cache = new SessionReadingStateCache();
        var ids = Enumerable.Range(0, 33).Select(_ => StrongId.NewSessionId()).ToArray();
        var rows = Enumerable.Range(0, 1000)
            .Select(index => new TimelineExpandedRow($"t{index}", TimelineExpansion.Tool)).ToArray();
        for (var index = 0; index < 32; index++)
            cache.Save(ids[index], new TimelineReadingState(rows));
        Assert.NotNull(cache.Get(ids[0]));
        cache.Save(ids[32], TimelineReadingState.Initial);
        Assert.Equal(32, cache.Count);
        Assert.Null(cache.Get(ids[1]));
        Assert.Equal(TimelineReadingState.MaxExpandedRows, cache.Get(ids[0])!.ExpandedRows.Count);
        Assert.All(cache.Get(ids[0])!.ExpandedRows, row => Assert.True(int.Parse(row.RowId[1..]) >= 744));
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    internal static ConversationRow[] CompletedRows()
    {
        var at = DateTimeOffset.UtcNow;
        return
        [
            new UserMessageRow("u1", "Work", at),
            new ToolCallRow("t1", StrongId.NewToolCallId(), "Read", JsonSerializer.SerializeToElement(new { path = "file.txt" }),
                ToolCallStatus.Succeeded, null, false, TimeSpan.FromSeconds(1), at),
            new AssistantMessageRow("a1", StrongId.NewMessageId(), [new ReasoningPart(StrongId.NewPartId(), "Reason")], false, at),
            new FileChangeSummaryRow("f1", [new FileChangeSummaryItem("file.txt", 2, 1)], 2, 1),
            new AssistantMessageRow("a2", StrongId.NewMessageId(), [new TextPart(StrongId.NewPartId(), "Done")], false, at),
        ];
    }

    private static async Task DrainGitAsync(WorkbenchViewModel workbench, QueuedScheduler scheduler)
    {
        await ProjectionStoreTests.WaitUntil(() =>
        {
            scheduler.RunPending();
            return workbench.GitPane is null or { IsLoading: false };
        });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class QueuedScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> _actions = new();
        public void Post(Action action) => _actions.Enqueue(action);
        public void RunPending()
        {
            while (_actions.TryDequeue(out var action))
                action();
        }
    }
}
