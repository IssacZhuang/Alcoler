using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Runtime;

/// <summary>
/// Single-slot signal for queued turn inputs. Version-based so a wait that starts after a
/// pulse still observes it; the completion source is replaced on each pulse so every
/// waiter wakes at most once per pulse.
/// </summary>
internal sealed class TurnInputSignal : ITurnInputSignal
{
    private readonly object _gate = new();
    private long _version;
    private TaskCompletionSource _pending = NewSource();

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public Task WaitAsync(long seenVersion, CancellationToken cancellationToken)
    {
        TaskCompletionSource pending;
        lock (_gate)
        {
            if (_version != seenVersion)
                return Task.CompletedTask;
            pending = _pending;
        }
        return pending.Task.WaitAsync(cancellationToken);
    }

    public void Pulse()
    {
        TaskCompletionSource released;
        lock (_gate)
        {
            _version++;
            released = _pending;
            _pending = NewSource();
        }
        released.TrySetResult();
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
