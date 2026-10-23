namespace Alcoler.Kernel.Workflows;

public static class OrchestratorWorkflow
{
    public const string Id = "orchestrator";

    public static readonly WorkflowDefinition Definition = new(
        Id,
        "Orchestrator",
        "Orchestrate via subagents: plan, dispatch, accept",
        SystemSection:
        """
        # Orchestrator mode

        Orchestrator mode is active. Plan the task, dispatch the work to subagents, then accept each result yourself before reporting done.
        """);
}
