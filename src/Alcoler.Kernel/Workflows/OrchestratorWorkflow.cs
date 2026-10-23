namespace Alcoler.Kernel.Workflows;

public static class OrchestratorWorkflow
{
    public const string Id = "orchestrator";

    public static readonly WorkflowDefinition Definition = new(
        Id,
        "Orchestrator",
        "Orchestrate via subagents: decompose, delegate in parallel, verify, integrate",
        SystemSection:
        """
        # Orchestrator mode

        Orchestrator mode is active: you are the orchestrator of this task, not its only worker. Decompose, delegate, verify, integrate.

        - Split the task into independent units and delegate the implementation of each to general-purpose agents; send independent units in parallel in a single message. Keep for yourself only the work that genuinely needs the lead context: integration, judgment calls, and the user-facing synthesis.
        - Keep your own context lean: push file-heavy exploration and bulk reading down to Explore agents. You integrate conclusions, not transcripts.
        - When implementation units finish, delegate verification — the project's build and test commands plus a review of the diff — to a fresh agent. Do not report work as verified until that agent confirms it.
        - The user still interacts only with you; relay what matters from the agents' results.
        """,
        FullReminder:
        "Orchestrator mode is active. Orchestrate instead of executing everything yourself: decompose the task into independent units, delegate implementation to general-purpose agents in parallel, push file-heavy exploration to Explore agents to keep your own context lean, and have a fresh agent run build and tests before you report work done. This supercedes your default working style.",
        SparseReminder:
        "Orchestrator mode still active (see full instructions earlier in conversation). Decompose and delegate via subagents, keep your own context lean, and verify through a fresh agent before reporting done.",
        ExitReminder:
        "## Exited Orchestrator Mode\n\nYou have exited orchestrator mode. Work directly unless the task still calls for delegation.");
}
