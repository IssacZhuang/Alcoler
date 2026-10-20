using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Subagents;

public class SubagentToolTests
{
    [Fact]
    public void Contract_ExposesTaskNameSchemaAndSubagentMetadata()
    {
        var tool = new SubagentTool(new FakeSubagentPort());

        Assert.Equal("Task", tool.Contract.Name);
        var schema = tool.Contract.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var required = schema.GetProperty("required");
        var requiredEntry = Assert.Single(required.EnumerateArray());
        Assert.Equal("prompt", requiredEntry.GetString());
        Assert.Equal(
            "boolean",
            schema.GetProperty("properties").GetProperty("background").GetProperty("type").GetString());

        var metadata = tool.Metadata;
        Assert.False(metadata.ReadOnly);
        Assert.False(metadata.Destructive);
        Assert.False(metadata.ConcurrentSafe);
        Assert.Equal(SideEffectScope.Workspace, metadata.SideEffectScope);
        Assert.Equal(RiskLevel.Medium, metadata.RiskLevel);
        Assert.False(metadata.NeedsApproval);
        Assert.False(metadata.AllowedInPlanMode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"prompt": ""}""")]
    [InlineData("""{"prompt": "   "}""")]
    [InlineData("""{"prompt": 7}""")]
    public async Task ValidateInput_MissingOrEmptyPrompt_ReportsIssue(string inputJson)
    {
        var tool = new SubagentTool(new FakeSubagentPort());

        var issue = await tool.ValidateInputAsync(Parse(inputJson), CancellationToken.None);

        Assert.NotNull(issue);
        Assert.Equal("missing_prompt", issue!.Code);
    }

    [Theory]
    [InlineData("""{"prompt": "x", "allowedTools": [""]}""")]
    [InlineData("""{"prompt": "x", "allowedTools": ["ReadFile", " "]}""")]
    [InlineData("""{"prompt": "x", "allowedTools": [3]}""")]
    [InlineData("""{"prompt": "x", "allowedTools": "ReadFile"}""")]
    public async Task ValidateInput_InvalidAllowedTools_ReportsIssue(string inputJson)
    {
        var tool = new SubagentTool(new FakeSubagentPort());

        var issue = await tool.ValidateInputAsync(Parse(inputJson), CancellationToken.None);

        Assert.NotNull(issue);
        Assert.Equal("invalid_allowed_tools", issue!.Code);
    }

    [Theory]
    [InlineData("""{"prompt": "do it"}""")]
    [InlineData("""{"prompt": "do it", "description": "label", "role": "researcher", "background": true, "allowedTools": ["ReadFile"]}""")]
    public async Task ValidateInput_ValidInput_ReturnsNoIssue(string inputJson)
    {
        var tool = new SubagentTool(new FakeSubagentPort());

        var issue = await tool.ValidateInputAsync(Parse(inputJson), CancellationToken.None);

        Assert.Null(issue);
    }

    [Fact]
    public async Task Execute_Foreground_ForwardsFieldsAndReturnsChildOutput()
    {
        using var workspace = new TempWorkspace();
        var port = new FakeSubagentPort { RunResult = SubagentTestResults.Completed(output: "the child findings") };
        var tool = new SubagentTool(port);

        var context = workspace.ContextFor(
            """{"prompt": "research X", "description": "research", "role": "researcher", "allowedTools": ["ReadFile", "Grep"]}""");
        var result = await tool.ExecuteAsync(context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("the child findings", result.SingleText());
        Assert.Equal("research", result.DisplaySummary);

        Assert.Empty(port.BackgroundRequests);
        var request = port.RunRequests.Single();
        Assert.Equal(context.SessionId, request.ParentSessionId);
        Assert.Equal("research X", request.Prompt);
        Assert.Equal("research", request.Description);
        Assert.Equal("researcher", request.Role);
        Assert.Equal(new[] { "ReadFile", "Grep" }, request.AllowedTools);
        Assert.Null(request.ModelSelection);
        Assert.False(request.Background);
    }

    [Fact]
    public async Task Execute_ForegroundFailedRun_ReturnsErrorResultWithChildOutput()
    {
        using var workspace = new TempWorkspace();
        var port = new FakeSubagentPort { RunResult = SubagentTestResults.Completed(output: "child broke", status: SubagentStatus.Failed) };
        var tool = new SubagentTool(port);

        var result = await tool.ExecuteAsync(workspace.ContextFor("""{"prompt": "p"}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("child broke", result.SingleText());
    }

    [Fact]
    public async Task Execute_Background_StartsSubagentAndReturnsImmediately()
    {
        using var workspace = new TempWorkspace();
        var handle = new SubagentHandle(StrongId.NewSubagentId(), StrongId.NewSessionId(), "watch logs", Background: true);
        var port = new FakeSubagentPort { BackgroundHandle = handle };
        var tool = new SubagentTool(port);

        var context = workspace.ContextFor(
            """{"prompt": "watch the logs", "description": "watch", "background": true}""");
        var result = await tool.ExecuteAsync(context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Subagent started: watch logs", result.SingleText());
        Assert.Equal("watch", result.DisplaySummary);
        Assert.Empty(port.RunRequests);

        var (request, sink) = port.BackgroundRequests.Single();
        Assert.Equal(context.SessionId, request.ParentSessionId);
        Assert.Equal("watch the logs", request.Prompt);
        Assert.True(request.Background);
        await sink(SubagentTestResults.Completed());
    }

    [Fact]
    public async Task Execute_Background_PassesInjectedSinkToPort()
    {
        using var workspace = new TempWorkspace();
        var port = new FakeSubagentPort();
        SubagentResult? seen = null;
        var tool = new SubagentTool(port, result => { seen = result; return ValueTask.CompletedTask; });

        await tool.ExecuteAsync(
            workspace.ContextFor("""{"prompt": "p", "background": true}"""),
            CancellationToken.None);

        var (_, sink) = port.BackgroundRequests.Single();
        var sample = SubagentTestResults.Completed(output: "done");
        await sink(sample);

        Assert.Same(sample, seen);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
