using System.Collections.Concurrent;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Workspace;
using Alcoler.Kernel.Skills;
using Alcoler.Kernel.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcoler.Kernel.Runtime;

public sealed class AgentRuntimeOptions
{
    public required ISessionStore Store { get; init; }

    public required IArtifactStore Artifacts { get; init; }

    public required IReadOnlyList<IModelProvider> Providers { get; init; }

    public required IReadOnlyList<ITool> Tools { get; init; }

    public required Func<string, IToolWorkspace> WorkspaceFactory { get; init; }

    public PermissionRuleset PermissionRules { get; init; } = PermissionRuleset.Empty;

    public ModelSelection? DefaultModel { get; init; }

    public string? AgentInstructions { get; init; }

    public string? SkillsListing { get; init; }

    public ITodoStore? TodoStore { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ILoggerFactory? Logging { get; init; }
}

public sealed class AgentRuntime : IAgentRuntime
{
    private readonly AgentRuntimeOptions _options;
    private readonly ConcurrentDictionary<SessionId, SessionRuntime> _sessions = new();
    private readonly ToolRegistry _tools;
    private readonly ConcurrentDictionary<(string ProviderId, string ModelId), IChatModel> _modelCache = new();
    private readonly ILoggerFactory _logging;
    private bool _disposed;

    public AgentRuntime(AgentRuntimeOptions options)
    {
        _options = options;
        _tools = new ToolRegistry(options.Tools);
        _logging = options.Logging ?? NullLoggerFactory.Instance;
    }

    public async ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken)
    {
        var now = _options.Clock.GetUtcNow();
        var title = options.Title ?? BuildDefaultTitle(options.WorkspaceRoot);
        var selection = options.ModelSelection ?? _options.DefaultModel;
        var record = new SessionRecord(
            StrongId.NewSessionId(),
            options.WorkspaceRoot,
            title,
            options.PermissionMode,
            selection,
            now,
            now);
        await _options.Store.UpsertSessionAsync(record, cancellationToken).ConfigureAwait(false);
        await CreateRuntimeAsync(record, isNewSession: true).ConfigureAwait(false);
        return new SessionHandle(record.Id, record.WorkspaceRoot, record.Title);
    }

    public async ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var active))
        {
            return new SessionHandle(active.SessionId, active.WorkspaceRoot, active.SessionId.Value);
        }

        var record = await _options.Store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {sessionId.Value} not found.");
        await CreateRuntimeAsync(record, isNewSession: false).ConfigureAwait(false);
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

    private SessionRuntime? GetActiveRuntime(SessionId sessionId) =>
        _sessions.TryGetValue(sessionId, out var runtime) ? runtime : null;

    private async Task<SessionRuntime> CreateRuntimeAsync(SessionRecord record, bool isNewSession)
    {
        var runtime = await SessionRuntime.CreateAsync(record, new SessionRuntimeDeps(
            _options.Store,
            ResolveModel,
            _tools,
            _options.WorkspaceFactory(record.WorkspaceRoot),
            _options.PermissionRules,
            _options.Clock,
            _logging.CreateLogger<SessionRuntime>(),
            new SessionRuntimeOptions
            {
                AgentInstructions = _options.AgentInstructions,
                SkillsListing = SkillsCatalog.RenderListing(new SkillsCatalog().Load(record.WorkspaceRoot)),
                DefaultModel = _options.DefaultModel,
            },
            _options.TodoStore),
            CancellationToken.None,
            persistStartEvents: isNewSession).ConfigureAwait(false);
        _sessions[runtime.SessionId] = runtime;
        return runtime;
    }

    internal AgentRuntimeOptions Options => _options;

    internal SessionRuntime? GetSession(SessionId sessionId) =>
        _sessions.TryGetValue(sessionId, out var runtime) ? runtime : null;

    internal IChatModel ResolveModel(ModelSelection? selection)
    {
        selection ??= _options.DefaultModel ?? throw new InvalidOperationException("No model configured.");
        return _modelCache.GetOrAdd((selection.ProviderId, selection.ModelId), static (key, state) =>
        {
            var (self, sel) = state;
            var provider = self._options.Providers.FirstOrDefault(p => string.Equals(p.Id, sel.ProviderId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No provider registered for '{sel.ProviderId}'.");
            return provider.BindAsync(sel, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }, (this, selection));
    }

    private static string BuildDefaultTitle(string workspaceRoot)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspaceRoot));
        return string.IsNullOrWhiteSpace(name) ? "New session" : name;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var runtime in _sessions.Values)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        _sessions.Clear();
    }
}
