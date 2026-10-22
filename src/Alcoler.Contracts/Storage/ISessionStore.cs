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
    ShellEnvironment? Shell = null);

public sealed record StoredMessage(long Sequence, ConversationMessage Message);

public interface ISessionStore
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask UpsertSessionAsync(SessionRecord record, CancellationToken cancellationToken);

    ValueTask<bool> TryUpdateSessionTitleAsync(SessionId sessionId, string title, SessionTitleSource source, CancellationToken cancellationToken);

    ValueTask<SessionRecord?> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken);

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
}
