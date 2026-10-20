using Alcoler.Contracts.Projections;
using Alcoler.Workbench.Ui;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Workbench.Stores;

public sealed record ProjectionUpdate(
    long Sequence,
    ConversationState State,
    IReadOnlyList<RowOp> Ops,
    bool Reset,
    IReadOnlyList<ConversationRow>? ResetRows = null);

/// <summary>
/// UI-side data layer over an <see cref="IAgentRuntime"/> projection: wholesale snapshot on
/// subscribe, deltas applied only when sequence-contiguous, any gap triggering a resubscribe
/// with a fresh snapshot. Updates are coalesced into a flush window and delivered on the UI
/// thread via <see cref="Updated"/>.
/// </summary>
public sealed class ProjectionStore : IDisposable
{
    private static readonly TimeSpan ResubscribeDelay = TimeSpan.FromMilliseconds(50);

    private readonly Func<IConversationProjection> _subscribe;
    private readonly IUiScheduler _scheduler;
    private readonly TimeSpan? _coalesceWindow;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly List<ConversationRow> _rows = [];
    private readonly Dictionary<string, int> _rowIndex = [];

    private IConversationProjection? _projection;
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;
    private ConversationState _state;
    private long _sequence;
    private long _revision;
    private List<RowOp>? _pending;
    private ITimer? _flushTimer;
    private bool _disposed;

    public ProjectionStore(
        Func<IConversationProjection> subscribe,
        SessionId sessionId,
        IUiScheduler scheduler,
        ConversationState initialState,
        TimeSpan? coalesceWindow = null,
        TimeProvider? timeProvider = null)
    {
        _subscribe = subscribe;
        SessionId = sessionId;
        _scheduler = scheduler;
        _state = initialState;
        _coalesceWindow = coalesceWindow;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SessionId SessionId { get; }

    public ConversationState State
    {
        get { lock (_sync) return _state; }
    }

    public IReadOnlyList<ConversationRow> Rows
    {
        get { lock (_sync) return [.. _rows]; }
    }

    public long Revision
    {
        get { lock (_sync) return _revision; }
    }

    public event Action<ProjectionUpdate>? Updated;

    /// <summary>Revision advances only through command acks; snapshots never move it backwards.</summary>
    public void SetRevision(long revision)
    {
        lock (_sync)
        {
            if (revision > _revision)
            {
                _revision = revision;
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pump is not null)
            {
                return;
            }
            _pumpCts = new CancellationTokenSource();
            _pump = Task.Run(() => RunAsync(_pumpCts.Token));
        }
    }

    public bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _pumpCts?.Cancel();
            _pumpCts?.Dispose();
            _flushTimer?.Dispose();
            _flushTimer = null;
            _projection?.Dispose();
            _projection = null;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IConversationProjection projection;
            try
            {
                lock (_sync)
                {
                    _projection = _subscribe();
                    projection = _projection;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The session runtime is gone; there is nothing left to observe.
                break;
            }

            var outcome = PumpOutcome.Failed;
            try
            {
                outcome = await PumpOnceAsync(projection, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_sync)
                {
                    projection.Dispose();
                    if (_projection == projection)
                    {
                        _projection = null;
                    }
                }
            }

            if (outcome == PumpOutcome.Failed || ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(ResubscribeDelay, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private enum PumpOutcome
    {
        Ended,
        Gap,
        Failed,
    }

    private async Task<PumpOutcome> PumpOnceAsync(IConversationProjection projection, CancellationToken ct)
    {
        var snapshot = projection.Snapshot();
        lock (_sync)
        {
            _rows.Clear();
            _rowIndex.Clear();
            foreach (var row in snapshot.Rows)
            {
                _rowIndex[row.RowId] = _rows.Count;
                _rows.Add(row);
            }
            _state = snapshot.State;
            _sequence = snapshot.Sequence;
            _pending = null;
        }
        Publish(new ProjectionUpdate(snapshot.Sequence, snapshot.State, [], Reset: true, ResetRows: snapshot.Rows));

        await foreach (var delta in projection.Deltas(ct).ConfigureAwait(false))
        {
            ProjectionUpdate? immediate = null;
            lock (_sync)
            {
                if (delta.Sequence != _sequence + 1)
                {
                    return PumpOutcome.Gap;
                }
                _sequence = delta.Sequence;
                if (delta.Ops.Count == 0)
                {
                    continue;
                }
                ApplyOps(delta.Ops);

                if (_coalesceWindow is { } window)
                {
                    _pending ??= [];
                    _pending.AddRange(delta.Ops);
                    _flushTimer ??= _timeProvider.CreateTimer(
                        static state => ((ProjectionStore)state!).Flush(),
                        this,
                        window,
                        Timeout.InfiniteTimeSpan);
                }
                else
                {
                    immediate = new ProjectionUpdate(_sequence, _state, [.. delta.Ops], Reset: false);
                }
            }
            if (immediate is not null)
            {
                Publish(immediate);
            }
        }
        return PumpOutcome.Ended;
    }

    private void ApplyOps(IReadOnlyList<RowOp> ops)
    {
        foreach (var op in ops)
        {
            switch (op)
            {
                case StatePatched patched:
                    _state = patched.State;
                    break;

                case RowAppended appended:
                    if (_rowIndex.TryAdd(appended.Row.RowId, _rows.Count))
                    {
                        _rows.Add(appended.Row);
                    }
                    break;

                case RowUpserted upserted:
                    if (_rowIndex.TryGetValue(upserted.Row.RowId, out var index))
                    {
                        _rows[index] = upserted.Row;
                    }
                    else
                    {
                        _rowIndex[upserted.Row.RowId] = _rows.Count;
                        _rows.Add(upserted.Row);
                    }
                    break;

                case RowRemoved removed:
                    if (_rowIndex.TryGetValue(removed.RowId, out var removeIndex))
                    {
                        _rows.RemoveAt(removeIndex);
                        _rowIndex.Remove(removed.RowId);
                        for (var i = removeIndex; i < _rows.Count; i++)
                        {
                            _rowIndex[_rows[i].RowId] = i;
                        }
                    }
                    break;
            }
        }
    }

    private void Flush()
    {
        ProjectionUpdate? update = null;
        lock (_sync)
        {
            if (_pending is { Count: > 0 } ops)
            {
                update = new ProjectionUpdate(_sequence, _state, [.. ops], Reset: false);
            }
            _pending = null;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        if (update is not null)
        {
            Publish(update);
        }
    }

    private void Publish(ProjectionUpdate update)
    {
        _scheduler.Post(() => Updated?.Invoke(update));
    }
}
