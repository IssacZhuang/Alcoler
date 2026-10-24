using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using System.Threading.Channels;

namespace Alcoler.Kernel.Runtime;

public sealed class SessionEventSubscriber : IDisposable
{
    private readonly Action _dispose;

    internal SessionEventSubscriber(ChannelReader<SequencedEvent> reader, Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    public ChannelReader<SequencedEvent> Reader { get; }

    public void Dispose() => _dispose();
}

public sealed class SessionEventStore
{
    private readonly object _sync = new();
    private readonly List<SequencedEvent> _events = [];
    private readonly List<ChannelWriter<SequencedEvent>> _subscribers = [];
    private readonly TimeProvider _clock;
    private long _nextSequence = 1;

    public SessionEventStore(SessionId sessionId, TimeProvider clock)
    {
        SessionId = sessionId;
        _clock = clock;
    }

    public SessionId SessionId { get; }

    public long LastSequence { get { lock (_sync) return _nextSequence - 1; } }

    public void AdvanceSequenceTo(long lastSequence)
    {
        lock (_sync)
        {
            if (_events.Count > 0 || _subscribers.Count > 0)
                throw new InvalidOperationException("Sequence must be restored before replay or subscription.");
            _nextSequence = Math.Max(_nextSequence, checked(lastSequence + 1));
        }
    }

    public SequencedEvent Append(SessionEvent sessionEvent, DateTimeOffset? occurredAt = null)
    {
        lock (_sync)
        {
            var sequenced = new SequencedEvent(_nextSequence++, SessionId, occurredAt ?? _clock.GetUtcNow(), sessionEvent);
            _events.Add(sequenced);
            foreach (var writer in _subscribers)
            {
                writer.TryWrite(sequenced);
            }
            return sequenced;
        }
    }

    public SessionEventSubscriber Subscribe(long afterSequence)
    {
        var channel = Channel.CreateUnbounded<SequencedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_sync)
        {
            _subscribers.Add(channel.Writer);
            // Replay must interleave with live appends under the lock, or a concurrent append
            // could reach the channel ahead of older replayed events.
            for (var index = FirstIndexAfter(afterSequence); index < _events.Count; index++)
            {
                channel.Writer.TryWrite(_events[index]);
            }
        }

        return new SessionEventSubscriber(channel.Reader, () =>
        {
            lock (_sync)
            {
                _subscribers.Remove(channel.Writer);
            }
            channel.Writer.TryComplete();
        });
    }

    public IReadOnlyList<SequencedEvent> EventsFrom(long afterSequence)
    {
        lock (_sync)
        {
            var start = FirstIndexAfter(afterSequence);
            return _events.GetRange(start, _events.Count - start);
        }
    }

    public IReadOnlyList<SequencedEvent> DurableEvents()
    {
        lock (_sync)
        {
            return _events.Where(e => !SessionEventRetention.IsTransient(e.Event)).ToList();
        }
    }

    // Turn-window retention: transient events lose value once their turn is sealed.
    public void EvictTransientOlderThan(DateTimeOffset cutoffUtc, long beforeSequence)
    {
        lock (_sync)
        {
            // Sequences below beforeSequence occupy a prefix of the list, so the scan can
            // stop at that bound instead of walking the whole log.
            var bound = FirstIndexAtOrAbove(beforeSequence);
            var write = 0;
            for (var read = 0; read < bound; read++)
            {
                var evt = _events[read];
                if (evt.OccurredAt < cutoffUtc && SessionEventRetention.IsTransient(evt.Event))
                    continue;
                if (write != read)
                    _events[write] = evt;
                write++;
            }
            if (write < bound)
                _events.RemoveRange(write, bound - write);
        }
    }

    public void DropAll()
    {
        lock (_sync)
        {
            _events.Clear();
            foreach (var subscriber in _subscribers)
            {
                subscriber.TryComplete();
            }
            _subscribers.Clear();
        }
    }

    // Sequences are appended strictly ascending, but transient eviction can leave gaps, so
    // slicing goes through a binary search instead of index arithmetic.
    private int FirstIndexAfter(long sequence) => FirstIndexAtOrAbove(sequence + 1);

    private int FirstIndexAtOrAbove(long sequence)
    {
        var low = 0;
        var high = _events.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_events[middle].Sequence < sequence)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }
}
