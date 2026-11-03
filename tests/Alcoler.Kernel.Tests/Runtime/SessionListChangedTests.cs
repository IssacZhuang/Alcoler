using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Kernel.Tests.EndToEnd;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionListChangedTests
{
    [Fact]
    public async Task CreateSession_RaisesSessionListChangedPerSession()
    {
        await using var harness = TestHarness.Create();
        var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
        var raised = 0;
        runtime.SessionListChanged += () => raised++;

        await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);

        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task SetSessionArchived_RaisesSessionListChangedInBothDirections()
    {
        await using var harness = TestHarness.Create();
        var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var raised = 0;
        runtime.SessionListChanged += () => raised++;

        await runtime.SetSessionArchivedAsync(session.Id, archived: true, CancellationToken.None);
        await runtime.SetSessionArchivedAsync(session.Id, archived: false, CancellationToken.None);

        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task ArchiveStaleSessions_RaisesOnlyWhenAnythingWasArchived()
    {
        await using var harness = TestHarness.Create();
        var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
        await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var raised = 0;
        runtime.SessionListChanged += () => raised++;

        Assert.Equal(0, await runtime.ArchiveStaleSessionsAsync(
            TimeSpan.FromDays(7), keepOpen: null, CancellationToken.None));
        Assert.Equal(0, raised);

        await AgeAllSessionsAsync(store);
        await runtime.DisposeAsync();

        await using var reopened = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
        reopened.SessionListChanged += () => raised++;

        Assert.Equal(1, await reopened.ArchiveStaleSessionsAsync(
            TimeSpan.FromDays(7), keepOpen: null, CancellationToken.None));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task DeleteArchivedSessions_RaisesOnlyWhenAnythingWasDeleted()
    {
        await using var harness = TestHarness.Create();
        var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var raised = 0;
        runtime.SessionListChanged += () => raised++;

        Assert.Equal(0, await runtime.DeleteArchivedSessionsAsync(CancellationToken.None));
        Assert.Equal(0, raised);

        await runtime.SetSessionArchivedAsync(session.Id, archived: true, CancellationToken.None);
        raised = 0;

        Assert.Equal(1, await runtime.DeleteArchivedSessionsAsync(CancellationToken.None));
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ForkSession_RaisesSessionListChanged()
    {
        await using var harness = ScriptedCallHarness.Create(
            [new ModelTextDelta("answer"), new ModelStreamFinished(ModelFinishReason.Stop)]);
        await using var runtime = harness.BuildRuntime();
        var parent = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(parent.Id);
        await SendAndWaitIdleAsync(runtime, projection, parent.Id);
        var raised = 0;
        runtime.SessionListChanged += () => raised++;

        MessageId boundaryId;
        await using (var store = harness.OpenStore())
        {
            boundaryId = (await store.LoadMessagesAsync(parent.Id, CancellationToken.None))[1].Message.Id;
        }
        var child = await runtime.ForkSessionAsync(parent.Id, boundaryId, CancellationToken.None);

        Assert.NotEqual(parent.Id, child.Id);
        Assert.Equal(1, raised);
    }

    private static async Task AgeAllSessionsAsync(SqliteSessionStore store)
    {
        foreach (var summary in await store.ListSessionsAsync(null, int.MaxValue, CancellationToken.None))
        {
            var record = Assert.IsType<SessionRecord>(await store.GetSessionAsync(summary.Id, CancellationToken.None));
            await store.UpsertSessionAsync(
                record with { UpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(30) },
                CancellationToken.None);
        }
    }

    private static async Task SendAndWaitIdleAsync(
        Kernel.Runtime.AgentRuntime runtime,
        IConversationProjection projection,
        SessionId id)
    {
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), id, 0, "question"), CancellationToken.None);
        Assert.True(ack.Accepted);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (projection.Snapshot() is not { State.Phase: TurnPhase.Idle } snapshot
            || snapshot.Rows.OfType<AssistantMessageRow>().Count() < 1)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("turn did not become idle");
            }
            await Task.Delay(10);
        }
    }
}
