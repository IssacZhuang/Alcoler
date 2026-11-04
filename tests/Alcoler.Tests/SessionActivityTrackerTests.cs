using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class SessionActivityTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public void ViewingAcknowledgesOnlyTheCurrentResultAndRepeatedRefreshDoesNotResurrectIt(TurnOutcome outcome)
    {
        var tracker = new SessionActivityTracker();
        var result = Result(outcome);
        tracker.Refresh([result]);
        Assert.True(tracker.Get(result.SessionId)!.HasUnviewedResult);

        tracker.SetViewingSession(result.SessionId);
        tracker.SetViewingSession(null);
        tracker.Refresh([result]);
        tracker.ApplyTitle(result.SessionId, "Renamed");
        tracker.Refresh([result with { Title = "Renamed", LastActivityUtc = Now.AddMinutes(1) }]);
        Assert.False(tracker.Get(result.SessionId)!.HasUnviewedResult);

        var next = result with { CompletedTurnId = StrongId.NewTurnId(), FinishedAt = Now.AddMinutes(2) };
        tracker.Refresh([next]);
        Assert.True(tracker.Get(result.SessionId)!.HasUnviewedResult);
        Assert.Single(tracker.Sessions);
    }

    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public void SelectionIsNotViewingAndSidebarFilteringDoesNotOwnUnreadState(TurnOutcome outcome)
    {
        var tracker = new SessionActivityTracker();
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask, tracker);
        var result = Result(outcome);
        var summary = new SessionSummaryViewModel(result.SessionId, result.Title, result.WorkspaceRoot, Now);
        tracker.Refresh([result]);
        sidebar.ApplySummaries([summary]);
        sidebar.SetActive(result.SessionId);
        sidebar.ApplySummaries([]);
        sidebar.IsArchivedView = true;
        sidebar.ApplySummaries([]);
        Assert.True(tracker.Get(result.SessionId)!.HasUnviewedResult);

        sidebar.IsArchivedView = false;
        sidebar.ApplySummaries([summary]);
        Assert.Equal(outcome == TurnOutcome.Failed ? SessionActivityIndicator.Failed : SessionActivityIndicator.Completed,
            summary.Indicator);
        tracker.SetViewingSession(result.SessionId);
        Assert.False(tracker.Get(result.SessionId)!.HasUnviewedResult);
        Assert.Equal(outcome == TurnOutcome.Failed ? SessionActivityIndicator.Failed : SessionActivityIndicator.None,
            summary.Indicator);
    }

    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public void UnloadAndColdResumePreserveUnviewedResultAndFrozenTiming(TurnOutcome outcome)
    {
        var tracker = new SessionActivityTracker();
        var result = Result(outcome);
        tracker.Refresh([result]);
        tracker.Refresh([]);
        var unloaded = tracker.Get(result.SessionId)!;
        Assert.True(unloaded.HasUnviewedResult);
        Assert.False(unloaded.HasActiveWork);
        Assert.Equal(result.StartedAt, unloaded.Activity.StartedAt);
        Assert.Equal(result.FinishedAt, unloaded.Activity.FinishedAt);

        var cold = new SessionActivitySnapshot(TurnPhase.Idle, null)
        {
            SessionId = result.SessionId,
            Title = "Reopened",
            WorkspaceRoot = result.WorkspaceRoot,
        };
        tracker.Refresh([cold]);
        var resumed = tracker.Get(result.SessionId)!;
        Assert.True(resumed.HasUnviewedResult);
        Assert.Equal(result.CompletedTurnId, resumed.Activity.CompletedTurnId);
        Assert.Equal(result.LastOutcome, resumed.Activity.LastOutcome);
        Assert.Equal(result.StartedAt, resumed.Activity.StartedAt);
        Assert.Equal(result.FinishedAt, resumed.Activity.FinishedAt);
        Assert.Equal(cold.Title, resumed.Activity.Title);

        tracker.SetViewingSession(result.SessionId);
        tracker.SetViewingSession(null);
        tracker.Refresh([cold]);
        Assert.False(tracker.Get(result.SessionId)!.HasUnviewedResult);
    }

    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Failed)]
    public void ResultsArrivingDuringActualViewingAreAcknowledgedButHiddenSelectionIsNot(TurnOutcome outcome)
    {
        var tracker = new SessionActivityTracker();
        var result = Result(outcome);
        tracker.SetViewingSession(result.SessionId);
        tracker.Refresh([result]);
        Assert.False(tracker.Get(result.SessionId)!.HasUnviewedResult);

        tracker.SetViewingSession(null);
        tracker.Refresh([result with { CompletedTurnId = StrongId.NewTurnId() }]);
        Assert.True(tracker.Get(result.SessionId)!.HasUnviewedResult);
    }

    [Theory]
    [InlineData(TurnOutcome.Cancelled)]
    [InlineData(TurnOutcome.ForcedStop)]
    public void NonResultTerminalOutcomeReplacesPreviousAttention(TurnOutcome outcome)
    {
        var tracker = new SessionActivityTracker();
        var result = Result(TurnOutcome.Completed);
        tracker.Refresh([result]);
        tracker.Refresh([result with { LastOutcome = outcome, CompletedTurnId = StrongId.NewTurnId() }]);
        Assert.False(tracker.Get(result.SessionId)!.HasUnviewedResult);
        Assert.Equal(SessionActivityIndicator.None, tracker.Get(result.SessionId)!.Indicator);
    }

    [Fact]
    public void RefreshPublishesOncePerBatchAndRetainsOnlyLatestStatePerSession()
    {
        var tracker = new SessionActivityTracker();
        var first = Result(TurnOutcome.Completed);
        var second = Result(TurnOutcome.Failed) with { SessionId = StrongId.NewSessionId() };
        var changes = 0;
        tracker.Changed += () => changes++;
        tracker.Refresh([first, second]);
        Assert.Equal(1, changes);
        tracker.Refresh([first, second]);
        Assert.Equal(1, changes);

        for (var turn = 0; turn < 1000; turn++)
            tracker.ApplyActivity(first.SessionId, first with { CompletedTurnId = StrongId.NewTurnId() });
        Assert.Equal(2, tracker.Sessions.Count());
        Assert.Equal(1001, changes);
        tracker.Remove(first.SessionId);
        Assert.Null(tracker.Get(first.SessionId));
        Assert.Single(tracker.Sessions);
    }

    [Fact]
    public void OverlayIncludesUnreadSessionOutsideSidebarVisiblePageAndListing()
    {
        var tracker = new SessionActivityTracker();
        var result = Result(TurnOutcome.Completed);
        tracker.Refresh([result]);
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask, tracker);
        var rows = Enumerable.Range(0, 6).Select(index => new SessionSummaryViewModel(
            StrongId.NewSessionId(), $"Other {index}", result.WorkspaceRoot, Now.AddMinutes(index + 1))).ToList();
        rows.Add(new SessionSummaryViewModel(result.SessionId, result.Title, result.WorkspaceRoot, Now));
        sidebar.ApplySummaries(rows);
        var group = Assert.Single(sidebar.Groups);
        Assert.DoesNotContain(group.VisibleSessions, row => row.Id == result.SessionId);
        var overlay = new TaskOverlayViewModel(tracker);
        overlay.Refresh(Now);
        var retained = Assert.Single(overlay.Sessions);
        Assert.Equal(result.SessionId, retained.SessionId);

        sidebar.ApplySummaries(rows.Where(row => row.Id != result.SessionId).ToArray());
        tracker.Refresh([result]);

        Assert.Same(retained, Assert.Single(overlay.Sessions));
        Assert.True(tracker.Get(result.SessionId)!.HasUnviewedResult);
    }

    [Fact]
    public void AcknowledgedFailureKeepsSidebarErrorButNewWorkReplacesItsIndicator()
    {
        var tracker = new SessionActivityTracker();
        var result = Result(TurnOutcome.Failed);
        tracker.Refresh([result]);
        tracker.SetViewingSession(result.SessionId);
        var failed = tracker.Get(result.SessionId)!;
        Assert.Equal(SessionActivityIndicator.Failed, failed.Indicator);
        Assert.False(failed.HasUnviewedResult);
        tracker.Refresh([result with { Phase = TurnPhase.Streaming, HasActiveWork = true, ActiveWorkStartedAt = Now }]);
        var running = tracker.Get(result.SessionId)!;
        Assert.Equal(SessionActivityIndicator.Running, running.Indicator);
        Assert.False(running.IsFailed);
        Assert.False(running.HasUnviewedResult);
    }

    private static SessionActivitySnapshot Result(TurnOutcome outcome) => new(TurnPhase.Idle, outcome)
    {
        SessionId = new SessionId("watched"),
        Title = "Watched",
        WorkspaceRoot = Path.GetTempPath(),
        LastActivityUtc = Now,
        StartedAt = Now.AddMinutes(-1),
        FinishedAt = Now,
        CompletedTurnId = StrongId.NewTurnId(),
    };
}
