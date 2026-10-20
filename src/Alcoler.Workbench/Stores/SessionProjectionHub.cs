using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Workbench.Ui;

namespace Alcoler.Workbench.Stores;

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
            if (_stores.TryGetValue(sessionId, out var entry))
            {
                entry.WarmTimer?.Dispose();
                entry.WarmTimer = null;
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
            entry.WarmTimer = _timeProvider.CreateTimer(
                static state => ((SessionProjectionHub)state!).Evict(),
                this,
                _keepWarm,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void Evict()
    {
        lock (_sync)
        {
            List<SessionId>? evicted = null;
            foreach (var (sessionId, entry) in _stores)
            {
                if (entry.RefCount <= 0)
                {
                    entry.WarmTimer?.Dispose();
                    entry.Store.Dispose();
                    (evicted ??= []).Add(sessionId);
                }
            }
            if (evicted is not null)
            {
                foreach (var sessionId in evicted)
                {
                    _stores.Remove(sessionId);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
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
    }
}
