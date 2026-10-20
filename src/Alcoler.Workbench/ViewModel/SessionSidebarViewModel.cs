using System.Collections.ObjectModel;
using Alcoler.Contracts.Identifiers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.Workbench.ViewModel;

public sealed partial class SessionSummaryViewModel : ObservableObject
{
    public SessionSummaryViewModel(SessionId id, string title, string workspaceRoot, DateTimeOffset updatedAt)
    {
        Id = id;
        Title = title;
        WorkspaceRoot = workspaceRoot;
        UpdatedAt = updatedAt;
    }

    public SessionId Id { get; }

    public string Title { get; }

    public string WorkspaceRoot { get; }

    public DateTimeOffset UpdatedAt { get; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled session" : Title;

    public string UpdatedAtText => RelativeTime.Format(UpdatedAt);

    [ObservableProperty]
    private bool _isActive;
}

public sealed partial class SessionGroupViewModel : ObservableObject
{
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

    [ObservableProperty]
    private bool _isExpanded = true;
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
        Groups.Clear();
        foreach (var group in summaries
                     .GroupBy(s => s.WorkspaceRoot, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var groupVm = new SessionGroupViewModel(group.Key);
            foreach (var session in group.OrderByDescending(s => s.UpdatedAt))
            {
                session.IsActive = session.Id == ActiveSessionId;
                groupVm.Sessions.Add(session);
            }
            Groups.Add(groupVm);
        }
    }

    public void SetActive(SessionId? sessionId)
    {
        ActiveSessionId = sessionId;
        foreach (var session in Groups.SelectMany(g => g.Sessions))
        {
            session.IsActive = session.Id == sessionId;
        }
    }
}
