using System.Collections.Concurrent;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Stores;
using Alcoler.Ui;
using Xunit;

namespace Alcoler.Tests;

public class ProjectionStoreTests
{
    private static (ProjectionStore Store, FakeConversationProjection Projection) Create(
        ConversationSnapshot snapshot,
        TimeSpan? coalesceWindow = null,
        IUiScheduler? scheduler = null)
    {
        var projection = new FakeConversationProjection(snapshot);
        var store = new ProjectionStore(
            () => projection,
            new SessionId(Guid.NewGuid().ToString("N")),
            scheduler ?? InlineUiScheduler.Instance,
            TestSnapshots.State(),
            coalesceWindow,
            TimeProvider.System);
        return (store, projection);
    }

    private static AssistantMessageRow AssistantRow(string rowId, MessageId id, string text, bool streaming) =>
        new(rowId, id, [new TextPart(StrongId.NewPartId(), text)], streaming, DateTimeOffset.UtcNow);

    private static ConversationSnapshot Snapshot(long sequence, ConversationState state, params ConversationRow[] rows) =>
        new(0, sequence, rows, state);

    private static UserMessageRow UserRow(string rowId, string text) => new(rowId, text, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Start_PublishesResetWithSnapshotRows()
    {
        var state = TestSnapshots.State();
        var (store, projection) = Create(Snapshot(2, state, UserRow("u1", "hello")));

        ProjectionUpdate? first = null;
        store.Updated += u => first ??= u;
        store.Start();
        await WaitUntil(() => first is not null);

        Assert.True(first!.Reset);
        Assert.Single(first.ResetRows!);
        Assert.Equal(2, first.Sequence);
        Assert.Equal("Test session", first.State.Title);
        store.Dispose();
    }

    [Fact]
    public async Task ContiguousDelta_AppliesRowOpsAndStatePatch()
    {
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State(), UserRow("u1", "hello")));
        var updates = new List<ProjectionUpdate>();
        store.Updated += updates.Add;
        store.Start();
        await WaitUntil(() => updates.Count > 0);

        projection.Emit(new ConversationDelta(2,
        [
            new RowAppended(UserRow("u2", "second")),
            new StatePatched(TestSnapshots.State(TurnPhase.Streaming, contextUsageTokens: 50_000)),
        ]));
        await WaitUntil(() => updates.Any(u => !u.Reset));

        var delta = updates.Single(u => !u.Reset);
        Assert.Equal(2, delta.Sequence);
        Assert.Equal(TurnPhase.Streaming, delta.State.Phase);
        Assert.Equal(2, store.Rows.Count);
        Assert.Equal(TurnPhase.Streaming, store.State.Phase);
        store.Dispose();
    }

    [Fact]
    public async Task EmptyDeltas_AdvanceSequenceWithoutResettingOrPublishingRows()
    {
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State()));
        using (store)
        {
            var resets = 0;
            var visible = new TaskCompletionSource<ProjectionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            store.Updated += update =>
            {
                if (update.Reset)
                    Interlocked.Increment(ref resets);
                else
                    visible.TrySetResult(update);
            };
            store.Start();
            await WaitUntil(() => Volatile.Read(ref resets) == 1);
            projection.Emit(new ConversationDelta(2, []));
            projection.Emit(new ConversationDelta(3, []));
            projection.Emit(new ConversationDelta(4, [new RowAppended(UserRow("u1", "visible after hidden events"))]));
            var update = await visible.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, update.Sequence);
            Assert.Single(update.Ops);
            Assert.Equal(1, Volatile.Read(ref resets));
            Assert.Equal(1, projection.SubscribeCount);
            Assert.Equal("u1", Assert.Single(store.Rows).RowId);
        }
    }

    [Fact]
    public async Task SequenceGap_TriggersResubscribeAndWholesaleReset()
    {
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State(), UserRow("u1", "hello")));
        var resets = 0;
        store.Updated += _ => { };
        store.Updated += u => { if (u.Reset) { Interlocked.Increment(ref resets); } };
        store.Start();
        await WaitUntil(() => Volatile.Read(ref resets) == 1);
        Assert.Equal(1, projection.SubscribeCount);

        // Seq jumps from 1 to 3: a gap that must force a resubscribe.
        projection.SetSnapshot(Snapshot(3, TestSnapshots.State(), UserRow("u1", "hello"), UserRow("u3", "gap filled")));
        projection.Emit(new ConversationDelta(3, [new RowAppended(UserRow("u3", "gap filled"))]));

        await WaitUntil(() => Volatile.Read(ref resets) == 2);
        Assert.Equal(2, projection.SubscribeCount);
        Assert.Equal(2, store.Rows.Count);
        store.Dispose();
    }

    [Fact]
    public async Task CoalesceWindow_BatchesDeltasIntoOneUpdate()
    {
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State()), coalesceWindow: TimeSpan.FromMilliseconds(50));
        var updates = new List<ProjectionUpdate>();
        store.Updated += updates.Add;
        store.Start();
        await WaitUntil(() => updates.Count == 1);

        projection.Emit(new ConversationDelta(2, [new RowAppended(UserRow("u2", "a"))]));
        projection.Emit(new ConversationDelta(3, [new RowAppended(UserRow("u3", "b"))]));
        await WaitUntil(() => updates.Count(u => !u.Reset) == 1, timeout: TimeSpan.FromSeconds(5));

        var batch = updates.Single(u => !u.Reset);
        Assert.Equal(3, batch.Sequence);
        Assert.Equal(2, batch.Ops.Count);
        Assert.Equal(2, store.Rows.Count);
        store.Dispose();
    }

    [Fact]
    public async Task CoalesceWindow_MergesRepeatedUpsertsAndKeepsLatestRow()
    {
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State()),
            coalesceWindow: TimeSpan.FromMilliseconds(60));
        var updates = new List<ProjectionUpdate>();
        store.Updated += updates.Add;
        store.Start();
        await WaitUntil(() => updates.Count == 1);

        var id = StrongId.NewMessageId();
        projection.Emit(new ConversationDelta(2,
        [
            new StatePatched(TestSnapshots.State(TurnPhase.Streaming)),
            new RowAppended(AssistantRow("a1", id, "a", streaming: true)),
        ]));
        projection.Emit(new ConversationDelta(3,
        [
            new StatePatched(TestSnapshots.State(TurnPhase.Streaming, contextUsageTokens: 42)),
            new RowUpserted(AssistantRow("a1", id, "ab", streaming: true)),
        ]));
        projection.Emit(new ConversationDelta(4,
        [
            new StatePatched(TestSnapshots.State(TurnPhase.Streaming, contextUsageTokens: 84)),
            new RowUpserted(AssistantRow("a1", id, "abc", streaming: true)),
        ]));
        await WaitUntil(() => updates.Count(u => !u.Reset) == 1, timeout: TimeSpan.FromSeconds(5));

        var batch = updates.Single(u => !u.Reset);
        Assert.Equal(4, batch.Sequence);
        Assert.Equal(2, batch.Ops.Count);
        var upsert = Assert.IsType<RowUpserted>(batch.Ops[0]);
        var row = Assert.IsType<AssistantMessageRow>(upsert.Row);
        Assert.Equal("abc", Assert.IsType<TextPart>(Assert.Single(row.Parts)).Text);
        var state = Assert.IsType<StatePatched>(batch.Ops[1]);
        Assert.Equal(84, state.State.ContextUsageTokens);
        store.Dispose();
    }

    [Fact]
    public async Task Delivery_IsSingleFlightAndConflatesWhileQueued()
    {
        var scheduler = new QueuedUiScheduler();
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State()),
            coalesceWindow: TimeSpan.FromMilliseconds(30), scheduler: scheduler);
        var updates = new List<ProjectionUpdate>();
        store.Updated += updates.Add;
        store.Start();

        await WaitUntil(() => scheduler.Count == 1);
        Assert.Empty(updates);
        scheduler.RunNext();
        Assert.True(Assert.Single(updates).Reset);

        var id = StrongId.NewMessageId();
        projection.Emit(new ConversationDelta(2,
            [new RowAppended(AssistantRow("a1", id, "a", streaming: true))]));
        await WaitUntil(() => scheduler.Count == 1);

        projection.Emit(new ConversationDelta(3,
            [new RowUpserted(AssistantRow("a1", id, "ab", streaming: true))]));
        await Task.Delay(80);
        Assert.Equal(1, scheduler.Count);
        scheduler.RunNext();

        var batch = Assert.Single(updates.Skip(1));
        Assert.False(batch.Reset);
        Assert.Equal(3, batch.Sequence);
        var upsert = Assert.Single(batch.Ops.OfType<RowUpserted>());
        var row = Assert.IsType<AssistantMessageRow>(upsert.Row);
        Assert.Equal("ab", Assert.IsType<TextPart>(Assert.Single(row.Parts)).Text);
        Assert.Equal(0, scheduler.Count);
        store.Dispose();
    }

    [Fact]
    public void SetRevision_NeverMovesBackwards()
    {
        var (store, _) = Create(Snapshot(1, TestSnapshots.State()));
        store.SetRevision(5);
        store.SetRevision(3);
        Assert.Equal(5, store.Revision);
        store.Dispose();
    }

    private sealed class QueuedUiScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> _actions = new();

        public int Count => _actions.Count;

        public void Post(Action action) => _actions.Enqueue(action);

        public void PostBackground(Action action) => _actions.Enqueue(action);

        public void RunNext()
        {
            Assert.True(_actions.TryDequeue(out var action));
            action!();
        }
    }

    internal static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not met within the timeout");
            await Task.Delay(10);
        }
    }
}
