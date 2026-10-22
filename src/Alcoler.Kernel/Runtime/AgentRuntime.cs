using System.Collections.Concurrent;
using Alcoler.Contracts.Commands;
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

    public Func<ShellEnvironment?, ShellEnvironment>? ResolveShell { get; init; }

    internal Func<ShellEnvironment?, string>? CustomSystemPromptFactory { get; init; }

    public Func<string, GitSnapshot?>? GitSnapshotProvider { get; init; }

    public string? SkillsListing { get; init; }

    public string? UserSkillsBase { get; init; }

    public ITodoStore? TodoStore { get; init; }

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
            Shell: options.Shell);
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

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken) =>
        _options.Store.ListSessionsAsync(workspaceRoot, limit, cancellationToken);

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
                ResolveShell = _options.ResolveShell,
                GitSnapshot = _options.GitSnapshotProvider?.Invoke(record.WorkspaceRoot),
                SkillsListing = _options.SkillsListing,
                UserSkillsBase = _options.UserSkillsBase,
                DefaultModel = Volatile.Read(ref _defaultModel),
                SessionTitleGenerationEnabled = _options.SessionTitleGenerationEnabled,
                SessionTitleGenerationTimeout = _options.SessionTitleGenerationTimeout,
            },
            _options.Artifacts,
            _options.TodoStore),
            CancellationToken.None,
            persistStartEvents: isNewSession).ConfigureAwait(false);
        runtime.TurnStarted += OnSessionTurnStarted;
        runtime.TurnFinished += OnSessionTurnFinished;
        runtime.TitleChanged += OnSessionTitleChanged;
        _sessions[runtime.SessionId] = runtime;
        return runtime;
    }

    private void OnSessionTitleChanged(SessionRuntime runtime, string title) =>
        SessionTitleChanged?.Invoke(runtime.SessionId, title);

    private void OnSessionTurnStarted(SessionRuntime runtime) =>
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
