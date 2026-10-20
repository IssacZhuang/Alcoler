using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Workspace;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Default per-session todo store. Stored lists are immutable snapshots, so values
/// returned by Get and Replace never alias internal state.
/// </summary>
public sealed class InMemoryTodoStore : ITodoStore
{
    private readonly object _sync = new();
    private readonly Dictionary<SessionId, IReadOnlyList<TodoItem>> _lists = [];

    public IReadOnlyList<TodoItem> Get(SessionId sessionId)
    {
        lock (_sync)
            return _lists.TryGetValue(sessionId, out var items) ? items : [];
    }

    public IReadOnlyList<TodoItem> Replace(SessionId sessionId, IReadOnlyList<TodoItem> items)
    {
        var snapshot = items.ToArray();
        lock (_sync)
            _lists[sessionId] = snapshot;
        return snapshot;
    }

    public void Clear(SessionId sessionId)
    {
        lock (_sync)
            _lists.Remove(sessionId);
    }
}
