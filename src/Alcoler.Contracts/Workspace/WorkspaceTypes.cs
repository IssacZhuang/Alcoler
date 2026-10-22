using Alcoler.Contracts.Identifiers;

namespace Alcoler.Contracts.Workspace;

public enum TodoItemStatus
{
    Pending,
    InProgress,
    Completed,
}

public enum TodoPriority
{
    High,
    Medium,
    Low,
}

public sealed record TodoItem(string Content, TodoItemStatus Status, TodoPriority Priority);

/// <summary>
/// Per-session todo list state. The model submits the complete list on every write
/// (ZCode contract style), so Replace is wholesale and returns the stored list.
/// </summary>
public interface ITodoStore
{
    IReadOnlyList<TodoItem> Get(SessionId sessionId);

    IReadOnlyList<TodoItem> Replace(SessionId sessionId, IReadOnlyList<TodoItem> items);

    void Clear(SessionId sessionId);
}

/// <summary>
/// Session-level side-channel commands reachable from tools (e.g. ExitPlanMode switching
/// the session's permission mode after user approval).
/// </summary>
public interface ISessionCommands
{
    ValueTask SetModeAsync(Permissions.PermissionMode mode, CancellationToken cancellationToken);

    ValueTask ExitPlanModeAsync(string plan, CancellationToken cancellationToken);
}
