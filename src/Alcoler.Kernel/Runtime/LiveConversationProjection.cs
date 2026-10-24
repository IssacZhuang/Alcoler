using System.Threading.Channels;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Projections;

namespace Alcoler.Kernel.Runtime;

public sealed class LiveConversationProjection : IConversationProjection
{
    private readonly ProjectionBuilder _builder = new();
    private readonly SessionEventSubscriber _subscriber;
    private readonly Channel<ConversationDelta> _deltas;
    private readonly Task _pump;
    private readonly Func<long> _revisionSource;
    private readonly object _waitLock = new();
    private readonly List<Waiter> _waiters = [];
    private long _appliedSequence;
    private long _lastSequence;
    private bool _disposed;

    private sealed class Waiter(long sequence)
    {
        public long Sequence { get; } = sequence;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
    }

    public LiveConversationProjection(SessionEventStore store, long fromSequence, Func<long> revisionSource)
    {
        _subscriber = store.Subscribe(fromSequence);
        _revisionSource = revisionSource;
        _deltas = Channel.CreateUnbounded<ConversationDelta>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        // The initial snapshot must be consistent the moment construction returns; only events
        // arriving after this point are delivered as deltas.
        while (_subscriber.Reader.TryRead(out var sequenced))
        {
            ApplyToBuilder(sequenced);
        }
        _pump = Task.Run(() => PumpAsync());
    }

    public ConversationSnapshot Snapshot()
    {
        var (rows, state) = _builder.SnapshotState();
        return new ConversationSnapshot(_revisionSource(), _lastSequence, rows, state);
    }

    public async IAsyncEnumerable<ConversationDelta> Deltas(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var delta in _deltas.Reader.ReadAllAsync(cancellationToken))
        {
            yield return delta;
        }
    }

    public Task WaitUntilSequenceAsync(long sequence, CancellationToken cancellationToken = default)
    {
        lock (_waitLock)
        {
            if (_appliedSequence >= sequence)
            {
                return Task.CompletedTask;
            }
            var waiter = new Waiter(sequence);
            _waiters.Add(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() =>
                {
                    // Dispose runs after TrySetCanceled; disposing under _waitLock could
                    // deadlock if the registration's callback is blocked on that lock.
                    waiter.Registration.Dispose();
                    lock (_waitLock)
                    {
                        _waiters.Remove(waiter);
                    }
                    waiter.Completion.TrySetCanceled(cancellationToken);
                });
            }
            return waiter.Completion.Task;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _subscriber.Dispose();
        _deltas.Writer.TryComplete();
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var sequenced in _subscriber.Reader.ReadAllAsync())
            {
                if (ApplyToBuilder(sequenced) is { } ops)
                {
                    await _deltas.Writer.WriteAsync(new ConversationDelta(sequenced.Sequence, ops));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _deltas.Writer.TryComplete();
        }
    }

    private IReadOnlyList<RowOp>? ApplyToBuilder(SequencedEvent sequenced)
    {
        if (sequenced.Sequence <= _lastSequence)
        {
            return null;
        }
        Interlocked.Exchange(ref _lastSequence, sequenced.Sequence);
        var ops = _builder.Apply(sequenced);
        // Advance only after the builder applied the event, so a completed wait always
        // observes the event's effect in the next Snapshot.
        List<Waiter>? completed = null;
        lock (_waitLock)
        {
            if (sequenced.Sequence > _appliedSequence)
            {
                _appliedSequence = sequenced.Sequence;
                for (var index = _waiters.Count - 1; index >= 0; index--)
                {
                    var waiter = _waiters[index];
                    if (waiter.Sequence <= sequenced.Sequence)
                    {
                        completed ??= [];
                        completed.Add(waiter);
                        _waiters.RemoveAt(index);
                    }
                }
            }
        }
        if (completed is not null)
        {
            // Disposed outside _waitLock: a cancel callback blocked on that lock could
            // otherwise stall Dispose, which waits on in-flight callbacks.
            foreach (var waiter in completed)
            {
                waiter.Registration.Dispose();
                waiter.Completion.TrySetResult();
            }
        }
        return ops;
    }
}
