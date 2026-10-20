namespace Alcoler.Kernel.Tests.Permissions;

// Time advances only via Advance; timers fire synchronously inside Advance once their due time passes.
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<TimerEntry> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _utcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var entry = new TimerEntry(
                this,
                callback,
                state,
                dueTime < TimeSpan.Zero ? DateTimeOffset.MaxValue : _utcNow + dueTime);
            _timers.Add(entry);
            return entry;
        }
    }

    public void Advance(TimeSpan elapsed)
    {
        TimerEntry[] due;
        lock (_gate)
        {
            _utcNow += elapsed;
            due = _timers.Where(t => t.DueAt <= _utcNow).ToArray();
        }
        foreach (var entry in due)
            entry.Fire();
    }

    private sealed class TimerEntry(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state,
        DateTimeOffset dueAt) : ITimer
    {
        public DateTimeOffset DueAt => dueAt;

        public void Fire() => callback(state);

        bool ITimer.Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        void IDisposable.Dispose()
        {
            lock (owner._gate) owner._timers.Remove(this);
        }

        ValueTask IAsyncDisposable.DisposeAsync()
        {
            ((IDisposable)this).Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
