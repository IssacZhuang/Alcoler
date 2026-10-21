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
}

public sealed record SessionActivitySnapshot(TurnPhase Phase, TurnOutcome? LastOutcome);

public interface ISessionActivityRuntime
{
    IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId);

    bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId);

    SessionActivitySnapshot? GetSessionActivity(SessionId sessionId) => null;

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

    ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken);

    ValueTask<Commands.CommandAck> SubmitAsync(Commands.ConversationCommand command, CancellationToken cancellationToken);

    event Action<SessionId, string>? SessionTitleChanged
    {
        add { }
        remove { }
    }

    IConversationProjection Subscribe(SessionId sessionId);

    IConversationProjection? TryObserve(SessionId sessionId);

    ShellEnvironment? GetSessionShell(SessionId sessionId) => null;
}
