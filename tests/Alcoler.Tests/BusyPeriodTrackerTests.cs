using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class BusyPeriodTrackerTests
{
    private static readonly TimeSpan LongBusy = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    private static DateTimeOffset TenOClock => new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnActivity_ShortBusyPeriodBelowThresholdNeverSignals()
    {
        var now = TenOClock;
        var tracker = new BusyPeriodTracker(() => now, LongBusy, Cooldown);
        var session = StrongId.NewSessionId();

        Assert.False(tracker.OnActivity(session, TurnPhase.Streaming));
        now += LongBusy - TimeSpan.FromSeconds(1);
        Assert.False(tracker.OnActivity(session, TurnPhase.Idle));
    }

    [Fact]
    public void OnActivity_BusyPeriodAtThresholdSignalsExactlyOnceWhenSessionGoesIdle()
    {
        var now = TenOClock;
        var tracker = new BusyPeriodTracker(() => now, LongBusy, Cooldown);
        var session = StrongId.NewSessionId();

        Assert.False(tracker.OnActivity(session, TurnPhase.ProcessingInput));
        now += TimeSpan.FromMinutes(5);
        Assert.False(tracker.OnActivity(session, TurnPhase.ExecutingTools));
        now += TimeSpan.FromMinutes(5);
        Assert.True(tracker.OnActivity(session, TurnPhase.Idle));
        now += TimeSpan.FromMinutes(1);
        Assert.False(tracker.OnActivity(session, TurnPhase.Idle));
    }

    [Fact]
    public void OnActivity_QualifyingPeriodsDuringCooldownSignalAgainOnlyAfterCooldownElapses()
    {
        var now = TenOClock;
        var tracker = new BusyPeriodTracker(() => now, LongBusy, Cooldown);
        var first = StrongId.NewSessionId();
        var second = StrongId.NewSessionId();

        tracker.OnActivity(first, TurnPhase.Streaming);
        now += LongBusy;
        Assert.True(tracker.OnActivity(first, TurnPhase.Idle));

        tracker.OnActivity(second, TurnPhase.Streaming);
        now += LongBusy;
        Assert.False(tracker.OnActivity(second, TurnPhase.Idle));

        tracker.OnActivity(first, TurnPhase.Streaming);
        now += TimeSpan.FromMinutes(20);
        Assert.True(tracker.OnActivity(first, TurnPhase.Idle));
    }

    [Fact]
    public void OnActivity_OneSessionBusyStartDoesNotAffectAnotherSessionIdleTransition()
    {
        var now = TenOClock;
        var tracker = new BusyPeriodTracker(() => now, LongBusy, Cooldown);
        var busy = StrongId.NewSessionId();
        var idle = StrongId.NewSessionId();
        var unknown = StrongId.NewSessionId();

        tracker.OnActivity(busy, TurnPhase.Streaming);
        now += LongBusy + TimeSpan.FromMinutes(1);
        Assert.False(tracker.OnActivity(idle, TurnPhase.Idle));
        Assert.False(tracker.OnActivity(unknown, null));
        Assert.True(tracker.OnActivity(busy, TurnPhase.Idle));
    }

    [Fact]
    public void OnActivity_RepeatedBusyObservationsNeverSignalUntilIdle()
    {
        var now = TenOClock;
        var tracker = new BusyPeriodTracker(() => now, LongBusy, Cooldown);
        var session = StrongId.NewSessionId();

        foreach (var phase in new[]
                 {
                     TurnPhase.ProcessingInput, TurnPhase.AwaitingModel, TurnPhase.Streaming,
                     TurnPhase.ExecutingTools, TurnPhase.AwaitingPermission, TurnPhase.Completing,
                 })
        {
            now += TimeSpan.FromMinutes(4);
            Assert.False(tracker.OnActivity(session, phase));
        }
        Assert.True(tracker.OnActivity(session, TurnPhase.Idle));
    }
}
