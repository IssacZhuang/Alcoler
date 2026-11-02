using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;

namespace Alcoler.Remote.Tests.Fakes;

// Thread-safe in-memory IAgentRuntime with just enough session state and command
// behavior to exercise the gateway end to end. Commands validate BaseRevision,
// bump the revision, and publish projection deltas synchronously.
public sealed class FakeAgentRuntime : IAgentRuntime, ISessionActivityRuntime
{
    private readonly object _lock = new();
    private readonly Dictionary<SessionId, FakeSession> _sessions = [];

    public ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken)
    {
        var id = StrongId.NewSessionId();
        var title = string.IsNullOrWhiteSpace(options.Title) ? "Session" : options.Title;
        var now = DateTimeOffset.UtcNow;
        var session = new FakeSession
        {
            Gate = new object(),
            Id = id,
            WorkspaceRoot = options.WorkspaceRoot,
            Title = title,
            Mode = options.PermissionMode,
            CreatedAt = now,
            UpdatedAt = now,
            State = ConversationState.Initial(title, options.PermissionMode, null, 200_000),
        };
        lock (_lock)
        {
            _sessions[id] = session;
        }
        return ValueTask.FromResult(new SessionHandle(id, options.WorkspaceRoot, title));
    }

    public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var session = RequireSession(sessionId);
            return ValueTask.FromResult(new SessionHandle(session.Id, session.WorkspaceRoot, session.Title));
        }
    }

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        string? workspaceRoot, int limit, CancellationToken cancellationToken) =>
        ListSessionsAsync(workspaceRoot, limit, SessionListKind.Active, cancellationToken);

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        string? workspaceRoot, int limit, SessionListKind kind, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var summaries = _sessions.Values
                .Where(session => IsArchived(session) == (kind == SessionListKind.Archived))
                .Where(session => workspaceRoot is null ||
                    string.Equals(session.WorkspaceRoot, workspaceRoot, StringComparison.Ordinal))
                .OrderByDescending(session => session.UpdatedAt)
                .Take(limit)
                .Select(ToSummary)
                .ToList();
            return ValueTask.FromResult<IReadOnlyList<SessionSummary>>(summaries);
        }
    }

    public ValueTask SetSessionArchivedAsync(SessionId sessionId, bool archived, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                throw new KeyNotFoundException($"Session {sessionId.Value} does not exist.");
            lock (session.Gate)
            {
                session.Archived = archived;
                session.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> UnloadIdleSessionsAsync(
        TimeSpan idleThreshold, SessionId? keepOpen, CancellationToken cancellationToken) =>
        ValueTask.FromResult(0);

    public IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId) => [];

    public bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId) => false;

    public SessionActivitySnapshot? GetSessionActivity(SessionId sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return null;
            lock (session.Gate)
                return new SessionActivitySnapshot(session.State.Phase, LastOutcome: null);
        }
    }

    public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(command.SessionId, out var session))
                return ValueTask.FromResult(CommandAck.Rejected(command.CommandId, 0, "unknown_session"));
            lock (session.Gate)
            {
                if (command.BaseRevision != session.Revision)
                    return ValueTask.FromResult(
                        CommandAck.Rejected(command.CommandId, session.Revision, "stale_base_revision"));
                session.Revision += 1;
                ApplyCommand(session, command);
                session.UpdatedAt = DateTimeOffset.UtcNow;
                return ValueTask.FromResult(
                    new CommandAck(command.CommandId, Accepted: true, session.Revision)
                    {
                        LastEventSequence = session.Sequence,
                    });
            }
        }
    }

    public IConversationProjection Subscribe(SessionId sessionId)
    {
        lock (_lock)
        {
            var session = RequireSession(sessionId);
            return new FakeConversationProjection(session);
        }
    }

    public IConversationProjection? TryObserve(SessionId sessionId)
    {
        lock (_lock)
        {
            var session = RequireSession(sessionId);
            return new FakeConversationProjection(session);
        }
    }

    public InteractionId SeedPendingPermission(SessionId sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                throw new KeyNotFoundException($"Session {sessionId.Value} does not exist.");
            var interactionId = StrongId.NewInteractionId();
            lock (session.Gate)
            {
                using var input = JsonDocument.Parse("{}");
                session.State = session.State with
                {
                    Phase = TurnPhase.AwaitingPermission,
                    PendingPermission = new PermissionRequest(
                        interactionId,
                        session.Id,
                        "shell",
                        input.RootElement.Clone(),
                        RiskLevel.Medium,
                        "seeded pending permission"),
                };
            }
            return interactionId;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            foreach (var session in _sessions.Values)
            {
                lock (session.Gate)
                {
                    foreach (var view in session.Views)
                        view.Writer.TryComplete();
                    session.Views.Clear();
                }
            }
            _sessions.Clear();
        }
        return ValueTask.CompletedTask;
    }

    private FakeSession RequireSession(SessionId sessionId)
    {
        // Caller holds _lock.
        return !_sessions.TryGetValue(sessionId, out var session)
            ? throw new InvalidOperationException($"Session {sessionId.Value} is not resident.")
            : session;
    }

    private static bool IsArchived(FakeSession session)
    {
        lock (session.Gate)
            return session.Archived;
    }

    private static SessionSummary ToSummary(FakeSession session)
    {
        lock (session.Gate)
        {
            return new SessionSummary(
                session.Id,
                session.Title,
                session.WorkspaceRoot,
                session.Mode,
                ModelSelection: null,
                session.CreatedAt,
                session.UpdatedAt,
                IsArchived: session.Archived);
        }
    }

    private static void ApplyCommand(FakeSession session, ConversationCommand command)
    {
        // Caller holds Gate.
        switch (command)
        {
            case SendTextCommand send:
                ApplySendText(session, send);
                break;
            case StopCommand:
                session.State = session.State with { Phase = TurnPhase.Idle, ActiveTurn = null };
                session.Publish([new StatePatched(session.State)]);
                break;
            case ResolveInteractionCommand:
                session.State = session.State with
                {
                    Phase = TurnPhase.Idle,
                    PendingPermission = null,
                    PendingInteraction = null,
                };
                session.Publish([new StatePatched(session.State)]);
                break;
        }
    }

    private static void ApplySendText(FakeSession session, SendTextCommand command)
    {
        var now = DateTimeOffset.UtcNow;
        var userRow = new UserMessageRow($"row_{session.Revision}_user", command.Text, now);
        var streaming = new AssistantMessageRow(
            $"row_{session.Revision}_assistant",
            StrongId.NewMessageId(),
            [new TextPart(StrongId.NewPartId(), "")],
            IsStreaming: true,
            At: now);
        session.Rows.Add(userRow);
        session.Rows.Add(streaming);
        session.State = session.State with
        {
            Phase = TurnPhase.Streaming,
            ActiveTurn = new TurnId($"turn_{session.Revision}"),
        };
        session.Publish([new RowAppended(userRow), new RowAppended(streaming), new StatePatched(session.State)]);

        var completed = streaming with
        {
            Parts = [new TextPart(StrongId.NewPartId(), $"echo: {command.Text}")],
            IsStreaming = false,
        };
        session.Rows[^1] = completed;
        session.State = session.State with { Phase = TurnPhase.Idle, ActiveTurn = null };
        session.Publish([new RowUpserted(completed), new StatePatched(session.State)]);
    }
}
