using System.Text.Json;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Alcoler.Kernel.Tests.Storage;

public sealed class SqliteSessionStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string DatabasePath => Path.Combine(_temp.Root, "data", "db.sqlite");

    [Fact]
    public async Task Initialize_IsIdempotent_AndKeepsSingleSchemaVersionWithWal()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await store.InitializeAsync(CancellationToken.None);

        await using var connection = OpenRawConnection();
        Assert.Equal("wal", ExecuteScalar(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, ExecuteScalar(connection, "SELECT COUNT(*) FROM schema_version;"));
        Assert.Equal(1L, ExecuteScalar(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_version;"));
    }

    [Fact]
    public async Task Upsert_InsertsThenReplaces_SessionRecordWithAllFields()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);

        var branch = new SessionBranch(
            new MessageId("msg_cut"),
            [new MessageId("msg_a"), new MessageId("msg_b")]);
        var record = NewRecord(branch: branch, isArchived: true);

        await store.UpsertSessionAsync(record, CancellationToken.None);

        var loaded = await store.GetSessionAsync(record.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(record.Id, loaded.Id);
        Assert.Equal(record.WorkspaceRoot, loaded.WorkspaceRoot);
        Assert.Equal(record.Title, loaded.Title);
        Assert.Equal(record.PermissionMode, loaded.PermissionMode);
        Assert.Equal(record.ModelSelection, loaded.ModelSelection);
        Assert.Equal(record.CreatedAt, loaded.CreatedAt);
        Assert.Equal(record.UpdatedAt, loaded.UpdatedAt);
        Assert.True(loaded.IsArchived);
        Assert.NotNull(loaded.Branch);
        Assert.Equal(branch.CutAfterMessageId, loaded.Branch.CutAfterMessageId);
        Assert.True(
            new HashSet<MessageId>(loaded.Branch.KeptMessageIds).SetEquals(branch.KeptMessageIds));

        await store.UpsertSessionAsync(
            record with { Title = "renamed", PermissionMode = PermissionMode.Yolo, IsArchived = false },
            CancellationToken.None);

        var replaced = await store.GetSessionAsync(record.Id, CancellationToken.None);
        Assert.NotNull(replaced);
        Assert.Equal("renamed", replaced.Title);
        Assert.Equal(PermissionMode.Yolo, replaced.PermissionMode);
        Assert.False(replaced.IsArchived);
    }

    [Fact]
    public async Task GetSession_MissingSession_ReturnsNull()
    {
        await using var store = await NewInitializedStoreAsync();

        var loaded = await store.GetSessionAsync(new SessionId("sess_missing"), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task ListSessions_OrdersByUpdatedAtDesc_AppliesLimitAndWorkspaceFilter()
    {
        await using var store = await NewInitializedStoreAsync();
        var oldest = NewRecord(title: "oldest", updated: Iso("2026-10-01T00:00:00Z"));
        var middle = NewRecord(title: "middle", workspaceRoot: @"C:\ws\beta", updated: Iso("2026-10-02T00:00:00Z"));
        var newest = NewRecord(title: "newest", updated: Iso("2026-10-03T00:00:00Z"));
        foreach (var record in new[] { oldest, middle, newest })
        {
            await store.UpsertSessionAsync(record, CancellationToken.None);
        }

        var topTwo = await store.ListSessionsAsync(null, 2, CancellationToken.None);
        Assert.Equal(
            new[] { newest.Id, middle.Id },
            topTwo.Select(summary => summary.Id).ToArray());
        Assert.Equal(newest.Title, topTwo[0].Title);
        Assert.Equal(newest.UpdatedAt, topTwo[0].UpdatedAt);

        var alphaOnly = await store.ListSessionsAsync(@"C:\ws\alpha", 10, CancellationToken.None);
        Assert.Equal(
            new[] { newest.Id, oldest.Id },
            alphaOnly.Select(summary => summary.Id).ToArray());

        var noneWorkspace = await store.ListSessionsAsync(@"C:\ws\none", 10, CancellationToken.None);
        Assert.Empty(noneWorkspace);

        var zeroLimit = await store.ListSessionsAsync(null, 0, CancellationToken.None);
        Assert.Empty(zeroLimit);
    }

    [Fact]
    public async Task DeleteSession_CascadesMessagesAndDurableEvents_LeavesOtherSessionsIntact()
    {
        await using var store = await NewInitializedStoreAsync();
        var target = NewRecord(title: "target");
        var survivor = NewRecord(title: "survivor", workspaceRoot: @"C:\ws\beta");
        await store.UpsertSessionAsync(target, CancellationToken.None);
        await store.UpsertSessionAsync(survivor, CancellationToken.None);
        await store.AppendMessagesAsync(target.Id, [NewMessage("a"), NewMessage("b")], 0, CancellationToken.None);
        await store.AppendMessagesAsync(survivor.Id, [NewMessage("s")], 0, CancellationToken.None);
        await store.AppendDurableEventsAsync(
            [NewEvent(target.Id, 0, new SessionModeChangedEvent(PermissionMode.Plan))],
            CancellationToken.None);

        await store.DeleteSessionAsync(target.Id, CancellationToken.None);

        Assert.Null(await store.GetSessionAsync(target.Id, CancellationToken.None));
        Assert.Empty(await store.LoadMessagesAsync(target.Id, CancellationToken.None));
        Assert.Empty(await store.LoadDurableEventsAsync(target.Id, CancellationToken.None));
        Assert.NotNull(await store.GetSessionAsync(survivor.Id, CancellationToken.None));
        Assert.Single(await store.LoadMessagesAsync(survivor.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AppendMessages_LoadMessages_RoundTripsAllPartTypesInOrder()
    {
        await using var store = await NewInitializedStoreAsync();
        var sessionId = StrongId.NewSessionId();
        var input = JsonSerializer.SerializeToElement(new { path = "src/a.cs", line = 12 });
        var message = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [
                new TextPart(StrongId.NewPartId(), "answer"),
                new ReasoningPart(StrongId.NewPartId(), "thinking", ProviderSignature: "sig-1"),
                new ToolCallPart(StrongId.NewPartId(), new ToolCallId("call_x"), "Read", input),
                new ToolResultPart(
                    StrongId.NewPartId(),
                    new ToolCallId("call_x"),
                    [
                        new ToolResultText("stdout"),
                        new ToolResultMedia("alcoler-artifact://s1/media.png", "image/png", "media.png"),
                    ],
                    IsError: true),
                new FilePart(
                    StrongId.NewPartId(), "alcoler-artifact://s1/doc.pdf", "application/pdf", "doc.pdf",
                    ProviderReady: false),
                new AttachmentPart(
                    StrongId.NewPartId(), AttachmentKind.SystemReminder, "reminder", ProviderVisible: false),
            ],
            Iso("2026-10-19T09:30:00Z"));

        await store.AppendMessagesAsync(sessionId, [message], 0, CancellationToken.None);

        var loaded = await store.LoadMessagesAsync(sessionId, CancellationToken.None);
        var stored = Assert.Single(loaded);
        Assert.Equal(0L, stored.Sequence);
        Assert.Equal(message.Id, stored.Message.Id);
        Assert.Equal(MessageRole.Assistant, stored.Message.Role);
        Assert.Equal(message.CreatedAt, stored.Message.CreatedAt);
        Assert.Equal(6, stored.Message.Parts.Count);

        Assert.Equal("answer", Assert.IsType<TextPart>(stored.Message.Parts[0]).Text);

        var reasoning = Assert.IsType<ReasoningPart>(stored.Message.Parts[1]);
        Assert.Equal("thinking", reasoning.Text);
        Assert.Equal("sig-1", reasoning.ProviderSignature);

        var toolCall = Assert.IsType<ToolCallPart>(stored.Message.Parts[2]);
        Assert.Equal(new ToolCallId("call_x"), toolCall.ToolCallId);
        Assert.Equal("Read", toolCall.ToolName);
        Assert.Equal(input.GetRawText(), toolCall.Input.GetRawText());

        var toolResult = Assert.IsType<ToolResultPart>(stored.Message.Parts[3]);
        Assert.Equal(new ToolCallId("call_x"), toolResult.ToolCallId);
        Assert.True(toolResult.IsError);
        Assert.Equal("stdout", Assert.IsType<ToolResultText>(toolResult.Content[0]).Text);
        var media = Assert.IsType<ToolResultMedia>(toolResult.Content[1]);
        Assert.Equal("alcoler-artifact://s1/media.png", media.ArtifactUri);
        Assert.Equal("image/png", media.MimeType);
        Assert.Equal("media.png", media.FileName);

        var file = Assert.IsType<FilePart>(stored.Message.Parts[4]);
        Assert.Equal("alcoler-artifact://s1/doc.pdf", file.ArtifactUri);
        Assert.Equal("application/pdf", file.MimeType);
        Assert.Equal("doc.pdf", file.FileName);
        Assert.False(file.ProviderReady);

        var attachment = Assert.IsType<AttachmentPart>(stored.Message.Parts[5]);
        Assert.Equal(AttachmentKind.SystemReminder, attachment.Kind);
        Assert.Equal("reminder", attachment.Content);
        Assert.False(attachment.ProviderVisible);
    }

    [Fact]
    public async Task AppendMessages_SequenceIsMonotonicAcrossAppends()
    {
        await using var store = await NewInitializedStoreAsync();
        var sessionId = StrongId.NewSessionId();

        await store.AppendMessagesAsync(sessionId, [NewMessage("a"), NewMessage("b")], 0, CancellationToken.None);
        Assert.Equal(2L, await store.GetNextMessageSequenceAsync(sessionId, CancellationToken.None));
        await store.AppendMessagesAsync(sessionId, [NewMessage("c")], 2, CancellationToken.None);
        Assert.Equal(3L, await store.GetNextMessageSequenceAsync(sessionId, CancellationToken.None));

        var loaded = await store.LoadMessagesAsync(sessionId, CancellationToken.None);
        Assert.Equal([0L, 1L, 2L], loaded.Select(stored => stored.Sequence).ToArray());
        Assert.Equal(["a", "b", "c"], loaded.Select(stored => ((TextPart)stored.Message.Parts[0]).Text).ToArray());
    }

    [Fact]
    public async Task GetNextMessageSequence_UnknownSession_ReturnsZero()
    {
        await using var store = await NewInitializedStoreAsync();

        var next = await store.GetNextMessageSequenceAsync(new SessionId("sess_unknown"), CancellationToken.None);

        Assert.Equal(0L, next);
    }

    [Fact]
    public async Task AppendDurableEvents_LoadDurableEvents_RoundTripsInSequenceOrder()
    {
        await using var store = await NewInitializedStoreAsync();
        var sessionId = StrongId.NewSessionId();
        var input = JsonSerializer.SerializeToElement(new { command = "git status" });
        var boundary = new CompactBoundary(
            "b1", CompactTrigger.Auto, 180_000, 40_000,
            new MessageId("msg_last"), null, new MessageId("msg_tail"), Iso("2026-10-19T09:02:00Z"));
        var events = new List<SequencedEvent>
        {
            NewEvent(sessionId, 11, new CompactBoundaryAppliedEvent(boundary)),
            NewEvent(sessionId, 7, new ToolCallStartedEvent(
                new TurnId("turn_1"), new ToolCallId("call_1"), "Bash", input)),
            NewEvent(sessionId, 3, new SessionModeChangedEvent(PermissionMode.Plan)),
        };

        await store.AppendDurableEventsAsync(events, CancellationToken.None);

        var loaded = await store.LoadDurableEventsAsync(sessionId, CancellationToken.None);
        Assert.Equal([3L, 7L, 11L], loaded.Select(sequenced => sequenced.Sequence).ToArray());
        Assert.All(loaded, sequenced => Assert.Equal(sessionId, sequenced.SessionId));
        Assert.Equal(Iso("2026-10-19T09:00:00Z"), loaded[0].OccurredAt);

        Assert.Equal(PermissionMode.Plan, Assert.IsType<SessionModeChangedEvent>(loaded[0].Event).Mode);

        var started = Assert.IsType<ToolCallStartedEvent>(loaded[1].Event);
        Assert.Equal("Bash", started.ToolName);
        Assert.Equal(new TurnId("turn_1"), started.TurnId);
        Assert.Equal(new ToolCallId("call_1"), started.ToolCallId);
        Assert.Equal(input.GetRawText(), started.Input.GetRawText());

        var applied = Assert.IsType<CompactBoundaryAppliedEvent>(loaded[2].Event);
        Assert.Equal("b1", applied.Boundary.BoundaryId);
        Assert.Equal(CompactTrigger.Auto, applied.Boundary.Trigger);
        Assert.Equal(180_000, applied.Boundary.PreCompactTokens);
        Assert.Equal(40_000, applied.Boundary.PostCompactTokens);
        Assert.Equal(new MessageId("msg_last"), applied.Boundary.LastSummarizedMessageId);
        Assert.Equal(new MessageId("msg_tail"), applied.Boundary.PreservedTailId);
    }

    [Fact]
    public async Task Restart_WithNewStoreInstance_PreservesSessionsMessagesAndNextSequence()
    {
        var sessionId = StrongId.NewSessionId();
        var record = NewRecord(title: "persisted");
        await using (var first = await NewInitializedStoreAsync())
        {
            await first.UpsertSessionAsync(record, CancellationToken.None);
            await first.AppendMessagesAsync(
                sessionId, [NewMessage("a"), NewMessage("b"), NewMessage("c")], 0, CancellationToken.None);
            await first.AppendDurableEventsAsync(
                [NewEvent(sessionId, 0, new SessionTitleChangedEvent("persisted"))],
                CancellationToken.None);
        }

        await using var second = new SqliteSessionStore(DatabasePath);
        await second.InitializeAsync(CancellationToken.None);

        Assert.Equal(3L, await second.GetNextMessageSequenceAsync(sessionId, CancellationToken.None));
        Assert.Equal(3, (await second.LoadMessagesAsync(sessionId, CancellationToken.None)).Count);
        var restored = await second.GetSessionAsync(record.Id, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal("persisted", restored.Title);
        var events = await second.LoadDurableEventsAsync(sessionId, CancellationToken.None);
        Assert.Equal("persisted", Assert.IsType<SessionTitleChangedEvent>(Assert.Single(events).Event).Title);
    }

    [Fact]
    public async Task Operations_BeforeInitialize_ThrowInvalidOperationException()
    {
        await using var store = new SqliteSessionStore(DatabasePath);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetSessionAsync(new SessionId("sess_x"), CancellationToken.None).AsTask());
    }

    public void Dispose() => _temp.Dispose();

    private async Task<SqliteSessionStore> NewInitializedStoreAsync()
    {
        var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    private static SessionRecord NewRecord(
        string title = "title",
        string workspaceRoot = @"C:\ws\alpha",
        DateTimeOffset? updated = null,
        SessionBranch? branch = null,
        bool isArchived = false) => new(
        StrongId.NewSessionId(),
        workspaceRoot,
        title,
        PermissionMode.Build,
        new ModelSelection("anthropic", "claude-4", new ModelCallOptions(ReasoningLevel: "high")),
        Iso("2026-10-01T10:00:00Z"),
        updated ?? Iso("2026-10-02T10:00:00Z"),
        branch,
        isArchived);

    private static ConversationMessage NewMessage(string text) => new(
        StrongId.NewMessageId(),
        MessageRole.User,
        [new TextPart(StrongId.NewPartId(), text)],
        Iso("2026-10-19T09:00:00Z"));

    private static SequencedEvent NewEvent(SessionId sessionId, long sequence, SessionEvent sessionEvent) => new(
        sequence,
        sessionId,
        Iso("2026-10-19T09:00:00Z"),
        sessionEvent);

    private static DateTimeOffset Iso(string value) => DateTimeOffset.Parse(value, null,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private SqliteConnection OpenRawConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static object ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }
}
