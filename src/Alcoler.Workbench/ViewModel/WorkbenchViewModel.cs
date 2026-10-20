using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Sessions;
using Alcoler.Workbench.Git;
using Alcoler.Workbench.Stores;
using Alcoler.Workbench.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;

namespace Alcoler.Workbench.ViewModel;

public sealed record ModelChoice(string ProviderId, string ModelId, string Display, ModelProperties? Properties = null)
{
    public ModelSelection Selection => new(ProviderId, ModelId);
}

public enum SidePaneKind
{
    Git,
    Browser,
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
    private readonly Dictionary<string, DraftConversationViewModel> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SessionId, SessionDraft> _sessionDrafts = [];
    private string? _focusedWorkspaceRoot;
    private long _navigationVersion;
    private string? _paneRoot;
    private bool _terminalAutoStarted;

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
        Draft = GetOrCreateDraft(Environment.CurrentDirectory);
    }

    public SessionSidebarViewModel Sidebar { get; }

    public IReadOnlyList<ModelChoice> ModelChoices { get; }

    public static string OutsideProjectRoot => Path.Combine(WorkbenchDataPaths.Root, "workspace", "default");

    public WorkbenchProviderConfig? FindProvider(string id) =>
        _config.Providers.FirstOrDefault(provider => provider.EffectiveId == id);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerHost))]
    [NotifyPropertyChangedFor(nameof(IsDraft))]
    private ConversationViewModel? _active;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerHost))]
    private DraftConversationViewModel? _draft;

    public IComposerHost? ComposerHost => Active ?? (IComposerHost?)Draft;

    public bool IsDraft => Active is null;

    [ObservableProperty]
    private string? _banner;

    [ObservableProperty]
    private string? _unsentText;

    [ObservableProperty]
    private TerminalPaneViewModel? _terminal;

    [ObservableProperty]
    private GitPaneViewModel? _gitPane;

    public BrowserPaneViewModel? BrowserPane { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitPaneSelected))]
    [NotifyPropertyChangedFor(nameof(IsBrowserPaneSelected))]
    private SidePaneKind _selectedSidePane = SidePaneKind.Git;

    [ObservableProperty]
    private bool _isSidePaneVisible;

    [ObservableProperty]
    private bool _isTerminalVisible;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    public bool IsGitPaneSelected => SelectedSidePane == SidePaneKind.Git;

    public bool IsBrowserPaneSelected => SelectedSidePane == SidePaneKind.Browser;

    [RelayCommand]
    private void ShowGitPane()
    {
        SelectedSidePane = SidePaneKind.Git;
        IsSidePaneVisible = true;
    }

    [RelayCommand]
    private void ShowBrowserPane()
    {
        SelectedSidePane = SidePaneKind.Browser;
        IsSidePaneVisible = true;
    }

    [RelayCommand]
    private void ToggleSidePane() => IsSidePaneVisible = !IsSidePaneVisible;

    [RelayCommand]
    private async Task ToggleTerminalAsync()
    {
        IsTerminalVisible = !IsTerminalVisible;
        if (IsTerminalVisible)
        {
            await StartTerminalOnceAsync();
        }
    }

    private async Task StartTerminalOnceAsync()
    {
        if (_terminalAutoStarted || Terminal is not { IsConnected: false } terminal)
        {
            return;
        }
        _terminalAutoStarted = true;
        try
        {
            await terminal.StartAsync();
        }
        catch (Exception)
        {
            // A failed spawn leaves the pane offline; the header Start button retries.
        }
    }

    [RelayCommand]
    private void CloseSidePane() => IsSidePaneVisible = false;

    public void Dispose()
    {
        CloseActive();
        _hub.Dispose();
        Terminal?.Dispose();
        BrowserPane?.Dispose();
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
        var navigation = ++_navigationVersion;
        if (_runtime.TryObserve(sessionId) is null)
        {
            var handle = await _runtime.ResumeSessionAsync(sessionId, CancellationToken.None);
            _sessionRoots[sessionId] = handle.WorkspaceRoot;
        }
        if (navigation != _navigationVersion)
        {
            return;
        }
        CloseActive();
        Draft = null;
        var store = _hub.Acquire(sessionId);
        _sessionDrafts.TryGetValue(sessionId, out var saved);
        var savedText = saved?.Composer.Draft;
        var savedMode = saved?.Composer.Mode;
        var conversation = new ConversationViewModel(_runtime, store, saved?.Composer);
        conversation.ApplyUpdate(new ProjectionUpdate(0, store.State, [], Reset: true, ResetRows: store.Rows));
        if (saved is not null)
        {
            if (FindModel(saved.Selection) is { } model)
            {
                conversation.ConfigureModel(model);
            }
            conversation.RestoreComposerState(savedText!, savedMode!.Value, saved.Selection);
            conversation.Banner = saved.Banner;
            if (saved.UnsentText is not null)
            {
                Banner = saved.Banner;
                UnsentText = saved.UnsentText;
            }
        }
        Active = conversation;
        Sidebar.SetActive(sessionId);
        BrowserPane?.Bind(sessionId);
        if (_sessionRoots.TryGetValue(sessionId, out var root))
        {
            _focusedWorkspaceRoot = root;
            EnsurePanes(root);
        }
        await Sidebar.RefreshAsync();
        if (Active?.SessionId == sessionId && _sessionRoots.TryGetValue(sessionId, out root))
        {
            _focusedWorkspaceRoot = root;
            EnsurePanes(root);
        }
    }

    public void ShowNewTask(string? workspaceRoot = null)
    {
        ++_navigationVersion;
        var root = workspaceRoot ?? _focusedWorkspaceRoot
            ?? (Active is { } active ? _sessionRoots.GetValueOrDefault(active.SessionId) : null)
            ?? Sidebar.Groups.OrderByDescending(group => group.Sessions.Select(session => session.UpdatedAt).DefaultIfEmpty().Max())
                .FirstOrDefault()?.WorkspaceRoot
            ?? Draft?.WorkspaceRoot
            ?? Environment.CurrentDirectory;
        CloseActive();
        _focusedWorkspaceRoot = Path.GetFullPath(root);
        Draft = GetOrCreateDraft(_focusedWorkspaceRoot);
        EnsurePanes(_focusedWorkspaceRoot);
    }

    public void FocusWorkspace(string workspaceRoot) => _focusedWorkspaceRoot = Path.GetFullPath(workspaceRoot);

    public void WorkOutsideProject()
    {
        Directory.CreateDirectory(OutsideProjectRoot);
        ShowNewTask(OutsideProjectRoot);
    }

    private DraftConversationViewModel GetOrCreateDraft(string root)
    {
        root = Path.GetFullPath(root);
        if (_drafts.TryGetValue(root, out var draft))
        {
            return draft;
        }
        var selection = WorkbenchModelCatalog.DefaultSelection(_config);
        draft = new DraftConversationViewModel(root, FindModel(selection), SendDraftAsync);
        if (selection?.Options?.ReasoningLevel is { } reasoning)
        {
            draft.ThoughtLevel = reasoning;
        }
        _drafts[root] = draft;
        return draft;
    }

    private ModelChoice? FindModel(ModelSelection? selection) => selection is null ? null : ModelChoices.FirstOrDefault(choice =>
        choice.ProviderId == selection.ProviderId && choice.ModelId == selection.ModelId);

    private async Task SendDraftAsync(DraftConversationViewModel draft)
    {
        if (!draft.Composer.CanSend)
        {
            return;
        }
        if (draft.CreatedSessionId is { } existing)
        {
            await OpenSessionAsync(existing);
            return;
        }
        var text = draft.Composer.Draft;
        var mode = draft.Composer.Mode;
        var selection = draft.Selection;
        var navigation = _navigationVersion;
        draft.Composer.IsSending = true;
        draft.Banner = null;
        draft.UnsentText = null;
        SessionId? createdId = null;
        var submitting = false;
        try
        {
            var handle = await _runtime.CreateSessionAsync(
                new SessionOptions(draft.WorkspaceRoot, PermissionMode: mode, ModelSelection: selection), CancellationToken.None);
            createdId = handle.Id;
            draft.CreatedSessionId = handle.Id;
            _sessionRoots[handle.Id] = handle.WorkspaceRoot;
            _sessionDrafts[handle.Id] = new SessionDraft(draft.Composer, draft.Selection, null);
            if (_drafts.GetValueOrDefault(draft.WorkspaceRoot) == draft)
            {
                _drafts.Remove(draft.WorkspaceRoot);
            }
            if (navigation == _navigationVersion && ReferenceEquals(Draft, draft))
            {
                await OpenSessionAsync(handle.Id);
            }
            submitting = true;
            var revision = _runtime.TryObserve(handle.Id)?.Snapshot().Revision ?? 0;
            var ack = await _runtime.SubmitAsync(new SendTextCommand(
                StrongId.NewCommandId(), handle.Id, revision, text, mode, selection), CancellationToken.None);
            if (ack.Accepted)
            {
                draft.Composer.ClearIfSendable(text);
            }
            else
            {
                SetSendFailure(draft, handle.Id, text, ack.RejectionReason ?? "Input was rejected. Your message has been kept.");
            }
            await Sidebar.RefreshAsync();
        }
        catch (Exception exception)
        {
            var message = submitting
                ? $"Delivery could not be confirmed. Your message has been kept; check this conversation before sending again. {exception.Message}"
                : createdId is not null
                    ? $"The conversation was created, but your message was not sent. {exception.Message}"
                    : $"Could not create the conversation. Your draft has been kept. {exception.Message}";
            SetSendFailure(draft, createdId, text, message);
        }
        finally
        {
            draft.Composer.IsSending = false;
        }
    }

    private void SetSendFailure(DraftConversationViewModel draft, SessionId? sessionId, string text, string message)
    {
        draft.Banner = message;
        draft.UnsentText = text;
        Banner = message;
        UnsentText = text;
        if (sessionId is not { } id)
        {
            return;
        }
        _sessionDrafts[id] = new SessionDraft(draft.Composer, draft.Selection, message, text);
        if (Active?.SessionId == id)
        {
            Active.Banner = message;
        }
    }

    public async Task<SessionHandle> NewSessionAsync(string workspaceRoot, string? title = null)
    {
        var handle = await _runtime.CreateSessionAsync(
            new SessionOptions(
                workspaceRoot,
                title,
                PermissionMode.Build,
                WorkbenchModelCatalog.DefaultSelection(_config)),
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

    public void ClearBanner()
    {
        Banner = null;
        UnsentText = null;
    }

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
        if (_focusedWorkspaceRoot is null && Active is null && Draft is { } initial
            && string.IsNullOrEmpty(initial.Composer.Draft) && !initial.Composer.IsSending
            && summaries.OrderByDescending(summary => summary.UpdatedAt).FirstOrDefault() is { } recent)
        {
            ShowNewTask(recent.WorkspaceRoot);
        }
    }

    private void EnsurePanes(string root)
    {
        if (Terminal is null || !string.Equals(_paneRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            Terminal?.Dispose();
            Terminal = new TerminalPaneViewModel(root, scheduler: _scheduler);
            _terminalAutoStarted = false;
            if (IsTerminalVisible)
            {
                _ = StartTerminalOnceAsync();
            }
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
        _sessionDrafts[sessionId] = new SessionDraft(Active.Composer, Active.CurrentSelection, Active.Banner,
            _sessionDrafts.GetValueOrDefault(sessionId)?.UnsentText);
        Active.Dispose();
        Active = null;
        _hub.Release(sessionId);
        Sidebar.SetActive(null);
    }

    private static IReadOnlyList<ModelChoice> BuildModelChoices(WorkbenchConfig config)
    {
        var choices = new List<ModelChoice>();
        foreach (var provider in config.Providers.Where(provider => provider.Enabled))
        {
            foreach (var model in provider.Models.Where(model => model.Enabled))
            {
                var display = model.DisplayName ?? $"{provider.EffectiveId}:{model.ModelId}";
                choices.Add(new ModelChoice(provider.EffectiveId, model.ModelId, display,
                    WorkbenchModelCatalog.Resolve(provider, model)));
            }
        }
        return choices;
    }

    private sealed record SessionDraft(ComposerViewModel Composer, ModelSelection? Selection, string? Banner, string? UnsentText = null);
}
