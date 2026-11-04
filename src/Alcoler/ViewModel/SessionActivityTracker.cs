using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;

namespace Alcoler.ViewModel;

public sealed record SessionActivityState(SessionActivitySnapshot Activity, bool HasUnviewedResult)
{
    public bool HasActiveWork => Activity.HasActiveWork
        || Activity.Phase is not (TurnPhase.Idle or TurnPhase.Error)
        || Activity.RunningJobCount > 0 || Activity.RunningSubagentCount > 0
        || IsAwaitingConfirmation;

    public bool IsAwaitingConfirmation => Activity.IsAwaitingConfirmation
        || Activity.Phase is TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction;

    public bool IsFailed => Activity.Phase == TurnPhase.Error
        || Activity.Phase == TurnPhase.Idle && Activity.LastOutcome == TurnOutcome.Failed;

    public bool IsBusy => HasActiveWork && !IsFailed;

    public bool IsCompleted => !HasActiveWork && !IsFailed && Activity.LastOutcome == TurnOutcome.Completed;

    public SessionActivityIndicator Indicator => IsFailed ? SessionActivityIndicator.Failed
        : IsAwaitingConfirmation ? SessionActivityIndicator.AwaitingConfirmation
        : HasActiveWork ? SessionActivityIndicator.Running
        : HasUnviewedResult && IsCompleted ? SessionActivityIndicator.Completed
        : SessionActivityIndicator.None;
}

public sealed class SessionActivityTracker
{
    private readonly Dictionary<SessionId, SessionActivityState> _sessions = [];
    private SessionId? _viewingSession;

    public IEnumerable<SessionActivityState> Sessions => _sessions.Values;

    public event Action? Changed;

    public SessionActivityState? Get(SessionId sessionId) => _sessions.GetValueOrDefault(sessionId);

    public void Refresh(IReadOnlyList<SessionActivitySnapshot> activities)
    {
        var resident = new HashSet<SessionId>();
        var changed = false;
        foreach (var activity in activities)
        {
            if (string.IsNullOrEmpty(activity.SessionId.Value))
                continue;
            resident.Add(activity.SessionId);
            changed |= ApplyCore(activity.SessionId, activity);
        }
        foreach (var (id, state) in _sessions.ToArray())
        {
            if (resident.Contains(id))
                continue;
            if (!state.HasUnviewedResult && !state.IsFailed && state.Activity.CompletedTurnId is null)
            {
                changed |= _sessions.Remove(id);
                continue;
            }
            var resting = state with
            {
                Activity = state.Activity with
                {
                    Phase = state.Activity.Phase == TurnPhase.Error ? TurnPhase.Error : TurnPhase.Idle,
                    HasActiveWork = false,
                    RunningJobCount = 0,
                    RunningSubagentCount = 0,
                    IsAwaitingConfirmation = false,
                    ActiveWorkStartedAt = null,
                },
            };
            if (resting != state)
            {
                _sessions[id] = resting;
                changed = true;
            }
        }
        if (changed)
            Changed?.Invoke();
    }

    public void ApplyActivity(SessionId sessionId, SessionActivitySnapshot? activity)
    {
        if (activity is null)
            return;
        if (ApplyCore(sessionId, activity with { SessionId = sessionId }))
            Changed?.Invoke();
    }

    public void ApplyTitle(SessionId sessionId, string title)
    {
        if (_sessions.TryGetValue(sessionId, out var state) && state.Activity.Title != title)
        {
            _sessions[sessionId] = state with { Activity = state.Activity with { Title = title } };
            Changed?.Invoke();
        }
    }

    public void SetViewingSession(SessionId? sessionId)
    {
        _viewingSession = sessionId;
        if (sessionId is { } id && _sessions.TryGetValue(id, out var state) && state.HasUnviewedResult)
        {
            _sessions[id] = state with { HasUnviewedResult = false };
            Changed?.Invoke();
        }
    }

    public void Remove(SessionId sessionId)
    {
        if (_sessions.Remove(sessionId))
            Changed?.Invoke();
    }

    private bool ApplyCore(SessionId sessionId, SessionActivitySnapshot activity)
    {
        _sessions.TryGetValue(sessionId, out var previous);
        if (previous is not null && activity.CompletedTurnId is null && previous.Activity.CompletedTurnId is not null)
        {
            // A cold runtime has no live completion metadata; it must not erase an unseen result.
            activity = activity with
            {
                CompletedTurnId = previous.Activity.CompletedTurnId,
                LastOutcome = previous.Activity.LastOutcome,
                StartedAt = previous.Activity.StartedAt,
                FinishedAt = previous.Activity.FinishedAt,
            };
        }
        var state = new SessionActivityState(activity, previous?.HasUnviewedResult ?? false);
        var terminal = activity.LastOutcome is TurnOutcome.Completed or TurnOutcome.Failed
            || activity.Phase == TurnPhase.Error;
        var newResult = activity.CompletedTurnId is { } completed
            ? previous?.Activity.CompletedTurnId != completed
            : terminal && (previous is null
                || previous.Activity.LastOutcome != activity.LastOutcome
                || previous.Activity.Phase != TurnPhase.Error && activity.Phase == TurnPhase.Error
                || previous.HasActiveWork && !state.HasActiveWork);
        if (newResult)
            state = state with { HasUnviewedResult = terminal };
        else if (activity.CompletedTurnId is null && !terminal && !state.HasActiveWork)
            state = state with { HasUnviewedResult = false };
        if (_viewingSession == sessionId)
            state = state with { HasUnviewedResult = false };
        if (previous == state)
            return false;
        _sessions[sessionId] = state;
        return true;
    }
}
