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
    public const string ToolName = "ExitPlanMode";
    public const int MaxPlanChars = 20_000;

    internal const string NotInPlanModeMessage =
        "You are not in plan mode. This tool is only for exiting plan mode after writing a plan. If your plan was already approved, continue with implementation.";

    internal const string PlanDeniedMessage = "The plan was not approved by the user.";

    private const string ApprovedWithPlanPrefix =
        "User has approved your plan. You can now start coding. Start with updating your todo list if applicable.\n\n## Approved Plan:\n";

    private const string ApprovedWithoutPlanMessage = "User has approved exiting plan mode. You can now proceed.";

    public ToolContract Contract { get; } = new(
        ToolName,
        """
        Use this tool when you are in plan mode and have finished writing your plan and are ready for user approval.

        ## How This Tool Works
        - You should have already explored the codebase and finalized the plan you want the user to review
        - This tool DOES take the plan content as the required plan parameter in Alcoler
        - Pass the complete plan in the plan field; the user will review that content before approving implementation
        - This tool simply signals that you're done planning and ready for the user to review and approve
        - The user will see the contents of the plan parameter when they review it

        ## When to Use This Tool
        IMPORTANT: Only use this tool when the task requires planning the implementation steps of a task that requires writing code. For research tasks where you're gathering information, searching files, reading files or in general trying to understand the codebase - do NOT use this tool.

        ## Before Using This Tool
        Ensure your plan is complete and unambiguous:
        - If you have unresolved questions about requirements or approach, use AskUserQuestion before finalizing your plan
        - Once your plan is finalized, use THIS tool to request approval

        **Important:** Do NOT use AskUserQuestion to ask "Is this plan okay?" or "Should I proceed?" - that's exactly what THIS tool does. ExitPlanMode inherently requests user approval of your plan.

        ## Examples

        1. Initial task: "Search for and understand the implementation of vim mode in the codebase" - Do not use the exit plan mode tool because you are not planning the implementation steps of a task.
        2. Initial task: "Help me implement yank mode for vim" - Use the exit plan mode tool after you have finished planning the implementation steps of the task.
        3. Initial task: "Add a new feature to handle user authentication" - If unsure about auth method (OAuth, JWT, etc.), use AskUserQuestion first, then use exit plan mode tool after clarifying the approach.
        """,
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "plan": { "type": "string", "minLength": 1, "maxLength": 20000, "description": "The implementation plan to present to the user for approval." },
            "allowedPrompts": {
              "type": "array",
              "description": "Prompt-based permissions needed to implement the plan. These describe categories of actions rather than specific commands.",
              "items": {
                "type": "object",
                "properties": {
                  "tool": { "type": "string", "enum": ["Bash"], "description": "The tool this prompt applies to" },
                  "prompt": { "type": "string", "description": "Semantic description of the action, e.g. \"run tests\", \"install dependencies\"" }
                },
                "required": ["tool", "prompt"],
                "additionalProperties": false
              }
            }
          },
          "required": ["plan"]
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
        var allowedPrompts = ParseAllowedPrompts(context.Input);
        await context.Commands.SetModeAsync(PermissionMode.Edit, cancellationToken).ConfigureAwait(false);

        var trimmed = plan.Trim();
        var message = trimmed.Length == 0
            ? ApprovedWithoutPlanMessage
            : ApprovedWithPlanPrefix + trimmed;
        return ToolExecutionResult.Text(message, displaySummary: BuildDisplaySummary(plan, allowedPrompts));
    }

    private static IReadOnlyList<(string Tool, string Prompt)> ParseAllowedPrompts(JsonElement input)
    {
        if (!input.TryGetProperty("allowedPrompts", out var prompts) || prompts.ValueKind != JsonValueKind.Array)
            return [];

        List<(string Tool, string Prompt)> parsed = [];
        foreach (var entry in prompts.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("tool", out var tool)
                || tool.ValueKind != JsonValueKind.String
                || !string.Equals(tool.GetString(), "Bash", StringComparison.OrdinalIgnoreCase)
                || !entry.TryGetProperty("prompt", out var prompt)
                || prompt.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(prompt.GetString()))
                continue;
            parsed.Add((tool.GetString()!, prompt.GetString()!));
        }
        return parsed;
    }

    private static string BuildDisplaySummary(string plan, IReadOnlyList<(string Tool, string Prompt)> allowedPrompts)
    {
        var summary = plan;
        if (allowedPrompts.Count > 0)
            summary += "\n\nAllowed prompts:\n" + string.Join("\n", allowedPrompts.Select(static entry => $"- {entry.Tool}: {entry.Prompt}"));
        return summary;
    }

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
