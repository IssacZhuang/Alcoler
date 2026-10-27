using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionEventStoreTests
{
    [Fact]
    public void Append_TransientOverflow_EvictsOldestToHalfCapAndKeepsDurableEvents()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var turnId = StrongId.NewTurnId();
        var transientTotal = SessionEventStore.MaxResidentTransientEvents + 1;
        var durableTotal = 100;
        var durableSequences = new List<long>();
        long lastSequence = 0;

        SequencedEvent Track(SequencedEvent sequenced)
        {
            Assert.True(sequenced.Sequence > lastSequence);
            lastSequence = sequenced.Sequence;
            return sequenced;
        }

        void AppendDurable(int index)
        {
            durableSequences.Add(Track(store.Append(new SessionTitleChangedEvent($"title-{index}"))).Sequence);
        }

        var durableAppended = 0;
        for (var transientAppended = 0; transientAppended < transientTotal; transientAppended++)
        {
            if (transientAppended % 20 == 0 && durableAppended < durableTotal)
            {
                AppendDurable(durableAppended);
                durableAppended++;
            }
            Track(store.Append(new ModelStreamingTextEvent(turnId, "delta")));
        }
        while (durableAppended < durableTotal)
        {
            AppendDurable(durableAppended);
            durableAppended++;
        }

        var resident = store.EventsFrom(0);
        var transientResident = resident.Count(e => SessionEventRetention.IsTransient(e.Event));
        Assert.InRange(transientResident, 0, SessionEventStore.MaxResidentTransientEvents / 2);
        Assert.Equal(durableSequences, resident.Where(e => !SessionEventRetention.IsTransient(e.Event)).Select(e => e.Sequence));
        Assert.Equal(transientResident + durableTotal, resident.Count);
        Assert.Equal(transientResident, store.TransientCount);
        for (var index = 1; index < resident.Count; index++)
        {
            Assert.True(resident[index].Sequence > resident[index - 1].Sequence);
        }
        Assert.Equal(lastSequence, store.LastSequence);
        Assert.Equal((long)(transientTotal + durableTotal), store.LastSequence);
    }

    [Fact]
    public void EvictTransientOlderThan_MixedCutoffs_TransientCountMatchesResidentTransients()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var turnId = StrongId.NewTurnId();
        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 10; index++)
        {
            store.Append(new ModelStreamingTextEvent(turnId, "delta"), older);
            store.Append(new SessionTitleChangedEvent($"old-{index}"), older);
        }
        for (var index = 0; index < 10; index++)
        {
            store.Append(new ModelStreamingReasoningEvent(turnId, "reasoning"), newer);
            store.Append(new SessionTitleChangedEvent($"new-{index}"), newer);
        }
        Assert.Equal(20, store.TransientCount);

        store.EvictTransientOlderThan(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), store.LastSequence + 1);

        var resident = store.EventsFrom(0);
        var transientResident = resident.Count(e => SessionEventRetention.IsTransient(e.Event));
        Assert.Equal(10, transientResident);
        Assert.Equal(transientResident, store.TransientCount);
        Assert.Equal(20, resident.Count(e => !SessionEventRetention.IsTransient(e.Event)));
    }

    [Fact]
    public void Append_TransientEviction_SubscriberReceivesEveryAppendedEvent()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var turnId = StrongId.NewTurnId();
        using var subscription = store.Subscribe(0);

        var appendedSequences = new List<long>();
        for (var index = 0; index < SessionEventStore.MaxResidentTransientEvents + 100; index++)
        {
            var sequenced = store.Append(index % 25 == 0
                ? new SessionTitleChangedEvent($"title-{index}")
                : new ModelStreamingTextEvent(turnId, "delta"));
            appendedSequences.Add(sequenced.Sequence);
        }
        Assert.True(store.EventsFrom(0).Count < appendedSequences.Count);

        var receivedSequences = new List<long>();
        while (subscription.Reader.TryRead(out var evt))
        {
            receivedSequences.Add(evt.Sequence);
        }

        Assert.Equal(appendedSequences, receivedSequences);
    }
}
