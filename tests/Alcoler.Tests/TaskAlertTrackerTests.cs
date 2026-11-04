using Alcoler.Audio;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class TaskAlertTrackerTests
{
    private static SessionActivitySnapshot Activity(TurnPhase phase, TurnOutcome? outcome = null) => new(phase, outcome);

    [Fact]
    public void OnActivity_FirstObservationNeverAlerts()
    {
        var tracker = new TaskAlertTracker();
        var session = StrongId.NewSessionId();

        Assert.Null(tracker.OnActivity(session, Activity(TurnPhase.Streaming)));
        Assert.Null(tracker.OnActivity(StrongId.NewSessionId(), Activity(TurnPhase.AwaitingPermission)));
    }

    [Fact]
    public void OnActivity_EnteringAwaitingFromBusyAlertsConfirmation()
    {
        var tracker = new TaskAlertTracker();
        var session = StrongId.NewSessionId();

        tracker.OnActivity(session, Activity(TurnPhase.Streaming));
        Assert.Equal(TaskAlert.AwaitingConfirmation,
            tracker.OnActivity(session, Activity(TurnPhase.AwaitingPermission, TurnOutcome.Completed)));
    }

    [Fact]
    public void OnActivity_StayingAwaitingDoesNotAlertAgain()
    {
        var tracker = new TaskAlertTracker();
        var session = StrongId.NewSessionId();

        tracker.OnActivity(session, Activity(TurnPhase.ExecutingTools));
        tracker.OnActivity(session, Activity(TurnPhase.AwaitingPermission));
        Assert.Null(tracker.OnActivity(session, Activity(TurnPhase.AwaitingPermission)));
        Assert.Null(tracker.OnActivity(session, Activity(TurnPhase.AwaitingInteraction)));
    }

    [Fact]
    public void OnActivity_BusyToIdleWithCompletedAlertsTaskCompleted()
    {
        var tracker = new TaskAlertTracker();
        var session = StrongId.NewSessionId();

        tracker.OnActivity(session, Activity(TurnPhase.ProcessingInput));
        tracker.OnActivity(session, Activity(TurnPhase.Streaming));
        Assert.Equal(TaskAlert.TaskCompleted,
            tracker.OnActivity(session, Activity(TurnPhase.Idle, TurnOutcome.Completed)));
        Assert.Null(tracker.OnActivity(session, Activity(TurnPhase.Idle, TurnOutcome.Completed)));
    }

    [Fact]
    public void OnActivity_BusyToErrorWithFailedAlertsConfirmation()
    {
        var tracker = new TaskAlertTracker();
        var session = StrongId.NewSessionId();

        tracker.OnActivity(session, Activity(TurnPhase.Streaming));
        Assert.Equal(TaskAlert.AwaitingConfirmation,
            tracker.OnActivity(session, Activity(TurnPhase.Error, TurnOutcome.Failed)));
    }

    [Theory]
    [InlineData(TurnOutcome.Cancelled)]
    [InlineData(TurnOutcome.ForcedStop)]
    public void OnActivity_UserInitiatedTurnEndsDoNotAlert(TurnOutcome outcome)
    {
        var tracker = new TaskAlertTracker();
        var session = StrongId.NewSessionId();

        tracker.OnActivity(session, Activity(TurnPhase.Streaming));
        Assert.Null(tracker.OnActivity(session, Activity(TurnPhase.Idle, outcome)));
    }

    [Fact]
    public void OnActivity_ApprovedAwaitingThenCompletionAlertsOnlyTaskCompleted()
    {
        var tracker = new TaskAlertTracker();
        var session = StrongId.NewSessionId();

        tracker.OnActivity(session, Activity(TurnPhase.ExecutingTools));
        Assert.Equal(TaskAlert.AwaitingConfirmation,
            tracker.OnActivity(session, Activity(TurnPhase.AwaitingPermission)));
        Assert.Null(tracker.OnActivity(session, Activity(TurnPhase.ExecutingTools)));
        Assert.Equal(TaskAlert.TaskCompleted,
            tracker.OnActivity(session, Activity(TurnPhase.Idle, TurnOutcome.Completed)));
    }

    [Fact]
    public void OnActivity_NullActivityForgetsTheSessionSoReappearanceStaysSilent()
    {
        var tracker = new TaskAlertTracker();
        var session = StrongId.NewSessionId();

        tracker.OnActivity(session, Activity(TurnPhase.Streaming));
        Assert.Null(tracker.OnActivity(session, null));
        // The session is unknown again: its reappearance is a first observation, not a transition.
        Assert.Null(tracker.OnActivity(session, Activity(TurnPhase.AwaitingPermission)));
    }

    [Fact]
    public void OnActivity_SessionsAreTrackedIndependently()
    {
        var tracker = new TaskAlertTracker();
        var first = StrongId.NewSessionId();
        var second = StrongId.NewSessionId();

        tracker.OnActivity(first, Activity(TurnPhase.Streaming));
        tracker.OnActivity(second, Activity(TurnPhase.Streaming));
        Assert.Equal(TaskAlert.TaskCompleted, tracker.OnActivity(first, Activity(TurnPhase.Idle, TurnOutcome.Completed)));
        Assert.Null(tracker.OnActivity(second, Activity(TurnPhase.Streaming)));
    }
}
