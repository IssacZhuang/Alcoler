using System.Threading.Channels;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Projections;

namespace Alcoler.Kernel.Runtime;

public sealed class LiveConversationProjection : IConversationProjection
{
    internal const int MaxPendingDeltas = 256;

    private readonly ProjectionBuilder _builder = new();
    private readonly SessionEventSubscriber _subscriber;
    private readonly Channel<ConversationDelta> _deltas;
    private readonly Task _pump;
    private readonly Func<long> _revisionSource;
    private readonly object _sync = new();
    private readonly List<Waiter> _waiters = [];
    private long _lastSequence;
    private bool _completed;

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
        _deltas = Channel.CreateBounded<ConversationDelta>(new BoundedChannelOptions(MaxPendingDeltas)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
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
        lock (_sync)
        {
            var (rows, state) = _builder.SnapshotState();
            return new ConversationSnapshot(_revisionSource(), _lastSequence, rows, state);
        }
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
        lock (_sync)
        {
            if (_lastSequence >= sequence)
            {
                return Task.CompletedTask;
            }
            if (_completed)
            {
                return Task.FromException(new InvalidOperationException("The projection ended before reaching the requested sequence."));
            }
            var waiter = new Waiter(sequence);
            _waiters.Add(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() =>
                {
                    // Dispose runs after TrySetCanceled; disposing under _sync could
                    // deadlock if the registration's callback is blocked on that lock.
                    waiter.Registration.Dispose();
                    lock (_sync)
                    {
                        _waiters.Remove(waiter);
                    }
                    waiter.Completion.TrySetCanceled(cancellationToken);
                });
            }
            return waiter.Completion.Task;
        }
    }

    public void Dispose() => Complete();

    private void Complete()
    {
        List<Waiter> abandoned;
        lock (_sync)
        {
            if (_completed)
                return;
            _completed = true;
            abandoned = [.. _waiters];
            _waiters.Clear();
        }
        _subscriber.Dispose();
        _deltas.Writer.TryComplete();
        foreach (var waiter in abandoned)
        {
            waiter.Registration.Dispose();
            waiter.Completion.TrySetException(
                new InvalidOperationException("The projection ended before reaching the requested sequence."));
        }
    }

    private async Task PumpAsync()
    {
        var publishDeltas = true;
        try
        {
            await foreach (var sequenced in _subscriber.Reader.ReadAllAsync())
            {
                var ops = ApplyToBuilder(sequenced);
                if (publishDeltas && ops is not null
                    && !_deltas.Writer.TryWrite(new ConversationDelta(sequenced.Sequence, ops)))
                {
                    // End a slow delta feed without freezing snapshot-only observers.
                    publishDeltas = false;
                    _deltas.Writer.TryComplete();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Complete();
            while (_subscriber.Reader.TryRead(out _))
            {
            }
        }
    }

    private IReadOnlyList<RowOp>? ApplyToBuilder(SequencedEvent sequenced)
    {
        IReadOnlyList<RowOp> ops;
        List<Waiter>? completed = null;
        lock (_sync)
        {
            if (_completed || sequenced.Sequence <= _lastSequence)
            {
                return null;
            }
            ops = _builder.Apply(sequenced);
            _lastSequence = sequenced.Sequence;
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
        if (completed is not null)
        {
            // Disposed outside _sync: a cancel callback blocked on that lock could
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
