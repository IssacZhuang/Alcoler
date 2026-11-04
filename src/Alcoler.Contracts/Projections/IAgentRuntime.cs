using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;

namespace Alcoler.Contracts.Projections;

public interface IUserInteractionPort
{
    ValueTask<InteractionAnswer> AskAsync(InteractionRequest request, CancellationToken cancellationToken);
}

public interface IConversationProjection : IDisposable
{
    ConversationSnapshot Snapshot();

    IAsyncEnumerable<ConversationDelta> Deltas(CancellationToken cancellationToken);

    // Completes once the projection has applied every event up to the sequence; a
    // completed wait guarantees the next Snapshot reflects those events.
    Task WaitUntilSequenceAsync(long sequence, CancellationToken cancellationToken = default);
}

public sealed record SessionActivitySnapshot(TurnPhase Phase, TurnOutcome? LastOutcome)
{
    public SessionId SessionId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string WorkspaceRoot { get; init; } = string.Empty;
    public DateTimeOffset LastActivityUtc { get; init; }
    public bool HasActiveWork { get; init; }
    public bool IsAwaitingConfirmation { get; init; }
    public int RunningJobCount { get; init; }
    public int RunningSubagentCount { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public TurnId? CompletedTurnId { get; init; }
    public DateTimeOffset? ActiveWorkStartedAt { get; init; }
}

public interface ISessionActivityRuntime
{
    IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId);

    bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId);

    ValueTask<bool> TryStopSubagentAsync(SessionId sessionId, string taskId) => ValueTask.FromResult(false);

    SessionActivitySnapshot? GetSessionActivity(SessionId sessionId) => null;

    IReadOnlyList<SessionActivitySnapshot> GetSessionActivities() => [];

    event Action<SessionId>? SessionActivityChanged
    {
        add { }
        remove { }
    }

    IConversationProjection? TryObserveSubagent(SessionId parentSessionId, SessionId childSessionId) => null;
}

public interface IAgentRuntime : IAsyncDisposable
{
    ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken);

    ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken);

    ValueTask<SessionHandle> ForkSessionAsync(
        SessionId sourceSessionId,
        MessageId boundaryMessageId,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<SessionHandle>(
            new NotSupportedException("This runtime does not support forking sessions."));

    ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        string? workspaceRoot,
        int limit,
        SessionListKind kind,
        CancellationToken cancellationToken) =>
        kind == SessionListKind.Active
            ? ListSessionsAsync(workspaceRoot, limit, cancellationToken)
            : ValueTask.FromException<IReadOnlyList<SessionSummary>>(
                new NotSupportedException("This runtime does not support archived session listings."));

    ValueTask SetSessionArchivedAsync(SessionId sessionId, bool archived, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("This runtime does not support archiving sessions."));

    ValueTask<int> ArchiveStaleSessionsAsync(
        TimeSpan olderThan,
        SessionId? keepOpen,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<int>(new NotSupportedException("This runtime does not support automatic session archiving."));

    ValueTask<int> UnloadIdleSessionsAsync(
        TimeSpan idleThreshold,
        SessionId? keepOpen,
        CancellationToken cancellationToken);

    ValueTask<int> CountArchivedSessionsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<int>(new NotSupportedException("This runtime does not support counting archived sessions."));

    ValueTask<int> DeleteArchivedSessionsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<int>(new NotSupportedException("This runtime does not support deleting archived sessions."));

    ValueTask<Commands.CommandAck> SubmitAsync(Commands.ConversationCommand command, CancellationToken cancellationToken);

    event Action<SessionId, string>? SessionTitleChanged
    {
        add { }
        remove { }
    }

    /// <summary>Raised after the persisted session set changes; listeners re-query ListSessionsAsync.</summary>
    event Action? SessionListChanged
    {
        add { }
        remove { }
    }

    event Action<SessionId>? SessionDeleted
    {
        add { }
        remove { }
    }

    IConversationProjection Subscribe(SessionId sessionId);

    IConversationProjection? TryObserve(SessionId sessionId);

    ShellEnvironment? GetSessionShell(SessionId sessionId) => null;
}
