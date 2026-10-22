using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Sessions;
using Alcoler.Auth;
using Alcoler.Git;
using Alcoler.Stores;
using Alcoler.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Alcoler.Adapters.Tools;

namespace Alcoler.ViewModel;

public sealed record ModelChoice(string ProviderId, string ModelId, string Display, ModelProperties? Properties = null)
{
    public ModelSelection Selection => new(ProviderId, ModelId);

    internal ModelSelection NormalizeSelection(ModelSelection selection)
    {
        var options = selection.Options ?? new ModelCallOptions();
        var levels = Properties?.ReasoningLevels ?? [];
        return selection with
        {
            Options = options with
            {
                ReasoningLevel = options.ReasoningLevel is { } level && levels.Contains(level)
                    ? level : levels.FirstOrDefault(),
                MaxOutputTokens = options.MaxOutputTokens is > 0 ? options.MaxOutputTokens : null,
            },
        };
    }
}

public sealed partial class WorkbenchViewModel : ObservableObject, IDisposable
{
    private readonly IAgentRuntime _runtime;
    private readonly SessionProjectionHub _hub;
    private WorkbenchConfig _config;
    private readonly Action<WorkbenchComposerConfig>? _saveComposer;
    private readonly Action<WorkbenchConfig>? _applySettings;
    private ShellEnvironment? _shellPreference;
    private WorkbenchComposerConfig _composerConfig;
    private readonly Dictionary<ComposerViewModel, Action<PermissionMode>> _modeObservers = [];
    private readonly Dictionary<IComposerHost, Action<ModelSelection>> _selectionObservers = [];
    private readonly Dictionary<string, long> _recentSubmissionSequences = new(StringComparer.OrdinalIgnoreCase);
    private long _submissionSequence;
    private readonly IAsyncDisposable? _ownedResource;
    private readonly IUiScheduler _scheduler;
    private readonly ISessionActivityRuntime? _activities;
    private readonly IGitService _gitService = new LibGit2GitService();
    private readonly Dictionary<SessionId, string> _sessionRoots = [];
    private readonly List<Dictionary<SessionId, string>> _refreshTitleOverrides = [];
    private readonly Dictionary<string, DraftConversationViewModel> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SessionId, SessionDraft> _sessionDrafts = [];
    private readonly HashSet<SessionId> _autoOpenedBrowserSessions = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DraftConversationViewModel, DraftDocumentOwner> _draftDocumentOwners = new();
    private string? _focusedWorkspaceRoot;
    private long _navigationVersion;
    private string? _paneRoot;
    private ShellEnvironment? _paneShell;
    private bool _terminalAutoStarted;
    private bool _disposed;

    public WorkbenchViewModel(
        IAgentRuntime runtime,
        IUiScheduler scheduler,
        WorkbenchConfig config,
        IAsyncDisposable? ownedResource = null,
        BrowserPaneViewModel? browserPane = null,
        Action<WorkbenchComposerConfig>? saveComposer = null,
        Action<WorkbenchConfig>? applySettings = null,
        ChatGptAccountService? chatGptAccount = null)
    {
        _runtime = runtime;
        _config = config;
        _saveComposer = saveComposer;
        _applySettings = applySettings;
        _shellPreference = config.IntegratedTerminalShell;
        var composerConfig = config.Composer ?? new WorkbenchComposerConfig();
        _composerConfig = composerConfig with
        {
            DefaultMode = Enum.IsDefined(composerConfig.DefaultMode) ? composerConfig.DefaultMode : PermissionMode.Build,
            PendingSessionModes = composerConfig.PendingSessionModes?
                .Where(pair => Enum.IsDefined(pair.Value)).ToDictionary(),
        };
        _ownedResource = ownedResource;
        _scheduler = scheduler;
        ChatGptAccount = chatGptAccount;
        BrowserPane = browserPane;
        _hub = new SessionProjectionHub(runtime, scheduler, coalesceWindow: TimeSpan.FromMilliseconds(30));
        Sidebar = new SessionSidebarViewModel(RefreshFromRuntimeAsync);
        runtime.SessionTitleChanged += OnSessionTitleChanged;
        if (runtime is ISessionActivityRuntime activities)
        {
            _activities = activities;
            activities.SessionActivityChanged += OnSessionActivityChanged;
        }
        ModelChoices = BuildModelChoices(config);
        Draft = GetOrCreateDraft(Environment.CurrentDirectory);
        Documents.PropertyChanged += OnDocumentsPropertyChanged;
        if (BrowserPane is not null)
        {
            BrowserPane.Bind(null);
            BrowserPane.PropertyChanged += OnBrowserPanePropertyChanged;
        }
        EnsureDocumentContext();
    }

    public SessionSidebarViewModel Sidebar { get; }

    public ChatGptAccountService? ChatGptAccount { get; }

    public IReadOnlyList<ModelChoice> ModelChoices { get; private set; }

    public IReadOnlyList<string> DisabledSkillPaths => _config.DisabledSkillPaths ?? [];

    public static string OutsideProjectRoot => Path.Combine(WorkbenchDataPaths.Root, "workspace", "default");

    public WorkbenchProviderConfig? FindProvider(string id) =>
        _config.Providers.FirstOrDefault(provider => provider.EffectiveId == id);

    public void ApplySettings(WorkbenchConfig config)
    {
        _config = config;
        _shellPreference = config.IntegratedTerminalShell;
        ModelChoices = BuildModelChoices(config);
        OnPropertyChanged(nameof(ModelChoices));
        var defaultSelection = WorkbenchModelCatalog.DefaultSelection(config);
        Active?.UpdateModelChoices(ModelChoices, defaultSelection);
        foreach (var draft in _drafts.Values)
        {
            draft.UpdateModelChoices(ModelChoices, defaultSelection);
        }
        _applySettings?.Invoke(config);
    }

    private Func<string> TerminalShellResolver()
    {
        var sessionId = Active?.SessionId;
        return () => ShellResolver.ResolveEnvironment(_shellPreference,
            sessionId is { } id ? _runtime.GetSessionShell(id) : null).FileName;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerHost))]
    [NotifyPropertyChangedFor(nameof(IsDraft))]
    private ConversationViewModel? _active;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposerHost))]
    private DraftConversationViewModel? _draft;

    public IComposerHost? ComposerHost => Active ?? (IComposerHost?)Draft;

    public bool IsDraft => Active is null;

    partial void OnActiveChanged(ConversationViewModel? value) => value?.Status.BindGit(GitPane);

    partial void OnGitPaneChanged(GitPaneViewModel? value) => Active?.Status.BindGit(value);

    [ObservableProperty]
    private string? _banner;

    [ObservableProperty]
    private string? _unsentText;

    [ObservableProperty]
    private TerminalPaneViewModel? _terminal;

    [ObservableProperty]
    private GitPaneViewModel? _gitPane;

    public BrowserPaneViewModel? BrowserPane { get; }

    public DocumentWorkspaceViewModel Documents { get; } = new();

    public string CurrentWorkspaceRoot => DocumentViewModel.NormalizeWorkspaceRoot(
        Active is { } active && _sessionRoots.TryGetValue(active.SessionId, out var root)
            ? root
            : Draft?.WorkspaceRoot ?? _focusedWorkspaceRoot ?? Environment.CurrentDirectory);

    public string CurrentDocumentOwnerId => Active?.SessionId.Value
        ?? GetDraftDocumentOwnerId(Draft ?? GetOrCreateDraft(CurrentWorkspaceRoot));

    [ObservableProperty]
    private bool _isSidePaneVisible;

    [ObservableProperty]
    private bool _isTerminalVisible;

    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    public DocumentViewModel OpenDocument(DocumentViewModel document)
    {
        EnsureDocumentContext();
        return Documents.Open(document);
    }

    [RelayCommand]
    private void ShowGitPane()
    {
        var root = CurrentWorkspaceRoot;
        EnsurePanes(root);
        var document = Documents.Items.FirstOrDefault(item => item.Id == "git" && item.OwnerId is null
            && string.Equals(item.WorkspaceRoot, root, StringComparison.OrdinalIgnoreCase));
        if (document is not null)
        {
            GitPane = (GitPaneViewModel)document.Content;
            OpenDocument(document);
            return;
        }
        OpenDocument(new DocumentViewModel("git", "git", "Review",
            "M6 3a3 3 0 1 0 0 6 3 3 0 0 0 0-6 M6 9v6 M6 15a3 3 0 1 0 0 6 3 3 0 0 0 0-6 M18 3a3 3 0 1 0 0 6 3 3 0 0 0 0-6 M18 9a9 9 0 0 1-9 9",
            GitPane!, root, ownsContent: false));
        _ = GitPane!.LoadAsync();
    }

    [RelayCommand]
    private void ShowBrowserPane()
    {
        EnsureDocumentContext();
        var existing = Documents.VisibleItems.FirstOrDefault(item => item.Id == "browser"
            && item.OwnerId == CurrentDocumentOwnerId);
        if (existing is not null)
        {
            Documents.Activate(existing);
            return;
        }
        OpenDocument(new DocumentViewModel("browser", "browser", "Browser",
            "M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M3 12h18 M12 3a17 17 0 0 1 0 18 17 17 0 0 1 0-18",
            (object?)BrowserPane?.CreateBoundView(Active?.SessionId)
                ?? "Browser is not available in this workbench.", CurrentWorkspaceRoot, CurrentDocumentOwnerId));
    }

    [RelayCommand]
    private async Task OpenTerminalDocumentAsync()
    {
        var terminal = new TerminalPaneViewModel(CurrentWorkspaceRoot, scheduler: _scheduler,
            resolveShell: TerminalShellResolver());
        OpenDocument(new DocumentViewModel($"terminal:{Guid.NewGuid():N}", "terminal", "Terminal",
            "M4 5h16v14H4z M7 9l3 3-3 3 M12 15h5", terminal, CurrentWorkspaceRoot, CurrentDocumentOwnerId));
        try
        {
            await terminal.StartAsync();
        }
        catch (Exception exception)
        {
            Banner = $"Could not start terminal. {exception.Message}";
        }
    }

    partial void OnIsSidePaneVisibleChanged(bool value) => Documents.IsVisible = value;

    private void OnDocumentsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentWorkspaceViewModel.IsVisible))
        {
            IsSidePaneVisible = Documents.IsVisible;
        }
    }

    private void OnBrowserPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserPaneViewModel.ActiveTabId))
        {
            TryShowActiveBrowserDocument();
        }
    }

    private void TryShowActiveBrowserDocument()
    {
        if (!_disposed && Active is { } active && BrowserPane is { ActiveTabId: not null } browser
            && browser.ActiveSessionId == active.SessionId && _autoOpenedBrowserSessions.Add(active.SessionId))
        {
            ShowBrowserPane();
        }
    }

    private void EnsureDocumentContext() => Documents.SetContext(CurrentWorkspaceRoot, CurrentDocumentOwnerId);

    private string GetDraftDocumentOwnerId(DraftConversationViewModel draft) =>
        _draftDocumentOwners.GetValue(draft, _ => new DraftDocumentOwner($"draft:{Guid.NewGuid():N}")).Id;

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
        _disposed = true;
        if (BrowserPane is not null)
        {
            BrowserPane.PropertyChanged -= OnBrowserPanePropertyChanged;
        }
        _autoOpenedBrowserSessions.Clear();
        _runtime.SessionTitleChanged -= OnSessionTitleChanged;
        _refreshTitleOverrides.Clear();
        if (_activities is not null)
        {
            _activities.SessionActivityChanged -= OnSessionActivityChanged;
        }
        CloseActive();
        foreach (var (composer, observer) in _modeObservers)
            composer.ModeSelected -= observer;
        _modeObservers.Clear();
        // Pending attachments are owned by the draft registries, not by observer
        // registration; CloseActive above has re-saved the active composer.
        foreach (var composer in _sessionDrafts.Values.Select(draft => draft.Composer)
                     .Concat(_drafts.Values.Select(draft => draft.Composer)))
            composer.ClearAttachments();
        foreach (var (host, observer) in _selectionObservers)
            host.ModelSelectionChanged -= observer;
        _selectionObservers.Clear();
        _hub.Dispose();
        Terminal?.Dispose();
        Documents.PropertyChanged -= OnDocumentsPropertyChanged;
        Documents.Dispose();
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
        using var residentProbe = _runtime.TryObserve(sessionId);
        if (residentProbe is null)
        {
            var handle = await _runtime.ResumeSessionAsync(sessionId, CancellationToken.None);
            _sessionRoots[sessionId] = handle.WorkspaceRoot;
        }
        if (navigation != _navigationVersion)
        {
            return;
        }
        if (!_sessionRoots.ContainsKey(sessionId))
        {
            var summaries = await _runtime.ListSessionsAsync(null, 200, CancellationToken.None);
            foreach (var summary in summaries)
                _sessionRoots[summary.Id] = summary.WorkspaceRoot;
            if (navigation != _navigationVersion)
                return;
        }
        var workspaceRoot = _sessionRoots.GetValueOrDefault(sessionId) ?? CurrentWorkspaceRoot;
        _sessionRoots.TryAdd(sessionId, workspaceRoot);
        CloseActive();
        Draft = null;
        var store = _hub.Acquire(sessionId);
        _sessionDrafts.TryGetValue(sessionId, out var saved);
        var savedText = saved?.Composer.Draft;
        var savedMode = saved?.Composer.Mode;
        var conversation = new ConversationViewModel(_runtime, store, saved?.Composer, ModelChoices,
            WorkbenchModelCatalog.DefaultSelection(_config)) { WorkspaceRoot = workspaceRoot };
        conversation.Composer.ConfigureWorkspace(workspaceRoot);
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
        // The saved draft is consumed on open; CloseActive re-saves whatever is still unsent.
        _sessionDrafts.Remove(sessionId);
        if (GetSavedSelection(workspaceRoot, sessionId.Value) is { } persistedSelection)
            conversation.RestoreModelSelection(persistedSelection);
        if (_composerConfig.PendingSessionModes?.TryGetValue(sessionId.Value, out var pendingMode) == true)
            conversation.RestorePendingMode(pendingMode);
        ObserveMode(conversation.Composer, () => sessionId);
        ObserveSelection(conversation, () => sessionId.Value);
        conversation.ModeSubmitted += mode => ClearSubmittedMode(sessionId, mode);
        conversation.ForkRequested = boundaryId => ForkSessionAsync(sessionId, boundaryId);
        conversation.NextSubmissionSequence = () => ++_submissionSequence;
        conversation.ModelSelectionSubmitted += (selection, sequence) => RecordAcceptedSelection(workspaceRoot, sessionId, selection, sequence);
        Active = conversation;
        Sidebar.SetActive(sessionId);
        Sidebar.SyncRunningState(sessionId, _activities?.GetSessionActivity(sessionId));
        BrowserPane?.Bind(sessionId);
        EnsureDocumentContext();
        TryShowActiveBrowserDocument();
        if (_sessionRoots.TryGetValue(sessionId, out var root))
        {
            _focusedWorkspaceRoot = root;
            EnsurePanes(root);
            EnsureDocumentContext();
        }
        await Sidebar.RefreshAsync();
        if (Active?.SessionId == sessionId && _sessionRoots.TryGetValue(sessionId, out root))
        {
            _focusedWorkspaceRoot = root;
            EnsurePanes(root);
            EnsureDocumentContext();
        }
    }

    private async Task ForkSessionAsync(SessionId sourceSessionId, MessageId boundaryMessageId)
    {
        try
        {
            var handle = await _runtime.ForkSessionAsync(sourceSessionId, boundaryMessageId, CancellationToken.None);
            _sessionRoots[handle.Id] = handle.WorkspaceRoot;
            await OpenSessionAsync(handle.Id);
        }
        catch (Exception exception)
        {
            if (Active is { } active && active.SessionId == sourceSessionId)
            {
                active.Banner = $"The conversation could not be forked. {exception.Message}";
            }
        }
    }

    public void ShowNewTask(string? workspaceRoot = null)
    {        ++_navigationVersion;
        var root = workspaceRoot ?? _focusedWorkspaceRoot
            ?? (Active is { } active ? _sessionRoots.GetValueOrDefault(active.SessionId) : null)
            ?? Sidebar.Groups.OrderByDescending(group => group.Sessions.Select(session => session.UpdatedAt).DefaultIfEmpty().Max())
                .FirstOrDefault()?.WorkspaceRoot
            ?? Draft?.WorkspaceRoot
            ?? Environment.CurrentDirectory;
        CloseActive();
        _focusedWorkspaceRoot = Path.GetFullPath(root);
        Draft = GetOrCreateDraft(_focusedWorkspaceRoot);
        BrowserPane?.Bind(null);
        EnsurePanes(_focusedWorkspaceRoot);
        EnsureDocumentContext();
    }

    public void FocusWorkspace(string workspaceRoot) => _focusedWorkspaceRoot = Path.GetFullPath(workspaceRoot);

    public void WorkOutsideProject()
    {
        Directory.CreateDirectory(OutsideProjectRoot);
        ShowNewTask(OutsideProjectRoot);
    }

    private DraftConversationViewModel GetOrCreateDraft(string root)
    {
        root = DocumentViewModel.NormalizeWorkspaceRoot(root);
        if (_drafts.TryGetValue(root, out var draft))
        {
            if (GetSavedSelection(root, WorkbenchComposerConfig.DraftSessionKey) is null
                && string.IsNullOrEmpty(draft.Composer.Draft) && !draft.Composer.IsSending
                && !draft.Composer.HasAttachments && GetNewDraftSelection(root) is { } recent)
            {
                if (FindModel(recent) is { } model)
                    draft.ConfigureModel(model);
                draft.RestoreModelSelection(recent);
            }
            return draft;
        }
        var selection = GetSavedSelection(root, WorkbenchComposerConfig.DraftSessionKey) ?? GetNewDraftSelection(root);
        draft = new DraftConversationViewModel(root, FindModel(selection), SendDraftAsync);
        draft.Composer.RestoreMode(_composerConfig.DefaultMode);
        if (selection is not null)
            draft.RestoreModelSelection(selection);
        var observedDraft = draft;
        ObserveMode(draft.Composer, () => observedDraft.CreatedSessionId);
        ObserveSelection(draft, () => observedDraft.CreatedSessionId?.Value ?? WorkbenchComposerConfig.DraftSessionKey);
        _drafts[root] = draft;
        return draft;
    }

    private ModelSelection? ValidateSelection(ModelSelection? selection) => FindModel(selection) is { } choice
        ? choice.NormalizeSelection(selection!) : WorkbenchModelCatalog.DefaultSelection(_config);

    private ModelSelection? GetSavedSelection(string root, string sessionKey)
    {
        var workspace = _composerConfig.ModelSelections?.FirstOrDefault(pair =>
            string.Equals(pair.Key, DocumentViewModel.NormalizeWorkspaceRoot(root), StringComparison.OrdinalIgnoreCase)).Value;
        return workspace?.TryGetValue(sessionKey, out var selection) == true ? ValidateSelection(selection) : null;
    }

    private ModelSelection? GetNewDraftSelection(string root)
    {
        var recent = _composerConfig.RecentModelSelections?.FirstOrDefault(pair =>
            string.Equals(pair.Key, DocumentViewModel.NormalizeWorkspaceRoot(root), StringComparison.OrdinalIgnoreCase)).Value;
        return ValidateSelection(recent);
    }

    private void ObserveSelection(IComposerHost host, Func<string> sessionKey)
    {
        Action<ModelSelection> observer = selection => SaveModelSelection(host.WorkspaceRoot, sessionKey(), selection);
        _selectionObservers[host] = observer;
        host.ModelSelectionChanged += observer;
    }

    private void UnobserveSelection(IComposerHost host)
    {
        if (_selectionObservers.Remove(host, out var observer))
            host.ModelSelectionChanged -= observer;
    }

    private void UnobserveMode(ComposerViewModel composer)
    {
        if (_modeObservers.Remove(composer, out var observer))
            composer.ModeSelected -= observer;
    }

    private Dictionary<string, IReadOnlyDictionary<string, ModelSelection>> CopyModelSelections() =>
        new(_composerConfig.ModelSelections ?? new Dictionary<string, IReadOnlyDictionary<string, ModelSelection>>(),
            StringComparer.OrdinalIgnoreCase);

    private void SaveModelSelection(string root, string sessionKey, ModelSelection selection)
    {
        if (ValidateSelection(selection) is not { } validated)
            return;
        root = DocumentViewModel.NormalizeWorkspaceRoot(root);
        var workspaces = CopyModelSelections();
        var sessions = new Dictionary<string, ModelSelection>(workspaces.GetValueOrDefault(root) ?? new Dictionary<string, ModelSelection>())
        {
            [sessionKey] = validated,
        };
        workspaces[root] = sessions;
        SaveComposerConfig(_composerConfig with { ModelSelections = workspaces });
    }

    private void TransferDraftSelection(DraftConversationViewModel draft, SessionId sessionId)
    {
        var root = draft.WorkspaceRoot;
        var workspaces = CopyModelSelections();
        var sessions = new Dictionary<string, ModelSelection>(workspaces.GetValueOrDefault(root) ?? new Dictionary<string, ModelSelection>());
        sessions.Remove(WorkbenchComposerConfig.DraftSessionKey);
        if (ValidateSelection(draft.CurrentSelection) is { } selection)
            sessions[sessionId.Value] = selection;
        workspaces[root] = sessions;
        SaveComposerConfig(_composerConfig with { ModelSelections = workspaces });
    }

    private void RecordAcceptedSelection(string root, SessionId sessionId, ModelSelection selection, long sequence)
    {
        if (ValidateSelection(selection) is not { } validated)
            return;
        root = DocumentViewModel.NormalizeWorkspaceRoot(root);
        var recent = new Dictionary<string, ModelSelection>(
            _composerConfig.RecentModelSelections ?? new Dictionary<string, ModelSelection>(), StringComparer.OrdinalIgnoreCase);
        if (!_recentSubmissionSequences.TryGetValue(root, out var acceptedSequence) || sequence >= acceptedSequence)
        {
            _recentSubmissionSequences[root] = sequence;
            recent[root] = validated;
        }
        var workspaces = CopyModelSelections();
        var sessions = new Dictionary<string, ModelSelection>(workspaces.GetValueOrDefault(root) ?? new Dictionary<string, ModelSelection>());
        sessions.TryAdd(sessionId.Value, validated);
        workspaces[root] = sessions;
        SaveComposerConfig(_composerConfig with { ModelSelections = workspaces, RecentModelSelections = recent });
    }

    private void ObserveMode(ComposerViewModel composer, Func<SessionId?> sessionId)
    {
        if (_modeObservers.ContainsKey(composer))
            return;
        Action<PermissionMode> observer = mode =>
        {
            if (!Enum.IsDefined(mode))
                return;
            var pending = new Dictionary<string, PermissionMode>(_composerConfig.PendingSessionModes ?? new Dictionary<string, PermissionMode>());
            if (sessionId() is { } id)
                pending[id.Value] = mode;
            SaveComposerConfig(_composerConfig with { DefaultMode = mode, PendingSessionModes = pending });
        };
        _modeObservers[composer] = observer;
        composer.ModeSelected += observer;
    }

    private void SavePendingMode(SessionId sessionId, PermissionMode mode)
    {
        var pending = new Dictionary<string, PermissionMode>(_composerConfig.PendingSessionModes ?? new Dictionary<string, PermissionMode>())
        {
            [sessionId.Value] = mode,
        };
        SaveComposerConfig(_composerConfig with { PendingSessionModes = pending });
    }

    private void ClearSubmittedMode(SessionId sessionId, PermissionMode mode)
    {
        if (_composerConfig.PendingSessionModes?.TryGetValue(sessionId.Value, out var pendingMode) != true || pendingMode != mode)
            return;
        var pending = new Dictionary<string, PermissionMode>(_composerConfig.PendingSessionModes);
        pending.Remove(sessionId.Value);
        SaveComposerConfig(_composerConfig with { PendingSessionModes = pending });
    }

    private void SaveComposerConfig(WorkbenchComposerConfig config)
    {
        _composerConfig = config;
        try
        {
            _saveComposer?.Invoke(config);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Banner = $"Could not save composer settings. {exception.Message}";
        }
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
        var submissionSequence = ++_submissionSequence;
        var navigation = _navigationVersion;
        draft.Composer.IsSending = true;
        draft.Banner = null;
        draft.UnsentText = null;
        SessionId? createdId = null;
        var submitting = false;
        try
        {
            var images = draft.Composer.SnapshotImages();
            var handle = await _runtime.CreateSessionAsync(
                new SessionOptions(draft.WorkspaceRoot, PermissionMode: mode, ModelSelection: selection), CancellationToken.None);
            createdId = handle.Id;
            draft.CreatedSessionId = handle.Id;
            TransferDraftSelection(draft, handle.Id);
            SavePendingMode(handle.Id, draft.Composer.Mode);
            _sessionRoots[handle.Id] = handle.WorkspaceRoot;
            var draftOwnerId = GetDraftDocumentOwnerId(draft);
            var browserDocuments = Documents.Items.Where(document => document.OwnerId == draftOwnerId
                && string.Equals(document.WorkspaceRoot,
                    DocumentViewModel.NormalizeWorkspaceRoot(draft.WorkspaceRoot), StringComparison.OrdinalIgnoreCase)
                && document.Content is BrowserPaneViewModel).ToArray();
            Documents.TransferOwner(draft.WorkspaceRoot, draftOwnerId, handle.Id.Value);
            foreach (var document in browserDocuments.Where(document => Documents.Items.Contains(document)))
            {
                ((BrowserPaneViewModel)document.Content).Bind(handle.Id);
            }
            _sessionDrafts[handle.Id] = new SessionDraft(draft.Composer, draft.Selection, null);
            if (_drafts.GetValueOrDefault(draft.WorkspaceRoot) == draft)
            {
                _drafts.Remove(draft.WorkspaceRoot);
            }
            UnobserveSelection(draft);
            if (navigation == _navigationVersion && ReferenceEquals(Draft, draft))
            {
                await OpenSessionAsync(handle.Id);
            }
            submitting = true;
            using var createdProjection = _runtime.TryObserve(handle.Id);
            var revision = createdProjection?.Snapshot().Revision ?? 0;
            var ack = await _runtime.SubmitAsync(new SendTextCommand(
                StrongId.NewCommandId(), handle.Id, revision, text, mode, selection, Images: images), CancellationToken.None);
            if (ack.Accepted)
            {
                ClearSubmittedMode(handle.Id, mode);
                if (Active?.SessionId == handle.Id)
                    Active.AcknowledgeMode(mode);
                if (selection is not null)
                    RecordAcceptedSelection(draft.WorkspaceRoot, handle.Id, selection, submissionSequence);
                draft.Composer.RecordSent(text);
                draft.Composer.ClearIfSendable(text, images);
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
                _composerConfig.DefaultMode,
                GetNewDraftSelection(workspaceRoot)),
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
        if (_disposed)
        {
            return;
        }
        var titles = new Dictionary<SessionId, string>();
        _refreshTitleOverrides.Add(titles);
        try
        {
            var summaries = await _runtime.ListSessionsAsync(null, 200, CancellationToken.None);
            if (_disposed)
            {
                return;
            }
            Sidebar.ApplySummaries(
            [
                .. summaries.Select(s => new SessionSummaryViewModel(
                    s.Id, titles.GetValueOrDefault(s.Id, s.Title), s.WorkspaceRoot, s.UpdatedAt))
            ]);
            foreach (var summary in summaries)
            {
                _sessionRoots[summary.Id] = summary.WorkspaceRoot;
                Sidebar.SyncRunningState(summary.Id, _activities?.GetSessionActivity(summary.Id));
            }
            if (_focusedWorkspaceRoot is null && Active is null && Draft is { } initial
                && string.IsNullOrEmpty(initial.Composer.Draft) && !initial.Composer.IsSending
                && summaries.OrderByDescending(summary => summary.UpdatedAt).FirstOrDefault() is { } recent)
            {
                ShowNewTask(recent.WorkspaceRoot);
            }
        }
        finally
        {
            _refreshTitleOverrides.Remove(titles);
        }
    }

    private void OnSessionTitleChanged(SessionId sessionId, string title)
    {
        if (_disposed)
        {
            return;
        }
        _scheduler.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            foreach (var titles in _refreshTitleOverrides)
            {
                titles[sessionId] = title;
            }
            Sidebar.ApplyTitle(sessionId, title);
            if (Active is { } active && active.SessionId == sessionId)
            {
                active.Title = title;
            }
        });
    }

    private void OnSessionActivityChanged(SessionId sessionId)
    {
        if (_disposed)
        {
            return;
        }
        _scheduler.Post(() => Sidebar.ApplyActivity(sessionId, _activities?.GetSessionActivity(sessionId)));
    }

    private void EnsurePanes(string root)
    {
        var shell = ShellResolver.ResolveEnvironment(_shellPreference,
            Active is { } active ? _runtime.GetSessionShell(active.SessionId) : null);
        if (Terminal is null || !string.Equals(_paneRoot, root, StringComparison.OrdinalIgnoreCase) || _paneShell != shell)
        {
            Terminal?.Dispose();
            Terminal = new TerminalPaneViewModel(root, scheduler: _scheduler,
                resolveShell: TerminalShellResolver());
            _paneShell = shell;
            _terminalAutoStarted = false;
            if (IsTerminalVisible)
            {
                _ = StartTerminalOnceAsync();
            }
        }
        if (GitPane is null || !string.Equals(_paneRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            var normalizedRoot = DocumentViewModel.NormalizeWorkspaceRoot(root);
            GitPane = Documents.Items.FirstOrDefault(document => document.Id == "git" && document.OwnerId is null
                && string.Equals(document.WorkspaceRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase))?.Content
                as GitPaneViewModel ?? new GitPaneViewModel(root, _gitService, _scheduler);
        }
        _paneRoot = root;
        Active?.Status.BindGit(GitPane);
        if (GitPane is { } git)
            _ = git.LoadAsync();
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
        UnobserveSelection(Active);
        UnobserveMode(Active.Composer);
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

    private sealed record DraftDocumentOwner(string Id);

    private sealed record SessionDraft(ComposerViewModel Composer, ModelSelection? Selection, string? Banner, string? UnsentText = null);
}
