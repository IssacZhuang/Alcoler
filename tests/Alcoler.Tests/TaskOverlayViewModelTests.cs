using System.Collections.Specialized;
using System.Reflection;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Events;
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
    public void TrackerChanges_AddUpdateMoveAndRemoveWithoutReplacingSurvivingRows()
    {
        var first = TaskSnapshot("first", startedAt: Now.AddMinutes(-3));
        var second = TaskSnapshot("second", startedAt: Now.AddMinutes(-2));
        var tracker = new SessionActivityTracker();
        var model = new TaskOverlayViewModel(tracker);
        List<NotifyCollectionChangedAction> changes = [];
        List<string?> properties = [];
        model.Sessions.CollectionChanged += (_, e) => changes.Add(e.Action);
        model.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        tracker.Refresh([second, first]);
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
        tracker.Refresh([first, updated]);
        model.Refresh(Now.AddSeconds(1));

        Assert.Same(secondRow, model.Sessions[0]);
        Assert.Same(firstRow, model.Sessions[1]);
        Assert.Equal("Needs review", secondRow.Title);
        Assert.Equal(updated.WorkspaceRoot, secondRow.WorkspaceRoot);
        Assert.Equal("overlay-updated", secondRow.WorkspaceName);
        Assert.True(secondRow.IsBusy);
        Assert.False(secondRow.IsCompleted);
        Assert.False(secondRow.IsFailed);
        Assert.Equal(Loc.Get("duration_minutes_seconds", 2, 1), secondRow.DurationText);
        Assert.NotEmpty(rowProperties);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Move }, changes);
        Assert.DoesNotContain(nameof(TaskOverlayViewModel.HasSessions), properties);

        var third = TaskSnapshot("third", startedAt: Now.AddMinutes(-1));
        changes.Clear();
        tracker.Refresh([third, updated]);

        Assert.Same(secondRow, model.Sessions[0]);
        Assert.Equal(third.SessionId, model.Sessions[1].SessionId);
        Assert.DoesNotContain(firstRow, model.Sessions);
        Assert.Contains(NotifyCollectionChangedAction.Remove, changes);
        Assert.Contains(NotifyCollectionChangedAction.Add, changes);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);

        properties.Clear();
        tracker.Refresh([]);

        Assert.Empty(model.Sessions);
        Assert.False(model.HasSessions);
        Assert.Contains(nameof(TaskOverlayViewModel.HasSessions), properties);
    }

    [Fact]
    public void TrackerChanges_UnchangedVisibleStateDoesNotResetCollectionOrNotifyRows()
    {
        var startedAt = DateTimeOffset.UtcNow.AddDays(1);
        var task = TaskSnapshot("running", startedAt: startedAt);
        var other = TaskSnapshot("other", startedAt: startedAt.AddMinutes(1));
        var tracker = CreateTracker(other, task);
        var model = new TaskOverlayViewModel(tracker);
        model.Refresh();
        var row = model.Sessions[0];
        var otherRow = model.Sessions[1];
        var collectionChanges = 0;
        var rowChanges = 0;
        var modelChanges = 0;
        model.Sessions.CollectionChanged += (_, _) => collectionChanges++;
        foreach (var session in model.Sessions)
            session.PropertyChanged += (_, _) => rowChanges++;
        model.PropertyChanged += (_, _) => modelChanges++;
        SessionActivitySnapshot[] snapshots =
        [
            task,
            task with { Phase = TurnPhase.AwaitingModel },
            task with { Phase = TurnPhase.ExecutingTools, RunningJobCount = 2, RunningSubagentCount = 3 },
            task with { LastActivityUtc = Now },
            task with { StartedAt = startedAt.AddMilliseconds(-100) },
            task with { Phase = TurnPhase.AwaitingPermission, IsAwaitingConfirmation = true },
            task with { Phase = TurnPhase.Idle, LastOutcome = TurnOutcome.Completed,
                HasActiveWork = false, RunningSubagentCount = 1 },
        ];
        foreach (var snapshot in snapshots)
        {
            tracker.Refresh([other, snapshot]);
            model.Refresh();

            Assert.Equal(new[] { row, otherRow }, model.Sessions);
            Assert.True(row.IsBusy);
            Assert.False(row.IsCompleted);
            Assert.False(row.IsFailed);
            Assert.Equal(Loc.Get("duration_seconds", 0), row.DurationText);
            Assert.Equal(0, collectionChanges);
            Assert.Equal(0, rowChanges);
            Assert.Equal(0, modelChanges);
        }

        model.Refresh(startedAt.AddSeconds(1));

        Assert.Equal(new[] { row, otherRow }, model.Sessions);
        Assert.Equal(Loc.Get("duration_seconds", 1), row.DurationText);
        Assert.True(rowChanges > 0);
        Assert.Equal(0, collectionChanges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrackerChanges_TitleOrWorkspaceChangeNotifiesRowWithoutChangingCollection(bool changeTitle)
    {
        var task = TaskSnapshot("visible", startedAt: DateTimeOffset.UtcNow.AddDays(1));
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        model.Refresh();
        var row = Assert.Single(model.Sessions);
        List<NotifyCollectionChangedAction> collectionChanges = [];
        List<string?> rowProperties = [];
        model.Sessions.CollectionChanged += (_, e) => collectionChanges.Add(e.Action);
        row.PropertyChanged += (_, e) => rowProperties.Add(e.PropertyName);
        var updated = changeTitle
            ? task with { Title = "Renamed task" }
            : task with { WorkspaceRoot = Path.Combine(Path.GetTempPath(), "overlay-renamed-workspace") };

        tracker.ApplyActivity(task.SessionId, updated);

        Assert.Same(row, Assert.Single(model.Sessions));
        Assert.Equal(updated.Title, row.Title);
        Assert.Equal(updated.WorkspaceRoot, row.WorkspaceRoot);
        Assert.NotEmpty(rowProperties);
        Assert.Empty(collectionChanges);
    }

    [Fact]
    public void TrackerChanges_ApplyTitleUpdatesHiddenOverlayImmediately()
    {
        var task = TaskSnapshot("title", startedAt: DateTimeOffset.UtcNow.AddDays(1));
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        model.Refresh();
        var row = Assert.Single(model.Sessions);

        tracker.ApplyTitle(task.SessionId, "Shared title");

        Assert.False(model.IsVisible);
        Assert.Same(row, Assert.Single(model.Sessions));
        Assert.Equal("Shared title", row.Title);
        Assert.Equal("Shared title", tracker.Get(task.SessionId)!.Activity.Title);
    }

    [Fact]
    public void Refresh_SortsWaitingFirstThenCurrentSubtreeStartOwnStartLastActivityAndSessionId()
    {
        var waitingEarly = TaskSnapshot("waiting-early", startedAt: Now.AddMinutes(-4), waiting: true);
        var waitingLate = TaskSnapshot("waiting-late", startedAt: Now.AddMinutes(-2), waiting: true);
        var oldest = TaskSnapshot("oldest", startedAt: Now.AddHours(-1));
        var subtree = TaskSnapshot("subtree", startedAt: Now.AddSeconds(-1)) with { ActiveWorkStartedAt = Now.AddMinutes(-20) };
        var ownStart = TaskSnapshot("own-start", startedAt: Now.AddMinutes(-15)) with { ActiveWorkStartedAt = null };
        var unknown = TaskSnapshot("unknown") with { LastActivityUtc = Now.AddMinutes(-10) };
        var tieA = TaskSnapshot("tie-a", startedAt: Now.AddMinutes(-5));
        var tieB = TaskSnapshot("tie-b", startedAt: Now.AddMinutes(-5));
        var tracker = CreateTracker(tieB, oldest, waitingLate, subtree, ownStart, unknown, tieA, waitingEarly);
        var model = new TaskOverlayViewModel(tracker);

        model.Refresh(Now);

        Assert.Equal(new[] { waitingEarly.SessionId, waitingLate.SessionId, oldest.SessionId, subtree.SessionId,
            ownStart.SessionId, unknown.SessionId, tieA.SessionId, tieB.SessionId }, model.Sessions.Select(row => row.SessionId));
    }

    [Theory]
    [InlineData(TurnPhase.ProcessingInput)]
    [InlineData(TurnPhase.AwaitingModel)]
    [InlineData(TurnPhase.Streaming)]
    [InlineData(TurnPhase.ExecutingTools)]
    [InlineData(TurnPhase.AwaitingPermission)]
    [InlineData(TurnPhase.AwaitingInteraction)]
    [InlineData(TurnPhase.Completing)]
    public void Refresh_ActivePhasesStayBusyAfterPreviousFailure(TurnPhase phase)
    {
        var waiting = phase is TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction;
        var task = TaskSnapshot("task", phase, Now.AddSeconds(-5), waiting) with
        {
            HasActiveWork = false,
            LastOutcome = TurnOutcome.Failed,
            CompletedTurnId = new TurnId("previous-failed-turn"),
        };
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);

        model.Refresh(Now);

        var row = Assert.Single(model.Sessions);
        Assert.True(row.IsBusy);
        Assert.False(row.IsCompleted);
        Assert.False(row.IsFailed);
        Assert.Equal(waiting ? SessionActivityIndicator.AwaitingConfirmation : SessionActivityIndicator.Running,
            tracker.Get(task.SessionId)!.Indicator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_KeepsIdleParentsWithRunningSubagentsAndWorkerConfirmation(bool waiting)
    {
        var task = TaskSnapshot("subagents", TurnPhase.Idle, Now.AddMinutes(-1), waiting) with
        {
            HasActiveWork = true,
            RunningSubagentCount = 2,
            LastOutcome = TurnOutcome.Completed,
            CompletedTurnId = new TurnId("subagents-own-turn"),
        };
        var model = new TaskOverlayViewModel(CreateTracker(task));

        model.Refresh(Now);

        var row = Assert.Single(model.Sessions);
        Assert.True(model.HasSessions);
        Assert.True(row.IsBusy);
        Assert.False(row.IsCompleted);
        Assert.False(row.IsFailed);
    }

    [Theory]
    [InlineData(1, 0, false, false, false)]
    [InlineData(0, 1, false, false, true)]
    [InlineData(0, 0, true, false, true)]
    [InlineData(0, 0, false, true, false)]
    [InlineData(1, 0, false, true, false)]
    public void Refresh_OnlyAgentWorkSignalsOverridePreviousCompletion(
        int jobs, int subagents, bool hasActiveWork, bool waiting, bool busy)
    {
        var task = TaskSnapshot("idle-work", TurnPhase.Idle, Now.AddMinutes(-1), waiting) with
        {
            RunningJobCount = jobs,
            RunningSubagentCount = subagents,
            HasActiveWork = hasActiveWork,
            LastOutcome = TurnOutcome.Completed,
            CompletedTurnId = new TurnId("idle-own-turn"),
        };
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);

        model.Refresh(Now);

        var row = Assert.Single(model.Sessions);
        Assert.Equal(busy, tracker.Get(task.SessionId)!.HasActiveWork);
        Assert.Equal(busy, row.IsBusy);
        Assert.Equal(!busy, row.IsCompleted);
        Assert.False(row.IsFailed);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Refresh_IdleJobAndConfirmationSignalsWithoutUnreadResultAreExcluded(int jobs, bool waiting)
    {
        var task = TaskSnapshot("idle-signals", TurnPhase.Idle, Now.AddMinutes(-1), waiting) with
        {
            RunningJobCount = jobs,
        };
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);

        model.Refresh(Now);

        var state = tracker.Get(task.SessionId)!;
        Assert.False(state.HasActiveWork);
        Assert.False(state.IsBusy);
        Assert.False(state.HasUnviewedResult);
        Assert.Equal(jobs, state.Activity.RunningJobCount);
        Assert.Empty(model.Sessions);
        Assert.False(model.HasSessions);
    }

    [Theory]
    [InlineData(TurnPhase.Idle, TurnOutcome.Completed, true, false)]
    [InlineData(TurnPhase.Idle, TurnOutcome.Failed, false, true)]
    [InlineData(TurnPhase.Error, null, false, true)]
    [InlineData(TurnPhase.Error, TurnOutcome.Completed, false, true)]
    public void Refresh_UnviewedTerminalSnapshotHasExclusivePresentation(
        TurnPhase phase, TurnOutcome? outcome, bool completed, bool failed)
    {
        var task = TerminalSnapshot("presentation", outcome) with { Phase = phase };
        var model = new TaskOverlayViewModel(CreateTracker(task));

        model.Refresh(Now);

        var row = Assert.Single(model.Sessions);
        Assert.False(row.IsBusy);
        Assert.Equal(completed, row.IsCompleted);
        Assert.Equal(failed, row.IsFailed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TurnOutcome.Cancelled)]
    [InlineData(TurnOutcome.ForcedStop)]
    public void Refresh_IdleWithoutSuccessfulOrFailedUnviewedResultIsExcluded(TurnOutcome? outcome)
    {
        var task = TerminalSnapshot("excluded", outcome);
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);

        model.Refresh(Now);

        Assert.Empty(model.Sessions);
        Assert.False(model.HasSessions);
        Assert.False(tracker.Get(task.SessionId)!.HasUnviewedResult);
    }

    [Theory]
    [InlineData(TurnPhase.Idle)]
    [InlineData(TurnPhase.Error)]
    public void Refresh_FailureOverridesLingeringWorkAndWorkerConfirmation(TurnPhase phase)
    {
        var task = TerminalSnapshot("failed-workers", TurnOutcome.Failed) with
        {
            Phase = phase,
            HasActiveWork = true,
            ActiveWorkStartedAt = Now.AddMinutes(-1),
            IsAwaitingConfirmation = true,
            RunningJobCount = 2,
            RunningSubagentCount = 1,
        };
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        model.Refresh(Now);
        var row = Assert.Single(model.Sessions);

        tracker.SetViewingSession(task.SessionId);

        Assert.Same(row, Assert.Single(model.Sessions));
        Assert.False(tracker.Get(task.SessionId)!.HasUnviewedResult);
        Assert.True(row.IsFailed);
        Assert.False(row.IsBusy);
        Assert.False(row.IsCompleted);
    }

    [Fact]
    public void TrackerChanges_PresentationTransitionsNotifyOnlyVisibleChangesAndReuseRow()
    {
        var task = TaskSnapshot("transitions", startedAt: DateTimeOffset.UtcNow.AddDays(1));
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        model.Refresh();
        var row = Assert.Single(model.Sessions);
        List<NotifyCollectionChangedAction> changes = [];
        List<string?> rowProperties = [];
        model.Sessions.CollectionChanged += (_, e) => changes.Add(e.Action);
        row.PropertyChanged += (_, e) => rowProperties.Add(e.PropertyName);
        (TurnPhase Phase, TurnOutcome? Outcome, bool Active, bool Busy, bool Completed, bool Failed)[] states =
        [
            (TurnPhase.Idle, TurnOutcome.Completed, false, false, true, false),
            (TurnPhase.Idle, TurnOutcome.Failed, false, false, false, true),
            (TurnPhase.Streaming, TurnOutcome.Failed, true, true, false, false),
            (TurnPhase.Idle, TurnOutcome.Completed, true, true, false, false),
            (TurnPhase.Error, TurnOutcome.Completed, false, false, false, true),
            (TurnPhase.Idle, TurnOutcome.Completed, false, false, true, false),
        ];
        for (var index = 0; index < states.Length; index++)
        {
            var state = states[index];
            var previous = (row.IsBusy, row.IsCompleted, row.IsFailed);
            tracker.ApplyActivity(task.SessionId, task with
            {
                Phase = state.Phase,
                LastOutcome = state.Outcome,
                HasActiveWork = state.Active,
                RunningSubagentCount = state.Active && state.Phase == TurnPhase.Idle ? 1 : 0,
                CompletedTurnId = new TurnId($"transition-{index}"),
                FinishedAt = state.Active ? null : task.StartedAt,
                ActiveWorkStartedAt = state.Active ? task.StartedAt : null,
            });

            Assert.Same(row, Assert.Single(model.Sessions));
            Assert.Equal(state.Busy, row.IsBusy);
            Assert.Equal(state.Completed, row.IsCompleted);
            Assert.Equal(state.Failed, row.IsFailed);
            Assert.Equal(previous != (state.Busy, state.Completed, state.Failed), rowProperties.Count > 0);
            Assert.Empty(changes);
            rowProperties.Clear();
        }

        tracker.Remove(task.SessionId);

        Assert.Empty(model.Sessions);
        Assert.False(model.HasSessions);
        Assert.Equal(NotifyCollectionChangedAction.Remove, Assert.Single(changes));
    }

    [Theory]
    [InlineData(null, "", 0, 0)]
    [InlineData(-10, "duration_seconds", 0, 0)]
    [InlineData(0, "duration_seconds", 0, 0)]
    [InlineData(59, "duration_seconds", 59, 0)]
    [InlineData(65, "duration_minutes_seconds", 1, 5)]
    [InlineData(3665, "duration_hours_minutes", 1, 1)]
    public void Refresh_DurationUsesCurrentWorkStartNotLastActivityAndClampsFutureStart(
        int? elapsedSeconds, string textKey, int first, int second)
    {
        var task = TaskSnapshot("duration", startedAt: elapsedSeconds is { } seconds ? Now.AddSeconds(-seconds) : null) with
        {
            LastActivityUtc = Now.AddDays(-20),
        };
        var model = new TaskOverlayViewModel(CreateTracker(task));

        model.Refresh(Now);

        Assert.Equal(textKey.Length == 0 ? string.Empty : Loc.Get(textKey, first, second),
            Assert.Single(model.Sessions).DurationText);
    }

    [Fact]
    public void Refresh_ActiveDurationUsesCurrentSubtreeStartInsteadOfPreviousOwnTurn()
    {
        var task = TerminalSnapshot("workers", TurnOutcome.Completed) with
        {
            HasActiveWork = true,
            RunningSubagentCount = 1,
            StartedAt = Now.AddHours(-2),
            FinishedAt = Now.AddHours(-1),
            ActiveWorkStartedAt = Now.AddSeconds(-65),
        };
        var model = new TaskOverlayViewModel(CreateTracker(task));

        model.Refresh(Now);

        Assert.Equal(Loc.Get("duration_minutes_seconds", 1, 5), Assert.Single(model.Sessions).DurationText);
    }

    [Fact]
    public void Refresh_ActiveDurationFallsBackToOwnStartOnlyWithoutTerminalFinish()
    {
        var task = TaskSnapshot("own-start", startedAt: Now.AddSeconds(-10)) with { ActiveWorkStartedAt = null };
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);

        model.Refresh(Now);

        Assert.Equal(Loc.Get("duration_seconds", 10), Assert.Single(model.Sessions).DurationText);
        tracker.ApplyActivity(task.SessionId, task with { FinishedAt = Now.AddSeconds(-4) });
        model.Refresh(Now.AddHours(1));
        Assert.Empty(Assert.Single(model.Sessions).DurationText);
    }

    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public void Refresh_TerminalDurationFreezesAtActualFinishAcrossRefreshesAndUnload(TurnOutcome outcome)
    {
        var task = TerminalSnapshot("frozen", outcome) with
        {
            StartedAt = Now.AddSeconds(-65),
            FinishedAt = Now,
        };
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        model.Refresh(Now.AddMinutes(1));
        var row = Assert.Single(model.Sessions);
        var changes = 0;
        row.PropertyChanged += (_, _) => changes++;

        model.Refresh(Now.AddDays(1));
        tracker.Refresh([]);
        model.Refresh(Now.AddDays(2));

        Assert.Same(row, Assert.Single(model.Sessions));
        Assert.Equal(Loc.Get("duration_minutes_seconds", 1, 5), row.DurationText);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void TrackerChanges_BackgroundJobsDoNotKeepCompletedTurnBusyOrExtendItsDuration()
    {
        var task = TaskSnapshot("background-job", startedAt: Now.AddSeconds(-65)) with { RunningJobCount = 1 };
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        model.Refresh(Now);
        var row = Assert.Single(model.Sessions);
        Assert.True(row.IsBusy);
        var finished = task with
        {
            Phase = TurnPhase.Idle,
            LastOutcome = TurnOutcome.Completed,
            HasActiveWork = false,
            ActiveWorkStartedAt = null,
            CompletedTurnId = new TurnId("background-job-finished-turn"),
            FinishedAt = Now,
        };

        tracker.ApplyActivity(task.SessionId, finished);

        Assert.Same(row, Assert.Single(model.Sessions));
        Assert.False(tracker.Get(task.SessionId)!.HasActiveWork);
        Assert.False(row.IsBusy);
        Assert.True(row.IsCompleted);
        Assert.Equal(Loc.Get("duration_minutes_seconds", 1, 5), row.DurationText);
        var changes = 0;
        row.PropertyChanged += (_, _) => changes++;
        tracker.ApplyActivity(task.SessionId, finished with { RunningJobCount = 2, LastActivityUtc = Now.AddMinutes(1) });
        model.Refresh(Now.AddDays(1));

        Assert.Same(row, Assert.Single(model.Sessions));
        Assert.Equal(Loc.Get("duration_minutes_seconds", 1, 5), row.DurationText);
        Assert.Equal(0, changes);
        tracker.SetViewingSession(task.SessionId);
        tracker.SetViewingSession(null);
        tracker.ApplyActivity(task.SessionId, finished with { LastActivityUtc = Now.AddDays(2) });
        model.Refresh(Now.AddDays(2));

        var state = tracker.Get(task.SessionId)!;
        Assert.Equal(1, state.Activity.RunningJobCount);
        Assert.False(state.HasActiveWork);
        Assert.False(state.IsBusy);
        Assert.False(state.HasUnviewedResult);
        Assert.Empty(model.Sessions);
        Assert.False(model.HasSessions);
    }

    [Fact]
    public void Refresh_TerminalWithoutFinishDoesNotInventObservedFinishOrKeepCounting()
    {
        var task = TerminalSnapshot("missing-finish", TurnOutcome.Completed) with { FinishedAt = null };
        var model = new TaskOverlayViewModel(CreateTracker(task));

        model.Refresh(Now);
        Assert.Empty(Assert.Single(model.Sessions).DurationText);
        model.Refresh(Now.AddHours(2));
        Assert.Empty(Assert.Single(model.Sessions).DurationText);
    }

    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public void TrackerChanges_ViewingAcknowledgesEachResultIdentityOnlyOnce(TurnOutcome outcome)
    {
        var task = TerminalSnapshot("result", outcome);
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        model.Refresh(Now);
        Assert.Single(model.Sessions);
        Assert.True(tracker.Get(task.SessionId)!.HasUnviewedResult);

        tracker.SetViewingSession(task.SessionId);

        Assert.Empty(model.Sessions);
        Assert.False(tracker.Get(task.SessionId)!.HasUnviewedResult);
        Assert.Equal(outcome == TurnOutcome.Failed, tracker.Get(task.SessionId)!.IsFailed);
        tracker.SetViewingSession(null);
        tracker.Refresh([task]);
        tracker.ApplyActivity(task.SessionId, task with { LastActivityUtc = Now.AddMinutes(1) });
        Assert.Empty(model.Sessions);
        Assert.False(tracker.Get(task.SessionId)!.HasUnviewedResult);

        tracker.ApplyActivity(task.SessionId, task with
        {
            CompletedTurnId = new TurnId("result-second-turn"),
            FinishedAt = Now.AddMinutes(2),
        });

        Assert.Single(model.Sessions);
        Assert.True(tracker.Get(task.SessionId)!.HasUnviewedResult);
        tracker.SetViewingSession(task.SessionId);
        Assert.Empty(model.Sessions);
    }

    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public void TrackerChanges_UnloadAndColdResumePreserveUnreadTerminalUntilActuallyViewed(TurnOutcome outcome)
    {
        var task = TerminalSnapshot("retained", outcome);
        var tracker = CreateTracker(task);
        var model = new TaskOverlayViewModel(tracker);
        model.Refresh(Now);
        var row = Assert.Single(model.Sessions);

        tracker.Refresh([]);
        tracker.ApplyActivity(task.SessionId, null);
        tracker.Refresh([TaskSnapshot(task.SessionId.Value, TurnPhase.Idle)]);

        Assert.False(model.IsVisible);
        Assert.Same(row, Assert.Single(model.Sessions));
        Assert.True(tracker.Get(task.SessionId)!.HasUnviewedResult);
        Assert.Equal(task.CompletedTurnId, tracker.Get(task.SessionId)!.Activity.CompletedTurnId);
        Assert.Equal(Loc.Get("duration_seconds", 55), row.DurationText);
        tracker.SetViewingSession(task.SessionId);
        Assert.Empty(model.Sessions);
        tracker.SetViewingSession(null);
        tracker.Refresh([]);
        tracker.Refresh([task]);
        Assert.Empty(model.Sessions);

        tracker.Remove(task.SessionId);
        Assert.Null(tracker.Get(task.SessionId));
        Assert.Empty(model.Sessions);
    }

    [Fact]
    public void Refresh_UsesUntitledFallbackAndTrimsWorkspaceSeparators()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var directory = Path.Combine(Path.GetTempPath(), "overlay-workspace");
        var tracker = CreateTracker(
            TaskSnapshot("blank") with { Title = " \t", WorkspaceRoot = directory + Path.DirectorySeparatorChar },
            TaskSnapshot("root") with { WorkspaceRoot = root });
        var model = new TaskOverlayViewModel(tracker);

        model.Refresh(Now);

        var blank = model.Sessions.Single(row => row.SessionId.Value == "blank");
        Assert.Equal(Loc.Get("sidebar_untitled_session"), blank.Title);
        Assert.Equal("overlay-workspace", blank.WorkspaceName);
        Assert.Equal(directory + Path.DirectorySeparatorChar, blank.WorkspaceRoot);
        Assert.Equal(root, model.Sessions.Single(row => row.SessionId.Value == "root").WorkspaceName);
    }

    [Fact]
    public void Refresh_UsesOnlySharedStateAndDoesNotHoldOrQueryRuntime()
    {
        var runtime = new TaskOverlayActivityRuntime { Tasks = [TaskSnapshot("resident", startedAt: Now)] };
        var tracker = CreateTracker(runtime.GetSessionActivities().ToArray());
        var model = new TaskOverlayViewModel(tracker);
        var queries = runtime.TaskQueryCount;
        runtime.Tasks = [];

        model.Refresh(Now);
        model.Refresh(Now.AddSeconds(1));

        Assert.Single(model.Sessions);
        Assert.Equal(queries, runtime.TaskQueryCount);
        Assert.DoesNotContain(typeof(TaskOverlayViewModel).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => typeof(IAgentRuntime).IsAssignableFrom(field.FieldType)
                || typeof(ISessionActivityRuntime).IsAssignableFrom(field.FieldType));
        Assert.Equal(0, runtime.BackgroundQueryCount);
        Assert.Equal(0, runtime.ResumeCount);
        Assert.Equal(0, runtime.ListCount);
        Assert.Equal(0, runtime.Inner.SubscribeCount);
        Assert.Equal(0, runtime.Inner.ObserveCount);
        Assert.Empty(runtime.Inner.Submitted);
    }

    [Fact]
    public void Refresh_EmptySharedStateRemainsEmpty()
    {
        var model = new TaskOverlayViewModel(new SessionActivityTracker());

        model.Refresh(Now);
        model.Refresh(Now.AddSeconds(1));

        Assert.Empty(model.Sessions);
        Assert.False(model.HasSessions);
    }

    internal static SessionActivityTracker CreateTracker(params SessionActivitySnapshot[] snapshots)
    {
        var tracker = new SessionActivityTracker();
        tracker.Refresh(snapshots);
        return tracker;
    }

    internal static SessionActivitySnapshot TaskSnapshot(string id, TurnPhase phase = TurnPhase.Streaming,
        DateTimeOffset? startedAt = null, bool waiting = false) => new(phase, null)
        {
            SessionId = new SessionId(id),
            Title = $"Task {id}",
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "overlay-workspace"),
            LastActivityUtc = Now.AddSeconds(-1),
            IsAwaitingConfirmation = waiting,
            HasActiveWork = phase is not (TurnPhase.Idle or TurnPhase.Error),
            StartedAt = startedAt,
            ActiveWorkStartedAt = phase is not (TurnPhase.Idle or TurnPhase.Error) ? startedAt : null,
        };

    internal static SessionActivitySnapshot TerminalSnapshot(string id, TurnOutcome? outcome,
        string? turnId = null) => TaskSnapshot(id, TurnPhase.Idle, Now.AddMinutes(-1)) with
        {
            LastOutcome = outcome,
            CompletedTurnId = new TurnId(turnId ?? $"{id}-finished-turn"),
            FinishedAt = Now.AddSeconds(-5),
            ActiveWorkStartedAt = null,
        };
}

internal class TaskOverlayRuntime : IAgentRuntime
{
    private int _resumeCount;
    private int _listCount;

    public FakeAgentRuntime Inner { get; } = new();
    public IReadOnlyList<SessionSummary> SessionSummaries { get; set; } = [];
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
        return ValueTask.FromResult<IReadOnlyList<SessionSummary>>(SessionSummaries
            .Where(summary => workspaceRoot is null || string.Equals(summary.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
            .Take(limit).ToArray());
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
    private IReadOnlyList<SessionActivitySnapshot> _tasks = [];
    private int _taskQueryCount;
    private int _backgroundQueryCount;

    public IReadOnlyList<SessionActivitySnapshot> Tasks
    {
        get => _tasks;
        set
        {
            var changed = _tasks.Select(task => task.SessionId).Concat(value.Select(task => task.SessionId)).Distinct().ToArray();
            _tasks = value;
            foreach (var id in changed)
                SessionActivityChanged?.Invoke(id);
        }
    }

    public int TaskQueryCount => Volatile.Read(ref _taskQueryCount);
    public int BackgroundQueryCount => Volatile.Read(ref _backgroundQueryCount);
    public event Action<SessionId>? SessionActivityChanged;

    public SessionActivitySnapshot? GetSessionActivity(SessionId sessionId) =>
        _tasks.FirstOrDefault(task => task.SessionId == sessionId);

    public IReadOnlyList<SessionActivitySnapshot> GetSessionActivities()
    {
        Interlocked.Increment(ref _taskQueryCount);
        return _tasks;
    }

    public IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId)
    {
        Interlocked.Increment(ref _backgroundQueryCount);
        return [];
    }

    public bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId) => false;
}
