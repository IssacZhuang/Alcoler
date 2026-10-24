using System.Text.Json;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Alcoler.Kernel.Tests.Storage;

public sealed class SqliteSessionStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string DatabasePath => Path.Combine(_temp.Root, "data", "db.sqlite");

    [Fact]
    public async Task Initialize_IsIdempotent_AndKeepsSchemaVersionsWithWal()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await store.InitializeAsync(CancellationToken.None);

        await using var connection = OpenRawConnection();
        Assert.Equal("wal", ExecuteScalar(connection, "PRAGMA journal_mode;"));
        Assert.Equal(8L, ExecuteScalar(connection, "SELECT COUNT(*) FROM schema_version;"));
        Assert.Equal(8L, ExecuteScalar(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_version;"));
        using var column = connection.CreateCommand();
        column.CommandText = "SELECT type, \"notnull\", dflt_value FROM pragma_table_info('session') WHERE name = 'title_source'";
        using var reader = column.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("TEXT", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal("'legacy'", reader.GetString(2));
    }

    [Theory]
    [InlineData(ShellDialect.Cmd, @"C:\Windows\System32\cmd.exe")]
    [InlineData(ShellDialect.GitBash, @"D:\Portable Git\bin\bash.exe")]
    [InlineData(ShellDialect.Posix, "/bin/sh")]
    public async Task Shell_RoundTripsAcrossUpsertAndRestart_AndCanBeCleared(ShellDialect dialect, string fileName)
    {
        var environment = new ShellEnvironment(fileName, dialect);
        var record = NewRecord() with { Shell = environment, ParentSessionId = StrongId.NewSessionId(), TitleSource = SessionTitleSource.Generated };
        await using (var store = await NewInitializedStoreAsync())
        {
            await store.UpsertSessionAsync(record, CancellationToken.None);
            Assert.Equal(record, await store.GetSessionAsync(record.Id, CancellationToken.None));
            await using var connection = OpenRawConnection();
            Assert.Equal(AlcolerJson.Serialize(environment), ExecuteScalar(connection, "SELECT shell_json FROM session"));
        }

        await using (var store = await NewInitializedStoreAsync())
        {
            var restored = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
            Assert.Equal(record, restored);
            await store.UpsertSessionAsync(restored with { Shell = null }, CancellationToken.None);
        }

        await using var reopened = await NewInitializedStoreAsync();
        Assert.Equal(record with { Shell = null }, await reopened.GetSessionAsync(record.Id, CancellationToken.None));
        await using var raw = OpenRawConnection();
        Assert.Equal(DBNull.Value, ExecuteScalar(raw, "SELECT shell_json FROM session"));
    }

    [Fact]
    public async Task Shell_NullLegacyRecordsAndJsonRemainUnselected()
    {
        var record = NewRecord();
        await using var store = await NewInitializedStoreAsync();
        await store.UpsertSessionAsync(record, CancellationToken.None);
        Assert.Null((await store.GetSessionAsync(record.Id, CancellationToken.None))!.Shell);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(AlcolerJson.Serialize(record))!.AsObject();
        payload.Remove("shell");
        Assert.Equal(record, AlcolerJson.Deserialize<SessionRecord>(payload.ToJsonString()));
    }

    [Fact]
    public async Task Initialize_VersionThree_AddsNullableShellAndRetainsExistingMetadata()
    {
        var branch = new SessionBranch(new MessageId("msg_cut"), [new MessageId("msg_kept")]);
        var record = NewRecord(title: "Retain generated title", branch: branch, isArchived: true) with
        {
            ParentSessionId = StrongId.NewSessionId(),
            PermissionMode = PermissionMode.Yolo,
            TitleSource = SessionTitleSource.Generated,
        };
        CreateVersionTwoDatabase([record], [(record.Id, [NewMessage("Do not backfill this title")])]);
        using (var connection = OpenRawConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE session ADD COLUMN title_source TEXT NOT NULL DEFAULT 'legacy';
                UPDATE session SET title_source = 'Generated';
                INSERT INTO schema_version VALUES (3);
                """;
            command.ExecuteNonQuery();
        }

        await using (var store = await NewInitializedStoreAsync())
        {
            AssertMetadata(Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None)));
            Assert.Single(await store.LoadMessagesAsync(record.Id, CancellationToken.None));
        }
        await using var reopened = await NewInitializedStoreAsync();
        AssertMetadata(Assert.IsType<SessionRecord>(await reopened.GetSessionAsync(record.Id, CancellationToken.None)));
        await using var raw = OpenRawConnection();
        Assert.Equal(8L, ExecuteScalar(raw, "SELECT MAX(version) FROM schema_version"));
        using var previousModeColumn = raw.CreateCommand();
        previousModeColumn.CommandText = "SELECT type FROM pragma_table_info('session') WHERE name = 'previous_permission_mode'";
        Assert.Equal("TEXT", previousModeColumn.ExecuteScalar());
        using var column = raw.CreateCommand();
        column.CommandText = "SELECT type, \"notnull\" FROM pragma_table_info('session') WHERE name = 'shell_json'";
        using var reader = column.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("TEXT", reader.GetString(0));
        Assert.Equal(0L, reader.GetInt64(1));
        using var forkColumn = raw.CreateCommand();
        forkColumn.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session') WHERE name LIKE 'forked_from_%'";
        Assert.Equal(2L, forkColumn.ExecuteScalar());

        void AssertMetadata(SessionRecord loaded)
        {
            Assert.Equal(record with { Branch = null }, loaded with { Branch = null });
            Assert.NotNull(loaded.Branch);
            Assert.Equal(branch.CutAfterMessageId, loaded.Branch.CutAfterMessageId);
            Assert.Equal(branch.KeptMessageIds, loaded.Branch.KeptMessageIds);
            Assert.Null(loaded.Branch.CutEventSequence);
            Assert.Null(loaded.Shell);
        }
    }

    [Fact]
    public async Task PlanEnabled_RoundTripsAcrossRestart()
    {
        var record = NewRecord() with { PermissionMode = PermissionMode.Edit, PlanEnabled = true };
        await using (var store = await NewInitializedStoreAsync())
        {
            await store.UpsertSessionAsync(record, CancellationToken.None);
        }

        await using var reopened = await NewInitializedStoreAsync();
        var loaded = Assert.IsType<SessionRecord>(await reopened.GetSessionAsync(record.Id, CancellationToken.None));
        Assert.Equal(PermissionMode.Edit, loaded.PermissionMode);
        Assert.True(loaded.PlanEnabled);

        var summary = Assert.Single(await reopened.ListSessionsAsync(null, 10, CancellationToken.None));
        Assert.Equal(PermissionMode.Edit, summary.PermissionMode);
        Assert.True(summary.PlanEnabled);
    }

    [Fact]
    public async Task Upsert_InsertsThenReplaces_SessionRecordWithAllFields()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);

        var branch = new SessionBranch(
            new MessageId("msg_cut"),
            [new MessageId("msg_a"), new MessageId("msg_b")],
            CutEventSequence: 42);
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
        Assert.Equal(branch.CutEventSequence, loaded.Branch.CutEventSequence);

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
    public async Task ForkMetadata_RoundTripsAcrossUpsertAndRestart_AndCanBeCleared()
    {
        var sourceId = StrongId.NewSessionId();
        var boundaryId = StrongId.NewMessageId();
        var record = NewRecord() with
        {
            ForkedFromSessionId = sourceId,
            ForkedFromMessageId = boundaryId,
        };
        await using (var store = await NewInitializedStoreAsync())
        {
            await store.UpsertSessionAsync(record, CancellationToken.None);
            var loaded = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
            Assert.Equal(sourceId, loaded.ForkedFromSessionId);
            Assert.Equal(boundaryId, loaded.ForkedFromMessageId);
            await using var connection = OpenRawConnection();
            Assert.Equal(sourceId.Value, ExecuteScalar(connection, "SELECT forked_from_session_id FROM session"));
            Assert.Equal(boundaryId.Value, ExecuteScalar(connection, "SELECT forked_from_message_id FROM session"));
        }

        await using (var store = await NewInitializedStoreAsync())
        {
            var loaded = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
            Assert.Equal(record, loaded);
            await store.UpsertSessionAsync(
                loaded with { ForkedFromSessionId = null, ForkedFromMessageId = null },
                CancellationToken.None);
        }

        await using var reopened = await NewInitializedStoreAsync();
        var cleared = Assert.IsType<SessionRecord>(await reopened.GetSessionAsync(record.Id, CancellationToken.None));
        Assert.Null(cleared.ForkedFromSessionId);
        Assert.Null(cleared.ForkedFromMessageId);
    }

    [Theory]
    [InlineData(SessionTitleSource.Default)]
    [InlineData(SessionTitleSource.FirstInput)]
    [InlineData(SessionTitleSource.Generated)]
    [InlineData(SessionTitleSource.Custom)]
    public async Task TitleSource_RoundTripsAsText_AcrossUpsertAndRestart(SessionTitleSource source)
    {
        var record = NewRecord(title: "alpha") with { TitleSource = source };
        await using (var store = await NewInitializedStoreAsync())
        {
            await store.UpsertSessionAsync(record, CancellationToken.None);
            await store.AppendMessagesAsync(record.Id, [NewMessage("Do not backfill a version-three session")], 0, CancellationToken.None);
            var loaded = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
            Assert.Equal(source, loaded.TitleSource);
            await using var connection = OpenRawConnection();
            Assert.Equal(source.ToString(), ExecuteScalar(connection, "SELECT title_source FROM session"));
        }

        await using (var store = await NewInitializedStoreAsync())
        {
            var loaded = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
            Assert.Equal(record, loaded);
            await store.UpsertSessionAsync(loaded with { Title = "replacement", TitleSource = SessionTitleSource.Custom }, CancellationToken.None);
        }

        await using var reopened = await NewInitializedStoreAsync();
        var replaced = Assert.IsType<SessionRecord>(await reopened.GetSessionAsync(record.Id, CancellationToken.None));
        Assert.Equal("replacement", replaced.Title);
        Assert.Equal(SessionTitleSource.Custom, replaced.TitleSource);
    }

    [Fact]
    public async Task TitleUpdate_PreservesOtherMetadata_ProtectsCustomAndDoesNotRecreateDeletedSessions()
    {
        await using var store = await NewInitializedStoreAsync();
        var record = NewRecord() with { TitleSource = SessionTitleSource.FirstInput };
        await store.UpsertSessionAsync(record, CancellationToken.None);

        Assert.True(await store.TryUpdateSessionTitleAsync(record.Id, "Generated task", SessionTitleSource.Generated, CancellationToken.None));
        Assert.Equal(record with { Title = "Generated task", TitleSource = SessionTitleSource.Generated },
            await store.GetSessionAsync(record.Id, CancellationToken.None));
        Assert.True(await store.TryUpdateSessionTitleAsync(record.Id, "Manual task", SessionTitleSource.Custom, CancellationToken.None));
        Assert.False(await store.TryUpdateSessionTitleAsync(record.Id, "Late automatic task", SessionTitleSource.Generated, CancellationToken.None));
        Assert.Equal(record with { Title = "Manual task", TitleSource = SessionTitleSource.Custom },
            await store.GetSessionAsync(record.Id, CancellationToken.None));

        await store.DeleteSessionAsync(record.Id, CancellationToken.None);
        Assert.False(await store.TryUpdateSessionTitleAsync(record.Id, "Deleted task", SessionTitleSource.Generated, CancellationToken.None));
        Assert.Null(await store.GetSessionAsync(record.Id, CancellationToken.None));
    }

    [Fact]
    public void SessionRecord_TitleSource_DefaultsToCustomForExistingRecordsAndLegacyJson()
    {
        var record = NewRecord();
        Assert.Equal(SessionTitleSource.Custom, record.TitleSource);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(AlcolerJson.Serialize(record))!.AsObject();
        payload.Remove("titleSource");
        Assert.Equal(record, AlcolerJson.Deserialize<SessionRecord>(payload.ToJsonString()));
        foreach (var source in Enum.GetValues<SessionTitleSource>())
        {
            var sourced = record with { TitleSource = source };
            Assert.Equal(sourced, AlcolerJson.Deserialize<SessionRecord>(AlcolerJson.Serialize(sourced)));
        }
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
    public async Task ListSessions_FiltersArchivedByKind()
    {
        await using var store = await NewInitializedStoreAsync();
        var active = NewRecord(title: "active", updated: Iso("2026-10-03T00:00:00Z"));
        var archived = NewRecord(title: "archived", updated: Iso("2026-10-02T00:00:00Z"), isArchived: true);
        var otherArchived = NewRecord(
            title: "other", workspaceRoot: @"C:\ws\beta", updated: Iso("2026-10-01T00:00:00Z"), isArchived: true);
        foreach (var record in new[] { active, archived, otherArchived })
        {
            await store.UpsertSessionAsync(record, CancellationToken.None);
        }

        var activePage = await store.ListSessionsAsync(
            null, 10, SessionListKind.Active, CancellationToken.None);
        Assert.Equal(new[] { active.Id }, activePage.Select(summary => summary.Id).ToArray());
        Assert.All(activePage, summary => Assert.False(summary.IsArchived));

        var archivedPage = await store.ListSessionsAsync(
            null, 10, SessionListKind.Archived, CancellationToken.None);
        Assert.Equal(
            new[] { archived.Id, otherArchived.Id },
            archivedPage.Select(summary => summary.Id).ToArray());
        Assert.All(archivedPage, summary => Assert.True(summary.IsArchived));

        var archivedInBeta = await store.ListSessionsAsync(
            @"C:\ws\beta", 10, SessionListKind.Archived, CancellationToken.None);
        Assert.Equal(new[] { otherArchived.Id }, archivedInBeta.Select(summary => summary.Id).ToArray());
    }

    [Fact]
    public async Task SetSessionArchived_RoundTripsWithoutChangingUpdatedAt()
    {
        await using var store = await NewInitializedStoreAsync();
        var record = NewRecord(updated: Iso("2026-10-02T10:00:00Z"));
        await store.UpsertSessionAsync(record, CancellationToken.None);

        await store.SetSessionArchivedAsync(record.Id, true, CancellationToken.None);

        var archived = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
        Assert.True(archived.IsArchived);
        Assert.Equal(record.UpdatedAt, archived.UpdatedAt);
        Assert.Empty(await store.ListSessionsAsync(null, 10, CancellationToken.None));
        Assert.Single(await store.ListSessionsAsync(
            null, 10, SessionListKind.Archived, CancellationToken.None));

        await store.SetSessionArchivedAsync(record.Id, false, CancellationToken.None);

        var restored = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
        Assert.False(restored.IsArchived);
        Assert.Single(await store.ListSessionsAsync(null, 10, CancellationToken.None));
        Assert.Empty(await store.ListSessionsAsync(
            null, 10, SessionListKind.Archived, CancellationToken.None));
    }

    [Fact]
    public async Task ArchiveSessionsUpdatedBefore_ArchivesOnlyStaleTopLevelSessions()
    {
        await using var store = await NewInitializedStoreAsync();
        var stale = NewRecord(title: "stale", updated: Iso("2026-10-01T00:00:00Z"));
        var recent = NewRecord(title: "recent", updated: Iso("2026-10-05T00:00:00Z"));
        var excluded = NewRecord(title: "excluded", updated: Iso("2026-10-01T00:00:00Z"));
        var alreadyArchived = NewRecord(
            title: "already", updated: Iso("2026-10-01T00:00:00Z"), isArchived: true);
        var child = NewRecord(title: "child", updated: Iso("2026-10-01T00:00:00Z"))
            with { ParentSessionId = stale.Id };
        foreach (var record in new[] { stale, recent, excluded, alreadyArchived, child })
        {
            await store.UpsertSessionAsync(record, CancellationToken.None);
        }

        var count = await store.ArchiveSessionsUpdatedBeforeAsync(
            Iso("2026-10-03T00:00:00Z"), [excluded.Id], CancellationToken.None);

        Assert.Equal(1, count);
        Assert.True(Assert.IsType<SessionRecord>(
            await store.GetSessionAsync(stale.Id, CancellationToken.None)).IsArchived);
        Assert.False(Assert.IsType<SessionRecord>(
            await store.GetSessionAsync(recent.Id, CancellationToken.None)).IsArchived);
        Assert.False(Assert.IsType<SessionRecord>(
            await store.GetSessionAsync(excluded.Id, CancellationToken.None)).IsArchived);
        Assert.False(Assert.IsType<SessionRecord>(
            await store.GetSessionAsync(child.Id, CancellationToken.None)).IsArchived);
        Assert.Equal(stale.UpdatedAt, Assert.IsType<SessionRecord>(
            await store.GetSessionAsync(stale.Id, CancellationToken.None)).UpdatedAt);
    }

    [Fact]
    public async Task DeleteArchivedSessions_RemovesArchivedRootsAndTheirDescendantsOnly()
    {
        await using var store = await NewInitializedStoreAsync();
        var archivedRoot = NewRecord(title: "archived", isArchived: true);
        var archivedChild = NewRecord(title: "archived child") with { ParentSessionId = archivedRoot.Id };
        var archivedGrandchild = NewRecord(title: "archived grandchild") with
        {
            ParentSessionId = archivedChild.Id,
        };
        var activeRoot = NewRecord(title: "active");
        var activeChild = NewRecord(title: "active child") with { ParentSessionId = activeRoot.Id };
        foreach (var record in new[] { archivedRoot, archivedChild, archivedGrandchild, activeRoot, activeChild })
        {
            await store.UpsertSessionAsync(record, CancellationToken.None);
        }

        await store.AppendMessagesAsync(
            archivedRoot.Id, [NewMessage("archived")], 0, CancellationToken.None);
        await store.AppendMessagesAsync(
            archivedChild.Id, [NewMessage("child")], 0, CancellationToken.None);
        await store.AppendMessagesAsync(
            activeRoot.Id, [NewMessage("active")], 0, CancellationToken.None);
        await store.AppendDurableEventsAsync(
            [
                NewEvent(archivedRoot.Id, 1, new SessionStartedEvent(
                    archivedRoot.WorkspaceRoot, archivedRoot.Title, PermissionMode.Build, null)),
            ],
            CancellationToken.None);

        Assert.Equal(1, await store.CountArchivedSessionsAsync(CancellationToken.None));

        var deletion = await store.DeleteArchivedSessionsAsync(CancellationToken.None);

        Assert.Equal(1, deletion.ArchivedTaskCount);
        Assert.Equal(
            new[] { archivedGrandchild.Id.Value, archivedChild.Id.Value, archivedRoot.Id.Value }
                .Order(StringComparer.Ordinal),
            deletion.DeletedSessionIds.Select(id => id.Value).Order(StringComparer.Ordinal));
        Assert.Null(await store.GetSessionAsync(archivedRoot.Id, CancellationToken.None));
        Assert.Null(await store.GetSessionAsync(archivedChild.Id, CancellationToken.None));
        Assert.Null(await store.GetSessionAsync(archivedGrandchild.Id, CancellationToken.None));
        Assert.NotNull(await store.GetSessionAsync(activeRoot.Id, CancellationToken.None));
        Assert.NotNull(await store.GetSessionAsync(activeChild.Id, CancellationToken.None));
        Assert.Empty(await store.LoadMessagesAsync(archivedRoot.Id, CancellationToken.None));
        Assert.Empty(await store.LoadMessagesAsync(archivedChild.Id, CancellationToken.None));
        Assert.Empty(await store.LoadDurableEventsAsync(archivedRoot.Id, CancellationToken.None));
        Assert.Single(await store.LoadMessagesAsync(activeRoot.Id, CancellationToken.None));
        Assert.Equal(0, await store.CountArchivedSessionsAsync(CancellationToken.None));

        var second = await store.DeleteArchivedSessionsAsync(CancellationToken.None);
        Assert.Equal(0, second.ArchivedTaskCount);
        Assert.Empty(second.DeletedSessionIds);

        await using var raw = OpenRawConnection();
        Assert.Equal(2L, ExecuteScalar(raw, "SELECT COUNT(*) FROM session"));
    }

    [Fact]
    public async Task ListSessions_ExcludesChildrenBeforeLimitAndWorkspaceFilter_AfterRestart()
    {
        var older = NewRecord(title: "older", updated: Iso("2026-10-01T00:00:00Z"));
        var newer = NewRecord(title: "newer", updated: Iso("2026-10-02T00:00:00Z"));
        var other = NewRecord(workspaceRoot: @"C:\ws\beta");
        var child = NewRecord(title: "child", updated: Iso("2026-10-03T00:00:00Z")) with
        {
            ParentSessionId = newer.Id,
        };
        var isolatedChild = NewRecord(workspaceRoot: @"C:\ws\children") with { ParentSessionId = newer.Id };
        await using (var store = await NewInitializedStoreAsync())
        {
            foreach (var record in new[] { older, newer, other, child, isolatedChild })
                await store.UpsertSessionAsync(record, CancellationToken.None);
            await store.UpsertSessionAsync(child with { Title = "renamed child" }, CancellationToken.None);
        }

        await using var reopened = await NewInitializedStoreAsync();
        var alpha = await reopened.ListSessionsAsync(@"C:\ws\alpha", 2, CancellationToken.None);
        Assert.Equal(new[] { newer.Id, older.Id }, alpha.Select(summary => summary.Id));
        Assert.All(alpha, summary => Assert.Null(summary.ParentSessionId));
        Assert.Equal(other.Id, Assert.Single(await reopened.ListSessionsAsync(null, 1, CancellationToken.None)).Id);
        Assert.Empty(await reopened.ListSessionsAsync(@"C:\ws\children", 1, CancellationToken.None));
        var loaded = Assert.IsType<SessionRecord>(await reopened.GetSessionAsync(child.Id, CancellationToken.None));
        Assert.Equal(newer.Id, loaded.ParentSessionId);
        Assert.Equal("renamed child", loaded.Title);
    }

    [Fact]
    public async Task Initialize_VersionOne_BackfillsOnlyKnownSubagentStarts_AndKeepsDirectChildAccess()
    {
        var parent = NewRecord(title: "parent");
        var child = NewRecord(title: "same title");
        var normal = NewRecord(title: "same title") with { PermissionMode = PermissionMode.Yolo };
        var unknown = NewRecord(title: "unknown event");
        SessionEvent started = new SubagentStartedEvent(new SubagentState(
            StrongId.NewSubagentId(), child.Id, "same title", SubagentStatus.Running, null, parent.CreatedAt, null));
        var self = new SubagentStartedEvent(((SubagentStartedEvent)started).State with { ChildSessionId = parent.Id });
        CreateVersionOneDatabase([parent, child, normal, unknown], parent.Id,
        [
            AlcolerJson.Serialize(started),
            AlcolerJson.Serialize<SessionEvent>(self),
            $$$"""{"$type":"future_subagent_started","state":{"childSessionId":"{{{unknown.Id.Value}}}"}}""",
            $$$"""{"$type":"subagent_started","state":{"childSessionId":"{{{normal.Id.Value}}}","status":"FutureStatus"}}""",
            "{\"$type\":\"subagent_started\",\"state\":null}",
            "{\"$type\":\"subagent_started\",\"state\":{}}",
            "not json",
        ]);

        await using (var migrated = await NewInitializedStoreAsync())
        {
            var summaries = await migrated.ListSessionsAsync(null, 10, CancellationToken.None);
            Assert.Equal(new[] { parent.Id, normal.Id, unknown.Id }.OrderBy(id => id.Value),
                summaries.Select(summary => summary.Id).OrderBy(id => id.Value));
            var restored = Assert.IsType<SessionRecord>(await migrated.GetSessionAsync(child.Id, CancellationToken.None));
            Assert.Equal(parent.Id, restored.ParentSessionId);
            Assert.Null((await migrated.GetSessionAsync(parent.Id, CancellationToken.None))!.ParentSessionId);
            Assert.Null((await migrated.GetSessionAsync(normal.Id, CancellationToken.None))!.ParentSessionId);
        }

        await using var reopened = await NewInitializedStoreAsync();
        Assert.Equal(3, (await reopened.ListSessionsAsync(null, 10, CancellationToken.None)).Count);
        Assert.Equal(parent.Id, (await reopened.GetSessionAsync(child.Id, CancellationToken.None))!.ParentSessionId);
    }

    [Fact]
    public async Task Initialize_VersionTwo_RepairsDefaultRoots_AndPreservesExplicitRenamedAndChildTitlesAfterRestart()
    {
        var root = NewRecord(title: "alpha", workspaceRoot: @"C:\ws\alpha\", isArchived: true);
        var otherRoot = NewRecord(title: "alpha");
        var fallback = NewRecord(title: "New session");
        var empty = NewRecord(title: "alpha");
        var emptyFallback = NewRecord(title: "New session");
        var explicitTitle = NewRecord(title: "Explicit title");
        var renamed = NewRecord(title: "alpha");
        var child = NewRecord(title: "alpha") with { ParentSessionId = root.Id };
        var fallbackChild = NewRecord(title: "New session") with { ParentSessionId = root.Id };
        var records = new[] { root, otherRoot, fallback, empty, emptyFallback, explicitTitle, renamed, child, fallbackChild };
        var firstInput = NewMessage(" \t Fix\u2003the\n") with
        {
            Parts = [new TextPart(StrongId.NewPartId(), " \t Fix\u2003the\n"), new TextPart(StrongId.NewPartId(), "sidebar \r\n naming  ")],
        };
        CreateVersionTwoDatabase(records,
        [
            (root.Id, [firstInput, NewMessage("Later input must not win")]),
            (otherRoot.Id, [NewMessage("A second root in the same workspace")]),
            (fallback.Id, [NewMessage("First input for the fallback")]),
            (emptyFallback.Id, [NewMessage(" \t\r\n")]),
            (explicitTitle.Id, [NewMessage("Do not replace the explicit title")]),
            (renamed.Id, [NewMessage("Do not replace a manual workspace title")]),
            (child.Id, [NewMessage("Do not replace a child title")]),
            (fallbackChild.Id, [NewMessage("Do not replace a fallback child title")]),
        ], [NewEvent(renamed.Id, 3, new SessionTitleChangedEvent("alpha"))]);
        var expected = records.Select(record => record.Id == root.Id
            ? record with { Title = "Fix the sidebar naming", TitleSource = SessionTitleSource.FirstInput }
            : record.Id == otherRoot.Id
                ? record with { Title = "A second root in the same workspace", TitleSource = SessionTitleSource.FirstInput }
                : record.Id == fallback.Id
                    ? record with { Title = "First input for the fallback", TitleSource = SessionTitleSource.FirstInput }
                    : record.Id == empty.Id || record.Id == emptyFallback.Id
                        ? record with { TitleSource = SessionTitleSource.Default }
                        : record).ToArray();

        await using (var migrated = await NewInitializedStoreAsync())
        {
            foreach (var record in expected)
                Assert.Equal(record, await migrated.GetSessionAsync(record.Id, CancellationToken.None));
            var summaries = await migrated.ListSessionsAsync(null, 20, CancellationToken.None);
            Assert.Equal(
                records.Count(record => record.ParentSessionId is null && !record.IsArchived),
                summaries.Count);
            var archivedRoot = Assert.Single(await migrated.ListSessionsAsync(
                null, 20, SessionListKind.Archived, CancellationToken.None));
            Assert.Equal(root.Id, archivedRoot.Id);
            Assert.Equal("Fix the sidebar naming", archivedRoot.Title);
            Assert.Equal(root.UpdatedAt, archivedRoot.UpdatedAt);
            Assert.Equal(2, (await migrated.LoadMessagesAsync(root.Id, CancellationToken.None)).Count);
            Assert.IsType<SessionTitleChangedEvent>(Assert.Single(await migrated.LoadDurableEventsAsync(renamed.Id, CancellationToken.None)).Event);
        }

        await using var reopened = await NewInitializedStoreAsync();
        foreach (var record in expected)
            Assert.Equal(record, await reopened.GetSessionAsync(record.Id, CancellationToken.None));
        await using var connection = OpenRawConnection();
        Assert.Equal(8L, ExecuteScalar(connection, "SELECT MAX(version) FROM schema_version"));
        Assert.Equal(0L, ExecuteScalar(connection, "SELECT COUNT(*) FROM session WHERE title_source = 'legacy'"));
    }

    [Fact]
    public async Task Initialize_VersionTwo_SkipsToolResultsRemindersSyntheticSourcesAndNonUserMessages()
    {
        var record = NewRecord(title: "alpha");
        var noUser = NewRecord(title: "New session");
        var toolResult = new ToolResultPart(StrongId.NewPartId(), new ToolCallId("call_legacy"), [new ToolResultText("Tool output")]);
        var reminder = new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, "Reminder");
        IReadOnlyList<ConversationMessage> skipped =
        [
            NewMessage("System text") with { Role = MessageRole.System },
            NewMessage("Assistant text") with { Role = MessageRole.Assistant },
            NewMessage("Tool output") with { Parts = [toolResult] },
            NewMessage("Tool result preamble") with { Parts = [new TextPart(StrongId.NewPartId(), "Tool result preamble"), toolResult] },
            NewMessage("Attachment reminder") with { Parts = [reminder] },
            NewMessage("Reminder preamble") with { Parts = [new TextPart(StrongId.NewPartId(), "Reminder preamble"), reminder] },
            NewMessage(" \t<system-reminder>Legacy reminder</system-reminder>"),
            NewMessage("Runtime-originated text") with { Source = MessageSource.RuntimeMode },
            NewMessage("Legacy synthetic text") with { Source = MessageSource.LegacySynthetic },
            NewMessage(" \r\n\t"),
        ];
        CreateVersionTwoDatabase([record, noUser],
        [
            (record.Id, [.. skipped, NewMessage("Actual user request"), NewMessage("Later real user request") with { Source = MessageSource.RealUser }]),
            (noUser.Id, skipped),
        ]);

        await using var store = await NewInitializedStoreAsync();
        var loaded = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
        Assert.Equal("Actual user request", loaded.Title);
        Assert.Equal(SessionTitleSource.FirstInput, loaded.TitleSource);
        Assert.Equal(record.UpdatedAt, loaded.UpdatedAt);
        var unchanged = Assert.IsType<SessionRecord>(await store.GetSessionAsync(noUser.Id, CancellationToken.None));
        Assert.Equal("New session", unchanged.Title);
        Assert.Equal(SessionTitleSource.Default, unchanged.TitleSource);
    }

    [Fact]
    public async Task Initialize_VersionTwo_RealUserSourceAllowsContextAttachments_AndRequiresText()
    {
        var record = NewRecord(title: "alpha");
        var context = new AttachmentPart(StrongId.NewPartId(), AttachmentKind.ContextPrefix, "Workspace context");
        CreateVersionTwoDatabase([record],
        [
            (record.Id,
            [
                NewMessage("") with { Source = MessageSource.RealUser, Parts = [context] },
                NewMessage("Actual request with context") with
                {
                    Source = MessageSource.RealUser,
                    Parts = [context, new TextPart(StrongId.NewPartId(), "Actual request with context")],
                },
            ]),
        ]);

        await using var store = await NewInitializedStoreAsync();
        var loaded = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
        Assert.Equal("Actual request with context", loaded.Title);
        Assert.Equal(SessionTitleSource.FirstInput, loaded.TitleSource);
    }

    [Theory]
    [InlineData(57)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(200)]
    public async Task Initialize_VersionTwo_CompactsWhitespaceAndTruncatesAfterSixtyUtf16Units(int length)
    {
        var record = NewRecord(title: "alpha");
        var text = "\U0001F680" + new string('x', length - 2);
        CreateVersionTwoDatabase([record], [(record.Id, [NewMessage(" \t\r\n" + text + "\u2003 ")])]);

        await using var store = await NewInitializedStoreAsync();
        var loaded = Assert.IsType<SessionRecord>(await store.GetSessionAsync(record.Id, CancellationToken.None));
        Assert.Equal(length <= 60 ? text : text[..57] + "...", loaded.Title);
        Assert.True(loaded.Title.Length <= 60);
        Assert.Equal(SessionTitleSource.FirstInput, loaded.TitleSource);
    }

    [Fact]
    public void SessionMetadata_SerializesParentAndDefaultsLegacyPayloadToRoot()
    {
        var parentId = StrongId.NewSessionId();
        var options = new SessionOptions(@"C:\ws\alpha", ParentSessionId: parentId);
        Assert.Equal(options, AlcolerJson.Deserialize<SessionOptions>(AlcolerJson.Serialize(options)));
        Assert.Null(AlcolerJson.Deserialize<SessionOptions>("{\"workspaceRoot\":\"root\"}")!.ParentSessionId);
        var record = NewRecord() with { ParentSessionId = parentId };
        Assert.Equal(record, AlcolerJson.Deserialize<SessionRecord>(AlcolerJson.Serialize(record)));
        var summary = new SessionSummary(record.Id, record.Title, record.WorkspaceRoot, record.PermissionMode,
            record.ModelSelection, record.CreatedAt, record.UpdatedAt, ParentSessionId: parentId);
        Assert.Equal(summary, AlcolerJson.Deserialize<SessionSummary>(AlcolerJson.Serialize(summary)));
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
            [NewEvent(target.Id, 0, new SessionModeChangedEvent(PermissionMode.Yolo))],
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
    public async Task MessageOriginAndReasoningMetadata_PersistAcrossRestartAndReplace()
    {
        var sessionId = StrongId.NewSessionId();
        var message = new ConversationMessage(StrongId.NewMessageId(), MessageRole.Assistant,
        [
            new ReasoningPart(StrongId.NewPartId(), "", RedactedData: "opaque"),
            new ReasoningPart(StrongId.NewPartId(), "summary", EncryptedContent: "cipher", ProviderItemId: "rs_1"),
        ], Iso("2026-10-19T09:30:00Z"), ProviderId: "custom-anthropic-account", ModelId: "model", Source: MessageSource.RuntimeMode);
        await using (var store = await NewInitializedStoreAsync())
            await store.AppendMessagesAsync(sessionId, [message], 0, CancellationToken.None);
        await using (var store = await NewInitializedStoreAsync())
        {
            var restored = Assert.Single(await store.LoadMessagesAsync(sessionId, CancellationToken.None)).Message;
            Assert.Equal(message.ProviderId, restored.ProviderId);
            Assert.Equal(message.ModelId, restored.ModelId);
            Assert.Equal(MessageSource.RuntimeMode, restored.Source);
            Assert.Equal(message.Parts, restored.Parts);
            await store.ReplaceMessagesAsync(sessionId, [restored], CancellationToken.None);
        }
        await using var reopened = await NewInitializedStoreAsync();
        var replaced = Assert.Single(await reopened.LoadMessagesAsync(sessionId, CancellationToken.None)).Message;
        Assert.Equal(message.Parts, replaced.Parts);
        Assert.Equal(message.ProviderId, replaced.ProviderId);
        Assert.Equal(message.ModelId, replaced.ModelId);
    }

    [Fact]
    public void LegacyMessagePayload_DefaultsUnknownOriginAndReasoningMetadata()
    {
        var message = new ConversationMessage(StrongId.NewMessageId(), MessageRole.Assistant,
            [new ReasoningPart(StrongId.NewPartId(), "thinking", "sig")], Iso("2026-10-19T09:30:00Z"));
        var payload = System.Text.Json.Nodes.JsonNode.Parse(AlcolerJson.Serialize(message))!.AsObject();
        payload.Remove("providerId");
        payload.Remove("modelId");
        payload.Remove("source");
        var part = payload["parts"]![0]!.AsObject();
        part.Remove("redactedData");
        part.Remove("encryptedContent");
        part.Remove("providerItemId");
        var restored = AlcolerJson.Deserialize<ConversationMessage>(payload.ToJsonString())!;
        Assert.Null(restored.ProviderId);
        Assert.Null(restored.ModelId);
        Assert.Null(restored.Source);
        Assert.Equal("sig", Assert.IsType<ReasoningPart>(Assert.Single(restored.Parts)).ProviderSignature);
        Assert.Null(((ReasoningPart)restored.Parts[0]).RedactedData);
        Assert.Null(((ReasoningPart)restored.Parts[0]).EncryptedContent);
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
            NewEvent(sessionId, 3, new SessionModeChangedEvent(PermissionMode.Yolo)),
        };

        await store.AppendDurableEventsAsync(events, CancellationToken.None);

        var loaded = await store.LoadDurableEventsAsync(sessionId, CancellationToken.None);
        Assert.Equal([3L, 7L, 11L], loaded.Select(sequenced => sequenced.Sequence).ToArray());
        Assert.All(loaded, sequenced => Assert.Equal(sessionId, sequenced.SessionId));
        Assert.Equal(Iso("2026-10-19T09:00:00Z"), loaded[0].OccurredAt);

        Assert.Equal(PermissionMode.Yolo, Assert.IsType<SessionModeChangedEvent>(loaded[0].Event).Mode);

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

    private void CreateVersionOneDatabase(IReadOnlyList<SessionRecord> records, SessionId parentId, IReadOnlyList<string> payloads)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_version (version INTEGER NOT NULL);
            INSERT INTO schema_version VALUES (1);
            CREATE TABLE session (
                id TEXT PRIMARY KEY, workspace_root TEXT NOT NULL, title TEXT NOT NULL,
                permission_mode TEXT NOT NULL, model_selection_json TEXT NULL,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, branch_json TEXT NULL,
                is_archived INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE durable_event (
                session_id TEXT NOT NULL, sequence INTEGER NOT NULL, occurred_at TEXT NOT NULL,
                payload_json TEXT NOT NULL, PRIMARY KEY (session_id, sequence));
            CREATE TABLE message (
                session_id TEXT NOT NULL, sequence INTEGER NOT NULL, message_id TEXT NOT NULL,
                payload_json TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY (session_id, sequence));
            """;
        command.ExecuteNonQuery();
        foreach (var record in records)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO session (id, workspace_root, title, permission_mode, created_at, updated_at)
                VALUES ($id, $root, $title, $mode, $created, $updated)
                """;
            command.Parameters.AddWithValue("$id", record.Id.Value);
            command.Parameters.AddWithValue("$root", record.WorkspaceRoot);
            command.Parameters.AddWithValue("$title", record.Title);
            command.Parameters.AddWithValue("$mode", record.PermissionMode.ToString());
            command.Parameters.AddWithValue("$created", record.CreatedAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$updated", record.UpdatedAt.UtcDateTime.ToString("O"));
            command.ExecuteNonQuery();
        }
        for (var index = 0; index < payloads.Count; index++)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO durable_event (session_id, sequence, occurred_at, payload_json)
                VALUES ($parent, $sequence, $at, $payload)
                """;
            command.Parameters.AddWithValue("$parent", parentId.Value);
            command.Parameters.AddWithValue("$sequence", index);
            command.Parameters.AddWithValue("$at", Iso("2026-10-19T09:00:00Z").UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$payload", payloads[index]);
            command.ExecuteNonQuery();
        }
    }

    private void CreateVersionTwoDatabase(
        IReadOnlyList<SessionRecord> records,
        IReadOnlyList<(SessionId SessionId, IReadOnlyList<ConversationMessage> Messages)> histories,
        IReadOnlyList<SequencedEvent>? events = null)
    {
        CreateVersionOneDatabase([], StrongId.NewSessionId(), []);
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE session ADD COLUMN parent_session_id TEXT NULL; INSERT INTO schema_version VALUES (2);";
        command.ExecuteNonQuery();
        foreach (var record in records)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO session (id, workspace_root, title, permission_mode, model_selection_json,
                    created_at, updated_at, branch_json, is_archived, parent_session_id)
                VALUES ($id, $root, $title, $mode, $model, $created, $updated, $branch, $archived, $parent)
                """;
            command.Parameters.AddWithValue("$id", record.Id.Value);
            command.Parameters.AddWithValue("$root", record.WorkspaceRoot);
            command.Parameters.AddWithValue("$title", record.Title);
            command.Parameters.AddWithValue("$mode", record.PermissionMode.ToString());
            command.Parameters.AddWithValue("$model", record.ModelSelection is null ? DBNull.Value : AlcolerJson.Serialize(record.ModelSelection));
            command.Parameters.AddWithValue("$created", record.CreatedAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$updated", record.UpdatedAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$branch", record.Branch is null ? DBNull.Value : AlcolerJson.Serialize(record.Branch));
            command.Parameters.AddWithValue("$archived", record.IsArchived);
            command.Parameters.AddWithValue("$parent", (object?)record.ParentSessionId?.Value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        foreach (var (sessionId, messages) in histories)
        {
            for (var sequence = 0; sequence < messages.Count; sequence++)
            {
                var message = messages[sequence];
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO message (session_id, sequence, message_id, payload_json, created_at)
                    VALUES ($id, $sequence, $message, $payload, $created)
                    """;
                command.Parameters.AddWithValue("$id", sessionId.Value);
                command.Parameters.AddWithValue("$sequence", sequence);
                command.Parameters.AddWithValue("$message", message.Id.Value);
                command.Parameters.AddWithValue("$payload", AlcolerJson.Serialize(message));
                command.Parameters.AddWithValue("$created", message.CreatedAt.UtcDateTime.ToString("O"));
                command.ExecuteNonQuery();
            }
        }

        foreach (var sequencedEvent in events ?? [])
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO durable_event (session_id, sequence, occurred_at, payload_json)
                VALUES ($id, $sequence, $at, $payload)
                """;
            command.Parameters.AddWithValue("$id", sequencedEvent.SessionId.Value);
            command.Parameters.AddWithValue("$sequence", sequencedEvent.Sequence);
            command.Parameters.AddWithValue("$at", sequencedEvent.OccurredAt.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$payload", AlcolerJson.Serialize(sequencedEvent.Event));
            command.ExecuteNonQuery();
        }
    }

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
