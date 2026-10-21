using System.ComponentModel;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Workbench.Stores;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.Workbench.ViewModel;

public sealed record StatusTodo
{
    public string Content { get; init; } = string.Empty;
    public string Status { get; init; } = "pending";
    public bool IsCompleted => Status == "completed";
    public bool IsActive => Status == "in_progress";
    public string IconPath => IsCompleted ? StatusIcons.Completed : IsActive ? StatusIcons.Active : StatusIcons.Pending;
}

public sealed record StatusPlan
{
    public string RowId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Markdown { get; init; } = string.Empty;
}

public sealed record StatusActivity
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string DurationText { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public string? OutputPath { get; init; }
    public string? OutputText { get; init; }
    public string? RowId { get; init; }
    public SessionId? ChildSessionId { get; init; }
    public BackgroundJobId? BackgroundJobId { get; init; }
    public bool CanStop { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

public sealed class ConversationStatusViewModel : ObservableObject, IDisposable
{
    private const int MaxTodoItems = 100;
    private const int MaxTodoContentCharacters = 20_000;

    private readonly SessionId _sessionId;
    private readonly ISessionActivityRuntime? _activities;
    private readonly Dictionary<string, StatusRow> _rows = [];
    private long _nextOrder;
    private TurnPhase? _phase;
    private bool _disposed;
    private GitPaneViewModel? _git;
    private IReadOnlyList<StatusTodo> _todos = [];
    private IReadOnlyList<StatusTodo> _visibleTodos = [];
    private IReadOnlyList<StatusTodo> _earlierTodos = [];
    private IReadOnlyList<StatusTodo> _laterTodos = [];
    private IReadOnlyList<StatusPlan> _plans = [];
    private IReadOnlyList<StatusActivity> _terminals = [];
    private IReadOnlyList<StatusActivity> _agents = [];
    private IReadOnlyList<StatusActivity> _endedAgents = [];
    private bool _hasContent;
    private string _summaryText = string.Empty;
    private string _summaryIconPath = string.Empty;

    public ConversationStatusViewModel(SessionId sessionId, IAgentRuntime runtime)
    {
        _sessionId = sessionId;
        _activities = runtime as ISessionActivityRuntime;
    }

    public bool HasContent => _hasContent;
    public string SummaryText => _summaryText;
    public string SummaryIconPath => _summaryIconPath;
    public GitPaneViewModel? Git => _git;
    public bool HasGit => Git is { IsRepo: true, HasChanges: true };
    public bool HasGitLineChanges => Git is { } git && (git.AddedLines > 0 || git.RemovedLines > 0);
    public string GitFileCountText => $"{Git?.ChangedFileCount ?? 0} {(Git?.ChangedFileCount == 1 ? "file" : "files")}";
    public string GitAddedText => $"+{Git?.AddedLines ?? 0}";
    public string GitRemovedText => $"-{Git?.RemovedLines ?? 0}";
    public IReadOnlyList<StatusTodo> Todos => _todos;
    public IReadOnlyList<StatusTodo> VisibleTodos => _visibleTodos;
    public IReadOnlyList<StatusTodo> EarlierTodos => _earlierTodos;
    public IReadOnlyList<StatusTodo> LaterTodos => _laterTodos;
    public bool HasTodos => Todos.Count > 0;
    public string TodoCountText => $"{Todos.Count(todo => todo.IsCompleted)}/{Todos.Count}";
    public IReadOnlyList<StatusPlan> Plans => _plans;
    public bool HasPlans => Plans.Count > 0;
    public IReadOnlyList<StatusActivity> Terminals => _terminals;
    public bool HasTerminals => Terminals.Count > 0;
    public string TerminalCountText => Terminals.Count.ToString();
    public IReadOnlyList<StatusActivity> Agents => _agents;
    public IReadOnlyList<StatusActivity> EndedAgents => _endedAgents;
    public bool HasAgents => Agents.Count + EndedAgents.Count > 0;
    public string AgentCountText => EndedAgents.Count == 0
        ? Agents.Count.ToString()
        : $"{Agents.Count}/{Agents.Count + EndedAgents.Count}";
    public bool HasRunningActivities => Terminals.Count > 0 || Agents.Count > 0;
    public string RunningActivityCountText => (Terminals.Count + Agents.Count) switch
    {
        1 => "1 running",
        var count => $"{count} running",
    };

    public void ApplyUpdate(ProjectionUpdate update)
    {
        if (_disposed)
        {
            return;
        }

        var changes = Changes.None;
        if (update.Reset)
        {
            var previous = new Dictionary<string, StatusRow>(_rows);
            _rows.Clear();
            _nextOrder = 0;
            foreach (var row in update.ResetRows ?? [])
            {
                if (Track(row, previous.GetValueOrDefault(row.RowId), _nextOrder++) is { } tracked)
                {
                    _rows[row.RowId] = tracked;
                }
            }
            changes = Changes.All;
        }
        else
        {
            foreach (var op in update.Ops)
            {
                switch (op)
                {
                    case RowAppended appended when !_rows.ContainsKey(appended.Row.RowId):
                        changes |= Upsert(appended.Row);
                        break;
                    case RowUpserted upserted:
                        changes |= Upsert(upserted.Row);
                        break;
                    case RowRemoved removed when _rows.Remove(removed.RowId, out var old):
                        changes |= old.Changes;
                        break;
                }
            }
        }

        if ((changes & Changes.Todos) != 0)
        {
            UpdateTodos();
        }
        if ((changes & Changes.Plans) != 0)
        {
            SetList(ref _plans, OrderedRows().Reverse().Where(row => row.Plan is not null)
                .Select(row => row.Plan!).ToArray(), nameof(Plans));
            OnPropertyChanged(nameof(HasPlans));
        }
        if ((changes & Changes.Activities) != 0)
        {
            RefreshActivities();
        }
        else if (changes != Changes.None)
        {
            UpdateSummary();
        }

        var refreshGit = update.State.Phase == TurnPhase.Idle && _phase is not null
            && _phase is not (TurnPhase.Idle or TurnPhase.Error);
        _phase = update.State.Phase;
        if (refreshGit && Git is { } git)
        {
            _ = git.LoadAsync();
        }
    }

    public void BindGit(GitPaneViewModel? git)
    {
        if (_disposed || ReferenceEquals(_git, git))
        {
            return;
        }
        if (_git is not null)
        {
            _git.PropertyChanged -= OnGitChanged;
        }
        SetProperty(ref _git, git, nameof(Git));
        if (_git is not null)
        {
            _git.PropertyChanged += OnGitChanged;
        }
        NotifyGitChanged();
    }

    public void RefreshActivities(DateTimeOffset? now = null)
    {
        if (_disposed)
        {
            return;
        }
        var currentTime = now ?? DateTimeOffset.UtcNow;
        List<StatusActivity> terminals = [];
        foreach (var row in OrderedRows())
        {
            if (row.Terminal is { } terminal)
            {
                terminals.Add(terminal with { DurationText = FormatDuration(terminal.StartedAt, currentTime) });
            }
        }
        foreach (var job in _activities?.GetBackgroundJobs(_sessionId) ?? [])
        {
            if (job.Status == BackgroundJobStatus.Running)
            {
                terminals.Add(new StatusActivity
                {
                    Id = job.Id.Value,
                    BackgroundJobId = job.Id,
                    Title = string.IsNullOrWhiteSpace(job.Description) ? "Background command" : job.Description,
                    StartedAt = job.StartedAt,
                    DurationText = FormatDuration(job.StartedAt, currentTime),
                    OutputPath = job.OutputPath,
                    StatusText = "Running",
                    CanStop = true,
                });
            }
        }
        SetList(ref _terminals, terminals.ToArray(), nameof(Terminals));
        OnPropertyChanged(nameof(HasTerminals));
        OnPropertyChanged(nameof(TerminalCountText));

        var latestAgents = OrderedRows().Where(row => row.Subagent is not null)
            .GroupBy(row => row.Subagent!.Id).Select(group => group.Last()).ToArray();
        var active = latestAgents.Where(row => row.Subagent!.Status == SubagentStatus.Running)
            .Select(row => AgentActivity(row, currentTime)).ToArray();
        var ended = latestAgents.Reverse().Where(row => row.Subagent!.Status != SubagentStatus.Running)
            .Select(row => AgentActivity(row, currentTime)).ToArray();
        SetList(ref _agents, active, nameof(Agents));
        SetList(ref _endedAgents, ended, nameof(EndedAgents));
        OnPropertyChanged(nameof(HasAgents));
        OnPropertyChanged(nameof(AgentCountText));
        OnPropertyChanged(nameof(HasRunningActivities));
        OnPropertyChanged(nameof(RunningActivityCountText));
        UpdateSummary();
    }

    public async Task<bool> StopActivityAsync(StatusActivity activity)
    {
        if (_disposed || _activities is null || !activity.CanStop)
        {
            return false;
        }
        if (activity.BackgroundJobId is { } id)
        {
            if (!Terminals.Any(terminal => terminal.BackgroundJobId == id && terminal.CanStop)
                || !_activities.GetBackgroundJobs(_sessionId).Any(job => job.Id == id && job.Status == BackgroundJobStatus.Running))
            {
                return false;
            }
            var stopped = _activities.StopBackgroundJob(_sessionId, id);
            RefreshActivities();
            return stopped;
        }
        if (activity.ChildSessionId is null)
        {
            return false;
        }
        var agentStopped = await _activities.TryStopSubagentAsync(_sessionId, activity.Id);
        RefreshActivities();
        return agentStopped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_git is not null)
        {
            _git.PropertyChanged -= OnGitChanged;
        }
        _rows.Clear();
    }

    private Changes Upsert(ConversationRow row)
    {
        var previous = _rows.GetValueOrDefault(row.RowId);
        var tracked = Track(row, previous, previous?.Order ?? _nextOrder++);
        var changes = previous?.Changes ?? Changes.None;
        if (row is ToolCallRow tool && string.Equals(tool.ToolName, "Bash", StringComparison.OrdinalIgnoreCase))
        {
            changes |= Changes.Activities;
        }
        if (tracked is null)
        {
            _rows.Remove(row.RowId);
        }
        else
        {
            _rows[row.RowId] = tracked;
            changes |= tracked.Changes;
        }
        return changes;
    }

    private static StatusRow? Track(ConversationRow row, StatusRow? previous, long order)
    {
        if (row is SystemNoticeRow { Subagent: { } subagent })
        {
            return new StatusRow(row.RowId, order, Changes.Activities) { Subagent = subagent };
        }
        if (row is not ToolCallRow tool)
        {
            return null;
        }
        var name = tool.ToolName.ToLowerInvariant();
        if (name == "todowrite")
        {
            return new StatusRow(row.RowId, order, Changes.Todos)
            {
                Todos = IsSuccessful(tool) && ParseTodos(tool.Input) is { } todos
                    ? todos
                    : previous?.Todos,
            };
        }
        if (name == "exitplanmode")
        {
            return new StatusRow(row.RowId, order, Changes.Plans)
            {
                Plan = IsSuccessful(tool) && ParsePlan(tool) is { } plan ? plan : previous?.Plan,
            };
        }
        if (name != "bash")
        {
            return null;
        }
        var running = tool.Status is ToolCallStatus.Pending or ToolCallStatus.Running;
        var detached = tool.Input.ValueKind == JsonValueKind.Object
            && tool.Input.TryGetProperty("run_in_background", out var background)
            && background.ValueKind == JsonValueKind.True;
        if (!running || detached)
        {
            return null;
        }
        return new StatusRow(row.RowId, order, Changes.Activities)
        {
            Terminal = new StatusActivity
            {
                Id = tool.Id.Value,
                RowId = tool.RowId,
                Title = ReadText(tool.Input, "description") ?? ReadText(tool.Input, "command") ?? "Terminal",
                StartedAt = tool.StartedAt,
                StatusText = tool.Status == ToolCallStatus.Pending ? "Pending" : "Running",
                OutputText = tool.Output is null ? string.Empty
                    : string.Join("\n", tool.Output.OfType<ToolResultText>().Select(block => block.Text)),
            },
        };
    }

    private static bool IsSuccessful(ToolCallRow row) => row.Status == ToolCallStatus.Succeeded && !row.IsError;

    private static IReadOnlyList<StatusTodo>? ParseTodos(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("todos", out var todos)
            || todos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        List<StatusTodo> result = [];
        var remainingCharacters = MaxTodoContentCharacters;
        foreach (var item in todos.EnumerateArray())
        {
            if (ReadText(item, "content") is not { } content)
            {
                return null;
            }
            var status = "pending";
            if (item.TryGetProperty("status", out var statusElement))
            {
                if (statusElement.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
                status = statusElement.GetString()!.ToLowerInvariant();
                if (status is not ("pending" or "in_progress" or "completed"))
                {
                    return null;
                }
            }
            var retained = content.Length <= remainingCharacters ? content : content[..remainingCharacters];
            remainingCharacters -= retained.Length;
            result.Add(new StatusTodo { Content = retained, Status = status });
            if (result.Count == MaxTodoItems)
            {
                break;
            }
        }
        return result.ToArray();
    }

    private static StatusPlan? ParsePlan(ToolCallRow row)
    {
        if (ReadText(row.Input, "plan") is not { } markdown)
        {
            return null;
        }
        return new StatusPlan
        {
            RowId = row.RowId,
            Markdown = markdown,
            Title = ReadText(row.Input, "summary")?.Trim() ?? FirstHeading(markdown) ?? "Plan",
        };
    }

    private static string? FirstHeading(string markdown)
    {
        string? fence = null;
        foreach (var line in markdown.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = text[..3];
                fence = fence is null ? marker : fence == marker ? null : fence;
                continue;
            }
            if (fence is not null)
            {
                continue;
            }
            var heading = text.TakeWhile(character => character == '#').Count();
            if (heading is >= 1 and <= 6 && text.Length > heading && char.IsWhiteSpace(text[heading]))
            {
                var title = text[heading..].Trim().TrimEnd('#').TrimEnd();
                if (title.Length > 0)
                {
                    return title;
                }
            }
        }
        return null;
    }

    private static string? ReadText(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private IEnumerable<StatusRow> OrderedRows() => _rows.Values.OrderBy(row => row.Order);

    private void UpdateTodos()
    {
        var todos = OrderedRows().LastOrDefault(row => row.Todos is not null)?.Todos ?? [];
        if (!SetList(ref _todos, todos, nameof(Todos)))
        {
            return;
        }
        var anchor = Todos.ToList().FindIndex(todo => todo.IsActive);
        if (anchor < 0)
        {
            anchor = Todos.ToList().FindIndex(todo => !todo.IsCompleted);
        }
        if (anchor < 0)
        {
            anchor = Todos.Count - 1;
        }
        var start = Todos.Count <= 6 ? 0 : Math.Clamp(anchor - 1, 0, Todos.Count - 3);
        var count = Todos.Count <= 6 ? Todos.Count : 3;
        SetList(ref _visibleTodos, Todos.Skip(start).Take(count).ToArray(), nameof(VisibleTodos));
        SetList(ref _earlierTodos, Todos.Take(start).ToArray(), nameof(EarlierTodos));
        SetList(ref _laterTodos, Todos.Skip(start + count).ToArray(), nameof(LaterTodos));
        OnPropertyChanged(nameof(HasTodos));
        OnPropertyChanged(nameof(TodoCountText));
    }

    private static StatusActivity AgentActivity(StatusRow row, DateTimeOffset now)
    {
        var agent = row.Subagent!;
        return new StatusActivity
        {
            Id = agent.Id.Value,
            Title = agent.Description,
            RowId = row.RowId,
            ChildSessionId = agent.ChildSessionId,
            StartedAt = agent.StartedAt,
            DurationText = FormatDuration(agent.StartedAt, agent.EndedAt ?? now),
            StatusText = agent.Status.ToString(),
            OutputText = agent.ResultSummary,
            CanStop = agent.Status == SubagentStatus.Running,
        };
    }

    private static string FormatDuration(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var elapsed = now > startedAt ? now - startedAt : TimeSpan.Zero;
        return elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
            : elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{(int)elapsed.TotalSeconds}s";
    }

    private void OnGitChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is nameof(GitPaneViewModel.IsRepo)
            or nameof(GitPaneViewModel.HasChanges) or nameof(GitPaneViewModel.ChangedFileCount)
            or nameof(GitPaneViewModel.AddedLines) or nameof(GitPaneViewModel.RemovedLines))
        {
            NotifyGitChanged();
        }
    }

    private void NotifyGitChanged()
    {
        OnPropertyChanged(nameof(HasGit));
        OnPropertyChanged(nameof(HasGitLineChanges));
        OnPropertyChanged(nameof(GitFileCountText));
        OnPropertyChanged(nameof(GitAddedText));
        OnPropertyChanged(nameof(GitRemovedText));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        string text;
        string icon;
        if ((Todos.FirstOrDefault(todo => todo.IsActive) ?? Todos.FirstOrDefault(todo => !todo.IsCompleted)) is { } unfinished)
        {
            text = unfinished.Content;
            icon = unfinished.IconPath;
        }
        else if (HasGit)
        {
            text = $"{Git!.ChangedFileCount} changed {(Git.ChangedFileCount == 1 ? "file" : "files")}";
            icon = StatusIcons.Git;
        }
        else if (Todos.LastOrDefault(todo => todo.IsCompleted) is { } completed)
        {
            text = completed.Content;
            icon = completed.IconPath;
        }
        else if (HasTodos)
        {
            text = TodoCountText;
            icon = StatusIcons.Tasks;
        }
        else if (Plans.FirstOrDefault() is { } plan)
        {
            text = plan.Title;
            icon = StatusIcons.Plan;
        }
        else
        {
            var terminals = $"{Terminals.Count} {(Terminals.Count == 1 ? "terminal" : "terminals")}";
            var agents = $"{Agents.Count} {(Agents.Count == 1 ? "agent" : "agents")}";
            text = HasTerminals && Agents.Count > 0 ? $"{terminals} and {agents} running"
                : HasTerminals ? $"{terminals} running" : Agents.Count > 0 ? $"{agents} running" : string.Empty;
            icon = HasTerminals && Agents.Count > 0 ? StatusIcons.Activities
                : HasTerminals ? StatusIcons.Terminal : Agents.Count > 0 ? StatusIcons.Agent : string.Empty;
        }
        SetProperty(ref _summaryText, text, nameof(SummaryText));
        SetProperty(ref _summaryIconPath, icon, nameof(SummaryIconPath));
        SetProperty(ref _hasContent, HasTodos || HasGit || HasPlans || HasTerminals || Agents.Count > 0, nameof(HasContent));
    }

    private bool SetList<T>(ref IReadOnlyList<T> field, IReadOnlyList<T> value, string propertyName) =>
        !field.SequenceEqual(value) && SetProperty(ref field, value, propertyName);

    [Flags]
    private enum Changes
    {
        None = 0,
        Todos = 1,
        Plans = 2,
        Activities = 4,
        All = Todos | Plans | Activities,
    }

    private sealed record StatusRow(string RowId, long Order, Changes Changes)
    {
        public IReadOnlyList<StatusTodo>? Todos { get; init; }
        public StatusPlan? Plan { get; init; }
        public StatusActivity? Terminal { get; init; }
        public SubagentState? Subagent { get; init; }
    }
}

internal static class StatusIcons
{
    internal const string Pending = "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Z";
    internal const string Active = "M5 12h14m-6-6 6 6-6 6";
    internal const string Completed = "m5 12 4 4L19 6";
    internal const string Tasks = "m3 6 2 2 4-4M13 6h8m-18 6 2 2 4-4M13 12h8m-18 6 2 2 4-4M13 18h8";
    internal const string Git = "M6 3v12a3 3 0 1 0 3 3M6 6a3 3 0 1 0 0-6M18 3a3 3 0 1 0 0 6M18 9v3a6 6 0 0 1-6 6H9";
    internal const string Plan = "M9 5H5a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2h-4M9 3h6v4H9ZM8 12h8M8 16h5";
    internal const string Terminal = "M4 17l6-6-6-6M12 19h8";
    internal const string Agent = "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75";
    internal const string Activities = "M4 4h16v16H4ZM7 8l3 3-3 3M12 15h5";
}
