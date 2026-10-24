using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class LiveConversationProjectionTests
{
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
