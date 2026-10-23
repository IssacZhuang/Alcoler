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
