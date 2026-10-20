using System.Text.Json;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Workspace;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Returns the current session todo list rendering without modifying state.
/// </summary>
public sealed class TodoReadTool : ITool
{
    private const string Title = "Current todo list";

    private readonly ITodoStore _store;

    public TodoReadTool(ITodoStore store) => _store = store;

    public ToolContract Contract { get; } = new(
        "TodoRead",
        "Returns the current session todo list. Does not modify state.",
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {},
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

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolExecutionResult.Text(TodoListRenderer.Render(_store.Get(context.SessionId), Title)));

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
