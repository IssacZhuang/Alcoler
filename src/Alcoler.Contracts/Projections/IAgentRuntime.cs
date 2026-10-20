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

public interface ISessionActivityRuntime
{
    IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId);

    bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId);

    IConversationProjection? TryObserveSubagent(SessionId parentSessionId, SessionId childSessionId) => null;
}

public interface IAgentRuntime : IAsyncDisposable
{
    ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken);

    ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken);

    ValueTask<Commands.CommandAck> SubmitAsync(Commands.ConversationCommand command, CancellationToken cancellationToken);

    IConversationProjection Subscribe(SessionId sessionId);

    IConversationProjection? TryObserve(SessionId sessionId);
}
