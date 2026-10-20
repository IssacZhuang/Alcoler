using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Sessions;
using Alcoler.Workbench.Git;
using Alcoler.Workbench.Stores;
using Alcoler.Workbench.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;

namespace Alcoler.Workbench.ViewModel;

public sealed record ModelChoice(string ProviderId, string ModelId, string Display)
{
    public ModelSelection Selection => new(ProviderId, ModelId);
}

public sealed partial class WorkbenchViewModel : ObservableObject, IDisposable
{
    private readonly IAgentRuntime _runtime;
    private readonly SessionProjectionHub _hub;
    private readonly WorkbenchConfig _config;
    private readonly IAsyncDisposable? _ownedResource;
    private readonly IUiScheduler _scheduler;
    private readonly IGitService _gitService = new LibGit2GitService();
    private readonly Dictionary<SessionId, string> _sessionRoots = [];
    private string? _paneRoot;

    public WorkbenchViewModel(
        IAgentRuntime runtime,
        IUiScheduler scheduler,
        WorkbenchConfig config,
        IAsyncDisposable? ownedResource = null,
        BrowserPaneViewModel? browserPane = null)
    {
        _runtime = runtime;
        _config = config;
        _ownedResource = ownedResource;
        _scheduler = scheduler;
        BrowserPane = browserPane;
        _hub = new SessionProjectionHub(runtime, scheduler, coalesceWindow: TimeSpan.FromMilliseconds(30));
        Sidebar = new SessionSidebarViewModel(RefreshFromRuntimeAsync);
        ModelChoices = BuildModelChoices(config);
    }

    public SessionSidebarViewModel Sidebar { get; }

    public IReadOnlyList<ModelChoice> ModelChoices { get; }

    [ObservableProperty]
    private ConversationViewModel? _active;

    [ObservableProperty]
    private string? _banner;

    [ObservableProperty]
    private TerminalPaneViewModel? _terminal;

    [ObservableProperty]
    private GitPaneViewModel? _gitPane;

    public BrowserPaneViewModel? BrowserPane { get; }

    public void Dispose()
    {
        CloseActive();
        _hub.Dispose();
        Terminal?.Dispose();
        if (_ownedResource is not null)
        {
            _ownedResource.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public async Task OpenSessionAsync(SessionId sessionId)
    {
        if (Active?.SessionId == sessionId)
        {
            return;
        }
        CloseActive();
        var store = _hub.Acquire(sessionId);
        Active = new ConversationViewModel(_runtime, store);
        Sidebar.SetActive(sessionId);
        BrowserPane?.Bind(sessionId);
        await Sidebar.RefreshAsync();
        if (_sessionRoots.TryGetValue(sessionId, out var root))
        {
            EnsurePanes(root);
        }
    }

    public async Task<SessionHandle> NewSessionAsync(string workspaceRoot, string? title = null)
    {
        var handle = await _runtime.CreateSessionAsync(
            new SessionOptions(
                workspaceRoot,
                title,
                PermissionMode.Build,
                _config.DefaultModel is { } d ? new ModelSelection(d.ProviderId, d.ModelId) : null),
            CancellationToken.None);
        _sessionRoots[handle.Id] = workspaceRoot;
        await OpenSessionAsync(handle.Id);
        await Sidebar.RefreshAsync();
        return handle;
    }

    public async Task RenameActiveAsync(string title)
    {
        if (Active is null)
        {
            return;
        }
        var ack = await _runtime.SubmitAsync(
            new RenameSessionCommand(StrongId.NewCommandId(), Active.SessionId, 0, title),
            CancellationToken.None);
        if (ack.Accepted)
        {
            await Sidebar.RefreshAsync();
        }
    }

    public void ClearBanner() => Banner = null;

    public async Task RefreshFromRuntimeAsync()
    {
        var summaries = await _runtime.ListSessionsAsync(null, 200, CancellationToken.None);
        Sidebar.ApplySummaries(
        [
            .. summaries.Select(s => new SessionSummaryViewModel(s.Id, s.Title, s.WorkspaceRoot, s.UpdatedAt))
        ]);
        foreach (var summary in summaries)
        {
            _sessionRoots[summary.Id] = summary.WorkspaceRoot;
        }
    }

    private void EnsurePanes(string root)
    {
        if (Terminal is null)
        {
            Terminal = new TerminalPaneViewModel(root, scheduler: _scheduler);
        }
        if (GitPane is null || !string.Equals(_paneRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            GitPane = new GitPaneViewModel(root, _gitService, _scheduler);
            _ = GitPane.LoadAsync();
        }
        _paneRoot = root;
    }

    private void CloseActive()
    {
        if (Active is null)
        {
            return;
        }
        var sessionId = Active.SessionId;
        Active.Dispose();
        Active = null;
        _hub.Release(sessionId);
        Sidebar.SetActive(null);
    }

    private static IReadOnlyList<ModelChoice> BuildModelChoices(WorkbenchConfig config)
    {
        var choices = new List<ModelChoice>();
        foreach (var provider in config.Providers)
        {
            foreach (var model in provider.Models)
            {
                var display = model.DisplayName ?? $"{provider.Kind}:{model.ModelId}";
                choices.Add(new ModelChoice(provider.Kind, model.ModelId, display));
            }
        }
        return choices;
    }
}
