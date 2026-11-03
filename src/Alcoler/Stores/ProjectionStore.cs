using System.Text;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Projections;
using Alcoler.Ui;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Stores;

public sealed record ProjectionUpdate(
    long Sequence,
    ConversationState State,
    IReadOnlyList<RowOp> Ops,
    bool Reset,
    IReadOnlyList<ConversationRow>? ResetRows = null,
    long? FromSequence = null);

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
    private readonly Dictionary<string, StreamingRow> _streamingRows = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IConversationProjection? _projection;
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;
    private ConversationState _state;
    private long _sequence;
    private long _revision;
    private readonly PendingRowOps _pending = new();
    private long _pendingFirstSequence;
    private ConversationSnapshot? _pendingReset;
    private ITimer? _flushTimer;
    private bool _deliveryScheduled;
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

    public Task Initialized => _initialized.Task;

    public ProjectionUpdate CaptureUpdate() => CaptureUpdate(out _);

    internal ProjectionUpdate CaptureUpdate(out bool initialized)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            initialized = _initialized.Task.IsCompletedSuccessfully;
            return new ProjectionUpdate(_sequence, _state, [], Reset: true, ResetRows: MaterializeRows());
        }
    }

    public ConversationState State
    {
        get { lock (_sync) return _state; }
    }

    public IReadOnlyList<ConversationRow> Rows
    {
        get
        {
            lock (_sync)
            {
                return MaterializeRows();
            }
        }
    }

    private ConversationRow[] MaterializeRows()
    {
        var rows = _rows.ToArray();
        foreach (var (rowId, streaming) in _streamingRows)
            rows[_rowIndex[rowId]] = streaming.Materialize();
        return rows;
    }

    public long Revision
    {
        get { lock (_sync) return _revision; }
    }

    public event Action<ProjectionUpdate>? Updated;

    /// <summary>Older snapshots and acknowledgements must not move the revision backwards.</summary>
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
            var ct = _pumpCts.Token;
            _pump = Task.Run(() => RunAsync(ct));
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
            _initialized.TrySetCanceled();
            _pumpCts?.Cancel();
            _pumpCts?.Dispose();
            _flushTimer?.Dispose();
            _flushTimer = null;
            _pending.Clear();
            _pendingReset = null;
            _streamingRows.Clear();
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
                projection = _subscribe();
                lock (_sync)
                {
                    if (_disposed)
                    {
                        projection.Dispose();
                        return;
                    }
                    _projection = projection;
                }
            }
            catch (Exception exception)
            {
                _initialized.TrySetException(exception);
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
            catch (Exception exception)
            {
                _initialized.TrySetException(exception);
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
            ct.ThrowIfCancellationRequested();
            _rows.Clear();
            _rowIndex.Clear();
            _streamingRows.Clear();
            foreach (var row in snapshot.Rows)
            {
                _rowIndex[row.RowId] = _rows.Count;
                _rows.Add(row);
            }
            _state = snapshot.State;
            _sequence = snapshot.Sequence;
            _revision = Math.Max(_revision, snapshot.Revision);
            _pending.Clear();
            _pendingReset = snapshot;
            _initialized.TrySetResult();
        }
        RequestDelivery();

        await foreach (var delta in projection.Deltas(ct).ConfigureAwait(false))
        {
            var coalesced = _coalesceWindow is not null;
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
                if (!ApplyOps(delta.Ops))
                    return PumpOutcome.Gap;
                if (_pending.IsEmpty)
                    _pendingFirstSequence = delta.Sequence;
                _pending.Add(delta.Ops);

                if (_coalesceWindow is { } window)
                {
                    _flushTimer ??= _timeProvider.CreateTimer(
                        static state => ((ProjectionStore)state!).Flush(),
                        this,
                        window,
                        Timeout.InfiniteTimeSpan);
                }
            }
            if (!coalesced)
            {
                RequestDelivery();
            }
        }
        return PumpOutcome.Ended;
    }

    private bool ApplyOps(IReadOnlyList<RowOp> ops)
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

                case RowTextAppended appended:
                    if (!_streamingRows.TryGetValue(appended.RowId, out var streaming))
                    {
                        if (!_rowIndex.TryGetValue(appended.RowId, out var textIndex) ||
                            _rows[textIndex] is not AssistantMessageRow assistant ||
                            StreamingRow.Create(assistant) is not { } initialized)
                            return false;
                        _streamingRows.Add(appended.RowId, streaming = initialized);
                    }
                    streaming.Append(appended);
                    break;

                case RowUpserted upserted:
                    _streamingRows.Remove(upserted.Row.RowId);
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
                    _streamingRows.Remove(removed.RowId);
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
        return true;
    }

    private void Flush()
    {
        lock (_sync)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        RequestDelivery();
    }

    private void RequestDelivery()
    {
        lock (_sync)
        {
            if (_disposed || _deliveryScheduled ||
                (_pendingReset is null && _pending.IsEmpty))
            {
                return;
            }
            _deliveryScheduled = true;
        }
        _scheduler.PostBackground(Deliver);
    }

    private void Deliver()
    {
        ProjectionUpdate? update = null;
        lock (_sync)
        {
            _deliveryScheduled = false;
            if (_disposed)
                return;

            if (_pendingReset is { } reset)
            {
                _pendingReset = null;
                update = new ProjectionUpdate(reset.Sequence, reset.State, [], Reset: true, ResetRows: reset.Rows);
            }
            else if (_pending.Take() is { } ops)
            {
                update = new ProjectionUpdate(_sequence, _state, ops, Reset: false, FromSequence: _pendingFirstSequence);
            }
        }
        if (update is not null)
        {
            Updated?.Invoke(update);
        }
        RequestDelivery();
    }

    private sealed class StreamingRow(AssistantMessageRow baseline, int partIndex, TextPart part)
    {
        private readonly List<string> _segments = [part.Text];
        private int _length = part.Text.Length;
        private string? _text = part.Text;
        private DateTimeOffset _at = baseline.At;
        private AssistantMessageRow? _materialized;

        public static StreamingRow? Create(AssistantMessageRow row)
        {
            if (!row.IsStreaming)
                return null;
            var textIndex = -1;
            for (var index = 0; index < row.Parts.Count; index++)
            {
                if (row.Parts[index] is not TextPart)
                    continue;
                if (textIndex >= 0)
                    return null;
                textIndex = index;
            }
            return textIndex < 0 ? null : new StreamingRow(row, textIndex, (TextPart)row.Parts[textIndex]);
        }

        public void Append(RowTextAppended appended)
        {
            if (appended.Delta.Length > 0)
            {
                _segments.Add(appended.Delta);
                _length += appended.Delta.Length;
                _text = null;
            }
            _at = appended.At;
            _materialized = null;
        }

        public AssistantMessageRow Materialize()
        {
            if (_materialized is not null)
                return _materialized;
            _text ??= string.Create(_length, _segments, static (destination, segments) =>
            {
                var offset = 0;
                foreach (var segment in segments)
                {
                    segment.AsSpan().CopyTo(destination[offset..]);
                    offset += segment.Length;
                }
            });
            var parts = baseline.Parts.ToArray();
            parts[partIndex] = part with { Text = _text };
            return _materialized = baseline with { Parts = parts, At = _at };
        }
    }

    private sealed class PendingRowOps
    {
        private readonly List<PendingRow> _ops = [];
        private readonly Dictionary<string, PendingRow> _rowSlots = new(StringComparer.Ordinal);
        private StatePatched? _state;

        public bool IsEmpty => _ops.Count == 0 && _state is null;

        public void Add(IReadOnlyList<RowOp> ops)
        {
            foreach (var op in ops)
            {
                switch (op)
                {
                    case StatePatched patched:
                        _state = patched;
                        break;

                    case RowRemoved removed:
                        if (_rowSlots.Remove(removed.RowId, out var slot))
                            slot.Replace(removed);
                        else
                            _ops.Add(new PendingRow(removed));
                        break;

                    case RowAppended appended:
                        GetOrAdd(appended.Row.RowId).Replace(op);
                        break;

                    case RowUpserted upserted:
                        GetOrAdd(upserted.Row.RowId).Replace(op);
                        break;

                    case RowTextAppended appended:
                        GetOrAdd(appended.RowId).Append(appended);
                        break;
                }
            }
        }

        public IReadOnlyList<RowOp>? Take()
        {
            if (IsEmpty)
                return null;
            var count = _state is null ? 0 : 1;
            foreach (var pending in _ops)
                count += pending.Count;
            var result = new RowOp[count];
            var index = 0;
            foreach (var pending in _ops)
            {
                if (pending.Operation is { } operation)
                    result[index++] = operation;
                if (pending.TakeAppend() is { } appended)
                    result[index++] = appended;
            }
            if (_state is not null)
                result[index] = _state;
            Clear();
            return result;
        }

        public void Clear()
        {
            _ops.Clear();
            _rowSlots.Clear();
            _state = null;
        }

        private PendingRow GetOrAdd(string rowId)
        {
            if (_rowSlots.TryGetValue(rowId, out var slot))
                return slot;
            slot = new PendingRow(null);
            _rowSlots.Add(rowId, slot);
            _ops.Add(slot);
            return slot;
        }

        private sealed class PendingRow(RowOp? operation)
        {
            private RowTextAppended? _firstAppend;
            private RowTextAppended? _lastAppend;
            private StringBuilder? _appendBuilder;

            public RowOp? Operation { get; private set; } = operation;
            public int Count => (Operation is null ? 0 : 1) + (_firstAppend is null ? 0 : 1);

            public void Replace(RowOp op)
            {
                Operation = op;
                _firstAppend = _lastAppend = null;
                _appendBuilder = null;
            }

            public void Append(RowTextAppended appended)
            {
                if (_firstAppend is null)
                    _firstAppend = appended;
                else if (appended.Delta.Length > 0)
                {
                    _appendBuilder ??= new StringBuilder(_firstAppend.Delta);
                    _appendBuilder.Append(appended.Delta);
                }
                _lastAppend = appended;
            }

            public RowTextAppended? TakeAppend() => _firstAppend is { } first
                ? first with { Delta = _appendBuilder?.ToString() ?? first.Delta, At = _lastAppend!.At }
                : null;
        }
    }
}
