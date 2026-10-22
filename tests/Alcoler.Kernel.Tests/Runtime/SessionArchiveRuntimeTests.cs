using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Kernel.Tests.EndToEnd;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionArchiveRuntimeTests
{
    [Fact]
    public async Task ArchivedResidentSessionSurvivesLaterRecordPersistence()
    {
        await using var harness = TestHarness.Create();
        var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);

        await runtime.SetSessionArchivedAsync(session.Id, true, CancellationToken.None);
        var ack = await runtime.SubmitAsync(
            new SetModeCommand(StrongId.NewCommandId(), session.Id, 0, PermissionMode.Plan),
            CancellationToken.None);

        Assert.True(ack.Accepted);
        var persisted = Assert.IsType<SessionRecord>(await store.GetSessionAsync(session.Id, CancellationToken.None));
        Assert.True(persisted.IsArchived);
        Assert.Empty(await store.ListSessionsAsync(null, 10, SessionListKind.Active, CancellationToken.None));
        Assert.Single(await store.ListSessionsAsync(
            null, 10, SessionListKind.Archived, CancellationToken.None));

        await runtime.SetSessionArchivedAsync(session.Id, false, CancellationToken.None);
        Assert.False(Assert.IsType<SessionRecord>(
            await store.GetSessionAsync(session.Id, CancellationToken.None)).IsArchived);
    }

    [Fact]
    public async Task ArchiveStaleSessions_SkipsResidentSessionsAndKeepOpen()
    {
        await using var harness = TestHarness.Create();
        var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        var firstRuntime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
        var first = await firstRuntime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var second = await firstRuntime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        await AgeSessionAsync(store, first.Id);
        await AgeSessionAsync(store, second.Id);

        var residentCount = await firstRuntime.ArchiveStaleSessionsAsync(
            TimeSpan.FromDays(7), keepOpen: null, CancellationToken.None);

        Assert.Equal(0, residentCount);
        Assert.Equal(2, (await store.ListSessionsAsync(
            null, 10, SessionListKind.Active, CancellationToken.None)).Count);

        await firstRuntime.DisposeAsync();

        await using var reopened = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
        var archivedCount = await reopened.ArchiveStaleSessionsAsync(
            TimeSpan.FromDays(7), first.Id, CancellationToken.None);

        Assert.Equal(1, archivedCount);
        var active = await store.ListSessionsAsync(null, 10, SessionListKind.Active, CancellationToken.None);
        Assert.Equal(new[] { first.Id }, active.Select(summary => summary.Id).ToArray());
        var archived = await store.ListSessionsAsync(
            null, 10, SessionListKind.Archived, CancellationToken.None);
        Assert.Equal(new[] { second.Id }, archived.Select(summary => summary.Id).ToArray());
    }

    private static async Task AgeSessionAsync(SqliteSessionStore store, SessionId sessionId)
    {
        var record = Assert.IsType<SessionRecord>(await store.GetSessionAsync(sessionId, CancellationToken.None));
        await store.UpsertSessionAsync(
            record with { UpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(30) },
            CancellationToken.None);
    }
}
