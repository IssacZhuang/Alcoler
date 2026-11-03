using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Ui;

namespace Alcoler.Stores;

/// <summary>
/// Ref-counted per-session projection stores. The last release keeps the store warm for a
/// grace period so switching sessions back and forth does not resubscribe every time.
/// </summary>
public sealed class SessionProjectionHub : IDisposable
{
    private readonly IAgentRuntime _runtime;
    private readonly IUiScheduler _scheduler;
    private readonly TimeSpan _keepWarm;
    private readonly TimeSpan? _coalesceWindow;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<SessionId, Entry> _stores = [];
    private bool _disposed;

    public SessionProjectionHub(
        IAgentRuntime runtime,
        IUiScheduler scheduler,
        TimeSpan? keepWarm = null,
        TimeSpan? coalesceWindow = null,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime;
        _scheduler = scheduler;
        _keepWarm = keepWarm ?? TimeSpan.FromSeconds(30);
        _coalesceWindow = coalesceWindow;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ProjectionStore Acquire(SessionId sessionId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stores.TryGetValue(sessionId, out var entry))
            {
                entry.WarmTimer?.Dispose();
                entry.WarmTimer = null;
                entry.WarmGeneration++;
                entry.RefCount++;
                return entry.Store;
            }

            var store = new ProjectionStore(
                () => _runtime.Subscribe(sessionId),
                sessionId,
                _scheduler,
                ConversationState.Initial(string.Empty, PermissionMode.Build, null, 0),
                _coalesceWindow,
                _timeProvider);
            store.Start();
            _stores[sessionId] = new Entry(store, refCount: 1);
            return store;
        }
    }

    public void Release(SessionId sessionId)
    {
        lock (_sync)
        {
            if (!_stores.TryGetValue(sessionId, out var entry))
            {
                return;
            }
            entry.RefCount--;
            if (entry.RefCount > 0)
            {
                return;
            }

            entry.WarmTimer?.Dispose();
            var generation = ++entry.WarmGeneration;
            entry.WarmTimer = _timeProvider.CreateTimer(
                _ => Evict(sessionId, entry, generation), null, _keepWarm, Timeout.InfiniteTimeSpan);
        }
    }

    private void Evict(SessionId sessionId, Entry released, long generation)
    {
        lock (_sync)
        {
            if (!_stores.TryGetValue(sessionId, out var entry) || !ReferenceEquals(entry, released) ||
                entry.RefCount > 0 || entry.WarmGeneration != generation)
                return;
            entry.WarmTimer?.Dispose();
            entry.Store.Dispose();
            _stores.Remove(sessionId);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            foreach (var entry in _stores.Values)
            {
                entry.WarmTimer?.Dispose();
                entry.Store.Dispose();
            }
            _stores.Clear();
        }
    }

    private sealed class Entry(ProjectionStore store, int refCount)
    {
        public ProjectionStore Store { get; } = store;
        public int RefCount { get; set; } = refCount;
        public ITimer? WarmTimer { get; set; }
        public long WarmGeneration { get; set; }
    }
}
