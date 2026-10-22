using System.Collections.ObjectModel;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.Workbench.ViewModel;

public enum SessionActivityIndicator
{
    None,
    Running,
    Completed,
    Failed,
}

public sealed partial class SessionSummaryViewModel : ObservableObject
{
    public SessionSummaryViewModel(SessionId id, string title, string workspaceRoot, DateTimeOffset updatedAt)
    {
        Id = id;
        _title = title;
        WorkspaceRoot = workspaceRoot;
        UpdatedAt = updatedAt;
    }

    public SessionId Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _title;

    public string WorkspaceRoot { get; }

    public DateTimeOffset UpdatedAt { get; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled session" : Title;

    public string UpdatedAtText => RelativeTime.Format(UpdatedAt);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActiveDot))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(ShowActiveDot))]
    private SessionActivityIndicator _indicator;

    public bool IsRunning => Indicator == SessionActivityIndicator.Running;

    public bool IsCompleted => Indicator == SessionActivityIndicator.Completed;

    public bool IsFailed => Indicator == SessionActivityIndicator.Failed;

    public bool ShowActiveDot => IsActive && Indicator == SessionActivityIndicator.None;
}

public sealed partial class SessionGroupViewModel : ObservableObject
{
    public const int SessionPageSize = 5;

    public SessionGroupViewModel(string workspaceRoot) => WorkspaceRoot = workspaceRoot;

    public string WorkspaceRoot { get; }

    public string DisplayTitle
    {
        get
        {
            var trimmed = WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? WorkspaceRoot : name;
        }
    }

    public ObservableCollection<SessionSummaryViewModel> Sessions { get; } = [];

    public ObservableCollection<SessionSummaryViewModel> VisibleSessions { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    public bool HasSessionOverflow => Sessions.Count > SessionPageSize;

    public bool CanShowMore => VisibleSessions.Count < Sessions.Count;

    public bool CanCollapse => VisibleSessions.Count > SessionPageSize;

    public void AddSession(SessionSummaryViewModel session)
    {
        Sessions.Add(session);
        if (VisibleSessions.Count < SessionPageSize)
        {
            VisibleSessions.Add(session);
        }
        OnPropertyChanged(nameof(HasSessionOverflow));
        NotifyVisibilityChanged();
    }

    public void ShowMore()
    {
        AddVisibleThrough(Math.Min(Sessions.Count, VisibleSessions.Count + SessionPageSize));
        NotifyVisibilityChanged();
    }

    public void Collapse()
    {
        while (VisibleSessions.Count > SessionPageSize)
        {
            VisibleSessions.RemoveAt(VisibleSessions.Count - 1);
        }
        NotifyVisibilityChanged();
    }

    public void SetVisibleSessionCount(int count)
    {
        var target = Math.Min(Sessions.Count, Math.Max(SessionPageSize, count));
        AddVisibleThrough(target);
        while (VisibleSessions.Count > target)
        {
            VisibleSessions.RemoveAt(VisibleSessions.Count - 1);
        }
        NotifyVisibilityChanged();
    }

    private void AddVisibleThrough(int target)
    {
        while (VisibleSessions.Count < target)
        {
            VisibleSessions.Add(Sessions[VisibleSessions.Count]);
        }
    }

    private void NotifyVisibilityChanged()
    {
        OnPropertyChanged(nameof(CanShowMore));
        OnPropertyChanged(nameof(CanCollapse));
    }
}

public static class RelativeTime
{
    public static string Format(DateTimeOffset utc)
    {
        var delta = DateTimeOffset.UtcNow - utc;
        return delta switch
        {
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)delta.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)delta.TotalHours}h ago",
            { TotalDays: < 7 } => $"{(int)delta.TotalDays}d ago",
            _ => utc.ToLocalTime().ToString("yyyy-MM-dd"),
        };
    }
}

public sealed partial class SessionSidebarViewModel : ObservableObject
{
    private readonly Func<Task> _refreshSource;
    private readonly Dictionary<SessionId, SessionActivityIndicator> _indicators = [];

    public SessionSidebarViewModel(Func<Task> refreshSource) => _refreshSource = refreshSource;

    public ObservableCollection<SessionGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private SessionId? _activeSessionId;

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            await _refreshSource();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ApplySummaries(IReadOnlyList<SessionSummaryViewModel> summaries)
    {
        if (_indicators.Count > 0)
        {
            // Running entries may belong to sessions created after this listing was taken.
            var listed = summaries.Select(summary => summary.Id).ToHashSet();
            foreach (var stale in _indicators
                         .Where(pair => !listed.Contains(pair.Key) && pair.Value != SessionActivityIndicator.Running)
                         .ToArray())
            {
                _indicators.Remove(stale.Key);
            }
        }
        var expansion = Groups.ToDictionary(
            g => g.WorkspaceRoot,
            g => (g.IsExpanded, VisibleCount: g.VisibleSessions.Count),
            StringComparer.OrdinalIgnoreCase);
        Groups.Clear();
        foreach (var group in summaries
                     .GroupBy(s => s.WorkspaceRoot, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var (isExpanded, visibleCount) = expansion.TryGetValue(group.Key, out var state)
                ? state
                : (true, SessionGroupViewModel.SessionPageSize);
            var groupVm = new SessionGroupViewModel(group.Key)
            {
                IsExpanded = isExpanded,
            };
            foreach (var session in group.OrderByDescending(s => s.UpdatedAt))
            {
                session.IsActive = session.Id == ActiveSessionId;
                if (session.IsActive && _indicators.GetValueOrDefault(session.Id) == SessionActivityIndicator.Completed)
                {
                    _indicators.Remove(session.Id);
                }
                session.Indicator = _indicators.GetValueOrDefault(session.Id);
                groupVm.AddSession(session);
            }
            groupVm.SetVisibleSessionCount(visibleCount);
            Groups.Add(groupVm);
        }
    }

    public void ApplyTitle(SessionId sessionId, string title)
    {
        foreach (var session in Groups.SelectMany(g => g.Sessions).Where(s => s.Id == sessionId))
        {
            session.Title = title;
        }
    }

    public void SetActive(SessionId? sessionId)
    {
        ActiveSessionId = sessionId;
        foreach (var session in Groups.SelectMany(g => g.Sessions))
        {
            session.IsActive = session.Id == sessionId;
            if (session.IsActive && session.Indicator == SessionActivityIndicator.Completed)
            {
                RecordIndicator(session.Id, SessionActivityIndicator.None);
            }
        }
    }

    public void ApplyActivity(SessionId sessionId, SessionActivitySnapshot? activity)
    {
        var indicator = activity switch
        {
            null => SessionActivityIndicator.None,
            { Phase: not TurnPhase.Idle } => SessionActivityIndicator.Running,
            { LastOutcome: TurnOutcome.Failed } => SessionActivityIndicator.Failed,
            // Completion is acknowledged when the task is opened, matching the unread dot.
            { LastOutcome: TurnOutcome.Completed } when ActiveSessionId != sessionId
                => SessionActivityIndicator.Completed,
            _ => SessionActivityIndicator.None,
        };
        RecordIndicator(sessionId, indicator);
    }

    /// <summary>Restores only running state; opening a completed task must not resurrect its indicator.</summary>
    public void SyncRunningState(SessionId sessionId, SessionActivitySnapshot? activity)
    {
        if (activity is { Phase: not TurnPhase.Idle })
        {
            RecordIndicator(sessionId, SessionActivityIndicator.Running);
        }
        else if (_indicators.GetValueOrDefault(sessionId) == SessionActivityIndicator.Running)
        {
            RecordIndicator(sessionId, SessionActivityIndicator.None);
        }
    }

    private void RecordIndicator(SessionId sessionId, SessionActivityIndicator indicator)
    {
        if (indicator == SessionActivityIndicator.None)
        {
            _indicators.Remove(sessionId);
        }
        else
        {
            _indicators[sessionId] = indicator;
        }
        foreach (var session in Groups.SelectMany(g => g.Sessions).Where(s => s.Id == sessionId))
        {
            session.Indicator = indicator;
        }
    }
}
