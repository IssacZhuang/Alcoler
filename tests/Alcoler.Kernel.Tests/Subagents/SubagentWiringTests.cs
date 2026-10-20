using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Subagents;

public class SubagentWiringTests
{
    private static readonly string[] ParentToolNames =
    [
        "Read", "Write", "Edit", "Glob", "Grep", "Bash", "WebFetch", "WebSearch",
        "TodoWrite", "TodoRead", "ExitPlanMode", "AskUserQuestion", "Skill",
        "TaskOutput", "TaskStop", "ReadSessionContext", "SendMessage", "Agent",
    ];

    private static SubagentRequest Request(string? agentType = null, IReadOnlyList<string>? allowedTools = null) =>
        new(new SessionId("sess_parent"), "prompt", AgentType: agentType, AllowedTools: allowedTools);

    private static IReadOnlyList<string> SelectNames(string? agentType = null, IReadOnlyList<string>? allowedTools = null) =>
        SubagentWiring.SelectChildTools([.. ParentToolNames.Select(name => new FakeTool(name))], Request(agentType, allowedTools))
            .Select(tool => tool.Contract.Name)
            .ToArray();

    [Fact]
    public void SelectChildTools_GeneralPurpose_GetsAllToolsExceptDispatchAndPlanTools()
    {
        Assert.Equal(
            [
                "Read", "Write", "Edit", "Glob", "Grep", "Bash", "WebFetch", "WebSearch",
                "TodoWrite", "TodoRead", "AskUserQuestion", "Skill",
                "TaskOutput", "TaskStop", "ReadSessionContext",
            ],
            SelectNames());
    }

    [Fact]
    public void SelectChildTools_Explore_RestrictsToReadOnlyScope()
    {
        var names = SelectNames(SubagentPrompts.ExploreAgentType);

        Assert.Equal(["Read", "Glob", "Grep", "Bash", "WebFetch", "WebSearch", "TodoWrite"], names);
        Assert.DoesNotContain(names, name => name is "Write" or "Edit" or "Agent" or "SendMessage");
    }

    [Fact]
    public void SelectChildTools_AllowedTools_NarrowsGeneralPurposeScope()
    {
        var names = SelectNames(allowedTools: ["Read", "Grep"]);

        Assert.Equal(["Read", "Grep"], names);
    }

    [Fact]
    public void SelectChildTools_Explore_ScopeWinsOverAllowedTools()
    {
        var names = SelectNames(SubagentPrompts.ExploreAgentType, allowedTools: ["Read", "Write"]);

        Assert.Equal(["Read"], names);
    }

    [Fact]
    public void SelectChildTools_UnknownAgentType_FallsBackToGeneralPurposeScope()
    {
        var names = SelectNames("planner");

        Assert.Contains("Write", names);
        Assert.Contains("Bash", names);
    }
}
