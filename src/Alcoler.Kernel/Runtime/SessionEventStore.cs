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

    public SequencedEvent Append(SessionEvent sessionEvent)
    {
        ChannelWriter<SequencedEvent>[] writers;
        SequencedEvent sequenced;
        lock (_sync)
        {
            sequenced = new SequencedEvent(_nextSequence++, SessionId, _clock.GetUtcNow(), sessionEvent);
            _events.Add(sequenced);
            writers = [.. _subscribers];
        }

        foreach (var writer in writers)
        {
            writer.TryWrite(sequenced);
        }

        return sequenced;
    }

    public SessionEventSubscriber Subscribe(long afterSequence)
    {
        var channel = Channel.CreateUnbounded<SequencedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        List<SequencedEvent> replay;
        lock (_sync)
        {
            _subscribers.Add(channel.Writer);
            replay = _events.Where(e => e.Sequence > afterSequence).ToList();
        }

        foreach (var evt in replay)
        {
            channel.Writer.TryWrite(evt);
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
            return _events.Where(e => e.Sequence > afterSequence).ToList();
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
            _events.RemoveAll(e =>
                e.Sequence < beforeSequence
                && e.OccurredAt < cutoffUtc
                && SessionEventRetention.IsTransient(e.Event));
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
}
