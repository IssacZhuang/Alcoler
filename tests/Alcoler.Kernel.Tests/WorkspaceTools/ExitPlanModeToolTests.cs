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
        """{"plan": "1. Add tests\n2. Implement the parser", "allowedPrompts": [{"tool": "Bash", "prompt": "run tests"}]}""";

    [Fact]
    public async Task ValidateInput_RequiresNonEmptyPlan()
    {
        var tool = new ExitPlanModeTool();

        foreach (var json in new[]
        {
            "{}",
            """{"plan": ""}""",
            """{"plan": "   "}""",
            """{"allowedPrompts": [{"tool": "Bash", "prompt": "run tests"}]}"""
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
    public async Task Execute_ApprovalMessageEchoesPlan_WithAllowedPromptsInSummary()
    {
        var commands = new FakeSessionCommands();
        var tool = new ExitPlanModeTool();

        var result = await tool.ExecuteAsync(
            WorkspaceToolTestContext.ForSession(StrongId.NewSessionId(), PlanJson, commands),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "User has approved your plan. You can now start coding. Start with updating your todo list if applicable.\n\n## Approved Plan:\n1. Add tests\n2. Implement the parser",
            result.SingleText());
        Assert.Equal([PermissionMode.Edit], commands.SetModes);
        Assert.NotNull(result.DisplaySummary);
        Assert.Contains("1. Add tests", result.DisplaySummary);
        Assert.Contains("Allowed prompts:", result.DisplaySummary);
        Assert.Contains("- Bash: run tests", result.DisplaySummary);
    }

    [Fact]
    public async Task Execute_WithoutAllowedPrompts_SummaryIsThePlanAlone()
    {
        var commands = new FakeSessionCommands();
        var result = await new ExitPlanModeTool().ExecuteAsync(
            WorkspaceToolTestContext.ForSession(
                StrongId.NewSessionId(),
                """{"plan": "1. Write the file."}""",
                commands),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.EndsWith("## Approved Plan:\n1. Write the file.", result.SingleText());
        Assert.Equal("1. Write the file.", result.DisplaySummary);
        Assert.DoesNotContain("Allowed prompts:", result.DisplaySummary);
    }

    [Fact]
    public void Contract_DescriptionAndSchemaMatchReference()
    {
        var contract = new ExitPlanModeTool().Contract;

        Assert.Equal("ExitPlanMode", contract.Name);
        Assert.StartsWith(
            "Use this tool when you are in plan mode and have finished writing your plan and are ready for user approval.",
            contract.Description);
        Assert.Contains("required plan parameter in Alcoler", contract.Description);

        var schema = contract.InputSchema.GetRawText();
        Assert.Contains("\"maxLength\": 20000", schema);
        Assert.Contains("\"allowedPrompts\"", schema);
        Assert.Contains("\"enum\": [\"Bash\"]", schema);
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
