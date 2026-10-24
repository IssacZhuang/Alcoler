using System.Collections.ObjectModel;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.ViewModel;

public enum SessionActivityIndicator
{
    None,
    Running,
    AwaitingConfirmation,
    Completed,
    Failed,
}

public sealed partial class SessionSummaryViewModel : ObservableObject
{
    public SessionSummaryViewModel(
        SessionId id,
        string title,
        string workspaceRoot,
        DateTimeOffset updatedAt,
        bool isArchived = false)
    {
        Id = id;
        _title = title;
        WorkspaceRoot = workspaceRoot;
        _updatedAt = updatedAt;
        _isArchived = isArchived;
    }

    public SessionId Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _title;

    public string WorkspaceRoot { get; }

    // Mutable so refreshes can update a reused instance in place instead of
    // rebuilding every summary view model.
    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (_updatedAt == value)
                return;
            _updatedAt = value;
            OnPropertyChanged(nameof(UpdatedAt));
            OnPropertyChanged(nameof(UpdatedAtText));
        }
    }

    private DateTimeOffset _updatedAt;

    public bool IsArchived
    {
        get => _isArchived;
        set
        {
            if (_isArchived == value)
                return;
            _isArchived = value;
            OnPropertyChanged(nameof(IsArchived));
        }
    }

    private bool _isArchived;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Loc.Get("sidebar_untitled_session") : Title;

    public string UpdatedAtText => RelativeTime.Format(UpdatedAt);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActiveDot))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingConfirmation))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(ActivityLabel))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(ShowActiveDot))]
    private SessionActivityIndicator _indicator;

    public bool IsRunning => Indicator == SessionActivityIndicator.Running;

    public bool IsAwaitingConfirmation => Indicator == SessionActivityIndicator.AwaitingConfirmation;

    public bool IsBusy => IsRunning || IsAwaitingConfirmation;

    public string ActivityLabel => IsAwaitingConfirmation
        ? Loc.Get("sidebar_waiting_for_confirmation")
        : Loc.Get("sidebar_activity_running");

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

    public bool HasRunningSessions => Sessions.Any(session => session.IsRunning);

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
            { TotalMinutes: < 1 } => Loc.Get("sidebar_relative_just_now"),
            { TotalMinutes: < 60 } => Loc.Get("sidebar_relative_minutes", (int)delta.TotalMinutes),
            { TotalHours: < 24 } => Loc.Get("sidebar_relative_hours", (int)delta.TotalHours),
            { TotalDays: < 7 } => Loc.Get("sidebar_relative_days", (int)delta.TotalDays),
            _ => utc.ToLocalTime().ToString("yyyy-MM-dd"),
        };
    }
}

public sealed partial class SessionSidebarViewModel : ObservableObject
{
    private readonly Func<Task> _refreshSource;
    private readonly Dictionary<SessionId, SessionActivityIndicator> _indicators = [];
    private readonly Dictionary<SessionId, SessionSummaryViewModel> _sessionsById = [];

    public SessionSidebarViewModel(Func<Task> refreshSource) => _refreshSource = refreshSource;

    public ObservableCollection<SessionGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private SessionId? _activeSessionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    [NotifyPropertyChangedFor(nameof(ArchivedToggleToolTip))]
    private bool _isArchivedView;

    public string HeaderTitle => IsArchivedView ? Loc.Get("sidebar_archived") : Loc.Get("sidebar_projects");

    public string EmptyText => IsArchivedView ? Loc.Get("sidebar_empty_archived") : Loc.Get("sidebar_empty_active");

    public string ArchivedToggleToolTip => IsArchivedView
        ? Loc.Get("sidebar_show_active_tooltip")
        : Loc.Get("sidebar_show_archived_tooltip");

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
            // Active entries may belong to sessions created after this listing was taken.
            var listed = summaries.Select(summary => summary.Id).ToHashSet();
            foreach (var stale in _indicators
                         .Where(pair => !listed.Contains(pair.Key)
                             && pair.Value is not (SessionActivityIndicator.Running or SessionActivityIndicator.AwaitingConfirmation))
                         .ToArray())
            {
                _indicators.Remove(stale.Key);
            }
        }

        // Same grouping, key order and per-group ordering a full rebuild produces.
        var desired = summaries
            .GroupBy(s => s.WorkspaceRoot, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Root: g.Key, Ordered: g.OrderByDescending(s => s.UpdatedAt).ToArray()))
            .ToArray();
        var previousById = new Dictionary<SessionId, SessionSummaryViewModel>(_sessionsById);
        var resolvedById = new Dictionary<SessionId, SessionSummaryViewModel>();
        var existingGroups = new Dictionary<string, SessionGroupViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
            existingGroups[group.WorkspaceRoot] = group;

        _sessionsById.Clear();
        var desiredGroups = new List<SessionGroupViewModel>(desired.Length);
        foreach (var (root, ordered) in desired)
        {
            existingGroups.TryGetValue(root, out var groupVm);
            var existingSessions = groupVm is null ? [] : groupVm.Sessions;
            var resolved = new SessionSummaryViewModel[ordered.Length];
            var sessionsUnchanged = existingSessions.Count == ordered.Length;
            for (var index = 0; index < ordered.Length; index++)
            {
                var incoming = ordered[index];
                SessionSummaryViewModel vm;
                if (!resolvedById.ContainsKey(incoming.Id)
                    && previousById.TryGetValue(incoming.Id, out var cached)
                    && string.Equals(cached.WorkspaceRoot, incoming.WorkspaceRoot, StringComparison.Ordinal))
                {
                    cached.Title = incoming.Title;
                    cached.UpdatedAt = incoming.UpdatedAt;
                    cached.IsArchived = incoming.IsArchived;
                    vm = cached;
                }
                else
                {
                    vm = incoming;
                }
                resolvedById[incoming.Id] = vm;
                resolved[index] = vm;
                var isActive = incoming.Id == ActiveSessionId;
                if (isActive && _indicators.GetValueOrDefault(incoming.Id) == SessionActivityIndicator.Completed)
                    _indicators.Remove(incoming.Id);
                var indicator = _indicators.GetValueOrDefault(incoming.Id);
                vm.IsActive = isActive;
                vm.Indicator = indicator;
                // Callers keep handing freshly built instances; keep their state in
                // sync too so refreshes observed through them behave as before.
                incoming.IsActive = isActive;
                incoming.Indicator = indicator;
                _sessionsById[vm.Id] = vm;
                if (sessionsUnchanged && !ReferenceEquals(existingSessions[index], vm))
                    sessionsUnchanged = false;
            }
            if (groupVm is null)
            {
                groupVm = new SessionGroupViewModel(root);
            }
            if (sessionsUnchanged)
            {
                groupVm.SetVisibleSessionCount(groupVm.VisibleSessions.Count);
            }
            else
            {
                var visibleCount = groupVm.VisibleSessions.Count;
                groupVm.Sessions.Clear();
                groupVm.VisibleSessions.Clear();
                foreach (var vm in resolved)
                    groupVm.AddSession(vm);
                // A rebuilt group restarts from one visible page, then restores the
                // previous window size, exactly like the old full rebuild did.
                groupVm.SetVisibleSessionCount(Math.Max(visibleCount, SessionGroupViewModel.SessionPageSize));
            }
            desiredGroups.Add(groupVm);
        }

        var groupsUnchanged = Groups.Count == desiredGroups.Count;
        for (var index = 0; groupsUnchanged && index < desiredGroups.Count; index++)
            groupsUnchanged = ReferenceEquals(Groups[index], desiredGroups[index]);
        if (!groupsUnchanged)
        {
            Groups.Clear();
            foreach (var group in desiredGroups)
                Groups.Add(group);
        }
    }

    public void ApplyTitle(SessionId sessionId, string title)
    {
        if (_sessionsById.TryGetValue(sessionId, out var session))
            session.Title = title;
    }

    public void SetActive(SessionId? sessionId)
    {
        ActiveSessionId = sessionId;
        foreach (var session in _sessionsById.Values)
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
            { Phase: TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction }
                => SessionActivityIndicator.AwaitingConfirmation,
            { Phase: not TurnPhase.Idle } => SessionActivityIndicator.Running,
            { LastOutcome: TurnOutcome.Failed } => SessionActivityIndicator.Failed,
            // Completion is acknowledged when the task is opened, matching the unread dot.
            { LastOutcome: TurnOutcome.Completed } when ActiveSessionId != sessionId
                => SessionActivityIndicator.Completed,
            _ => SessionActivityIndicator.None,
        };
        RecordIndicator(sessionId, indicator);
    }

    /// <summary>Restores only non-terminal state; opening a completed task must not resurrect its indicator.</summary>
    public void SyncActiveState(SessionId sessionId, SessionActivitySnapshot? activity)
    {
        var indicator = activity switch
        {
            { Phase: TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction }
                => SessionActivityIndicator.AwaitingConfirmation,
            { Phase: not TurnPhase.Idle } => SessionActivityIndicator.Running,
            _ => SessionActivityIndicator.None,
        };
        if (indicator != SessionActivityIndicator.None)
        {
            RecordIndicator(sessionId, indicator);
        }
        else if (_indicators.GetValueOrDefault(sessionId) is
                 SessionActivityIndicator.Running or SessionActivityIndicator.AwaitingConfirmation)
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
        if (_sessionsById.TryGetValue(sessionId, out var session))
            session.Indicator = indicator;
    }
}
