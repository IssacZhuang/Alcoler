using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;

namespace Alcoler.Remote;

internal sealed class StreamProjectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<SessionId, List<Registration>> _sessions = [];

    public IDisposable Register(SessionId sessionId, IConversationProjection projection)
    {
        var registration = new Registration(this, sessionId, projection);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var observers))
                _sessions[sessionId] = observers = [];
            observers.Add(registration);
        }
        return registration;
    }

    public ConversationSnapshot? TryCaptureSnapshot(SessionId sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(sessionId, out var observers)
                ? observers[^1].Projection.Snapshot()
                : null;
        }
    }

    private void Unregister(Registration registration)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(registration.SessionId, out var observers))
                return;
            observers.Remove(registration);
            if (observers.Count == 0)
                _sessions.Remove(registration.SessionId);
        }
    }

    private sealed class Registration(
        StreamProjectionRegistry owner, SessionId sessionId, IConversationProjection projection) : IDisposable
    {
        public SessionId SessionId { get; } = sessionId;
        public IConversationProjection Projection { get; } = projection;
        public void Dispose() => owner.Unregister(this);
    }
}
