using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Sessions;
using Alcoler.Auth;
using Alcoler.Audio;
using Alcoler.Git;
using Alcoler.Localization;
using Alcoler.Stores;
using Alcoler.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Alcoler.Adapters.Tools;
using Alcoler.Kernel.Workflows;

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

public sealed partial class WorkbenchViewModel : ObservableObject, IDisposable, IArchivedSessionMaintenance
{
    private readonly IAgentRuntime _runtime;
    private readonly SessionProjectionHub _hub;
    private readonly IExternalEditorLauncher _externalEditorLauncher;
    private WorkbenchConfig _config;
    private readonly Action<WorkbenchComposerConfig>? _saveComposer;
    private readonly Action<WorkbenchTaskOverlayConfig>? _saveTaskOverlay;
    private readonly Action<WorkbenchConfig>? _applySettings;
    private ShellEnvironment? _shellPreference;
    private WorkbenchComposerConfig _composerConfig;
    private readonly Dictionary<ComposerViewModel, (Action<PermissionMode> Mode, Action<bool> Plan)> _modeObservers = [];
    private readonly Dictionary<IComposerHost, Action<ModelSelection>> _selectionObservers = [];
    private readonly Dictionary<string, long> _recentSubmissionSequences = new(StringComparer.OrdinalIgnoreCase);
    private long _submissionSequence;
    private readonly IAsyncDisposable? _ownedResource;
    private readonly IUiScheduler _scheduler;
    private readonly ISessionActivityRuntime? _activities;
    private readonly IGitService _gitService = new LibGit2GitService();
    private readonly Dictionary<SessionId, string> _sessionRoots = [];
    private readonly List<Dictionary<SessionId, string>> _refreshTitleOverrides = [];
    private bool _sessionListRefreshActive;
    private bool _sessionListRefreshRetriggered;
    private readonly Dictionary<string, DraftConversationViewModel> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SessionId, SessionDraft> _sessionDrafts = [];
    private readonly HashSet<SessionId> _autoOpenedBrowserSessions = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DraftConversationViewModel, DraftDocumentOwner> _draftDocumentOwners = new();
    private string? _focusedWorkspaceRoot;
    private long _navigationVersion;
    private CancellationTokenSource? _openSessionCts;
    private readonly SessionReadingStateCache _readingStates = new();
    private string? _paneRoot;
    private ShellEnvironment? _paneShell;
    private bool _terminalAutoStarted;
    private bool _disposed;
    private static readonly TimeSpan AutoArchiveInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdleSessionUnloadThreshold = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaintenanceTickInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongBusySessionThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CompactionCooldown = TimeSpan.FromMinutes(30);
    private DateTimeOffset? _lastAutoArchiveAt;
    private bool _autoArchiveRunning;
    private CancellationTokenSource? _maintenanceCts = new();
    private readonly BusyPeriodTracker _busyPeriods = new(() => DateTimeOffset.UtcNow,
        LongBusySessionThreshold, CompactionCooldown);
    private readonly TaskAlertTracker _taskAlerts = new();
    private readonly ITaskAlertSounds _taskAlertSounds;
    private bool _conversationViewActive;

    public WorkbenchViewModel(
        IAgentRuntime runtime,
        IUiScheduler scheduler,
        WorkbenchConfig config,
        IAsyncDisposable? ownedResource = null,
        BrowserPaneViewModel? browserPane = null,
        Action<WorkbenchComposerConfig>? saveComposer = null,
        Action<WorkbenchTaskOverlayConfig>? saveTaskOverlay = null,
        Action<WorkbenchConfig>? applySettings = null,
        ChatGptAccountService? chatGptAccount = null,
        IExternalEditorLauncher? externalEditorLauncher = null,
        RemoteAccessViewModel? remoteAccess = null,
        ITaskAlertSounds? taskAlertSounds = null)
    {
        _runtime = runtime;
        _config = config;
        _externalEditorLauncher = externalEditorLauncher ?? new ExternalEditorLauncher();
        _saveComposer = saveComposer;
        _saveTaskOverlay = saveTaskOverlay;
        _applySettings = applySettings;
        _shellPreference = config.IntegratedTerminalShell;
        var composerConfig = config.Composer ?? new WorkbenchComposerConfig();
        _composerConfig = composerConfig with
        {
            DefaultMode = Enum.IsDefined(composerConfig.DefaultMode) ? composerConfig.DefaultMode : PermissionMode.Yolo,
            PendingSessionModes = composerConfig.PendingSessionModes?.Where(pair => Enum.IsDefined(pair.Value)).ToDictionary(),
        };
        _ownedResource = ownedResource;
        _scheduler = scheduler;
        _taskAlertSounds = taskAlertSounds ?? NoopTaskAlertSounds.Instance;
        ChatGptAccount = chatGptAccount;
        BrowserPane = browserPane;
        Remote = remoteAccess;
        _hub = new SessionProjectionHub(runtime, scheduler, coalesceWindow: TimeSpan.FromMilliseconds(30));
        Sidebar = new SessionSidebarViewModel(RefreshFromRuntimeAsync, SessionActivities);
        TaskOverlay = new TaskOverlayViewModel(SessionActivities)
        {
            IsVisible = config.TaskOverlay?.Visible ?? false,
        };
        TaskOverlay.PropertyChanged += OnTaskOverlayPropertyChanged;
        runtime.SessionTitleChanged += OnSessionTitleChanged;
        runtime.SessionListChanged += OnSessionListChanged;
        runtime.SessionDeleted += OnSessionDeleted;
        if (runtime is ISessionActivityRuntime activities)
        {
            _activities = activities;
            activities.SessionActivityChanged += OnSessionActivityChanged;
            RefreshSessionActivities();
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
        var maintenanceCts = _maintenanceCts;
        _ = Task.Run(() => RunMaintenanceTicksAsync(maintenanceCts.Token));
    }

    public SessionSidebarViewModel Sidebar { get; }

    public TaskOverlayViewModel TaskOverlay { get; }

    public SessionActivityTracker SessionActivities { get; } = new();

    internal void SetConversationViewActive(bool active)
    {
        if (_disposed)
            return;
        _conversationViewActive = active;
        SessionActivities.SetViewingSession(active ? Active?.SessionId : null);
    }

    private void RefreshSessionActivities()
    {
        if (_activities is not null)
            SessionActivities.Refresh(_activities.GetSessionActivities());
    }

    public ChatGptAccountService? ChatGptAccount { get; }

    public IReadOnlyList<ModelChoice> ModelChoices { get; private set; }

    public IReadOnlyList<string> DisabledSkillPaths => _config.DisabledSkillPaths ?? [];

    public static string OutsideProjectRoot => Path.Combine(WorkbenchDataPaths.Root, "workspace", "default");

    public WorkbenchProviderConfig? FindProvider(string id) =>
        _config.Providers.FirstOrDefault(provider => provider.EffectiveId == id);

    public void ApplySettings(WorkbenchConfig config)
    {
        _config = config;
        _lastAutoArchiveAt = null;
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

    partial void OnActiveChanged(ConversationViewModel? value)
    {
        value?.Status.BindGit(GitPane);
        SessionActivities.SetViewingSession(_conversationViewActive ? value?.SessionId : null);
    }

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

    public RemoteAccessViewModel? Remote { get; }

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
        OpenDocument(new DocumentViewModel("git", "git", Loc.Get("workbench_review"),
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
        OpenDocument(new DocumentViewModel("browser", "browser", Loc.Get("workbench_browser"),
            "M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0 M3 12h18 M12 3a17 17 0 0 1 0 18 17 17 0 0 1 0-18",
            (object?)BrowserPane?.CreateBoundView(Active?.SessionId)
                ?? Loc.Get("workbench_browser_unavailable"), CurrentWorkspaceRoot, CurrentDocumentOwnerId));
    }

    [RelayCommand]
    private async Task OpenTerminalDocumentAsync()
    {
        var terminal = new TerminalPaneViewModel(CurrentWorkspaceRoot, scheduler: _scheduler,
            resolveShell: TerminalShellResolver());
        OpenDocument(new DocumentViewModel($"terminal:{Guid.NewGuid():N}", "terminal", Loc.Get("workbench_terminal"),
            "M4 5h16v14H4z M7 9l3 3-3 3 M12 15h5", terminal, CurrentWorkspaceRoot, CurrentDocumentOwnerId));
        try
        {
            await terminal.StartAsync();
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("workbench_terminal_error", exception.Message);
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
        if (_disposed)
            return;
        _disposed = true;
        BeginNavigation();
        // Dispose runs a second time through explicit close plus using; the swap keeps the
        // maintenance teardown idempotent.
        var maintenanceCts = Interlocked.Exchange(ref _maintenanceCts, null);
        if (maintenanceCts is not null)
        {
            maintenanceCts.Cancel();
            maintenanceCts.Dispose();
        }
        if (BrowserPane is not null)
        {
            BrowserPane.PropertyChanged -= OnBrowserPanePropertyChanged;
        }
        _autoOpenedBrowserSessions.Clear();
        _runtime.SessionTitleChanged -= OnSessionTitleChanged;
        _runtime.SessionListChanged -= OnSessionListChanged;
        _runtime.SessionDeleted -= OnSessionDeleted;
        _refreshTitleOverrides.Clear();
        if (_activities is not null)
        {
            _activities.SessionActivityChanged -= OnSessionActivityChanged;
        }
        CloseActive();
        foreach (var (composer, observers) in _modeObservers)
        {
            composer.ModeSelected -= observers.Mode;
            composer.PlanModeSelected -= observers.Plan;
        }
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
        _readingStates.Clear();
        Terminal?.Dispose();
        TaskOverlay.PropertyChanged -= OnTaskOverlayPropertyChanged;
        Documents.PropertyChanged -= OnDocumentsPropertyChanged;
        Documents.Dispose();
        _gitService.Dispose();
        BrowserPane?.Dispose();
        if (Remote is { } remote)
        {
            // The gateway serves the runtime; it must stop before owned runtime resources go.
            Task.Run(() => remote.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(30));
        }
        if (_ownedResource is not null)
        {
            // Shutdown-time async disposal can marshal through the UI thread it is
            // blocking, so it runs on the pool with a bounded wait instead.
            Task.Run(() => _ownedResource.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(30));
        }
    }

    public async Task OpenSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        var navigation = BeginNavigation();
        if (Active?.SessionId == sessionId)
        {
            RefreshSessionActivities();
            SessionActivities.SetViewingSession(_conversationViewActive ? sessionId : null);
            return;
        }
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _openSessionCts = opening;
        ProjectionStore? acquired = null;
        ConversationViewModel? conversation = null;
        var adopted = false;
        try
        {
            var handle = await Task.Run(() => _runtime.ResumeSessionAsync(sessionId, opening.Token).AsTask(), opening.Token)
                .WaitAsync(opening.Token);
            if (_disposed || navigation != _navigationVersion)
                return;
            acquired = _hub.Acquire(sessionId);
            await acquired.Initialized.WaitAsync(opening.Token);
            opening.Token.ThrowIfCancellationRequested();
            if (_disposed || navigation != _navigationVersion)
                return;

            var workspaceRoot = handle.WorkspaceRoot;
            _sessionRoots[sessionId] = workspaceRoot;
            RevealWorkspace(workspaceRoot);
            CloseActive();
            Draft = null;
            _sessionDrafts.TryGetValue(sessionId, out var saved);
            var savedText = saved?.Composer.Draft;
            var savedMode = saved?.Composer.Mode;
            conversation = new ConversationViewModel(_runtime, acquired, saved?.Composer, ModelChoices,
                WorkbenchModelCatalog.DefaultSelection(_config), readingState: _readingStates.Get(sessionId))
            { WorkspaceRoot = workspaceRoot };
            conversation.Composer.ConfigureWorkspace(workspaceRoot);
            if (saved is not null)
            {
                if (FindModel(saved.Selection) is { } model)
                    conversation.ConfigureModel(model);
                conversation.RestoreComposerState(savedText!, savedMode!.Value, saved.Selection);
                conversation.Banner = saved.Banner;
                if (saved.UnsentText is not null)
                {
                    Banner = saved.Banner;
                    UnsentText = saved.UnsentText;
                }
            }
            _sessionDrafts.Remove(sessionId);
            if (GetSavedSelection(workspaceRoot, sessionId.Value) is { } persistedSelection)
                conversation.RestoreModelSelection(persistedSelection);
            if (_composerConfig.PendingSessionModes?.TryGetValue(sessionId.Value, out var pendingMode) == true)
                conversation.RestorePendingMode(pendingMode);
            conversation.Composer.RestorePlanEnabled(conversation.PlanEnabled);
            ObserveMode(conversation.Composer, () => sessionId);
            ObserveSelection(conversation, () => sessionId.Value);
            conversation.ModeSubmitted += (mode, _) => ClearSubmittedMode(sessionId, mode);
            conversation.ForkRequested = boundaryId => ForkSessionAsync(sessionId, boundaryId);
            conversation.NextSubmissionSequence = () => ++_submissionSequence;
            conversation.ModelSelectionSubmitted += (selection, sequence) => RecordAcceptedSelection(workspaceRoot, sessionId, selection, sequence);
            RefreshSessionActivities();
            adopted = true;
            Active = conversation;
            Sidebar.SetActive(sessionId);
            BrowserPane?.Bind(sessionId);
            EnsureDocumentContext();
            TryShowActiveBrowserDocument();
            _focusedWorkspaceRoot = workspaceRoot;
            EnsurePanes(workspaceRoot);
            EnsureDocumentContext();
            await Sidebar.RefreshAsync();
        }
        catch (OperationCanceledException) when (opening.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!adopted)
            {
                conversation?.Dispose();
                if (acquired is not null)
                    _hub.Release(sessionId);
            }
            if (ReferenceEquals(_openSessionCts, opening))
                _openSessionCts = null;
        }
    }

    private long BeginNavigation()
    {
        _openSessionCts?.Cancel();
        _openSessionCts = null;
        return ++_navigationVersion;
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
                active.Banner = Loc.Get("workbench_fork_error", exception.Message);
            }
        }
    }

    public void ShowNewTask(string? workspaceRoot = null)
    {
        if (_disposed)
            return;
        BeginNavigation();
        if (workspaceRoot is not null)
        {
            RevealWorkspace(workspaceRoot);
        }
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
        draft.Composer.RestoreExecutionState(_composerConfig.DefaultMode, _composerConfig.DefaultPlanEnabled);
        if (selection is not null)
            draft.RestoreModelSelection(selection);
        var observedDraft = draft;
        ObserveMode(draft.Composer, () => observedDraft.CreatedSessionId);
        ObserveSelection(draft, () => observedDraft.CreatedSessionId?.Value ?? WorkbenchComposerConfig.DraftSessionKey);
        _drafts[root] = draft;
        return draft;
    }

    // Overlay visibility applies the moment it changes, so it persists immediately
    // rather than waiting for the settings dialog to save.
    private void OnTaskOverlayPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TaskOverlayViewModel.IsVisible))
            return;
        try
        {
            _saveTaskOverlay?.Invoke(new WorkbenchTaskOverlayConfig(TaskOverlay.IsVisible));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Banner = Loc.Get("workbench_task_overlay_save_error", exception.Message);
        }
    }

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
        SessionHandle? createdHandle = null;
        var submitting = false;
        ProjectionStore? submissionStore = null;
        await ComposerSubmission.SubmitAsync(new ComposerSubmission.Request<DraftSendState>
        {
            Composer = draft.Composer,
            ApplyCommandAsync = command =>
            {
                if (command.Kind == ComposerCommandKind.Orchestrator)
                {
                    if (command.Prompt.Length == 0)
                    {
                        draft.Composer.ToggleArmedWorkflow(OrchestratorWorkflow.Id);
                    }
                    else
                    {
                        draft.Composer.ArmWorkflow(OrchestratorWorkflow.Id);
                    }
                }
                return Task.FromResult(false);
            },
            CaptureState = () => new DraftSendState(draft.Composer.Mode, draft.Composer.PlanEnabled,
                draft.Selection, ++_submissionSequence, _navigationVersion),
            BeginSending = () =>
            {
                draft.Banner = null;
                draft.UnsentText = null;
            },
            BuildCommandAsync = async (state, text, images) =>
            {
                var options = new SessionOptions(draft.WorkspaceRoot, PermissionMode: state.Mode,
                    ModelSelection: state.Selection,
                    ActiveWorkflows: draft.Composer.ArmedWorkflows is { Count: > 0 } armed ? armed : null,
                    PlanEnabled: state.PlanEnabled);
                var handle = await Task.Run(() => _runtime.CreateSessionAsync(options, CancellationToken.None).AsTask());
                createdHandle = handle;
                draft.CreatedSessionId = handle.Id;
                TransferDraftSelection(draft, handle.Id);
                SavePendingMode(handle.Id, draft.Composer.Mode);
                _sessionRoots[handle.Id] = handle.WorkspaceRoot;
                RevealWorkspace(draft.WorkspaceRoot);
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
                // Workflows now live on the session record; a stale armed copy would outlive the
                // one-shot workflow and keep the badge visible after the turn clears it.
                draft.Composer.ClearArmedWorkflows();
                // The plan flag the draft carried now lives on the session.
                if (_composerConfig.DefaultPlanEnabled)
                    SaveComposerConfig(_composerConfig with { DefaultPlanEnabled = false });
                if (_drafts.GetValueOrDefault(draft.WorkspaceRoot) == draft)
                {
                    _drafts.Remove(draft.WorkspaceRoot);
                }
                UnobserveSelection(draft);
                if (state.Navigation == _navigationVersion && ReferenceEquals(Draft, draft))
                {
                    await OpenSessionAsync(handle.Id);
                }
                submitting = true;
                var store = _hub.Acquire(handle.Id);
                submissionStore = store;
                await store.Initialized;
                return new SendTextCommand(
                    StrongId.NewCommandId(), handle.Id, store.Revision, text, state.Mode, state.Selection,
                    Images: images, PlanEnabled: state.PlanEnabled);
            },
            SubmitCommandAsync = async send =>
            {
                var ack = await Task.Run(() => _runtime.SubmitAsync(send, CancellationToken.None).AsTask());
                submissionStore!.SetRevision(ack.Revision);
                return ack;
            },
            OnAccepted = state =>
            {
                ClearSubmittedMode(createdHandle!.Id, state.Mode);
                if (state.Selection is not null)
                    RecordAcceptedSelection(draft.WorkspaceRoot, createdHandle!.Id, state.Selection, state.Sequence);
            },
            OnRejected = (text, ack) => SetSendFailure(draft, createdHandle!.Id, text,
                ack.RejectionReason ?? Loc.Get("workbench_input_rejected")),
            DescribeFailure = exception => submitting
                ? Loc.Get("workbench_delivery_unconfirmed", exception.Message)
                : createdHandle is not null
                    ? Loc.Get("workbench_message_not_sent", exception.Message)
                    : Loc.Get("workbench_create_conversation_error", exception.Message),
            ReportFailure = (text, message) => SetSendFailure(draft, createdHandle?.Id, text, message),
            AfterOutcomeAsync = Sidebar.RefreshAsync,
            Release = () =>
            {
                if (submissionStore is not null)
                    _hub.Release(submissionStore.SessionId);
            },
        });
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
                GetNewDraftSelection(workspaceRoot),
                PlanEnabled: false),
            CancellationToken.None);
        _sessionRoots[handle.Id] = workspaceRoot;
        RevealWorkspace(workspaceRoot);
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
        await TryAutoArchiveAsync();
        var titles = new Dictionary<SessionId, string>();
        _refreshTitleOverrides.Add(titles);
        try
        {
            var active = await _runtime.ListSessionsAsync(
                null, 200, SessionListKind.Active, CancellationToken.None);
            if (_disposed)
            {
                return;
            }
            foreach (var summary in active)
            {
                _sessionRoots[summary.Id] = summary.WorkspaceRoot;
            }

            var hiddenRoots = BuildRemovedWorkspaceRoots();
            var displayed = Sidebar.IsArchivedView
                ? await _runtime.ListSessionsAsync(null, 200, SessionListKind.Archived, CancellationToken.None)
                : active;
            if (_disposed)
            {
                return;
            }
            displayed = hiddenRoots is { Count: > 0 }
                ? displayed.Where(s => !IsWorkspaceRemoved(hiddenRoots, s.WorkspaceRoot)).ToArray()
                : displayed;
            RefreshSessionActivities();
            Sidebar.ApplySummaries(
            [
                .. displayed.Select(s => new SessionSummaryViewModel(
                    s.Id, titles.GetValueOrDefault(s.Id, s.Title), s.WorkspaceRoot, s.UpdatedAt, s.IsArchived))
            ]);
            foreach (var summary in displayed)
            {
                _sessionRoots.TryAdd(summary.Id, summary.WorkspaceRoot);
            }
            if (_openSessionCts is null && _focusedWorkspaceRoot is null && Active is null && Draft is { } initial
                && string.IsNullOrEmpty(initial.Composer.Draft) && !initial.Composer.IsSending
                && displayed.OrderByDescending(summary => summary.UpdatedAt).FirstOrDefault() is { } recent)
            {
                ShowNewTask(recent.WorkspaceRoot);
            }
        }
        finally
        {
            _refreshTitleOverrides.Remove(titles);
        }
    }

    /// <summary>
    /// Removal only hides the sidebar entry; sessions stay stored and running, and the
    /// group returns as soon as a task is opened or started in the folder again.
    /// </summary>
    public async Task RemoveWorkspaceAsync(string workspaceRoot)
    {
        if (_disposed)
        {
            return;
        }
        var root = DocumentViewModel.NormalizeWorkspaceRoot(workspaceRoot);
        var removed = new HashSet<string>(
            _composerConfig.RemovedWorkspaceRoots ?? (IReadOnlyList<string>)[],
            StringComparer.OrdinalIgnoreCase)
        {
            root,
        };
        SaveComposerConfig(_composerConfig with { RemovedWorkspaceRoots = [.. removed] });
        var navigation = _navigationVersion;
        var current = ComposerHost;
        await Sidebar.RefreshAsync();
        if (_disposed || navigation != _navigationVersion || !ReferenceEquals(ComposerHost, current))
        {
            return;
        }
        if (Active is { } active
            && _sessionRoots.TryGetValue(active.SessionId, out var activeRoot)
            && string.Equals(DocumentViewModel.NormalizeWorkspaceRoot(activeRoot), root, StringComparison.OrdinalIgnoreCase))
        {
            CloseActive();
        }
        if (_focusedWorkspaceRoot is { } focused
            && string.Equals(DocumentViewModel.NormalizeWorkspaceRoot(focused), root, StringComparison.OrdinalIgnoreCase))
        {
            _focusedWorkspaceRoot = null;
        }
        if ((Draft is { } draft && string.Equals(DocumentViewModel.NormalizeWorkspaceRoot(draft.WorkspaceRoot), root, StringComparison.OrdinalIgnoreCase))
            || (Active is null && Draft is null))
        {
            ShowNewTask(null);
        }
    }

    private HashSet<string>? BuildRemovedWorkspaceRoots() =>
        _composerConfig.RemovedWorkspaceRoots is { Count: > 0 } removed
            ? new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase)
            : null;

    private static bool IsWorkspaceRemoved(IReadOnlyCollection<string> removedRoots, string workspaceRoot) =>
        removedRoots.Contains(DocumentViewModel.NormalizeWorkspaceRoot(workspaceRoot), StringComparer.OrdinalIgnoreCase);

    private void RevealWorkspace(string workspaceRoot)
    {
        if (_composerConfig.RemovedWorkspaceRoots is not { } removed
            || !IsWorkspaceRemoved(removed, workspaceRoot))
        {
            return;
        }
        var root = DocumentViewModel.NormalizeWorkspaceRoot(workspaceRoot);
        SaveComposerConfig(_composerConfig with
        {
            RemovedWorkspaceRoots = removed
                .Where(stored => !string.Equals(stored, root, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        });
    }

    public async Task SetSessionArchivedAsync(SessionId sessionId, bool archived)
    {
        if (_disposed)
        {
            return;
        }
        await _runtime.SetSessionArchivedAsync(sessionId, archived, CancellationToken.None);
        await Sidebar.RefreshAsync();
    }

    public async Task ToggleArchivedViewAsync()
    {
        if (_disposed)
        {
            return;
        }
        Sidebar.IsArchivedView = !Sidebar.IsArchivedView;
        await Sidebar.RefreshAsync();
    }

    public ValueTask<int> CountArchivedSessionsAsync(CancellationToken cancellationToken) =>
        _disposed ? ValueTask.FromResult(0) : _runtime.CountArchivedSessionsAsync(cancellationToken);

    public async ValueTask<int> DeleteArchivedSessionsAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return 0;
        }

        if (Active is { } active)
        {
            var archived = await _runtime.ListSessionsAsync(
                null, int.MaxValue, SessionListKind.Archived, cancellationToken);
            if (archived.Any(summary => summary.Id == active.SessionId))
            {
                ShowNewTask();
                _sessionDrafts.Remove(active.SessionId);
            }
        }

        var deleted = await Task.Run(
            () => _runtime.DeleteArchivedSessionsAsync(cancellationToken).AsTask(),
            cancellationToken);
        await Sidebar.RefreshAsync();
        return deleted;
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
            SessionActivities.ApplyTitle(sessionId, title);
            Sidebar.ApplyTitle(sessionId, title);
            if (Active is { } active && active.SessionId == sessionId)
            {
                active.Title = title;
            }
        });
    }

    private void OnSessionDeleted(SessionId sessionId)
    {
        if (_disposed)
            return;
        _scheduler.Post(() =>
        {
            if (!_disposed)
                SessionActivities.Remove(sessionId);
        });
    }

    // Session-list mutations can originate off the UI thread (remote gateway, maintenance
    // tick); refresh to quiescence so a change landing mid-refresh is not lost.
    private void OnSessionListChanged()
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
            if (_sessionListRefreshActive)
            {
                _sessionListRefreshRetriggered = true;
                return;
            }
            _ = RefreshSessionListAsync();
        });
    }

    private async Task RefreshSessionListAsync()
    {
        _sessionListRefreshActive = true;
        try
        {
            do
            {
                _sessionListRefreshRetriggered = false;
                await Sidebar.RefreshAsync();
            }
            while (_sessionListRefreshRetriggered);
        }
        finally
        {
            _sessionListRefreshActive = false;
        }
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
        _readingStates.Save(sessionId, Active.CaptureReadingState());
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

    private sealed record DraftSendState(
        PermissionMode Mode,
        bool PlanEnabled,
        ModelSelection? Selection,
        long Sequence,
        long Navigation);

    private sealed record SessionDraft(ComposerViewModel Composer, ModelSelection? Selection, string? Banner, string? UnsentText = null);
}
