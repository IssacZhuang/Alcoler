using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;

namespace Alcoler.ViewModel;

// Every observation arrives on the UI scheduler thread, so the state needs no locking; the
// cooldown spans all sessions because each signal schedules a full blocking collection.
internal sealed class BusyPeriodTracker(
    Func<DateTimeOffset> clock,
    TimeSpan longBusyThreshold,
    TimeSpan compactionCooldown)
{
    private readonly Dictionary<SessionId, DateTimeOffset> _busySince = [];
    private DateTimeOffset? _lastSignalAt;

    public bool OnActivity(SessionId sessionId, TurnPhase? phase) => OnActivity(sessionId, phase, clock());

    public bool OnActivity(SessionId sessionId, TurnPhase? phase, DateTimeOffset now)
    {
        if (phase is { } busy && busy != TurnPhase.Idle)
        {
            _busySince.TryAdd(sessionId, now);
            return false;
        }
        if (!_busySince.Remove(sessionId, out var startedAt))
        {
            return false;
        }
        if (now - startedAt < longBusyThreshold
            || (_lastSignalAt is { } lastSignal && now - lastSignal < compactionCooldown))
        {
            return false;
        }
        _lastSignalAt = now;
        return true;
    }
}
