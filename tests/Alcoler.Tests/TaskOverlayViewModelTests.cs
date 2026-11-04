using System.Collections.Specialized;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;
using Alcoler.Localization;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class TaskOverlayViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Refresh_AddsUpdatesMovesAndRemovesWithoutReplacingSurvivingRows()
    {
        var first = TaskSnapshot("first", startedAt: Now.AddMinutes(-3));
        var second = TaskSnapshot("second", startedAt: Now.AddMinutes(-2));
        var runtime = new TaskOverlayActivityRuntime { Tasks = [second, first] };
        var model = new TaskOverlayViewModel(runtime);
        List<NotifyCollectionChangedAction> changes = [];
        List<string?> properties = [];
        model.Sessions.CollectionChanged += (_, e) => changes.Add(e.Action);
        model.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        model.Refresh(Now);

        Assert.True(model.HasSessions);
        Assert.Equal(new[] { first.SessionId, second.SessionId }, model.Sessions.Select(row => row.SessionId));
        var firstRow = model.Sessions[0];
        var secondRow = model.Sessions[1];
        Assert.Contains(nameof(TaskOverlayViewModel.HasSessions), properties);
        List<string?> rowProperties = [];
        secondRow.PropertyChanged += (_, e) => rowProperties.Add(e.PropertyName);
        changes.Clear();
        properties.Clear();

        var updated = second with
        {
            Title = "Needs review",
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "overlay-updated"),
            Phase = TurnPhase.AwaitingPermission,
            RunningJobCount = 2,
            RunningSubagentCount = 1,
            IsAwaitingConfirmation = true,
        };
        runtime.Tasks = [first, updated];
        model.Refresh(Now.AddSeconds(1));

        Assert.Same(secondRow, model.Sessions[0]);
        Assert.Same(firstRow, model.Sessions[1]);
        Assert.Equal("Needs review", secondRow.Title);
        Assert.Equal(updated.WorkspaceRoot, secondRow.WorkspaceRoot);
        Assert.Equal("overlay-updated", secondRow.WorkspaceName);
        Assert.True(secondRow.NeedsAttention);
        Assert.Equal(Loc.Get("duration_minutes_seconds", 2, 1), secondRow.DurationText);
        Assert.Equal(string.Join(" · ", Loc.Get("status_phase_awaiting_permission"),
            Loc.Get("task_overlay_background_jobs", 2), Loc.Get("status_agent_one", 1)), secondRow.DetailText);
        Assert.NotEmpty(rowProperties);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Move }, changes);
        Assert.DoesNotContain(nameof(TaskOverlayViewModel.HasSessions), properties);

        var third = TaskSnapshot("third", startedAt: Now.AddMinutes(-1));
        runtime.Tasks = [third, updated];
        changes.Clear();
        model.Refresh(Now.AddSeconds(2));

        Assert.Same(secondRow, model.Sessions[0]);
        Assert.Equal(third.SessionId, model.Sessions[1].SessionId);
        Assert.DoesNotContain(firstRow, model.Sessions);
        Assert.Contains(NotifyCollectionChangedAction.Remove, changes);
        Assert.Contains(NotifyCollectionChangedAction.Add, changes);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);

        runtime.Tasks = [];
        properties.Clear();
        model.Refresh(Now.AddSeconds(3));

        Assert.Empty(model.Sessions);
        Assert.False(model.HasSessions);
        Assert.Contains(nameof(TaskOverlayViewModel.HasSessions), properties);
    }

    [Fact]
    public void Refresh_UnchangedSnapshotDoesNotResetCollectionOrNotifyRows()
    {
        var runtime = new TaskOverlayActivityRuntime { Tasks = [TaskSnapshot("running", startedAt: Now.AddSeconds(-10))] };
        var model = new TaskOverlayViewModel(runtime);
        model.Refresh(Now);
        var row = Assert.Single(model.Sessions);
        var collectionChanges = 0;
        var rowChanges = 0;
        var modelChanges = 0;
        model.Sessions.CollectionChanged += (_, _) => collectionChanges++;
        row.PropertyChanged += (_, _) => rowChanges++;
        model.PropertyChanged += (_, _) => modelChanges++;

        model.Refresh(Now);

        Assert.Same(row, Assert.Single(model.Sessions));
        Assert.Equal(0, collectionChanges);
        Assert.Equal(0, rowChanges);
        Assert.Equal(0, modelChanges);

        model.Refresh(Now.AddSeconds(1));

        Assert.Same(row, Assert.Single(model.Sessions));
        Assert.Equal(Loc.Get("duration_seconds", 11), row.DurationText);
        Assert.True(rowChanges > 0);
        Assert.Equal(0, collectionChanges);
    }

    [Fact]
    public void Refresh_SortsWaitingFirstThenStartOrLastActivityAndSessionId()
    {
        var waitingEarly = TaskSnapshot("waiting-early", startedAt: Now.AddMinutes(-4), waiting: true);
        var waitingLate = TaskSnapshot("waiting-late", startedAt: Now.AddMinutes(-2), waiting: true);
        var oldest = TaskSnapshot("oldest", startedAt: Now.AddHours(-1));
        var unknown = TaskSnapshot("unknown", startedAt: null) with { LastActivityUtc = Now.AddMinutes(-10) };
        var tieA = TaskSnapshot("tie-a", startedAt: Now.AddMinutes(-5));
        var tieB = TaskSnapshot("tie-b", startedAt: Now.AddMinutes(-5));
        var runtime = new TaskOverlayActivityRuntime { Tasks = [tieB, oldest, waitingLate, unknown, tieA, waitingEarly] };
        var model = new TaskOverlayViewModel(runtime);

        model.Refresh(Now);

        Assert.Equal(new[] { waitingEarly.SessionId, waitingLate.SessionId, oldest.SessionId,
            unknown.SessionId, tieA.SessionId, tieB.SessionId }, model.Sessions.Select(row => row.SessionId));
    }

    [Theory]
    [InlineData(TurnPhase.ProcessingInput, "status_phase_preparing")]
    [InlineData(TurnPhase.AwaitingModel, "status_phase_awaiting_model")]
    [InlineData(TurnPhase.Streaming, "status_phase_streaming")]
    [InlineData(TurnPhase.ExecutingTools, "status_phase_running_tools")]
    [InlineData(TurnPhase.AwaitingPermission, "status_phase_awaiting_permission")]
    [InlineData(TurnPhase.AwaitingInteraction, "status_phase_awaiting_interaction")]
    [InlineData(TurnPhase.Completing, "status_phase_completing")]
    public void Refresh_UsesLocalizedPhaseText(TurnPhase phase, string textKey)
    {
        var waiting = phase is TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction;
        var runtime = new TaskOverlayActivityRuntime { Tasks = [TaskSnapshot("task", phase, Now.AddSeconds(-5), waiting)] };
        var model = new TaskOverlayViewModel(runtime);

        model.Refresh(Now);

        var row = Assert.Single(model.Sessions);
        Assert.Equal(Loc.Get(textKey), row.DetailText);
        Assert.Equal(waiting, row.NeedsAttention);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_KeepsIdleParentsWithBackgroundWorkAndWorkerConfirmation(bool waiting)
    {
        var task = TaskSnapshot("background", TurnPhase.Idle, Now.AddMinutes(-1), waiting) with
        {
            RunningJobCount = 3,
            RunningSubagentCount = 2,
        };
        var runtime = new TaskOverlayActivityRuntime { Tasks = [task] };
        var model = new TaskOverlayViewModel(runtime);

        model.Refresh(Now);

        var row = Assert.Single(model.Sessions);
        Assert.True(model.HasSessions);
        Assert.Equal(waiting, row.NeedsAttention);
        Assert.Equal(string.Join(" · ", Loc.Get(waiting ? "task_overlay_worker_waiting" : "status_running"),
            Loc.Get("task_overlay_background_jobs", 3), Loc.Get("status_agent_many", 2)), row.DetailText);
    }

    [Theory]
    [InlineData(null, "", 0, 0)]
    [InlineData(-10, "duration_seconds", 0, 0)]
    [InlineData(0, "duration_seconds", 0, 0)]
    [InlineData(59, "duration_seconds", 59, 0)]
    [InlineData(65, "duration_minutes_seconds", 1, 5)]
    [InlineData(3665, "duration_hours_minutes", 1, 1)]
    public void Refresh_DurationUsesStartTimeNotLastActivityAndClampsFutureStart(
        int? elapsedSeconds, string textKey, int first, int second)
    {
        var task = TaskSnapshot("duration", startedAt: elapsedSeconds is { } seconds ? Now.AddSeconds(-seconds) : null)
            with { LastActivityUtc = Now.AddDays(-20) };
        var runtime = new TaskOverlayActivityRuntime { Tasks = [task] };
        var model = new TaskOverlayViewModel(runtime);

        model.Refresh(Now);

        Assert.Equal(textKey.Length == 0 ? string.Empty : Loc.Get(textKey, first, second),
            Assert.Single(model.Sessions).DurationText);
    }

    [Fact]
    public void Refresh_UsesUntitledFallbackAndTrimsWorkspaceSeparators()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var directory = Path.Combine(Path.GetTempPath(), "overlay-workspace");
        var runtime = new TaskOverlayActivityRuntime
        {
            Tasks =
            [
                TaskSnapshot("blank") with { Title = " \t", WorkspaceRoot = directory + Path.DirectorySeparatorChar },
                TaskSnapshot("root") with { WorkspaceRoot = root },
            ],
        };
        var model = new TaskOverlayViewModel(runtime);

        model.Refresh(Now);

        var blank = model.Sessions.Single(row => row.SessionId.Value == "blank");
        Assert.Equal(Loc.Get("sidebar_untitled_session"), blank.Title);
        Assert.Equal("overlay-workspace", blank.WorkspaceName);
        Assert.Equal(directory + Path.DirectorySeparatorChar, blank.WorkspaceRoot);
        Assert.Equal(root, model.Sessions.Single(row => row.SessionId.Value == "root").WorkspaceName);
    }

    [Fact]
    public void Refresh_OnlyQueriesResidentTaskSnapshotsWithoutOpeningOrListingSessions()
    {
        var runtime = new TaskOverlayActivityRuntime { Tasks = [TaskSnapshot("resident", startedAt: Now)] };
        var model = new TaskOverlayViewModel(runtime);

        model.Refresh(Now);
        model.Refresh(Now.AddSeconds(1));

        Assert.Equal(2, runtime.TaskQueryCount);
        Assert.Equal(0, runtime.BackgroundQueryCount);
        Assert.Equal(0, runtime.ResumeCount);
        Assert.Equal(0, runtime.ListCount);
        Assert.Equal(0, runtime.Inner.SubscribeCount);
        Assert.Equal(0, runtime.Inner.ObserveCount);
        Assert.Empty(runtime.Inner.Submitted);
    }

    [Fact]
    public void Refresh_RuntimeWithoutActivitySupportRemainsEmptyWithoutOpeningOrListingSessions()
    {
        var runtime = new TaskOverlayRuntime();
        var model = new TaskOverlayViewModel(runtime);

        model.Refresh(Now);
        model.Refresh(Now.AddSeconds(1));

        Assert.Empty(model.Sessions);
        Assert.False(model.HasSessions);
        Assert.Equal(0, runtime.ResumeCount);
        Assert.Equal(0, runtime.ListCount);
        Assert.Equal(0, runtime.Inner.SubscribeCount);
        Assert.Equal(0, runtime.Inner.ObserveCount);
    }

    internal static SessionTaskSnapshot TaskSnapshot(string id, TurnPhase phase = TurnPhase.Streaming,
        DateTimeOffset? startedAt = null, bool waiting = false) =>
        new(new SessionId(id), $"Task {id}", Path.Combine(Path.GetTempPath(), "overlay-workspace"), phase,
            Now.AddSeconds(-1), 0, 0, waiting, startedAt);
}

internal class TaskOverlayRuntime : IAgentRuntime
{
    private int _resumeCount;
    private int _listCount;

    public FakeAgentRuntime Inner { get; } = new();
    public int ResumeCount => Volatile.Read(ref _resumeCount);
    public int ListCount => Volatile.Read(ref _listCount);

    public ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken) =>
        Inner.CreateSessionAsync(options, cancellationToken);

    public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _resumeCount);
        return Inner.ResumeSessionAsync(sessionId, cancellationToken);
    }

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listCount);
        return Inner.ListSessionsAsync(workspaceRoot, limit, cancellationToken);
    }

    public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken) =>
        Inner.SubmitAsync(command, cancellationToken);

    public ValueTask<int> UnloadIdleSessionsAsync(TimeSpan idleThreshold, SessionId? keepOpen, CancellationToken cancellationToken) =>
        Inner.UnloadIdleSessionsAsync(idleThreshold, keepOpen, cancellationToken);

    public IConversationProjection Subscribe(SessionId sessionId) => Inner.Subscribe(sessionId);
    public IConversationProjection? TryObserve(SessionId sessionId) => Inner.TryObserve(sessionId);
    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}

internal sealed class TaskOverlayActivityRuntime : TaskOverlayRuntime, ISessionActivityRuntime
{
    public IReadOnlyList<SessionTaskSnapshot> Tasks { get; set; } = [];
    public int TaskQueryCount { get; private set; }
    public int BackgroundQueryCount { get; private set; }

    public IReadOnlyList<SessionTaskSnapshot> GetActiveSessionTasks()
    {
        TaskQueryCount++;
        return Tasks;
    }

    public IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId)
    {
        BackgroundQueryCount++;
        return [];
    }

    public bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId) => false;
}
