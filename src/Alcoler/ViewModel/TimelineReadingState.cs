using Alcoler.Contracts.Identifiers;

namespace Alcoler.ViewModel;

[Flags]
public enum TimelineExpansion
{
    None = 0,
    Turn = 1,
    Tool = 2,
    Reasoning = 4,
    Files = 8,
}

public sealed record TimelineExpandedRow(string RowId, TimelineExpansion Expansion);

public sealed record TimelineScrollAnchor(string RowId, double RelativeOffset);

public sealed record TimelineReadingState(
    IReadOnlyList<TimelineExpandedRow> ExpandedRows,
    TimelineScrollAnchor? Anchor = null,
    bool FollowLatest = true)
{
    public const int MaxExpandedRows = 256;
    public static TimelineReadingState Initial { get; } = new([]);
}

internal sealed class SessionReadingStateCache(int capacity = 32)
{
    private readonly Dictionary<SessionId, LinkedListNode<(SessionId Id, TimelineReadingState State)>> _entries = [];
    private readonly LinkedList<(SessionId Id, TimelineReadingState State)> _recent = [];

    internal int Count => _entries.Count;

    public TimelineReadingState? Get(SessionId sessionId)
    {
        if (!_entries.TryGetValue(sessionId, out var node))
            return null;
        _recent.Remove(node);
        _recent.AddLast(node);
        return node.Value.State;
    }

    public void Save(SessionId sessionId, TimelineReadingState state)
    {
        if (_entries.Remove(sessionId, out var previous))
            _recent.Remove(previous);
        var bounded = state with { ExpandedRows = state.ExpandedRows.TakeLast(TimelineReadingState.MaxExpandedRows).ToArray() };
        _entries[sessionId] = _recent.AddLast((sessionId, bounded));
        while (_entries.Count > Math.Max(1, capacity))
        {
            var oldest = _recent.First!;
            _entries.Remove(oldest.Value.Id);
            _recent.RemoveFirst();
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _recent.Clear();
    }
}
