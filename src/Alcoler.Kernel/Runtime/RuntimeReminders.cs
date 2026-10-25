using Alcoler.Kernel.Subagents;

namespace Alcoler.Kernel.Runtime;

/// <summary>
/// Plan-mode runtime reminder texts: the full Plan Workflow on the 1st, 6th, ... attachment
/// and the sparse form otherwise, with at most one attachment per 5 human turns; plus the
/// one-shot body after ExitPlanMode approval. Approved plans execute via subagents.
/// </summary>
public static class RuntimeReminders
{
    public const int TurnsBetweenAttachments = 5;
    public const int FullReminderEveryNAttachments = 5;

    private const int PlanResearchAgentCount = 3;
    private const string AskUserQuestionToolName = "AskUserQuestion";
    private const string ExitPlanModeToolName = "ExitPlanMode";

    private static readonly string PlanWorkflow = $"""
        ## Plan Workflow

        ### Phase 1: Initial Understanding
        Goal: Gain a comprehensive understanding of the user's request by reading through code and asking them questions. Critical: In this phase you should only use the {SubagentPrompts.ExploreAgentType} subagent type.

        1. Focus on understanding the user's request and the code associated with their request. Actively search for existing functions, utilities, and patterns that can be reused — avoid proposing new code when suitable implementations already exist.

        2. **Launch up to {PlanResearchAgentCount} {SubagentPrompts.ExploreAgentType} agents IN PARALLEL** (single message, multiple tool calls) to efficiently explore the codebase.
           - Use 1 agent when the task is isolated to known files, the user provided specific file paths, or you're making a small targeted change.
           - Use multiple agents when: the scope is uncertain, multiple areas of the codebase are involved, or you need to understand existing patterns before planning.
           - Quality over quantity - {PlanResearchAgentCount} agents maximum, but you should try to use the minimum number of agents necessary (usually just 1)
           - If using multiple agents: Provide each agent with a specific search focus or area to explore. Example: One agent searches for existing implementations, another explores related components, a third investigating testing patterns

        ### Phase 2: Design
        Goal: Design an implementation approach.

        **Guidelines:**
        - Use the context gathered in Phase 1, including relevant files and code paths.
        - Account for the user's requirements and constraints.
        - Produce a concrete implementation plan that is detailed enough to execute.
        - Consider useful perspectives for the task type:
          - New feature: simplicity vs performance vs maintainability
          - Bug fix: root cause vs workaround vs prevention
          - Refactoring: minimal change vs clean architecture

        ### Phase 3: Review
        Goal: Review the plan(s) from Phase 2 and ensure alignment with the user's intentions.
        1. Read the critical files to deepen your understanding
        2. Ensure that the plans align with the user's original request
        3. Use {AskUserQuestionToolName} to clarify any remaining questions with the user

        ### Phase 4: Call {ExitPlanModeToolName}
        At the very end of your turn, once you have asked the user questions and are happy with your final plan - you should always call {ExitPlanModeToolName} to indicate to the user that you are done planning.
        This is critical - your turn should only end with either using the {AskUserQuestionToolName} tool OR calling {ExitPlanModeToolName}. Do not stop unless it's for these 2 reasons

        **Important:** Use {AskUserQuestionToolName} ONLY to clarify requirements or choose between approaches. Use {ExitPlanModeToolName} to request plan approval. Do NOT ask for plan approval in any other way - no text questions, no AskUserQuestion. Phrases like "Is this plan okay?", "Should I proceed?", "How does this plan look?", "Any changes before we start?", or similar MUST use {ExitPlanModeToolName}.

        NOTE: At any point in time through this workflow you should feel free to ask the user questions or clarifications using the {AskUserQuestionToolName} tool. Do not make large assumptions about user intent. The goal is to present a well researched plan to the user, and tie any loose ends before implementation begins.
        """;

    public static readonly string PlanModeFullReminder =
        "Plan mode is active. The user indicated that they do not want you to execute yet -- you MUST NOT make any edits, run any non-readonly tools (including changing configs or making commits), or otherwise make any changes to the system. This supercedes any other instructions you have received.\n"
        + PlanWorkflow;

    public const string PlanModeSparseReminder =
        $"Plan mode still active (see full instructions earlier in conversation). Read-only. Follow 4-phase workflow. End turns with {AskUserQuestionToolName} (for clarifications) or {ExitPlanModeToolName} (for plan approval). Never ask for plan approval via text or AskUserQuestion.";

    public const string PlanModeExitReminder =
        "## Exited Plan Mode\n\nYou have exited plan mode. Dispatch the approved plan's implementation to subagents and accept each result yourself before reporting done.";

    public static bool IsPlanModeReminder(string text) =>
        text.Contains("Plan mode is active.", StringComparison.Ordinal)
        || text.Contains("Plan mode still active", StringComparison.Ordinal);

    // Full on the 1st, 6th, ... attachment; sparse otherwise.
    public static string BuildModeReminder(int attachmentIndex) =>
        attachmentIndex % FullReminderEveryNAttachments == 1 ? PlanModeFullReminder : PlanModeSparseReminder;
}
