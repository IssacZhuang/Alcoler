using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;

namespace Alcoler.Contracts.Storage;

public sealed record SessionBranch(
    MessageId CutAfterMessageId,
    IReadOnlyList<MessageId> KeptMessageIds,
    long? CutEventSequence = null);

public sealed record SessionRecord(
    SessionId Id,
    string WorkspaceRoot,
    string Title,
    PermissionMode PermissionMode,
    ModelSelection? ModelSelection,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SessionBranch? Branch = null,
    bool IsArchived = false,
    SessionId? ParentSessionId = null,
    SessionTitleSource TitleSource = SessionTitleSource.Custom,
    ShellEnvironment? Shell = null,
    SessionId? ForkedFromSessionId = null,
    MessageId? ForkedFromMessageId = null,
    PermissionMode? PreviousPermissionMode = null,
    IReadOnlyList<string>? ActiveWorkflows = null,
    bool PlanEnabled = false);

public sealed record StoredMessage(long Sequence, ConversationMessage Message);

public sealed record ArchivedSessionDeletion(int ArchivedTaskCount, IReadOnlyList<SessionId> DeletedSessionIds);

public interface ISessionStore
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask UpsertSessionAsync(SessionRecord record, CancellationToken cancellationToken);

    ValueTask<bool> TryUpdateSessionTitleAsync(SessionId sessionId, string title, SessionTitleSource source, CancellationToken cancellationToken);

    ValueTask<SessionRecord?> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        string? workspaceRoot,
        int limit,
        SessionListKind kind,
        CancellationToken cancellationToken) =>
        kind == SessionListKind.Active
            ? ListSessionsAsync(workspaceRoot, limit, cancellationToken)
            : ValueTask.FromException<IReadOnlyList<SessionSummary>>(
                new NotSupportedException("This session store does not support archived session listings."));

    ValueTask SetSessionArchivedAsync(SessionId sessionId, bool archived, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("This session store does not support archiving sessions."));

    ValueTask<int> ArchiveSessionsUpdatedBeforeAsync(
        DateTimeOffset cutoff,
        IReadOnlyCollection<SessionId> excludedSessionIds,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<int>(new NotSupportedException("This session store does not support automatic session archiving."));

    ValueTask<int> CountArchivedSessionsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<int>(new NotSupportedException("This session store does not support counting archived sessions."));

    ValueTask<ArchivedSessionDeletion> DeleteArchivedSessionsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<ArchivedSessionDeletion>(new NotSupportedException("This session store does not support deleting archived sessions."));

    ValueTask DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken);

    ValueTask AppendMessagesAsync(SessionId sessionId, IReadOnlyList<ConversationMessage> messages, long startingSequence, CancellationToken cancellationToken);

    ValueTask ReplaceMessagesAsync(SessionId sessionId, IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<StoredMessage>> LoadMessagesAsync(SessionId sessionId, CancellationToken cancellationToken);

    ValueTask AppendDurableEventsAsync(IReadOnlyList<SequencedEvent> events, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SequencedEvent>> LoadDurableEventsAsync(SessionId sessionId, CancellationToken cancellationToken);

    ValueTask<long> GetNextMessageSequenceAsync(SessionId sessionId, CancellationToken cancellationToken);
}

public interface IArtifactStore
{
    ValueTask<Tools.ArtifactReference> SaveAsync(
        SessionId sessionId,
        string fileName,
        string mimeType,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken);

    ValueTask<ReadOnlyMemory<byte>?> TryLoadAsync(string artifactUri, CancellationToken cancellationToken);

    ValueTask DeleteSessionArtifactsAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("This artifact store does not support deleting session artifacts."));
}
