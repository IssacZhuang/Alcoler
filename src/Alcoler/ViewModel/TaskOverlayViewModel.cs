using System.Collections.ObjectModel;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.ViewModel;

public sealed partial class TaskOverlayViewModel(IAgentRuntime runtime) : ObservableObject
{
    private readonly ISessionActivityRuntime? _activities = runtime as ISessionActivityRuntime;

    [ObservableProperty]
    private bool _isVisible;

    public ObservableCollection<TaskOverlaySessionViewModel> Sessions { get; } = [];
    public bool HasSessions => Sessions.Count > 0;

    public void Refresh(DateTimeOffset? now = null)
    {
        var tasks = (_activities?.GetActiveSessionTasks() ?? [])
            .OrderByDescending(task => task.IsAwaitingConfirmation)
            .ThenBy(task => task.StartedAt ?? task.LastActivityUtc)
            .ThenBy(task => task.SessionId.Value, StringComparer.Ordinal)
            .ToArray();
        var existing = Sessions.ToDictionary(session => session.SessionId);
        var activeIds = tasks.Select(task => task.SessionId).ToHashSet();
        var previousCount = Sessions.Count;
        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!activeIds.Contains(Sessions[index].SessionId))
                Sessions.RemoveAt(index);
        }
        var currentTime = now ?? DateTimeOffset.UtcNow;
        for (var index = 0; index < tasks.Length; index++)
        {
            var task = tasks[index];
            if (!existing.TryGetValue(task.SessionId, out var session))
            {
                session = new TaskOverlaySessionViewModel(task);
                Sessions.Insert(index, session);
            }
            else if (Sessions.IndexOf(session) != index)
            {
                Sessions.Move(Sessions.IndexOf(session), index);
            }
            session.Update(task, currentTime);
        }
        if (previousCount != Sessions.Count)
            OnPropertyChanged(nameof(HasSessions));
    }
}

public sealed class TaskOverlaySessionViewModel : ObservableObject
{
    private SessionTaskSnapshot _task;
    private string _durationText = string.Empty;

    internal TaskOverlaySessionViewModel(SessionTaskSnapshot task) => _task = task;

    public SessionId SessionId => _task.SessionId;
    public string Title => string.IsNullOrWhiteSpace(_task.Title) ? Loc.Get("sidebar_untitled_session") : _task.Title;
    public string WorkspaceRoot => _task.WorkspaceRoot;
    public string WorkspaceName => Path.GetFileName(Path.TrimEndingDirectorySeparator(WorkspaceRoot)) is { Length: > 0 } name
        ? name : WorkspaceRoot;
    public bool NeedsAttention => _task.IsAwaitingConfirmation;
    public string DurationText => _durationText;
    public string DetailText
    {
        get
        {
            var phase = NeedsAttention && _task.Phase is not (TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction)
                ? Loc.Get("task_overlay_worker_waiting")
                : SessionActivityText.Phase(_task.Phase);
            List<string> parts = [string.IsNullOrEmpty(phase) ? Loc.Get("status_running") : phase];
            if (_task.RunningJobCount > 0)
                parts.Add(Loc.Get("task_overlay_background_jobs", _task.RunningJobCount));
            if (_task.RunningSubagentCount > 0)
                parts.Add(Loc.Get(_task.RunningSubagentCount == 1 ? "status_agent_one" : "status_agent_many", _task.RunningSubagentCount));
            return string.Join(" · ", parts);
        }
    }

    internal void Update(SessionTaskSnapshot task, DateTimeOffset now)
    {
        var duration = task.StartedAt is { } startedAt ? SessionActivityText.Duration(now - startedAt) : string.Empty;
        if (_task == task && _durationText == duration)
            return;
        _task = task;
        _durationText = duration;
        OnPropertyChanged(string.Empty);
    }
}
