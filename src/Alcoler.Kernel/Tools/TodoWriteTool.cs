using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Workspace;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Wholesale replacement of the session todo list. The model submits the complete list on
/// every call; the model-visible result is the reference JSON shape (oldTodos, todos, summary).
/// </summary>
public sealed class TodoWriteTool : ITool
{
    // Matches JSON.stringify output for the reference result contract: compact, non-ASCII kept literal.
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ITodoStore _store;

    public TodoWriteTool(ITodoStore store) => _store = store;

    public ToolContract Contract { get; } = new(
        "TodoWrite",
        """
        Create and update a task list for the current session. The list is rendered to the user as your working plan.

        - Each todo has `content`, `status` ("pending" | "in_progress" | "completed"), and `priority` ("high" | "medium" | "low").
        - Send the full list each call; it replaces the previous one.
        - Keep one item `in_progress` at a time and mark it `completed` when done.
        """,
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "todos": {
              "type": "array",
              "description": "The complete updated todo list. At most one item may be in_progress at a time.",
              "items": {
                "type": "object",
                "properties": {
                  "content": { "type": "string", "minLength": 1, "description": "Brief description of the task" },
                  "status": { "type": "string", "enum": ["pending", "in_progress", "completed"], "description": "Current status of the task" },
                  "priority": { "type": "string", "enum": ["high", "medium", "low"], "description": "Priority level of the task" }
                },
                "required": ["content", "status", "priority"]
              }
            }
          },
          "required": ["todos"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.None,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        TryNormalize(input, out _, out var error);
        return ValueTask.FromResult(error is null ? null : new ToolInputIssue("invalid_todos", error));
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryNormalize(context.Input, out var items, out var error))
            return ValueTask.FromResult(ToolExecutionResult.Text(error, isError: true));

        var oldTodos = _store.Get(context.SessionId);
        var todos = _store.Replace(context.SessionId, items);
        return ValueTask.FromResult(ToolExecutionResult.Text(SerializeWriteResult(oldTodos, todos)));
    }

    internal static string SerializeWriteResult(IReadOnlyList<TodoItem> oldTodos, IReadOnlyList<TodoItem> todos) =>
        JsonSerializer.Serialize(new
        {
            oldTodos = ToDto(oldTodos),
            todos = ToDto(todos),
            summary = new
            {
                total = todos.Count,
                pending = Count(todos, TodoItemStatus.Pending),
                inProgress = Count(todos, TodoItemStatus.InProgress),
                completed = Count(todos, TodoItemStatus.Completed),
            },
        }, ResultJsonOptions);

    internal static string SerializeReadResult(IReadOnlyList<TodoItem> todos) =>
        JsonSerializer.Serialize(new { todos = ToDto(todos) }, ResultJsonOptions);

    internal static bool TryNormalize(
        JsonElement input,
        [NotNullWhen(true)] out IReadOnlyList<TodoItem> items,
        [NotNullWhen(false)] out string? error)
    {
        items = [];
        error = null;

        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("todos", out var todos)
            || todos.ValueKind != JsonValueKind.Array)
        {
            error = "the 'todos' property must be an array";
            return false;
        }

        var normalized = new List<TodoItem>();
        var index = 0;
        foreach (var element in todos.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"todos[{index - 1}] must be an object";
                return false;
            }

            if (!element.TryGetProperty("content", out var contentElement)
                || contentElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(contentElement.GetString()))
            {
                error = $"todos[{index - 1}].content must be a non-empty string";
                return false;
            }

            if (!element.TryGetProperty("status", out var statusElement)
                || statusElement.ValueKind != JsonValueKind.String
                || !TryParseStatus(statusElement.GetString(), out var status))
            {
                error = $"todos[{index - 1}].status '{statusElement}' is not one of: pending, in_progress, completed";
                return false;
            }

            if (!element.TryGetProperty("priority", out var priorityElement)
                || priorityElement.ValueKind != JsonValueKind.String
                || !TryParsePriority(priorityElement.GetString(), out var priority))
            {
                error = $"todos[{index - 1}].priority '{priorityElement}' is not one of: high, medium, low";
                return false;
            }

            normalized.Add(new TodoItem(contentElement.GetString()!, status, priority));
        }

        items = normalized;
        return true;
    }

    internal static bool TryParseStatus(string? raw, out TodoItemStatus status)
    {
        status = TodoItemStatus.Pending;
        if (raw is null)
            return false;
        if (string.Equals(raw, "pending", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(raw, "in_progress", StringComparison.OrdinalIgnoreCase))
        {
            status = TodoItemStatus.InProgress;
            return true;
        }
        if (string.Equals(raw, "completed", StringComparison.OrdinalIgnoreCase))
        {
            status = TodoItemStatus.Completed;
            return true;
        }
        return false;
    }

    internal static bool TryParsePriority(string? raw, out TodoPriority priority)
    {
        priority = TodoPriority.Medium;
        if (raw is null)
            return false;
        if (string.Equals(raw, "high", StringComparison.OrdinalIgnoreCase))
        {
            priority = TodoPriority.High;
            return true;
        }
        if (string.Equals(raw, "medium", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(raw, "low", StringComparison.OrdinalIgnoreCase))
        {
            priority = TodoPriority.Low;
            return true;
        }
        return false;
    }

    internal static string StatusName(TodoItemStatus status) => status switch
    {
        TodoItemStatus.InProgress => "in_progress",
        TodoItemStatus.Completed => "completed",
        _ => "pending",
    };

    internal static string PriorityName(TodoPriority priority) => priority switch
    {
        TodoPriority.High => "high",
        TodoPriority.Low => "low",
        _ => "medium",
    };

    private static object[] ToDto(IReadOnlyList<TodoItem> items) =>
        items.Select(static item => new
        {
            content = item.Content,
            status = StatusName(item.Status),
            priority = PriorityName(item.Priority),
        }).ToArray();

    private static int Count(IReadOnlyList<TodoItem> items, TodoItemStatus status)
    {
        var count = 0;
        foreach (var item in items)
            if (item.Status == status)
                count++;
        return count;
    }

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
