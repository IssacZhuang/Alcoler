using Alcoler.Audio;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;

namespace Alcoler.ViewModel;

// Every observation arrives on the UI scheduler thread, so the state needs no locking.
internal sealed class TaskAlertTracker
{
    private readonly Dictionary<SessionId, TurnPhase> _lastPhases = [];

    public TaskAlert? OnActivity(SessionId sessionId, SessionActivitySnapshot? activity)
    {
        if (activity is null)
        {
            _lastPhases.Remove(sessionId);
            return null;
        }
        var known = _lastPhases.TryGetValue(sessionId, out var previous);
        _lastPhases[sessionId] = activity.Phase;
        if (!known)
            return null;

        var nowAwaiting = activity.Phase is TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction;
        if (nowAwaiting && previous is not (TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction))
            return TaskAlert.AwaitingConfirmation;

        // Error is the resting phase of a failed turn; only the next turn start leaves it.
        if (!IsActive(previous) || IsActive(activity.Phase))
            return null;
        return activity.LastOutcome switch
        {
            TurnOutcome.Completed => TaskAlert.TaskCompleted,
            TurnOutcome.Failed => TaskAlert.AwaitingConfirmation,
            _ => null,
        };
    }

    private static bool IsActive(TurnPhase phase) => phase is not (TurnPhase.Idle or TurnPhase.Error);
}
