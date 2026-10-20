using System.Text.Json;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Ends plan mode. RequiresUserInteraction routes every call through the permission broker,
/// so a denial means ExecuteAsync never runs and the session stays in plan mode.
/// </summary>
public sealed class ExitPlanModeTool : ITool
{
    private const string ApprovedMessage = "Plan approved. Mode switched to Edit. Proceed with implementation.";

    public ToolContract Contract { get; } = new(
        "ExitPlanMode",
        "Submits the implementation plan for user approval and, once approved, switches the session to edit mode. Call only after presenting a complete plan.",
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "plan": { "type": "string", "description": "Full plan text submitted for approval." },
            "summary": { "type": "string", "description": "Optional one-line summary of the plan." }
          },
          "required": ["plan"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: false,
        RequiresUserInteraction: true,
        SideEffectScope: SideEffectScope.None,
        RiskLevel: RiskLevel.Medium,
        NeedsApproval: true,
        AllowedInPlanMode: true);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("plan", out var plan)
            || plan.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(plan.GetString()))
            return ValueTask.FromResult<ToolInputIssue?>(new ToolInputIssue(
                "missing_plan",
                "'plan' must be a non-empty string"));

        return ValueTask.FromResult<ToolInputIssue?>(null);
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Commands is null)
            return ToolExecutionResult.Text("session commands are not available", isError: true);

        var plan = context.Input.GetProperty("plan").GetString()!;
        await context.Commands.SetModeAsync(PermissionMode.Edit, cancellationToken).ConfigureAwait(false);

        var displaySummary = plan;
        if (context.Input.TryGetProperty("summary", out var summary)
            && summary.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(summary.GetString()))
            displaySummary += "\n" + summary.GetString();

        return ToolExecutionResult.Text(ApprovedMessage, displaySummary: displaySummary);
    }

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
