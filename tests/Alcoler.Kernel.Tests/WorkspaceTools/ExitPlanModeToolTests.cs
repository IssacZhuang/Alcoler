using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Kernel.Tests.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.WorkspaceTools;

public class ExitPlanModeToolTests
{
    private const string PlanJson =
        """{"plan": "1. Add tests\n2. Implement the parser", "summary": "parser rollout"}""";

    [Fact]
    public async Task ValidateInput_RequiresNonEmptyPlan()
    {
        var tool = new ExitPlanModeTool();

        foreach (var json in new[]
        {
            "{}",
            """{"plan": ""}""",
            """{"plan": "   "}""",
            """{"summary": "no plan here"}"""
        })
        {
            var issue = await tool.ValidateInputAsync(JsonDocument.Parse(json).RootElement, CancellationToken.None);
            Assert.NotNull(issue);
        }

        Assert.Null(await tool.ValidateInputAsync(
            JsonDocument.Parse("""{"plan": "do the thing"}""").RootElement,
            CancellationToken.None));
    }

    [Fact]
    public async Task Execute_WithoutCommands_ReportsUnavailable()
    {
        var commands = new FakeSessionCommands();
        var result = await new ExitPlanModeTool().ExecuteAsync(
            WorkspaceToolTestContext.ForSession(StrongId.NewSessionId(), PlanJson, commands: null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("session commands are not available", result.SingleText());
        Assert.Empty(commands.SetModes);
    }

    [Fact]
    public async Task Execute_SwitchesModeToEditAndEchoesPlan()
    {
        var commands = new FakeSessionCommands();
        var tool = new ExitPlanModeTool();

        var result = await tool.ExecuteAsync(
            WorkspaceToolTestContext.ForSession(StrongId.NewSessionId(), PlanJson, commands),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Plan approved. Mode switched to Edit. Proceed with implementation.", result.SingleText());
        Assert.Equal([PermissionMode.Edit], commands.SetModes);
        Assert.NotNull(result.DisplaySummary);
        Assert.Contains("1. Add tests", result.DisplaySummary);
        Assert.Contains("parser rollout", result.DisplaySummary);
    }

    [Fact]
    public void Metadata_RoutesThroughBrokerForApproval()
    {
        var metadata = new ExitPlanModeTool().Metadata;

        Assert.True(metadata.RequiresUserInteraction);
        Assert.True(metadata.NeedsApproval);
        Assert.True(metadata.AllowedInPlanMode);
        Assert.False(metadata.ConcurrentSafe);
    }
}
