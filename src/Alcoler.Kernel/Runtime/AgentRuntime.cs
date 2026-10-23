using System.Collections.Concurrent;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Workspace;
using Alcoler.Kernel.Prompts;
using Alcoler.Kernel.Tools;
using Alcoler.Kernel.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcoler.Kernel.Runtime;

public sealed record AgentRuntimeOptions
{
    public required ISessionStore Store { get; init; }

    public required IArtifactStore Artifacts { get; init; }

    public required IReadOnlyList<IModelProvider> Providers { get; init; }

    public required IReadOnlyList<ITool> Tools { get; init; }

    public Func<CancellationToken, ValueTask<IReadOnlyList<ITool>>>? SessionToolsProvider { get; init; }

    public required Func<string, IToolWorkspace> WorkspaceFactory { get; init; }

    public PermissionRuleset PermissionRules { get; init; } = PermissionRuleset.Empty;

    public ModelSelection? DefaultModel { get; init; }

    public string? AgentInstructions { get; init; }

    public string? CustomSystemPrompt { get; init; }

    public bool SessionTitleGenerationEnabled { get; init; }

    public TimeSpan SessionTitleGenerationTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public string? ShellName { get; init; }

    public string? SubagentOutputRoot { get; init; }

    public string? ToolOutputRoot { get; init; }

    public Func<ShellEnvironment?, ShellEnvironment>? ResolveShell { get; init; }

    internal Func<ShellEnvironment?, string>? CustomSystemPromptFactory { get; init; }

    public Func<string, GitSnapshot?>? GitSnapshotProvider { get; init; }

    public string? SkillsListing { get; init; }

    public string? UserSkillsBase { get; init; }

    public Func<IReadOnlyList<string>>? DisabledSkillPathsProvider { get; init; }

    public ITodoStore? TodoStore { get; init; }

    public IPlanFileStore PlanFileStore { get; init; } = NullPlanFileStore.Instance;

    public WorkflowRegistry Workflows { get; init; } = WorkflowRegistry.BuiltIn;

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ILoggerFactory? Logging { get; init; }
}

public sealed class AgentRuntime : IAgentRuntime, ISessionActivityRuntime
{
    private readonly AgentRuntimeOptions _options;
    private readonly ConcurrentDictionary<SessionId, SessionRuntime> _sessions = new();
    private readonly ConcurrentDictionary<(SessionId Parent, SessionId Child), WeakReference<AgentRuntime>> _subagentRuntimes = new();
    private readonly ToolRegistry _tools;
    private readonly ConcurrentDictionary<(long Generation, string ProviderId, string ModelId), IChatModel> _modelCache = new();
    private readonly ILoggerFactory _logging;
    private IReadOnlyList<IModelProvider> _providers;
    private ModelSelection? _defaultModel;
    private long _providerGeneration;
    private bool _disposed;

    public AgentRuntime(AgentRuntimeOptions options)
    {
        _options = options;
        _tools = new ToolRegistry(options.Tools);
        _providers = options.Providers;
        _defaultModel = options.DefaultModel;
        _logging = options.Logging ?? NullLoggerFactory.Instance;
    }

    public async ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken)
    {
        if (!_options.Workflows.TryNormalize(options.ActiveWorkflows, out var activeWorkflows, out var workflowError))
        {
            throw new InvalidOperationException(workflowError);
        }
        var now = _options.Clock.GetUtcNow();
        var title = options.Title ?? "New session";
        var selection = options.ModelSelection ?? Volatile.Read(ref _defaultModel);
        var record = new SessionRecord(
            StrongId.NewSessionId(),
            options.WorkspaceRoot,
            title,
            options.PermissionMode,
            selection,
            now,
            now,
            ParentSessionId: options.ParentSessionId,
            TitleSource: options.Title is null ? SessionTitleSource.Default : SessionTitleSource.Custom,
            Shell: options.Shell,
            ActiveWorkflows: activeWorkflows.Count == 0 ? null : activeWorkflows);
        await _options.Store.UpsertSessionAsync(record, cancellationToken).ConfigureAwait(false);
        await CreateRuntimeAsync(record, isNewSession: true, cancellationToken).ConfigureAwait(false);
        return new SessionHandle(record.Id, record.WorkspaceRoot, record.Title);
    }

    public async ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var active))
        {
            return new SessionHandle(active.SessionId, active.WorkspaceRoot, active.Title);
        }

        var record = await _options.Store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {sessionId.Value} not found.");
        await CreateRuntimeAsync(record, isNewSession: false, cancellationToken).ConfigureAwait(false);
        return new SessionHandle(record.Id, record.WorkspaceRoot, record.Title);
    }

    public async ValueTask<SessionHandle> ForkSessionAsync(
        SessionId sourceSessionId,
        MessageId boundaryMessageId,
        CancellationToken cancellationToken)
    {
        var parent = await _options.Store.GetSessionAsync(sourceSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {sourceSessionId.Value} not found.");
        var stored = await _options.Store.LoadMessagesAsync(sourceSessionId, cancellationToken).ConfigureAwait(false);
        var active = SessionBranches.SelectActive(stored, parent.Branch);
        var boundaryIndex = -1;
        for (var index = 0; index < active.Count; index++)
        {
            if (active[index].Message.Id == boundaryMessageId)
            {
                boundaryIndex = index;
                break;
            }
        }
        if (boundaryIndex < 0 || active[boundaryIndex].Message.Role != MessageRole.Assistant)
        {
            throw new InvalidOperationException("Fork boundary must be an assistant message in the active conversation.");
        }

        var durable = await _options.Store.LoadDurableEventsAsync(sourceSessionId, cancellationToken).ConfigureAwait(false);
        var activeIds = active.Select(storedMessage => storedMessage.Message.Id).ToHashSet();
        var completedTails = SessionBranches.FindCompletedTurnTails(durable, activeIds);
        if (!completedTails.ContainsKey(boundaryMessageId))
        {
            throw new InvalidOperationException("Only a completed assistant turn can be forked.");
        }

        var copyEnd = boundaryIndex + 1;
        while (copyEnd < active.Count && !SessionRuntime.IsRealUserTurn(active[copyEnd].Message))
        {
            copyEnd++;
        }
        if (copyEnd == active.Count
            && _sessions.TryGetValue(sourceSessionId, out var activeRuntime)
            && activeRuntime.IsTurnActive)
        {
            throw new InvalidOperationException("The active turn cannot be forked; wait for it to complete.");
        }

        var now = _options.Clock.GetUtcNow();
        var child = new SessionRecord(
            StrongId.NewSessionId(),
            parent.WorkspaceRoot,
            $"Fork of {parent.Title}",
            parent.PermissionMode,
            parent.ModelSelection,
            now,
            now,
            IsArchived: false,
            ParentSessionId: null,
            TitleSource: SessionTitleSource.Custom,
            Shell: parent.Shell,
            ForkedFromSessionId: parent.Id,
            ForkedFromMessageId: boundaryMessageId,
            PreviousPermissionMode: parent.PreviousPermissionMode,
            ActiveWorkflows: parent.ActiveWorkflows);
        var copied = active.Take(copyEnd).Select(storedMessage => storedMessage.Message).ToList();
        var events = new List<SequencedEvent>(copied.Count + completedTails.Count);
        long sequence = 1;
        foreach (var message in copied)
        {
            var committed = message.Role == MessageRole.User
                ? (SessionEvent)new UserMessageCommittedEvent(message)
                : new AssistantMessageCommittedEvent(message);
            events.Add(new SequencedEvent(sequence++, child.Id, message.CreatedAt, committed));
            if (completedTails.TryGetValue(message.Id, out var turnId))
            {
                events.Add(new SequencedEvent(sequence++, child.Id, message.CreatedAt,
                    new TurnCompletedEvent(turnId, TurnOutcome.Completed)));
            }
        }

        await _options.Store.UpsertSessionAsync(child, cancellationToken).ConfigureAwait(false);
        try
        {
            if (copied.Count > 0)
            {
                await _options.Store.AppendMessagesAsync(child.Id, copied, 0, cancellationToken).ConfigureAwait(false);
            }
            if (events.Count > 0)
            {
                await _options.Store.AppendDurableEventsAsync(events, cancellationToken).ConfigureAwait(false);
            }
            await CreateRuntimeAsync(child, isNewSession: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _options.Store.DeleteSessionAsync(child.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
        return new SessionHandle(child.Id, child.WorkspaceRoot, child.Title);
    }

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken) =>
        _options.Store.ListSessionsAsync(workspaceRoot, limit, cancellationToken);

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        string? workspaceRoot,
        int limit,
        SessionListKind kind,
        CancellationToken cancellationToken) =>
        _options.Store.ListSessionsAsync(workspaceRoot, limit, kind, cancellationToken);

    public async ValueTask SetSessionArchivedAsync(
        SessionId sessionId,
        bool archived,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_sessions.TryGetValue(sessionId, out var runtime))
        {
            await runtime.SetArchivedAsync(archived, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _options.Store.SetSessionArchivedAsync(sessionId, archived, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ArchiveStaleSessionsAsync(
        TimeSpan olderThan,
        SessionId? keepOpen,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (olderThan < TimeSpan.FromDays(1))
        {
            olderThan = TimeSpan.FromDays(1);
        }

        var cutoff = _options.Clock.GetUtcNow() - olderThan;
        // Resident sessions keep their record in memory and would write it back on the next turn.
        var excluded = new HashSet<SessionId>(_sessions.Keys);
        if (keepOpen is { } open)
        {
            excluded.Add(open);
        }

        return await _options.Store.ArchiveSessionsUpdatedBeforeAsync(cutoff, excluded, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<int> CountArchivedSessionsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        return _options.Store.CountArchivedSessionsAsync(cancellationToken);
    }

    public async ValueTask<int> DeleteArchivedSessionsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        var archived = await _options.Store
            .ListSessionsAsync(null, int.MaxValue, SessionListKind.Archived, cancellationToken)
            .ConfigureAwait(false);
        var archivedIds = archived.Select(summary => summary.Id).ToHashSet();
        foreach (var (sessionId, runtime) in _sessions.ToArray())
        {
            if ((runtime.IsArchived || archivedIds.Contains(sessionId)) &&
                _sessions.TryRemove(sessionId, out var removed))
            {
                await removed.DisposeAsync().ConfigureAwait(false);
            }
        }

        foreach (var (key, reference) in _subagentRuntimes.ToArray())
        {
            if (!archivedIds.Contains(key.Parent) || !_subagentRuntimes.TryRemove(key, out _))
            {
                continue;
            }

            if (reference.TryGetTarget(out var childRuntime))
            {
                await childRuntime.DisposeAsync().ConfigureAwait(false);
            }
        }

        var deletion = await _options.Store.DeleteArchivedSessionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sessionId in deletion.DeletedSessionIds)
        {
            try
            {
                await _options.Artifacts
                    .DeleteSessionArtifactsAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is NotSupportedException or IOException or UnauthorizedAccessException)
            {
            }

            DeleteSessionOutputDirectory(_options.ToolOutputRoot, sessionId);
            if (!string.Equals(
                    _options.ToolOutputRoot, _options.SubagentOutputRoot, StringComparison.OrdinalIgnoreCase))
            {
                DeleteSessionOutputDirectory(_options.SubagentOutputRoot, sessionId);
            }
        }

        return deletion.ArchivedTaskCount;
    }

    private static void DeleteSessionOutputDirectory(string? root, SessionId sessionId)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        try
        {
            var fullRoot = Path.GetFullPath(root);
            var directory = Path.GetFullPath(Path.Combine(fullRoot, sessionId.Value));
            if (!string.Equals(Path.GetDirectoryName(directory), fullRoot, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(directory))
            {
                return;
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(command.SessionId, out var runtime))
        {
            return CommandAck.Rejected(command.CommandId, 0, "session is not active; resume it first");
        }
        return await runtime.SubmitAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public IConversationProjection Subscribe(SessionId sessionId)
    {
        var runtime = GetActiveRuntime(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId.Value} is not active.");
        return runtime.CreateProjection();
    }

    public IConversationProjection? TryObserve(SessionId sessionId) =>
        _sessions.TryGetValue(sessionId, out var runtime) ? runtime.CreateProjection() : null;

    public ShellEnvironment? GetSessionShell(SessionId sessionId) =>
        !Volatile.Read(ref _disposed) && _sessions.TryGetValue(sessionId, out var runtime) ? runtime.Shell : null;

    public IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId) =>
        !Volatile.Read(ref _disposed) && _sessions.TryGetValue(sessionId, out var runtime)
            ? runtime.GetBackgroundJobs()
            : [];

    public SessionActivitySnapshot? GetSessionActivity(SessionId sessionId) =>
        !Volatile.Read(ref _disposed) && _sessions.TryGetValue(sessionId, out var runtime)
            ? runtime.ActivitySnapshot()
            : null;

    public event Action<SessionId>? SessionActivityChanged;

    public event Action<SessionId, string>? SessionTitleChanged;

    public bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId) =>
        !Volatile.Read(ref _disposed) && _sessions.TryGetValue(sessionId, out var runtime) && runtime.StopBackgroundJob(jobId);

    public async ValueTask<bool> TryStopSubagentAsync(SessionId sessionId, string taskId)
    {
        if (Volatile.Read(ref _disposed) || !_sessions.TryGetValue(sessionId, out var runtime))
            return false;
        return await runtime.TryStopSubagentAsync(taskId).ConfigureAwait(false);
    }

    public IConversationProjection? TryObserveSubagent(SessionId parentSessionId, SessionId childSessionId)
    {
        if (Volatile.Read(ref _disposed) || !_sessions.ContainsKey(parentSessionId) ||
            !_subagentRuntimes.TryGetValue((parentSessionId, childSessionId), out var reference) ||
            !reference.TryGetTarget(out var runtime) || Volatile.Read(ref runtime._disposed))
            return null;

        return runtime.TryObserve(childSessionId);
    }

    private SessionRuntime? GetActiveRuntime(SessionId sessionId) =>
        _sessions.TryGetValue(sessionId, out var runtime) ? runtime : null;

    private async Task<SessionRuntime> CreateRuntimeAsync(SessionRecord record, bool isNewSession, CancellationToken cancellationToken)
    {
        var tools = _tools;
        if (_options.SessionToolsProvider is { } sessionTools)
        {
            var additional = await sessionTools(cancellationToken).ConfigureAwait(false);
            if (additional.Count > 0)
                tools = new ToolRegistry([.. _options.Tools, .. additional]);
        }
        var runtime = await SessionRuntime.CreateAsync(record, new SessionRuntimeDeps(
            _options.Store,
            ResolveModel,
            tools,
            _options.WorkspaceFactory(record.WorkspaceRoot),
            _options.PermissionRules,
            _options.Clock,
            _logging.CreateLogger<SessionRuntime>(),
            new SessionRuntimeOptions
            {
                AgentInstructions = RenderAgentInstructions(record.WorkspaceRoot),
                CustomSystemPrompt = _options.CustomSystemPrompt,
                CustomSystemPromptFactory = _options.CustomSystemPromptFactory,
                ShellName = _options.ShellName,
                ToolOutputRoot = _options.ToolOutputRoot,
                ResolveShell = _options.ResolveShell,
                GitSnapshot = _options.GitSnapshotProvider?.Invoke(record.WorkspaceRoot),
                SkillsListing = _options.SkillsListing,
                UserSkillsBase = _options.UserSkillsBase,
                DisabledSkillPathsProvider = _options.DisabledSkillPathsProvider,
                DefaultModel = Volatile.Read(ref _defaultModel),
                SessionTitleGenerationEnabled = _options.SessionTitleGenerationEnabled,
                SessionTitleGenerationTimeout = _options.SessionTitleGenerationTimeout,
                Workflows = _options.Workflows,
            },
            _options.Artifacts,
            _options.TodoStore,
            _options.PlanFileStore),
            CancellationToken.None,
            persistStartEvents: isNewSession).ConfigureAwait(false);
        runtime.TurnStarted += OnSessionTurnStarted;
        runtime.TurnFinished += OnSessionTurnFinished;
        runtime.ActivityChanged += OnSessionActivityChanged;
        runtime.TitleChanged += OnSessionTitleChanged;
        _sessions[runtime.SessionId] = runtime;
        return runtime;
    }

    private void OnSessionTitleChanged(SessionRuntime runtime, string title) =>
        SessionTitleChanged?.Invoke(runtime.SessionId, title);

    private void OnSessionTurnStarted(SessionRuntime runtime) =>
        SessionActivityChanged?.Invoke(runtime.SessionId);

    private void OnSessionActivityChanged(SessionRuntime runtime) =>
        SessionActivityChanged?.Invoke(runtime.SessionId);

    private ValueTask OnSessionTurnFinished(SessionRuntime runtime, TurnOutcome outcome)
    {
        SessionActivityChanged?.Invoke(runtime.SessionId);
        return ValueTask.CompletedTask;
    }

    // A host-provided AgentInstructions value is treated as the workspace source verbatim;
    // otherwise instructions are discovered on disk (user scope first, then workspace walk-up).
    private string? RenderAgentInstructions(string workspaceRoot)
    {
        var sources = _options.AgentInstructions is { Length: > 0 } raw
            ? [new AgentInstructionSource("AGENTS.md", "AGENTS.md", raw, Truncated: false, AgentInstructionScope.Workspace)]
            : AgentInstructionsLoader.Load(workspaceRoot);
        return AgentsMdSection.Render(sources);
    }

    internal AgentRuntimeOptions Options => _options;

    internal void RegisterSubagentRuntime(SessionId parentSessionId, SessionId childSessionId, AgentRuntime runtime)
    {
        if (Volatile.Read(ref _disposed) || !_sessions.ContainsKey(parentSessionId))
            return;

        var key = (parentSessionId, childSessionId);
        _subagentRuntimes[key] = new WeakReference<AgentRuntime>(runtime);
        if (Volatile.Read(ref _disposed))
            _subagentRuntimes.TryRemove(key, out _);
    }

    internal SessionRuntime? GetSession(SessionId sessionId) =>
        _sessions.TryGetValue(sessionId, out var runtime) ? runtime : null;

    internal IReadOnlyList<IModelProvider> CurrentProviders => Volatile.Read(ref _providers);

    internal ModelSelection? CurrentDefaultModel => Volatile.Read(ref _defaultModel);

    public void ApplyProviderConfiguration(IReadOnlyList<IModelProvider> providers, ModelSelection? defaultModel)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Volatile.Write(ref _providers, providers);
        Volatile.Write(ref _defaultModel, defaultModel);
        Interlocked.Increment(ref _providerGeneration);
        _modelCache.Clear();
    }

    internal IChatModel ResolveModel(ModelSelection? selection)
    {
        selection ??= Volatile.Read(ref _defaultModel) ?? throw new InvalidOperationException("No model configured.");
        var generation = Volatile.Read(ref _providerGeneration);
        return _modelCache.GetOrAdd((generation, selection.ProviderId, selection.ModelId), static (key, state) =>
        {
            var (self, sel) = state;
            var provider = self.CurrentProviders.FirstOrDefault(p => string.Equals(p.Id, sel.ProviderId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No provider registered for '{sel.ProviderId}'.");
            return provider.BindAsync(sel, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }, (this, selection));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _subagentRuntimes.Clear();
        foreach (var runtime in _sessions.Values)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        _sessions.Clear();
    }
}
