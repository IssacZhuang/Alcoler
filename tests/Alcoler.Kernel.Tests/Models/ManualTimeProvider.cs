namespace Alcoler.Kernel.Tests.Models;

/// <summary>
/// TimeProvider whose timers only fire when time is advanced with <see cref="Advance"/>,
/// or immediately on a worker thread when <see cref="AutoFireTimers"/> is set
/// (so arbitrary delays pass without real waiting).
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly DateTimeOffset _origin = DateTimeOffset.UtcNow;
    private long _offsetTicks;

    public bool AutoFireTimers { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _origin.AddTicks(_offsetTicks);
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, period);
        lock (_gate)
            _timers.Add(timer);
        Schedule(timer, dueTime);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _offsetTicks += by.Ticks;
            var now = _origin.AddTicks(_offsetTicks);
            due = _timers
                .Where(t => t.IsDue(now))
                .ToList();
        }
        foreach (var timer in due)
            timer.Fire();
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime)
    {
        if (dueTime == Timeout.InfiniteTimeSpan)
            return;

        lock (_gate)
            timer.SetDueTime(_origin.AddTicks(_offsetTicks).Add(dueTime));

        if (AutoFireTimers && dueTime > TimeSpan.Zero)
        {
            ThreadPool.QueueUserWorkItem(_ => timer.Fire());
            return;
        }
        if (dueTime <= TimeSpan.Zero)
            timer.Fire();
    }

    internal sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state,
        TimeSpan period) : ITimer
    {
        private readonly object _gate = new();
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = period;
        private bool _disposed;

        public void SetDueTime(DateTimeOffset dueAt)
        {
            lock (_gate)
                _dueAt = dueAt;
        }

        public bool IsDue(DateTimeOffset now)
        {
            lock (_gate)
                return !_disposed && _dueAt is { } due && due <= now;
        }

        public void Fire()
        {
            lock (_gate)
            {
                if (_disposed || _dueAt is null)
                    return;
                _dueAt = _period == Timeout.InfiniteTimeSpan
                    ? null
                    : _dueAt.Value + _period;
            }
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;
                _period = period;
            }
            owner.Schedule(this, dueTime);
            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _dueAt = null;
            }
            lock (owner._gate)
                owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
