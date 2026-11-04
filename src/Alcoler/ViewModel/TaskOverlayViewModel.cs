using System.Collections.ObjectModel;
using Alcoler.Contracts.Identifiers;
using Alcoler.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.ViewModel;

public sealed partial class TaskOverlayViewModel : ObservableObject
{
    private readonly SessionActivityTracker _activities;

    public TaskOverlayViewModel(SessionActivityTracker activities)
    {
        _activities = activities;
        activities.Changed += () => Refresh();
    }

    [ObservableProperty]
    private bool _isVisible;

    public ObservableCollection<TaskOverlaySessionViewModel> Sessions { get; } = [];

    public bool HasSessions => Sessions.Count > 0;

    public void Refresh(DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        var desired = _activities.Sessions
            .Where(state => state.HasActiveWork || state.HasUnviewedResult)
            .OrderByDescending(state => state.IsAwaitingConfirmation)
            .ThenBy(state => state.Activity.ActiveWorkStartedAt ?? state.Activity.StartedAt ?? state.Activity.LastActivityUtc)
            .ThenBy(state => state.Activity.SessionId.Value, StringComparer.Ordinal)
            .ToArray();
        var previousHasSessions = HasSessions;
        var wanted = desired.Select(state => state.Activity.SessionId).ToHashSet();
        for (var index = Sessions.Count - 1; index >= 0; index--)
            if (!wanted.Contains(Sessions[index].SessionId))
                Sessions.RemoveAt(index);
        var existing = Sessions.ToDictionary(row => row.SessionId);
        for (var index = 0; index < desired.Length; index++)
        {
            var state = desired[index];
            if (!existing.TryGetValue(state.Activity.SessionId, out var row))
            {
                row = new TaskOverlaySessionViewModel(state);
                row.Update(state, time);
                Sessions.Insert(index, row);
            }
            else
            {
                row.Update(state, time);
                var current = Sessions.IndexOf(row);
                if (current != index)
                    Sessions.Move(current, index);
            }
        }
        if (previousHasSessions != HasSessions)
            OnPropertyChanged(nameof(HasSessions));
    }
}

public sealed class TaskOverlaySessionViewModel : ObservableObject
{
    private SessionActivityState _state;
    private string _durationText = string.Empty;

    internal TaskOverlaySessionViewModel(SessionActivityState state) => _state = state;

    public SessionId SessionId => _state.Activity.SessionId;
    public string Title => string.IsNullOrWhiteSpace(_state.Activity.Title)
        ? Loc.Get("sidebar_untitled_session") : _state.Activity.Title;
    public string WorkspaceRoot => _state.Activity.WorkspaceRoot;
    public string WorkspaceName => Path.GetFileName(Path.TrimEndingDirectorySeparator(WorkspaceRoot))
        is { Length: > 0 } name ? name : WorkspaceRoot;
    public bool IsFailed => _state.IsFailed;
    public bool IsBusy => _state.IsBusy;
    public bool IsCompleted => _state.IsCompleted;
    public string DurationText => _durationText;

    internal void Update(SessionActivityState state, DateTimeOffset now)
    {
        var activity = state.Activity;
        var start = state.IsBusy
            ? activity.ActiveWorkStartedAt ?? (activity.FinishedAt is null ? activity.StartedAt : null)
            : activity.StartedAt;
        var end = state.IsBusy ? now : activity.FinishedAt;
        var duration = start is { } startedAt && end is { } endedAt
            ? SessionActivityText.Duration(endedAt - startedAt)
            : string.Empty;
        if (_state == state && _durationText == duration)
            return;
        var previous = (Title, WorkspaceRoot, IsBusy, IsCompleted, IsFailed, DurationText);
        _state = state;
        _durationText = duration;
        if (previous != (Title, WorkspaceRoot, IsBusy, IsCompleted, IsFailed, DurationText))
            OnPropertyChanged(string.Empty);
    }
}
