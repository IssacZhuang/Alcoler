using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Workspace;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Wholesale replacement of the session todo list. The model submits the complete list on
/// every call; ids and statuses are normalized before storing.
/// </summary>
public sealed class TodoWriteTool : ITool
{
    public const int MaxItems = 100;
    public const int MaxTotalContentChars = 20_000;

    private const string Title = "Todo list updated";

    private readonly ITodoStore _store;

    public TodoWriteTool(ITodoStore store) => _store = store;

    public ToolContract Contract { get; } = new(
        "TodoWrite",
        "Replaces the session todo list with the submitted items. Submit the complete list every time, not just the changed item. Use it to plan and track multi-step work.",
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "todos": {
              "type": "array",
              "maxItems": 100,
              "description": "The complete todo list.",
              "items": {
                "type": "object",
                "properties": {
                  "content": { "type": "string", "description": "Task description." },
                  "status": { "type": "string", "enum": ["pending", "in_progress", "completed"], "default": "pending", "description": "Task status." },
                  "id": { "type": "string", "description": "Stable item id; generated as todo-N when omitted." }
                },
                "required": ["content"],
                "additionalProperties": false
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
        TryNormalize(input, out _, out _, out var error);
        return ValueTask.FromResult(error is null ? null : new ToolInputIssue("invalid_todos", error));
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryNormalize(context.Input, out var items, out var notes, out var error))
            return ValueTask.FromResult(ToolExecutionResult.Text(error, isError: true));

        var stored = _store.Replace(context.SessionId, items);
        var text = TodoListRenderer.Render(stored, Title);
        if (notes.Count > 0)
            text = string.Join("\n", [text, .. notes]);
        return ValueTask.FromResult(ToolExecutionResult.Text(text));
    }

    internal static bool TryNormalize(
        JsonElement input,
        [NotNullWhen(true)] out IReadOnlyList<TodoItem> items,
        out IReadOnlyList<string> notes,
        [NotNullWhen(false)] out string? error)
    {
        items = [];
        notes = [];
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

            var status = TodoItemStatus.Pending;
            if (element.TryGetProperty("status", out var statusElement)
                && (statusElement.ValueKind != JsonValueKind.String || !TryParseStatus(statusElement.GetString(), out status)))
            {
                error = $"todos[{index - 1}].status '{statusElement}' is not one of: pending, in_progress, completed";
                return false;
            }

            var id = element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id))
                id = $"todo-{index}";

            normalized.Add(new TodoItem(id, contentElement.GetString()!, status));

            if (normalized.Count == MaxItems && index < todos.GetArrayLength())
            {
                notes = [.. notes, $"[todo list truncated at {MaxItems} items]"];
                break;
            }
        }

        var truncatedContent = false;
        var budget = MaxTotalContentChars;
        for (var i = 0; i < normalized.Count; i++)
        {
            if (normalized[i].Content.Length <= budget)
            {
                budget -= normalized[i].Content.Length;
                continue;
            }
            normalized[i] = normalized[i] with { Content = normalized[i].Content[..budget] };
            budget = 0;
            truncatedContent = true;
        }
        if (truncatedContent)
            notes = [.. notes, $"[todo content truncated at the {MaxTotalContentChars:N0} character total limit]"];

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

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
