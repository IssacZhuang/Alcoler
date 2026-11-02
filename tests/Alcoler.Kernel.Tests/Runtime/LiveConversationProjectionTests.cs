using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class LiveConversationProjectionTests
{
    [Fact]
    public async Task Snapshot_ConcurrentStreamingRowsAndState_MatchAppliedSequence()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var turn = StrongId.NewTurnId();
        store.Append(new SessionTitleChangedEvent("1"));
        using var projection = new LiveConversationProjection(store, 0, () =>
        {
            Thread.Yield();
            return 0;
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = Task.Run(async () =>
        {
            await foreach (var _ in projection.Deltas(timeout.Token))
            {
            }
        });
        var producer = Task.Run(async () =>
        {
            await start.Task;
            for (var sequence = 2; sequence <= 2001; sequence++)
            {
                store.Append(sequence % 2 == 0
                    ? new ModelStreamingTextEvent(turn, "x")
                    : new SessionTitleChangedEvent(sequence.ToString()));
                await projection.WaitUntilSequenceAsync(sequence, timeout.Token);
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            for (var index = 0; index < 2000; index++)
            {
                var snapshot = projection.Snapshot();
                Assert.Equal((snapshot.Sequence % 2 == 0 ? snapshot.Sequence - 1 : snapshot.Sequence).ToString(),
                    snapshot.State.Title);
                var text = snapshot.Rows.OfType<AssistantMessageRow>()
                    .SelectMany(row => row.Parts.OfType<TextPart>()).SingleOrDefault()?.Text ?? "";
                Assert.Equal(snapshot.Sequence / 2, text.Length);
            }
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(readers.Append(producer)).WaitAsync(timeout.Token);
        Assert.Equal(2001, projection.Snapshot().Sequence);
        projection.Dispose();
        await drain.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Snapshot_DelayedDeltaEnumeration_DoesNotLoseLaterStreamingChunks()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var turn = StrongId.NewTurnId();
        store.Append(new TurnStartedEvent(turn, StrongId.NewCommandId()));
        store.Append(new ModelStreamingTextEvent(turn, "already running"));
        using var projection = new LiveConversationProjection(store, 0, () => 7);
        var snapshot = projection.Snapshot();
        Assert.Equal(2, snapshot.Sequence);
        AssertStreamingText(snapshot.Rows, "already running");

        var between = store.Append(new ModelStreamingTextEvent(turn, ", before enumeration"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await projection.WaitUntilSequenceAsync(between.Sequence, timeout.Token);
        await using var deltas = projection.Deltas(timeout.Token).GetAsyncEnumerator();
        Assert.True(await deltas.MoveNextAsync());
        Assert.Equal(snapshot.Sequence + 1, deltas.Current.Sequence);
        AssertStreamingText(deltas.Current.Ops.OfType<RowUpserted>().Select(op => op.Row),
            "already running, before enumeration");

        var pending = deltas.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        var later = store.Append(new ModelStreamingTextEvent(turn, ", later"));
        Assert.True(await pending.WaitAsync(timeout.Token));
        Assert.Equal(later.Sequence, deltas.Current.Sequence);
        AssertStreamingText(deltas.Current.Ops.OfType<RowUpserted>().Select(op => op.Row),
            "already running, before enumeration, later");
        AssertStreamingText(snapshot.Rows, "already running");
    }

    [Fact]
    public async Task IndependentObservers_DisconnectAndReconnect_PreserveSubsequentChunks()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var turn = StrongId.NewTurnId();
        store.Append(new ModelStreamingTextEvent(turn, "one"));
        using var first = new LiveConversationProjection(store, 0, () => 0);
        using var second = new LiveConversationProjection(store, 0, () => 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var secondDeltas = second.Deltas(timeout.Token).GetAsyncEnumerator();
        var middle = store.Append(new ModelStreamingTextEvent(turn, " two"));
        await first.WaitUntilSequenceAsync(middle.Sequence, timeout.Token);
        Assert.True(await secondDeltas.MoveNextAsync());
        Assert.Equal(middle.Sequence, secondDeltas.Current.Sequence);
        first.Dispose();

        using var reconnected = new LiveConversationProjection(store, 0, () => 0);
        AssertStreamingText(reconnected.Snapshot().Rows, "one two");
        await using var reconnectedDeltas = reconnected.Deltas(timeout.Token).GetAsyncEnumerator();
        var last = store.Append(new ModelStreamingTextEvent(turn, " three"));
        Assert.True(await secondDeltas.MoveNextAsync());
        Assert.True(await reconnectedDeltas.MoveNextAsync());
        Assert.Equal(last.Sequence, secondDeltas.Current.Sequence);
        Assert.Equal(last.Sequence, reconnectedDeltas.Current.Sequence);
        AssertStreamingText(secondDeltas.Current.Ops.OfType<RowUpserted>().Select(op => op.Row), "one two three");
        AssertStreamingText(reconnectedDeltas.Current.Ops.OfType<RowUpserted>().Select(op => op.Row), "one two three");
    }

    [Fact]
    public async Task SlowObserver_OverflowCompletesGracefully_WithoutStoppingHealthyObserver()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var turn = StrongId.NewTurnId();
        using var slow = new LiveConversationProjection(store, 0, () => 0);
        using var healthy = new LiveConversationProjection(store, 0, () => 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var healthyDeltas = healthy.Deltas(timeout.Token).GetAsyncEnumerator();
        var total = LiveConversationProjection.MaxPendingDeltas + 10;
        for (var index = 1; index <= total; index++)
        {
            store.Append(new ModelStreamingTextEvent(turn, "x"));
            Assert.True(await healthyDeltas.MoveNextAsync());
            Assert.Equal(index, healthyDeltas.Current.Sequence);
        }
        await slow.WaitUntilSequenceAsync(total, timeout.Token);
        var snapshot = slow.Snapshot();
        Assert.Equal(total, snapshot.Sequence);
        AssertStreamingText(snapshot.Rows, new string('x', total));
        var received = new List<ConversationDelta>();
        await foreach (var delta in slow.Deltas(timeout.Token))
            received.Add(delta);
        Assert.Equal(LiveConversationProjection.MaxPendingDeltas, received.Count);
        Assert.Equal(Enumerable.Range(1, received.Count).Select(index => (long)index), received.Select(delta => delta.Sequence));

        using var reconnected = new LiveConversationProjection(store, 0, () => 0);
        Assert.Equal(total, reconnected.Snapshot().Sequence);
        AssertStreamingText(reconnected.Snapshot().Rows, new string('x', total));
        var later = store.Append(new ModelStreamingTextEvent(turn, " later"));
        Assert.True(await healthyDeltas.MoveNextAsync());
        Assert.Equal(later.Sequence, healthyDeltas.Current.Sequence);
        await reconnected.WaitUntilSequenceAsync(later.Sequence, timeout.Token);
        AssertStreamingText(reconnected.Snapshot().Rows, new string('x', total) + " later");
        await slow.WaitUntilSequenceAsync(later.Sequence, timeout.Token);
        Assert.Equal(later.Sequence, slow.Snapshot().Sequence);
        AssertStreamingText(slow.Snapshot().Rows, new string('x', total) + " later");
    }

    [Fact]
    public async Task SnapshotOnlyObserver_AfterDeltaOverflow_StillAppliesEveryEventAndCompletesWaiters()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        using var projection = new LiveConversationProjection(store, 0, () => 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var total = LiveConversationProjection.MaxPendingDeltas * 4;
        for (var sequence = 1; sequence <= total; sequence++)
        {
            store.Append(new SessionTitleChangedEvent(sequence.ToString()));
            await projection.WaitUntilSequenceAsync(sequence, timeout.Token);
            var snapshot = projection.Snapshot();
            Assert.Equal(sequence, snapshot.Sequence);
            Assert.Equal(sequence.ToString(), snapshot.State.Title);
        }
        var count = 0;
        await foreach (var _ in projection.Deltas(timeout.Token))
            count++;
        Assert.Equal(LiveConversationProjection.MaxPendingDeltas, count);
        var later = store.Append(new SessionTitleChangedEvent("after draining ended feed"));
        await projection.WaitUntilSequenceAsync(later.Sequence, timeout.Token);
        Assert.Equal("after draining ended feed", projection.Snapshot().State.Title);
    }

    [Fact]
    public async Task HistoricalReplay_LargerThanLiveBacklogLimit_RemainsCompleteAndLive()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var total = LiveConversationProjection.MaxPendingDeltas * 2;
        for (var index = 0; index < total; index++)
            store.Append(new SessionTitleChangedEvent(index.ToString()));
        using var projection = new LiveConversationProjection(store, 0, () => 0);
        Assert.Equal(total, projection.Snapshot().Sequence);
        Assert.Equal((total - 1).ToString(), projection.Snapshot().State.Title);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var later = store.Append(new SessionTitleChangedEvent("live"));
        await using var deltas = projection.Deltas(timeout.Token).GetAsyncEnumerator();
        Assert.True(await deltas.MoveNextAsync());
        Assert.Equal(later.Sequence, deltas.Current.Sequence);
        Assert.Equal("live", Assert.Single(deltas.Current.Ops.OfType<StatePatched>()).State.Title);
    }

    [Fact]
    public async Task Dispose_PendingSequenceWaiters_FinishWithoutHanging()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        using var projection = new LiveConversationProjection(store, 0, () => 0);
        var waiter = projection.WaitUntilSequenceAsync(1);
        projection.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => projection.WaitUntilSequenceAsync(1));
        Assert.True(projection.WaitUntilSequenceAsync(0).IsCompletedSuccessfully);
    }

    private static void AssertStreamingText(IEnumerable<ConversationRow> rows, string expected)
    {
        var row = Assert.IsType<AssistantMessageRow>(Assert.Single(rows));
        Assert.True(row.IsStreaming);
        Assert.Equal(expected, Assert.IsType<TextPart>(Assert.Single(row.Parts)).Text);
    }

    [Fact]
    public void WaitUntilSequenceAsync_AppliedSequence_CompletesSynchronously()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        store.Append(new SessionTitleChangedEvent("renamed"));
        using var projection = new LiveConversationProjection(store, 0, () => 0);

        Assert.True(projection.WaitUntilSequenceAsync(store.LastSequence).IsCompleted);
        Assert.True(projection.WaitUntilSequenceAsync(1).IsCompleted);
    }

    [Fact]
    public async Task WaitUntilSequenceAsync_FutureSequence_CompletesAfterStateIsApplied()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        using var projection = new LiveConversationProjection(store, 0, () => 0);

        var wait = projection.WaitUntilSequenceAsync(store.LastSequence + 1);
        Assert.False(wait.IsCompleted);

        store.Append(new SessionTitleChangedEvent("renamed"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await wait.WaitAsync(timeout.Token);

        Assert.Equal("renamed", projection.Snapshot().State.Title);
    }

    [Fact]
    public void EventStore_AfterMaximumSequence_ReplaysNothing()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        store.Append(new SessionTitleChangedEvent("one"));

        Assert.Empty(store.EventsFrom(long.MaxValue));
        using var subscription = store.Subscribe(long.MaxValue);
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public async Task WaitUntilSequenceAsync_CancellationRacingEvent_DoesNotBlockReplay()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        using var projection = new LiveConversationProjection(store, 0, () => 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var sequence = 1; sequence <= 100; sequence++)
        {
            using var cancellation = new CancellationTokenSource();
            var wait = projection.WaitUntilSequenceAsync(sequence, cancellation.Token);
            await Task.WhenAll(
                Task.Run(cancellation.Cancel),
                Task.Run(() => store.Append(new SessionTitleChangedEvent(sequence.ToString()))))
                .WaitAsync(timeout.Token);
            try
            {
                await wait.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (wait.IsCanceled)
            {
            }
            await projection.WaitUntilSequenceAsync(sequence, timeout.Token);
        }
    }

    [Fact]
    public async Task WaitUntilSequenceAsync_ConcurrentWaiters_AllCompleteAtTheirSequences()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        store.Append(new SessionTitleChangedEvent("one"));
        using var projection = new LiveConversationProjection(store, 0, () => 0);

        var later = projection.WaitUntilSequenceAsync(store.LastSequence + 2);
        var earlier = projection.WaitUntilSequenceAsync(store.LastSequence + 1);
        store.Append(new SessionTitleChangedEvent("two"));
        store.Append(new SessionTitleChangedEvent("three"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await earlier.WaitAsync(timeout.Token);
        await later.WaitAsync(timeout.Token);

        Assert.Equal("three", projection.Snapshot().State.Title);
    }
}
