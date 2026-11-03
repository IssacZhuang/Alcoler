using System.Globalization;
using System.Text;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;

namespace Alcoler.Remote;

internal sealed class CompactTranscript
{
    private readonly List<CompactRowEntry> _rows = [];
    private readonly Dictionary<string, CompactRowEntry> _byId = new(StringComparer.Ordinal);
    private List<CompactTurn> _turns = [];
    private ConversationState _state;
    private readonly bool _paged;
    private int _start;
    private int _end;

    public CompactTranscript(ConversationSnapshot snapshot, bool paged = false, string? around = null)
    {
        _state = snapshot.State;
        _paged = paged;
        foreach (var row in snapshot.Rows)
        {
            var entry = new CompactRowEntry(row);
            _rows.Add(entry);
            _byId.Add(row.RowId, entry);
        }
        RebuildTurns();
        (_start, _end) = paged ? SelectWindow(around) : (0, _turns.Count);
        foreach (var turn in LoadedTurns())
            SetVisible(turn, BuildVisible(turn));
    }

    private HistoryBounds? History => _paged ? new HistoryBounds(
        _start > 0 ? _turns[_start].Rows[0].RowId : null,
        _end < _turns.Count ? _turns[_end - 1].Rows[0].RowId : null) : null;

    public byte[] SnapshotFrame(long revision, long sequence) =>
        CompactJson.SnapshotFrame(revision, sequence, VisibleRows(), _state, History);

    public byte[] Page(HistoryRequest request, long sequence)
    {
        var history = History!;
        if (request.Direction != "latest"
            && (request.Cursor is null || request.Cursor != (request.Direction == "before" ? history.Before : history.After)))
            return CompactJson.StaleHistory(request.RequestId, sequence, history);

        int start;
        int end;
        if (request.Direction == "latest")
        {
            (start, end) = SelectWindow(null);
            foreach (var turn in LoadedTurns()) SetVisible(turn, []);
            _start = start;
            _end = end;
        }
        else if (request.Direction == "before")
        {
            end = _start;
            start = Extend(end - 1, -1, CompactHistoryBudget.MaxTurns, 0).Index + 1;
            _start = start;
        }
        else
        {
            start = _end;
            end = Extend(start, 1, CompactHistoryBudget.MaxTurns, 0).Index;
            _end = end;
        }
        for (var index = start; index < end; index++)
            SetVisible(_turns[index], BuildVisible(_turns[index]));
        return CompactJson.HistoryFrame(request.RequestId, sequence,
            _turns.Skip(start).Take(end - start).SelectMany(turn => turn.Visible), History!, request.Direction);
    }

    public byte[] Apply(ConversationDelta delta, bool appendText)
    {
        var nextState = _state;
        foreach (var op in delta.Ops)
            if (op is StatePatched patched) nextState = patched.State;
        if (CanAppendDirectly(delta, nextState))
        {
            List<CompactOperation> direct = [];
            foreach (var op in delta.Ops)
            {
                if (op is RowTextAppended appended)
                {
                    var entry = _byId[appended.RowId];
                    entry.Append(appended);
                    if (entry.Visible is { } visible)
                    {
                        visible.Revision = entry.Revision;
                        direct.Add(appendText
                            ? new CompactOperation(Original: op)
                            : new CompactOperation("row_upserted", visible));
                    }
                }
                else
                    direct.Add(new CompactOperation(Original: op));
            }
            _state = nextState;
            return CompactJson.DeltaFrame(delta.Sequence, direct, History);
        }

        var regroup = delta.Ops.Any(op => op is RowRemoved || op switch
        {
            RowUpserted upserted when _byId.TryGetValue(upserted.Row.RowId, out var entry) =>
                (entry.Row is UserMessageRow) != (upserted.Row is UserMessageRow),
            _ => false,
        });
        var oldAll = regroup ? VisibleRows().ToList() : null;
        var loaded = regroup ? LoadedTurns().SelectMany(turn => turn.Rows).ToHashSet() : null;
        var followTail = _end == _turns.Count;
        HashSet<CompactTurn> dirty = [];
        foreach (var op in delta.Ops)
        {
            switch (op)
            {
                case RowAppended appended:
                    Put(appended.Row, dirty);
                    break;
                case RowUpserted upserted:
                    Put(upserted.Row, dirty);
                    break;
                case RowTextAppended appended when _byId.TryGetValue(appended.RowId, out var entry):
                    entry.Append(appended);
                    dirty.Add(entry.Turn!);
                    break;
                case RowRemoved removed when _byId.Remove(removed.RowId, out var entry):
                    entry.Removed = true;
                    break;
            }
        }
        if (IsActive(_state) != IsActive(nextState) && _turns.Count > 0)
            dirty.Add(_turns[^1]);
        _state = nextState;

        List<CompactOperation> operations;
        if (regroup)
        {
            _rows.RemoveAll(entry => entry.Removed);
            RebuildTurns();
            var surviving = _turns.Where(turn => turn.Rows.Any(loaded!.Contains)).ToList();
            if (!_paged)
                (_start, _end) = (0, _turns.Count);
            else if (surviving.Count == 0)
                (_start, _end) = SelectWindow(null);
            else
                (_start, _end) = (surviving[0].Index, followTail ? _turns.Count : surviving[^1].Index + 1);
            foreach (var turn in LoadedTurns())
                SetVisible(turn, BuildVisible(turn));
            operations = Difference(oldAll!, VisibleRows().ToList());
        }
        else
        {
            if (followTail) _end = _turns.Count;
            var changes = dirty.Where(IsLoaded).OrderBy(turn => turn.Index)
                .Select(turn => (Turn: turn, Rows: BuildVisible(turn))).ToList();
            var needsReorder = changes.Any(change => NeedsReorder(change.Turn, change.Rows));
            oldAll = needsReorder ? VisibleRows().ToList() : null;
            operations = [];
            foreach (var change in changes)
            {
                if (!needsReorder)
                    operations.AddRange(Difference(change.Turn.Visible, change.Rows));
                SetVisible(change.Turn, change.Rows);
            }
            if (needsReorder)
                operations = Difference(oldAll!, VisibleRows().ToList());
        }
        foreach (var op in delta.Ops)
            if (op is StatePatched) operations.Add(new CompactOperation(Original: op));
        return CompactJson.DeltaFrame(delta.Sequence, operations, History);
    }

    public static CompactTurnSummary? FindTurn(ConversationSnapshot snapshot, string rowId)
    {
        var start = -1;
        var end = snapshot.Rows.Count;
        for (var index = 0; index < snapshot.Rows.Count; index++)
        {
            if (snapshot.Rows[index] is not UserMessageRow user)
                continue;
            if (start >= 0)
            {
                end = index;
                break;
            }
            if (user.RowId == rowId)
                start = index;
        }
        if (start < 0)
            return null;
        var turn = new CompactTurn();
        for (var index = start; index < end; index++)
            turn.Rows.Add(new CompactRowEntry(snapshot.Rows[index]));
        return Summarize(turn, end == snapshot.Rows.Count && IsActive(snapshot.State));
    }

    private bool CanAppendDirectly(ConversationDelta delta, ConversationState nextState)
    {
        if (IsActive(_state) != IsActive(nextState))
            return false;
        foreach (var op in delta.Ops)
        {
            if (op is StatePatched)
                continue;
            if (op is not RowTextAppended appended
                || !_byId.TryGetValue(appended.RowId, out var entry)
                || IsLoaded(entry.Turn!) && entry.Visible is not { Summary: null }
                || !entry.CanAppend)
                return false;
        }
        return true;
    }

    private void Put(ConversationRow row, HashSet<CompactTurn> dirty)
    {
        if (_byId.TryGetValue(row.RowId, out var entry))
        {
            entry.Replace(row);
            dirty.Add(entry.Turn!);
            return;
        }
        entry = new CompactRowEntry(row);
        _rows.Add(entry);
        _byId.Add(row.RowId, entry);
        if (_turns.Count == 0 || row is UserMessageRow)
        {
            if (_turns.Count > 0)
                dirty.Add(_turns[^1]);
            _turns.Add(new CompactTurn { Index = _turns.Count });
        }
        entry.Turn = _turns[^1];
        entry.Turn.Rows.Add(entry);
        dirty.Add(entry.Turn);
    }

    private void RebuildTurns()
    {
        _turns = [];
        foreach (var entry in _rows)
        {
            entry.Visible = null;
            if (_turns.Count == 0 || entry.Row is UserMessageRow)
                _turns.Add(new CompactTurn { Index = _turns.Count });
            entry.Turn = _turns[^1];
            entry.Turn.Rows.Add(entry);
        }
    }

    private List<CompactWireRow> BuildVisible(CompactTurn turn)
    {
        var summary = Summarize(turn, ReferenceEquals(turn, _turns[^1]) && IsActive(_state));
        if (summary is null)
            return turn.Rows.Select(entry => new CompactWireRow(entry)).ToList();
        List<CompactWireRow> visible = [new(turn.Rows[0], summary)];
        for (var index = summary.FinalIndex + 1; index < turn.Rows.Count; index++)
            visible.Add(new CompactWireRow(turn.Rows[index]));
        return visible;
    }

    private static int SummaryFinalIndex(CompactTurn turn, bool active)
    {
        if (active || turn.Rows.Count < 3 || turn.Rows[0].Row is not UserMessageRow)
            return -1;
        var finalIndex = -1;
        for (var index = turn.Rows.Count - 1; index > 0; index--)
        {
            if (turn.Rows[index].Row is AssistantMessageRow assistant
                && assistant.Parts.OfType<TextPart>().Any(text => text.Text.Length > 0))
            {
                finalIndex = index;
                break;
            }
        }
        if (finalIndex < 2)
            return -1;
        for (var index = 1; index <= finalIndex; index++)
            if (Important(turn.Rows[index].Row)) return -1;
        return finalIndex;
    }

    private static CompactTurnSummary? Summarize(CompactTurn turn, bool active)
    {
        var finalIndex = SummaryFinalIndex(turn, active);
        if (finalIndex < 0)
            return null;
        var user = (UserMessageRow)turn.Rows[0].Row;
        var answer = (AssistantMessageRow)turn.Rows[finalIndex].Row;
        var finish = user.At;
        foreach (var entry in turn.Rows)
        {
            var candidate = entry.Row switch
            {
                AssistantMessageRow assistant => assistant.At,
                ToolCallRow tool => tool.StartedAt + (tool.Duration ?? TimeSpan.Zero),
                _ => finish,
            };
            if (candidate > finish) finish = candidate;
        }
        var duration = finish > user.At ? finish - user.At : TimeSpan.Zero;
        using var version = new CompactContentVersion();
        version.Add("compact-turn-1");
        version.Add(duration.ToString("c", CultureInfo.InvariantCulture));
        for (var index = 0; index <= finalIndex; index++)
            version.Add(turn.Rows[index].Version);
        var reasoning = answer.Parts.Any(part => part is ReasoningPart);
        return new CompactTurnSummary(turn, user, answer, finalIndex, duration,
            finalIndex - 1 + (reasoning ? 1 : 0), version.Finish());
    }

    private static bool Important(ConversationRow row) => row switch
    {
        AssistantMessageRow { IsStreaming: true } => true,
        ToolCallRow tool => tool.IsError || tool.Status is not ToolCallStatus.Succeeded,
        SystemNoticeRow notice => notice.Kind.Contains("error", StringComparison.OrdinalIgnoreCase)
            || notice.Kind.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || notice.Kind.Contains("pending", StringComparison.OrdinalIgnoreCase)
            || notice.Kind.Contains("running", StringComparison.OrdinalIgnoreCase)
            || notice.Kind.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || notice.Subagent?.Status is SubagentStatus.Running or SubagentStatus.Failed or SubagentStatus.Cancelled,
        _ => false,
    };

    private static bool IsActive(ConversationState state) => state.Phase is not (TurnPhase.Idle or TurnPhase.Error)
        || state.ActiveTurn is not null || state.PendingPermission is not null || state.PendingInteraction is not null;

    private bool NeedsReorder(CompactTurn turn, List<CompactWireRow> rows)
    {
        var oldIds = turn.Visible.Select(row => row.RowId).ToHashSet(StringComparer.Ordinal);
        var sawNew = false;
        foreach (var row in rows)
        {
            if (!oldIds.Contains(row.RowId))
                sawNew = true;
            else if (sawNew)
                return true;
        }
        return sawNew && !ReferenceEquals(turn, _turns[^1]);
    }

    private static List<CompactOperation> Difference(List<CompactWireRow> before, List<CompactWireRow> after)
    {
        var newIds = after.Select(row => row.RowId).ToHashSet(StringComparer.Ordinal);
        var retained = before.Where(row => newIds.Contains(row.RowId)).ToList();
        var prefix = 0;
        while (prefix < retained.Count && prefix < after.Count && retained[prefix].RowId == after[prefix].RowId)
            prefix++;
        var keep = after.Take(prefix).Select(row => row.RowId).ToHashSet(StringComparer.Ordinal);
        List<CompactOperation> result = [];
        for (var index = 0; index < prefix; index++)
            if (!retained[index].SameContent(after[index]))
                result.Add(new CompactOperation("row_upserted", after[index]));
        foreach (var row in before)
            if (!keep.Contains(row.RowId)) result.Add(new CompactOperation(Original: new RowRemoved(row.RowId)));
        for (var index = prefix; index < after.Count; index++)
            result.Add(new CompactOperation("row_appended", after[index]));
        return result;
    }

    private static void SetVisible(CompactTurn turn, List<CompactWireRow> rows)
    {
        foreach (var entry in turn.Rows) entry.Visible = null;
        turn.Visible = rows;
        foreach (var row in rows) row.Entry.Visible = row;
    }

    private (int Start, int End) SelectWindow(string? around)
    {
        if (_turns.Count == 0)
            return (0, 0);
        if (around is null || !_byId.TryGetValue(around, out var anchor))
            return (Extend(_turns.Count - 1, -1, CompactHistoryBudget.MaxTurns, 0).Index + 1, _turns.Count);

        var center = anchor.Turn!.Index;
        var (left, bytes) = Extend(center, -1, CompactHistoryBudget.MaxTurns / 2, 0);
        var start = left + 1;
        var (end, total) = Extend(center + 1, 1, CompactHistoryBudget.MaxTurns - (center - start + 1), bytes);
        start = Extend(start - 1, -1, CompactHistoryBudget.MaxTurns - (end - start), total).Index + 1;
        return (start, end);
    }

    private (int Index, long Bytes) Extend(int index, int step, int count, long bytes)
    {
        for (var added = 0; added < count && index >= 0 && index < _turns.Count; added++)
        {
            var turn = _turns[index];
            var finalIndex = SummaryFinalIndex(turn, index == _turns.Count - 1 && IsActive(_state));
            long size = 0;
            for (var row = 0; row < turn.Rows.Count; row++)
            {
                if (finalIndex >= 0 && row > 0 && row < finalIndex)
                    continue;
                size += CompactHistoryBudget.RowBytes(turn.Rows[row].Row, includeReasoning: finalIndex < 0);
            }
            if (bytes > 0 && bytes + size > CompactHistoryBudget.MaxBytes)
                break;
            bytes += size;
            index += step;
        }
        return (index, bytes);
    }

    private bool IsLoaded(CompactTurn turn) => turn.Index >= _start && turn.Index < _end;
    private IEnumerable<CompactTurn> LoadedTurns() => _turns.Skip(_start).Take(_end - _start);
    private IEnumerable<CompactWireRow> VisibleRows() => LoadedTurns().SelectMany(turn => turn.Visible);
}

internal sealed class CompactRowEntry(ConversationRow row)
{
    private ConversationRow _row = row;
    private StringBuilder? _text;
    private DateTimeOffset _at;
    private int _materializedRevision;
    private string? _version;

    public string RowId => _row.RowId;
    public int Revision { get; private set; }
    public CompactTurn? Turn { get; set; }
    public CompactWireRow? Visible { get; set; }
    public bool Removed { get; set; }
    public bool CanAppend => _row is AssistantMessageRow { IsStreaming: true, Parts: [TextPart] };
    public string Version => _version ??= CompactContentVersion.ForRow(Row);

    public ConversationRow Row
    {
        get
        {
            if (_text is not null && _materializedRevision != Revision)
            {
                var assistant = (AssistantMessageRow)_row;
                _row = assistant with { Parts = [((TextPart)assistant.Parts[0]) with { Text = _text.ToString() }], At = _at };
                _materializedRevision = Revision;
            }
            return _row;
        }
    }

    public void Replace(ConversationRow value)
    {
        _row = value;
        _text = null;
        _version = null;
        Revision++;
    }

    public void Append(RowTextAppended appended)
    {
        if (!CanAppend)
            throw new InvalidOperationException("An appended text operation requires its streaming row baseline.");
        _text ??= new StringBuilder(((TextPart)((AssistantMessageRow)_row).Parts[0]).Text);
        _text.Append(appended.Delta);
        _at = appended.At;
        _version = null;
        Revision++;
    }
}

internal sealed class CompactTurn
{
    public int Index { get; init; }
    public List<CompactRowEntry> Rows { get; } = [];
    public List<CompactWireRow> Visible { get; set; } = [];
}

internal sealed record CompactTurnSummary(
    CompactTurn Turn, UserMessageRow User, AssistantMessageRow Answer, int FinalIndex,
    TimeSpan Duration, int DetailCount, string Version);

internal sealed class CompactWireRow(CompactRowEntry entry, CompactTurnSummary? summary = null)
{
    public CompactRowEntry Entry { get; } = entry;
    public CompactTurnSummary? Summary { get; } = summary;
    public string RowId => Entry.RowId;
    public int Revision { get; set; } = entry.Revision;

    public bool SameContent(CompactWireRow other) => Summary is not null || other.Summary is not null
        ? Summary?.Version == other.Summary?.Version
        : ReferenceEquals(Entry, other.Entry) ? Revision == other.Revision : Entry.Version == other.Entry.Version;
}

internal sealed record CompactOperation(string? Type = null, CompactWireRow? Row = null, RowOp? Original = null);
