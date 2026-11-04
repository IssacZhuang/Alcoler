using System.Collections.Concurrent;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public class ProjectionStoreTests(Xunit.Abstractions.ITestOutputHelper output)
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
    public async Task AppendText_MaterializesImmutableRowsAndPreservesMetadata()
    {
        var id = StrongId.NewMessageId();
        var row = AssistantRow("a1", id, "initial", streaming: true) with { CanFork = true };
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State(), row));
        using (store)
        {
            var updates = new ConcurrentQueue<ProjectionUpdate>();
            store.Updated += updates.Enqueue;
            store.Start();
            await WaitUntil(() => updates.Count == 1);
            var original = Assert.IsType<AssistantMessageRow>(Assert.Single(store.Rows));
            var at = row.At.AddSeconds(1);

            projection.Emit(new ConversationDelta(2, [new RowTextAppended(row.RowId, " appended", at)]));
            await WaitUntil(() => updates.Any(update => update.Sequence == 2));
            var appended = Assert.IsType<AssistantMessageRow>(Assert.Single(store.Rows));
            var appendedText = Assert.IsType<TextPart>(Assert.Single(appended.Parts));
            Assert.Equal("initial appended", appendedText.Text);
            Assert.Equal(id, appended.Id);
            Assert.Equal(at, appended.At);
            Assert.True(appended.IsStreaming);
            Assert.True(appended.CanFork);
            Assert.Equal(Assert.IsType<TextPart>(Assert.Single(row.Parts)).Id, appendedText.Id);
            Assert.Equal("initial", Assert.IsType<TextPart>(Assert.Single(original.Parts)).Text);

            projection.Emit(new ConversationDelta(3, [new RowTextAppended(row.RowId, string.Empty, at.AddSeconds(1))]));
            await WaitUntil(() => updates.Any(update => update.Sequence == 3));
            var timestampOnly = Assert.IsType<AssistantMessageRow>(Assert.Single(store.Rows));
            Assert.Same(appendedText.Text, Assert.IsType<TextPart>(Assert.Single(timestampOnly.Parts)).Text);
            Assert.Equal(at.AddSeconds(1), timestampOnly.At);
            Assert.Equal(at, appended.At);

            var replacement = row with
            {
                Parts = [new TextPart(StrongId.NewPartId(), "corrected")],
                IsStreaming = false,
            };
            projection.Emit(new ConversationDelta(4, [new RowUpserted(replacement)]));
            await WaitUntil(() => updates.Any(update => update.Sequence == 4));
            Assert.Same(replacement, Assert.Single(store.Rows));
            Assert.Equal("initial appended", appendedText.Text);
        }
    }

    [Fact]
    public async Task QueuedAppendText_ConcatenatesEveryDeltaAndKeepsLatestTimestamp()
    {
        var scheduler = new QueuedUiScheduler();
        var row = AssistantRow("a1", StrongId.NewMessageId(), "initial", streaming: true);
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State(), row), scheduler: scheduler);
        using (store)
        {
            var updates = new List<ProjectionUpdate>();
            store.Updated += updates.Add;
            store.Start();
            await WaitUntil(() => scheduler.Count == 1);
            scheduler.RunNext();
            projection.Emit(new ConversationDelta(2, [new RowTextAppended(row.RowId, " one", row.At.AddSeconds(1))]));
            projection.Emit(new ConversationDelta(3, [new RowTextAppended(row.RowId, " two", row.At.AddSeconds(2))]));
            projection.Emit(new ConversationDelta(4,
            [
                new RowTextAppended(row.RowId, string.Empty, row.At.AddSeconds(3)),
                new StatePatched(TestSnapshots.State(contextUsageTokens: 42)),
            ]));
            await WaitUntil(() => store.State.ContextUsageTokens == 42 && scheduler.Count == 1);

            scheduler.RunNext();

            var update = Assert.Single(updates.Skip(1));
            var appended = Assert.Single(update.Ops.OfType<RowTextAppended>());
            Assert.Equal(" one two", appended.Delta);
            Assert.Equal(row.At.AddSeconds(3), appended.At);
            Assert.Equal(4, update.Sequence);
            var stored = Assert.IsType<AssistantMessageRow>(Assert.Single(store.Rows));
            Assert.Equal("initial one two", Assert.IsType<TextPart>(Assert.Single(stored.Parts)).Text);
        }
    }

    [Fact]
    public async Task QueuedAppendText_FullReplacementAndRemoveReaddDiscardSupersededAppends()
    {
        var scheduler = new QueuedUiScheduler();
        var row = AssistantRow("a1", StrongId.NewMessageId(), "initial", streaming: true);
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State(), row), scheduler: scheduler);
        using (store)
        using (var viewModel = new Alcoler.ViewModel.ConversationViewModel(new FakeAgentRuntime(), store))
        {
            var updates = new List<ProjectionUpdate>();
            store.Updated += updates.Add;
            store.Start();
            await WaitUntil(() => scheduler.Count == 1);
            scheduler.RunNext();
            projection.Emit(new ConversationDelta(2, [new RowTextAppended(row.RowId, " superseded", row.At)]));
            projection.Emit(new ConversationDelta(3, [new RowUpserted(row with
            {
                Parts = [new TextPart(StrongId.NewPartId(), "replacement")],
            })]));
            projection.Emit(new ConversationDelta(4, [new RowTextAppended(row.RowId, " removed", row.At)]));
            projection.Emit(new ConversationDelta(5, [new RowRemoved(row.RowId)]));
            projection.Emit(new ConversationDelta(6, [new RowAppended(row with
            {
                Parts = [new TextPart(StrongId.NewPartId(), "new")],
            })]));
            projection.Emit(new ConversationDelta(7,
            [
                new RowTextAppended(row.RowId, " suffix", row.At),
                new StatePatched(TestSnapshots.State(TurnPhase.Streaming, contextUsageTokens: 42)),
            ]));
            await WaitUntil(() => store.State.ContextUsageTokens == 42 && scheduler.Count == 1);

            scheduler.RunNext();

            var batch = Assert.Single(updates.Skip(1));
            Assert.Collection(batch.Ops,
                op => Assert.IsType<RowRemoved>(op),
                op => Assert.IsType<RowAppended>(op),
                op => Assert.Equal(" suffix", Assert.IsType<RowTextAppended>(op).Delta),
                op => Assert.IsType<StatePatched>(op));
            var assistant = Assert.IsType<Alcoler.ViewModel.AssistantMessageItemViewModel>(Assert.Single(viewModel.Items));
            Assert.Equal("new suffix", assistant.MarkdownText);
            Assert.Equal("new suffix", Assert.IsType<TextPart>(Assert.Single(
                Assert.IsType<AssistantMessageRow>(Assert.Single(store.Rows)).Parts)).Text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendText_RecoveryUsesAuthoritativeSnapshotAndClearsOldAccumulation(bool missingBaseline)
    {
        var row = AssistantRow("a1", StrongId.NewMessageId(), "initial", streaming: true);
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State(), missingBaseline ? [] : [row]));
        using (store)
        {
            var updates = new ConcurrentQueue<ProjectionUpdate>();
            store.Updated += updates.Enqueue;
            store.Start();
            await WaitUntil(() => updates.Count == 1);
            if (!missingBaseline)
            {
                projection.Emit(new ConversationDelta(2, [new RowTextAppended(row.RowId, " obsolete", row.At)]));
                await WaitUntil(() => updates.Any(update => update.Sequence == 2));
            }
            var authoritative = row with { Parts = [new TextPart(StrongId.NewPartId(), "authoritative")] };
            projection.SetSnapshot(Snapshot(4, TestSnapshots.State(), authoritative));
            projection.Emit(new ConversationDelta(missingBaseline ? 2 : 4,
                [new RowTextAppended(row.RowId, " discarded", row.At)]));
            await WaitUntil(() => updates.Count(update => update.Reset) == 2);
            projection.Emit(new ConversationDelta(5, [new RowTextAppended(row.RowId, " suffix", row.At)]));
            await WaitUntil(() => updates.Any(update => update.Sequence == 5));

            var stored = Assert.IsType<AssistantMessageRow>(Assert.Single(store.Rows));
            Assert.Equal("authoritative suffix", Assert.IsType<TextPart>(Assert.Single(stored.Parts)).Text);
            Assert.Equal("authoritative", Assert.IsType<TextPart>(Assert.Single(authoritative.Parts)).Text);
        }
    }

    [Fact]
    public async Task AppendText_PumpAndQueuedCoalescingAvoidFullPrefixAllocations()
    {
        const int count = 4096;
        const string delta = "0123456789abcdef";
        var row = AssistantRow("a1", StrongId.NewMessageId(), new string('x', 100_000), streaming: true);
        var deltas = Enumerable.Range(0, count).Select(index => new ConversationDelta(index + 2,
            [new RowTextAppended(row.RowId, delta, row.At.AddTicks(index))])).ToArray();
        var projection = new AllocationProjection(Snapshot(1, TestSnapshots.State(), row), deltas);
        var scheduler = new QueuedUiScheduler();
        using var store = new ProjectionStore(() => projection, StrongId.NewSessionId(), scheduler, TestSnapshots.State(),
            coalesceWindow: TimeSpan.FromHours(1));
        var updates = new List<ProjectionUpdate>();
        store.Updated += updates.Add;

        store.Start();
        var allocated = await projection.Allocated.Task.WaitAsync(TimeSpan.FromSeconds(10));

        output.WriteLine($"4,096 queued store appends: {allocated:N0} bytes allocated.");
        Assert.True(allocated < 4_000_000, $"Append pump allocated {allocated:N0} bytes.");
        scheduler.RunNext();
        scheduler.RunNext();
        var appended = Assert.Single(Assert.Single(updates, update => !update.Reset).Ops.OfType<RowTextAppended>());
        Assert.Equal(string.Concat(Enumerable.Repeat(delta, count)), appended.Delta);
        var stored = Assert.IsType<AssistantMessageRow>(Assert.Single(store.Rows));
        Assert.Equal(100_000 + count * delta.Length, Assert.IsType<TextPart>(Assert.Single(stored.Parts)).Text.Length);
        Assert.Equal(100_000, Assert.IsType<TextPart>(Assert.Single(row.Parts)).Text.Length);
    }

    [Fact]
    public async Task Initialized_ExposesCoherentStateBeforeUiDeliveryAndDoesNotRepeatInitialMaterialization()
    {
        var scheduler = new QueuedUiScheduler();
        var state = TestSnapshots.State(TurnPhase.Streaming) with { PlanEnabled = true };
        var (store, projection) = Create(new ConversationSnapshot(7, 4, [UserRow("u1", "ready")], state), scheduler: scheduler);
        using (store)
        {
            store.Start();
            await store.Initialized.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(7, store.Revision);
            using var conversation = new Alcoler.ViewModel.ConversationViewModel(new FakeAgentRuntime(), store);
            var repeats = 0;
            conversation.TimelineAppended += () => repeats++;
            Assert.Equal(TurnPhase.Streaming.ToString(), conversation.PhaseText);
            Assert.True(conversation.Composer.PlanEnabled);
            Assert.Single(conversation.Items);
            await WaitUntil(() => scheduler.Count == 1);
            scheduler.RunNext();
            Assert.Equal(0, repeats);
            Assert.Equal(1, projection.SnapshotCount);
        }
    }

    [Fact]
    public async Task ReaderAttachingBeforeInitialization_DoesNotSkipTheInitialSequenceZeroUpdate()
    {
        var scheduler = new QueuedUiScheduler();
        var state = TestSnapshots.State(TurnPhase.Streaming) with { PlanEnabled = true };
        var (store, projection) = Create(new ConversationSnapshot(0, 0, [UserRow("u1", "ready")], state), scheduler: scheduler);
        using (store)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            projection.BeforeSnapshot = () =>
            {
                entered.TrySetResult();
                Assert.True(release.Task.Wait(TimeSpan.FromSeconds(10)));
            };
            try
            {
                store.Start();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var conversation = new Alcoler.ViewModel.ConversationViewModel(new FakeAgentRuntime(), store);
                Assert.Empty(conversation.Items);
                release.TrySetResult();
                await store.Initialized.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitUntil(() => scheduler.Count == 1);
                scheduler.RunNext();
                Assert.Single(conversation.Items);
                Assert.Equal(TurnPhase.Streaming.ToString(), conversation.PhaseText);
                Assert.True(conversation.Composer.PlanEnabled);
            }
            finally
            {
                release.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task ReaderAttachingInsideCoalescedBatch_DoesNotRepeatTextOrLosePendingRowsAndState()
    {
        var scheduler = new QueuedUiScheduler();
        var row = AssistantRow("a1", StrongId.NewMessageId(), "initial", streaming: true);
        var changed = UserRow("changed", "before");
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State(TurnPhase.Streaming),
            row, changed, UserRow("removed-before", "remove"), UserRow("removed-after", "remove")), scheduler: scheduler);
        using (store)
        {
            store.Start();
            await WaitUntil(() => scheduler.Count == 1);
            scheduler.RunNext();
            using var existing = new ConversationViewModel(new FakeAgentRuntime(), store);
            using var otherExisting = new ConversationViewModel(new FakeAgentRuntime(), store);
            projection.Emit(new ConversationDelta(2,
            [
                new RowTextAppended("a1", " one", row.At),
                new RowRemoved("removed-before"),
                new RowUpserted(changed with { Text = "first change" }),
                new StatePatched(TestSnapshots.State(TurnPhase.Streaming, contextUsageTokens: 2)),
            ]));
            await WaitUntil(() => store.State.ContextUsageTokens == 2);
            using var conversation = new ConversationViewModel(new FakeAgentRuntime(), store);
            var appended = 0;
            conversation.TimelineAppended += () => appended++;
            projection.Emit(new ConversationDelta(3,
            [
                new RowTextAppended("a1", " two", row.At.AddSeconds(1)),
                new RowRemoved("removed-after"),
                new RowUpserted(changed with { Text = "second change" }),
                new RowAppended(UserRow("u2", "new row")),
                new StatePatched(TestSnapshots.State(TurnPhase.ExecutingTools, contextUsageTokens: 42)),
            ]));
            await WaitUntil(() => store.State.ContextUsageTokens == 42 && scheduler.Count == 1);
            scheduler.RunNext();

            foreach (var reader in new[] { existing, otherExisting, conversation })
            {
                Assert.Equal(new[] { "a1", "changed", "u2" }, reader.Items.Select(item => item.RowId));
                var assistant = Assert.IsType<AssistantMessageItemViewModel>(reader.Items[0]);
                Assert.Equal("initial one two", assistant.MarkdownText);
                Assert.Equal(row.At.AddSeconds(1), assistant.At);
                Assert.Equal("second change", Assert.IsType<UserMessageItemViewModel>(reader.Items[1]).Text);
                Assert.Equal(TurnPhase.ExecutingTools.ToString(), reader.PhaseText);
                Assert.Equal(42, reader.ContextUsage!.Usage.UsedTokens);
            }
            Assert.Equal(1, appended);
            Assert.Equal(0, scheduler.Count);
            Assert.Equal(1, projection.SnapshotCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderAttachingDuringDelivery_DoesNotReplayExtractedResetOrAppendBatches(bool duringReset)
    {
        var scheduler = new QueuedUiScheduler();
        var state = TestSnapshots.State(TurnPhase.Streaming);
        var row = AssistantRow("a1", StrongId.NewMessageId(), "initial", streaming: true);
        var (store, projection) = Create(Snapshot(1, state, row), scheduler: scheduler);
        using (store)
        using (var existing = new ConversationViewModel(new FakeAgentRuntime(), store))
        {
            ConversationViewModel? attached = null;
            var armed = false;
            var appended = 0;
            store.Updated += update =>
            {
                if (!armed || update.Reset != duringReset)
                    return;
                armed = false;
                Emit(3);
                attached = new ConversationViewModel(new FakeAgentRuntime(), store);
                attached.TimelineAppended += () => appended++;
                Emit(4);
                Assert.Equal(0, scheduler.Count);
            };
            try
            {
                store.Start();
                await WaitUntil(() => scheduler.Count == 1);
                if (!duringReset)
                    scheduler.RunNext();
                Emit(2);
                armed = true;
                scheduler.RunNext();
                while (scheduler.Count > 0)
                    scheduler.RunNext();

                Assert.NotNull(attached);
                foreach (var reader in new[] { existing, attached })
                {
                    Assert.Equal("initial 2 3 4", Assert.IsType<AssistantMessageItemViewModel>(Assert.Single(reader.Items)).MarkdownText);
                    Assert.Equal(4, reader.ContextUsage!.Usage.UsedTokens);
                }
                Assert.Equal(0, appended);
                Assert.Equal(1, projection.SnapshotCount);
            }
            finally
            {
                attached?.Dispose();
            }
        }

        void Emit(int sequence) => EmitAndWaitForState(store, projection, new ConversationDelta(sequence,
        [
            new RowTextAppended(row.RowId, $" {sequence}", row.At.AddTicks(sequence)),
            new StatePatched(state with { ContextUsageTokens = sequence }),
        ]));
    }

    [Fact]
    public async Task SynchronousDelivery_KeepsAllReaderCallbacksInSequenceWhenProducerAdvances()
    {
        var state = TestSnapshots.State(TurnPhase.Streaming);
        var row = AssistantRow("a1", StrongId.NewMessageId(), "initial", streaming: true);
        var projection = new FakeConversationProjection(Snapshot(1, state, row));
        var time = new CapturedTimerProvider();
        using var store = new ProjectionStore(() => projection, StrongId.NewSessionId(), InlineUiScheduler.Instance, state,
            coalesceWindow: TimeSpan.FromHours(1), timeProvider: time);
        var resets = 0;
        var depth = 0;
        var maximumDepth = 0;
        var sequences = new List<long>();
        store.Updated += update =>
        {
            if (update.Reset)
            {
                Interlocked.Increment(ref resets);
                return;
            }
            depth++;
            maximumDepth = Math.Max(maximumDepth, depth);
            sequences.Add(update.Sequence);
            if (update.Sequence == 2)
            {
                Emit(3);
                time.Timers[1].Fire();
            }
            depth--;
        };
        store.Start();
        await WaitUntil(() => Volatile.Read(ref resets) == 1);
        using var reader = new ConversationViewModel(new FakeAgentRuntime(), store);
        Emit(2);

        time.Timers[0].Fire();

        Assert.Equal(1, maximumDepth);
        Assert.Equal(new long[] { 2, 3 }, sequences);
        Assert.Equal("initial 2 3", Assert.IsType<AssistantMessageItemViewModel>(Assert.Single(reader.Items)).MarkdownText);
        Assert.Equal(3, reader.ContextUsage!.Usage.UsedTokens);

        void Emit(int sequence) => EmitAndWaitForState(store, projection, new ConversationDelta(sequence,
        [
            new RowTextAppended(row.RowId, $" {sequence}", row.At),
            new StatePatched(state with { ContextUsageTokens = sequence }),
        ]));
    }

    [Fact]
    public async Task DetachedReaders_DiscardPendingUpdatesBeforeUiDelivery()
    {
        var scheduler = new QueuedUiScheduler();
        var state = TestSnapshots.State(TurnPhase.Streaming);
        var (store, projection) = Create(Snapshot(1, state), scheduler: scheduler);
        using (store)
        {
            store.Start();
            await WaitUntil(() => scheduler.Count == 1);
            scheduler.RunNext();
            var detachedCalls = 0;
            Action<ProjectionUpdate> detached = _ => detachedCalls++;
            var liveUpdates = new List<ProjectionUpdate>();
            store.Attach(liveUpdates.Add, out _);
            for (var sequence = 2; sequence <= 129; sequence++)
            {
                store.Attach(detached, out _);
                EmitAndWaitForState(store, projection, new ConversationDelta(sequence,
                    [new StatePatched(state with { ContextUsageTokens = sequence })]));
                store.Detach(detached);
            }

            scheduler.RunNext();

            Assert.Equal(0, detachedCalls);
            Assert.Equal(129, Assert.Single(liveUpdates).Sequence);
            Assert.Equal(0, scheduler.Count);
            store.Detach(liveUpdates.Add);

            var disposed = AttachAndDisposeReader(store, projection, new ConversationDelta(130,
                [new StatePatched(state with { ContextUsageTokens = 130 })]));
            Assert.Equal(1, scheduler.Count);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(disposed.IsAlive);
            scheduler.RunNext();
        }
    }

    [Fact]
    public async Task ReaderRecovery_DiscardsEachReadersPendingOpsBeforeTheAuthoritativeReset()
    {
        var scheduler = new QueuedUiScheduler();
        var state = TestSnapshots.State(TurnPhase.Streaming);
        var row = AssistantRow("a1", StrongId.NewMessageId(), "initial", streaming: true);
        var (store, projection) = Create(Snapshot(1, state, row, UserRow("removed", "obsolete")), scheduler: scheduler);
        using (store)
        using (var existing = new ConversationViewModel(new FakeAgentRuntime(), store))
        {
            store.Start();
            await WaitUntil(() => scheduler.Count == 1);
            scheduler.RunNext();
            EmitAndWaitForState(store, projection, new ConversationDelta(2,
            [
                new RowTextAppended(row.RowId, " obsolete", row.At),
                new RowRemoved("removed"),
                new StatePatched(state with { ContextUsageTokens = 2 }),
            ]));
            using var attached = new ConversationViewModel(new FakeAgentRuntime(), store);
            projection.SetSnapshot(Snapshot(4, state with { ContextUsageTokens = 4 },
                row with { Parts = [new TextPart(StrongId.NewPartId(), "authoritative")] }, UserRow("restored", "snapshot")));
            projection.Emit(new ConversationDelta(4, [new RowTextAppended(row.RowId, " discarded", row.At)]));
            await WaitUntil(() => store.State.ContextUsageTokens == 4 && projection.SnapshotCount == 2);
            EmitAndWaitForState(store, projection, new ConversationDelta(5,
            [
                new RowTextAppended(row.RowId, " suffix", row.At),
                new RowRemoved("restored"),
                new StatePatched(state with { ContextUsageTokens = 5 }),
            ]));

            scheduler.RunNext();
            scheduler.RunNext();

            foreach (var reader in new[] { existing, attached })
            {
                Assert.Equal("authoritative suffix", Assert.IsType<AssistantMessageItemViewModel>(Assert.Single(reader.Items)).MarkdownText);
                Assert.Equal(5, reader.ContextUsage!.Usage.UsedTokens);
            }
            Assert.Equal(0, scheduler.Count);
            Assert.Equal(2, projection.SnapshotCount);
        }
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(15000)]
    public async Task ReaderAttachment_OverlappingProducerProgressDoesNotAllocateWithHistorySize(int rowCount)
    {
        const int batches = 100;
        var scheduler = new QueuedUiScheduler();
        var state = TestSnapshots.State(TurnPhase.Streaming);
        var rows = Enumerable.Range(0, rowCount).Select(index => (ConversationRow)UserRow($"u{index}", "history")).ToArray();
        var streaming = AssistantRow("streaming", StrongId.NewMessageId(), "initial", streaming: true);
        rows[^1] = streaming;
        var (store, projection) = Create(new ConversationSnapshot(0, 1, rows, state), scheduler: scheduler);
        using (store)
        {
            store.Start();
            await WaitUntil(() => scheduler.Count == 1);
            scheduler.RunNext();
            var sequence = 1;
            var armed = false;
            store.Updated += update =>
            {
                if (!update.Reset && armed)
                    Emit();
            };
            Emit();
            using var reader = new ConversationViewModel(new FakeAgentRuntime(), store);
            Emit();
            var appended = 0;
            reader.TimelineAppended += () => appended++;
            armed = true;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < batches; index++)
            {
                if (index > 0)
                    Emit();
                scheduler.RunNext();
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            armed = false;
            while (scheduler.Count > 0)
                scheduler.RunNext();

            output.WriteLine($"100 attachment-overlap deliveries with {rowCount:N0} rows: {allocated:N0} bytes allocated.");
            Assert.True(allocated < 1_000_000, $"Overlapping deliveries allocated {allocated:N0} bytes with {rowCount:N0} rows.");
            Assert.Equal(0, appended);
            Assert.Equal(sequence, reader.ContextUsage!.Usage.UsedTokens);
            Assert.Equal(rowCount, reader.Items.Count);
            Assert.Equal("initial" + new string('x', sequence - 1),
                Assert.IsType<AssistantMessageItemViewModel>(reader.Items[^1]).MarkdownText);
            Assert.Equal(1, projection.SnapshotCount);

            void Emit()
            {
                var next = ++sequence;
                EmitAndWaitForState(store, projection, new ConversationDelta(next,
                [
                    new RowTextAppended(streaming.RowId, "x", streaming.At),
                    new StatePatched(state with { ContextUsageTokens = next }),
                ]));
            }
        }
    }

    [Fact]
    public async Task Hub_ReacquireKeepsTheWarmStoreAndItsLiveStateWithoutAnotherProjection()
    {
        var runtime = new FakeAgentRuntime();
        var id = StrongId.NewSessionId();
        var projection = runtime.RegisterSession(id, Snapshot(1, TestSnapshots.State(), UserRow("u1", "ready")));
        using var hub = new SessionProjectionHub(runtime, InlineUiScheduler.Instance);
        var original = hub.Acquire(id);
        await original.Initialized.WaitAsync(TimeSpan.FromSeconds(5));
        hub.Release(id);
        projection.Emit(new ConversationDelta(2, [new StatePatched(TestSnapshots.State(TurnPhase.ExecutingTools))]));
        await WaitUntil(() => original.State.Phase == TurnPhase.ExecutingTools);

        var warm = hub.Acquire(id);

        Assert.Same(original, warm);
        Assert.Equal(TurnPhase.ExecutingTools, warm.CaptureUpdate().State.Phase);
        Assert.Equal(1, runtime.SubscribeCount);
        Assert.Equal(1, projection.SnapshotCount);
        hub.Release(id);
        hub.Dispose();
        Assert.Throws<ObjectDisposedException>(() => hub.Acquire(id));
    }

    [Fact]
    public async Task Hub_WarmTimersOnlyEvictTheirOwnReleaseAndIgnoreSupersededCallbacks()
    {
        var runtime = new FakeAgentRuntime();
        var first = StrongId.NewSessionId();
        var second = StrongId.NewSessionId();
        runtime.RegisterSession(first, Snapshot(0, TestSnapshots.State()));
        runtime.RegisterSession(second, Snapshot(0, TestSnapshots.State()));
        var time = new CapturedTimerProvider();
        using var hub = new SessionProjectionHub(runtime, InlineUiScheduler.Instance, timeProvider: time);
        var firstStore = hub.Acquire(first);
        var secondStore = hub.Acquire(second);
        await Task.WhenAll(firstStore.Initialized, secondStore.Initialized).WaitAsync(TimeSpan.FromSeconds(5));
        hub.Release(first);
        hub.Release(second);
        time.Timers[0].Fire();
        Assert.Same(secondStore, hub.Acquire(second));
        var reopened = hub.Acquire(first);
        await reopened.Initialized.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(firstStore, reopened);
        hub.Release(second);
        time.Timers[1].Fire();
        Assert.Same(secondStore, hub.Acquire(second));
        Assert.Equal(3, runtime.SubscribeCount);
        hub.Release(first);
        time.Timers[0].Fire();
        Assert.Same(reopened, hub.Acquire(first));
        hub.Release(first);
        hub.Release(second);
    }

    [Fact]
    public async Task Dispose_DoesNotWaitForABlockedInitialSnapshot()
    {
        var (store, projection) = Create(Snapshot(1, TestSnapshots.State()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        projection.BeforeSnapshot = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Task.Wait(TimeSpan.FromSeconds(10)));
        };
        try
        {
            store.Start();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(store.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Initialized);
        }
        finally
        {
            release.TrySetResult();
            store.Dispose();
        }
    }

    [Fact]
    public async Task InitialSubscriptionFailure_CompletesInitializationWithTheError()
    {
        using var store = new ProjectionStore(() => throw new InvalidOperationException("subscribe failed"),
            StrongId.NewSessionId(), InlineUiScheduler.Instance, TestSnapshots.State());
        store.Start();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.Initialized.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("subscribe failed", exception.Message);
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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndDisposeReader(ProjectionStore store, FakeConversationProjection projection, ConversationDelta delta)
    {
        using var reader = new ConversationViewModel(new FakeAgentRuntime(), store);
        EmitAndWaitForState(store, projection, delta);
        return new WeakReference(reader);
    }

    private static void EmitAndWaitForState(ProjectionStore store, FakeConversationProjection projection, ConversationDelta delta)
    {
        projection.Emit(delta);
        Assert.True(SpinWait.SpinUntil(() => store.State.ContextUsageTokens == delta.Sequence, TimeSpan.FromSeconds(10)),
            $"Projection did not advance to sequence {delta.Sequence}.");
    }

    private sealed class AllocationProjection(ConversationSnapshot snapshot, IReadOnlyList<ConversationDelta> deltas)
        : IConversationProjection
    {
        public TaskCompletionSource<long> Allocated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConversationSnapshot Snapshot() => snapshot;

        public async IAsyncEnumerable<ConversationDelta> Deltas(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var delta in deltas)
                yield return delta;
            Allocated.TrySetResult(GC.GetAllocatedBytesForCurrentThread() - before);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task WaitUntilSequenceAsync(long sequence, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class CapturedTimerProvider : TimeProvider
    {
        public List<CapturedTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new CapturedTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class CapturedTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
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
