using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Serialization;
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
        Assert.Equal(new RowTextAppended($"stream:{turn.Value}", ", before enumeration", between.OccurredAt),
            Assert.Single(deltas.Current.Ops.OfType<RowTextAppended>()));
        Assert.Equal(TurnPhase.Streaming, Assert.Single(deltas.Current.Ops.OfType<StatePatched>()).State.Phase);

        var pending = deltas.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        var later = store.Append(new ModelStreamingTextEvent(turn, ", later"));
        Assert.True(await pending.WaitAsync(timeout.Token));
        Assert.Equal(later.Sequence, deltas.Current.Sequence);
        Assert.Equal(new RowTextAppended($"stream:{turn.Value}", ", later", later.OccurredAt),
            Assert.Single(deltas.Current.Ops.OfType<RowTextAppended>()));
        AssertStreamingText(projection.Snapshot().Rows, "already running, before enumeration, later");
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
        var appended = new RowTextAppended($"stream:{turn.Value}", " three", last.OccurredAt);
        Assert.Equal(appended, Assert.Single(secondDeltas.Current.Ops.OfType<RowTextAppended>()));
        Assert.Equal(appended, Assert.Single(reconnectedDeltas.Current.Ops.OfType<RowTextAppended>()));
        AssertStreamingText(second.Snapshot().Rows, "one two three");
        AssertStreamingText(reconnected.Snapshot().Rows, "one two three");
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
            var item = store.Append(new ModelStreamingTextEvent(turn, "x"));
            Assert.True(await healthyDeltas.MoveNextAsync());
            Assert.Equal(index, healthyDeltas.Current.Sequence);
            Assert.Empty(healthyDeltas.Current.Ops.OfType<RowUpserted>());
            Assert.Equal(TurnPhase.Streaming, Assert.Single(healthyDeltas.Current.Ops.OfType<StatePatched>()).State.Phase);
            if (index == 1)
                AssertStreamingText(healthyDeltas.Current.Ops.OfType<RowAppended>().Select(op => op.Row), "x");
            else
                Assert.Equal(new RowTextAppended($"stream:{turn.Value}", "x", item.OccurredAt),
                    Assert.Single(healthyDeltas.Current.Ops.OfType<RowTextAppended>()));
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
        Assert.Equal(total, snapshot.Sequence);
        AssertStreamingText(snapshot.Rows, new string('x', total));
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
    public async Task HistoricalStreamingReplay_LargerThanDeltaBacklog_CatchesUpWithoutChangingOldSnapshot()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var turn = StrongId.NewTurnId();
        const string chunk = "0123456789abcdef";
        var chunks = LiveConversationProjection.MaxPendingDeltas * 2;
        for (var index = 0; index < chunks; index++)
            store.Append(new ModelStreamingTextEvent(turn, chunk));
        using var projection = new LiveConversationProjection(store, 0, () => 3);
        var snapshot = projection.Snapshot();
        Assert.Equal(chunks, snapshot.Sequence);
        AssertStreamingText(snapshot.Rows, string.Concat(Enumerable.Repeat(chunk, chunks)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var deltas = projection.Deltas(timeout.Token).GetAsyncEnumerator();
        var later = store.Append(new ModelStreamingTextEvent(turn, " tail"));
        Assert.True(await deltas.MoveNextAsync());
        Assert.Equal(later.Sequence, deltas.Current.Sequence);
        Assert.Equal(new RowTextAppended($"stream:{turn.Value}", " tail", later.OccurredAt),
            Assert.Single(deltas.Current.Ops.OfType<RowTextAppended>()));
        AssertStreamingText(projection.Snapshot().Rows, string.Concat(Enumerable.Repeat(chunk, chunks)) + " tail");
        AssertStreamingText(snapshot.Rows, string.Concat(Enumerable.Repeat(chunk, chunks)));
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

    [Fact]
    public void StreamingOps_InitializeOnceThenAppendVerbatim_WithImmutableSnapshots()
    {
        var builder = new ProjectionBuilder();
        var session = StrongId.NewSessionId();
        var turn = StrongId.NewTurnId();
        var at = DateTimeOffset.UtcNow;
        var first = builder.Apply(new SequencedEvent(1, session, at, new ModelStreamingTextEvent(turn, "first")));
        var seed = Assert.IsType<AssistantMessageRow>(Assert.Single(first.OfType<RowAppended>()).Row);
        AssertStreamingText([seed], "first");
        Assert.False(seed.CanFork);
        Assert.Equal(default, seed.Id);
        var before = builder.SnapshotState();

        var delta = builder.Apply(new SequencedEvent(2, session, at.AddSeconds(1),
            new ModelStreamingTextEvent(turn, "\n\ud83d\ude00 last")));
        Assert.Equal(2, delta.Count);
        Assert.Equal(new RowTextAppended(seed.RowId, "\n\ud83d\ude00 last", at.AddSeconds(1)), delta[0]);
        Assert.Equal(builder.State, Assert.IsType<StatePatched>(delta[1]).State);
        var json = AlcolerJson.Serialize(new ConversationDelta(2, delta));
        Assert.Contains("\"$type\":\"row_text_appended\"", json, StringComparison.Ordinal);
        var replay = AlcolerJson.Deserialize<ConversationDelta>(json)!;
        Assert.Equal(delta[0], Assert.IsType<RowTextAppended>(replay.Ops[0]));
        Assert.Equal(2, replay.Sequence);

        var after = builder.SnapshotState();
        var current = Assert.IsType<AssistantMessageRow>(Assert.Single(after.Rows));
        AssertStreamingText(after.Rows, "first\n\ud83d\ude00 last");
        Assert.Equal(at.AddSeconds(1), current.At);
        Assert.Same(current, Assert.Single(builder.Rows()));
        Assert.Same(seed, Assert.Single(before.Rows));
        AssertStreamingText(before.Rows, "first");
        Assert.Equal(at, seed.At);

        var empty = builder.Apply(new SequencedEvent(3, session, at.AddSeconds(2), new ModelStreamingTextEvent(turn, "")));
        Assert.Equal(new RowTextAppended(seed.RowId, "", at.AddSeconds(2)), empty[0]);
        Assert.Equal(at.AddSeconds(2), Assert.IsType<AssistantMessageRow>(Assert.Single(builder.Rows())).At);
        Assert.Equal(at.AddSeconds(1), current.At);
    }

    [Fact]
    public void CommitCorrection_ReplacesStreamAndPreservesOldRows_ThenMarksOnlyFinalRowForkable()
    {
        var builder = new ProjectionBuilder();
        var session = StrongId.NewSessionId();
        var turn = StrongId.NewTurnId();
        var at = DateTimeOffset.UtcNow;
        long sequence = 0;
        IReadOnlyList<RowOp> Apply(SessionEvent item) => builder.Apply(new SequencedEvent(++sequence, session, at, item));
        Apply(new TurnStartedEvent(turn, StrongId.NewCommandId()));
        Apply(new ModelStreamingTextEvent(turn, "discarded"));
        Apply(new ModelStreamingTextEvent(turn, " tail"));
        var old = builder.Rows();
        var message = new ConversationMessage(StrongId.NewMessageId(), MessageRole.Assistant,
            [new ReasoningPart(StrongId.NewPartId(), "reasoning"), new TextPart(StrongId.NewPartId(), "corrected")], at);
        var commit = Apply(new AssistantMessageCommittedEvent(message));
        Assert.Equal($"stream:{turn.Value}", Assert.IsType<RowRemoved>(commit[0]).RowId);
        var committed = Assert.IsType<AssistantMessageRow>(Assert.IsType<RowAppended>(commit[1]).Row);
        Assert.Same(message.Parts, committed.Parts);
        Assert.False(committed.IsStreaming);
        Assert.False(committed.CanFork);
        var completed = Apply(new TurnCompletedEvent(turn, TurnOutcome.Completed));
        Assert.True(Assert.IsType<AssistantMessageRow>(Assert.Single(completed.OfType<RowUpserted>()).Row).CanFork);
        Assert.True(Assert.IsType<AssistantMessageRow>(Assert.Single(builder.Rows())).CanFork);
        AssertStreamingText(old, "discarded tail");
        Assert.False(committed.CanFork);

        Apply(new ModelStreamingTextEvent(turn, "new step"));
        Assert.Equal(2, builder.Rows().Count);
        AssertStreamingText(builder.Rows().TakeLast(1), "new step");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalEvent_RetainsAllUncommittedText_WithoutChangingPriorSnapshots(bool failed)
    {
        var builder = new ProjectionBuilder();
        var session = StrongId.NewSessionId();
        var turn = StrongId.NewTurnId();
        var at = DateTimeOffset.UtcNow;
        long sequence = 0;
        void Apply(SessionEvent item) => builder.Apply(new SequencedEvent(++sequence, session, at, item));
        Apply(new TurnStartedEvent(turn, StrongId.NewCommandId()));
        Apply(new ModelStreamingTextEvent(turn, "partial"));
        var old = builder.Rows();
        Apply(new ModelStreamingTextEvent(turn, " answer"));
        Apply(failed ? new TurnFailedEvent(turn, "failed") : new TurnCompletedEvent(turn, TurnOutcome.Cancelled));
        AssertStreamingText(builder.Rows().OfType<AssistantMessageRow>(), "partial answer");
        AssertStreamingText(old, "partial");
        Assert.Equal(failed ? TurnPhase.Error : TurnPhase.Idle, builder.State.Phase);
        Assert.Null(builder.State.ActiveTurn);
        var nextTurn = StrongId.NewTurnId();
        Apply(new TurnStartedEvent(nextTurn, StrongId.NewCommandId()));
        Apply(new ModelStreamingTextEvent(nextTurn, "next"));
        AssertStreamingText(builder.Rows().OfType<AssistantMessageRow>().TakeLast(1), "next");
        AssertStreamingText(old, "partial");
    }

    [Fact]
    public void Rewind_RemovesStreamAndResetsAppendBaseline()
    {
        var builder = new ProjectionBuilder();
        var session = StrongId.NewSessionId();
        var turn = StrongId.NewTurnId();
        var at = DateTimeOffset.UtcNow;
        long sequence = 0;
        IReadOnlyList<RowOp> Apply(SessionEvent item) => builder.Apply(new SequencedEvent(++sequence, session, at, item));
        var message = new ConversationMessage(StrongId.NewMessageId(), MessageRole.User,
            [new TextPart(StrongId.NewPartId(), "question")], at, Source: MessageSource.RealUser);
        Apply(new UserMessageCommittedEvent(message));
        Apply(new TurnStartedEvent(turn, StrongId.NewCommandId()));
        Apply(new ModelStreamingTextEvent(turn, "before"));
        Apply(new ModelStreamingTextEvent(turn, " rewind"));
        var old = builder.Rows();
        var removed = Apply(new ConversationRewoundEvent(message.Id, default, [], 0));
        Assert.Equal(new[] { $"stream:{turn.Value}", message.Id.Value }, removed.OfType<RowRemoved>().Select(op => op.RowId));
        Assert.Empty(builder.Rows());
        var restarted = Apply(new ModelStreamingTextEvent(turn, "after rewind"));
        Assert.Single(restarted.OfType<RowAppended>());
        Assert.Empty(restarted.OfType<RowTextAppended>());
        AssertStreamingText(builder.Rows(), "after rewind");
        AssertStreamingText(old.OfType<AssistantMessageRow>(), "before rewind");
    }

    [Fact]
    public void StreamingAllocation_4096ChunksOf16Characters_ScalesLinearly()
    {
        MeasureStreamingAllocation(16);
        var small = MeasureStreamingAllocation(1024);
        var large = MeasureStreamingAllocation(4096);
        Assert.True(large < small * 6, $"1024 chunks allocated {small:N0} bytes; 4096 chunks allocated {large:N0} bytes.");
        Assert.True(large < 8 * 1024 * 1024, $"4096 chunks allocated {large:N0} bytes.");
    }

    private static long MeasureStreamingAllocation(int chunks)
    {
        const string chunk = "0123456789abcdef";
        var session = StrongId.NewSessionId();
        var turn = StrongId.NewTurnId();
        var at = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(1, chunks)
            .Select(index => new SequencedEvent(index, session, at, new ModelStreamingTextEvent(turn, chunk))).ToArray();
        var builder = new ProjectionBuilder();
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var item in events)
            builder.Apply(item);
        var rows = builder.Rows();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        AssertStreamingText(rows, string.Concat(Enumerable.Repeat(chunk, chunks)));
        return allocated;
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
