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
    public void Contract_ExposesAgentNameSchemaAndSubagentMetadata()
    {
        var tool = new SubagentTool(new FakeSubagentPort());

        Assert.Equal("Agent", tool.Contract.Name);
        Assert.Contains("Available agent types and the tools they have access to:", tool.Contract.Description);
        Assert.Contains("## When to use", tool.Contract.Description);

        var schema = tool.Contract.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var required = schema.GetProperty("required");
        Assert.Equal(["description", "prompt"], required.EnumerateArray().Select(item => item.GetString()));
        var properties = schema.GetProperty("properties");
        Assert.Equal(
            ["description", "prompt", "subagent_type", "run_in_background"],
            properties.EnumerateObject().Select(property => property.Name));
        var subagentType = properties.GetProperty("subagent_type");
        Assert.Equal("string", subagentType.GetProperty("type").GetString());
        Assert.Equal(
            ["general-purpose", "Explore"],
            subagentType.GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            "boolean",
            properties.GetProperty("run_in_background").GetProperty("type").GetString());

        var metadata = tool.Metadata;
        Assert.True(metadata.ReadOnly);
        Assert.False(metadata.Destructive);
        Assert.True(metadata.ConcurrentSafe);
        Assert.Equal(SideEffectScope.Session, metadata.SideEffectScope);
        Assert.Equal(RiskLevel.Low, metadata.RiskLevel);
        Assert.False(metadata.NeedsApproval);
        Assert.False(metadata.AllowedInPlanMode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"prompt": ""}""")]
    [InlineData("""{"prompt": "   "}""")]
    [InlineData("""{"prompt": 7}""")]
    [InlineData("""{"description": "label"}""")]
    public async Task ValidateInput_MissingOrEmptyPromptOrDescription_ReportsIssue(string inputJson)
    {
        var tool = new SubagentTool(new FakeSubagentPort());

        var issue = await tool.ValidateInputAsync(Parse(inputJson), CancellationToken.None);

        Assert.NotNull(issue);
        Assert.Equal("missing_prompt", issue!.Code);
    }

    [Theory]
    [InlineData("""{"prompt": "x", "description": 7}""")]
    public async Task ValidateInput_EmptyDescription_ReportsDescriptionIssue(string inputJson)
    {
        var tool = new SubagentTool(new FakeSubagentPort());

        var issue = await tool.ValidateInputAsync(Parse(inputJson), CancellationToken.None);

        Assert.NotNull(issue);
        Assert.Equal("missing_description", issue!.Code);
    }

    [Theory]
    [InlineData("""{"prompt": "x", "description": "d", "subagent_type": "Explore"}""")]
    [InlineData("""{"prompt": "x", "description": "d", "subagent_type": "general-purpose"}""")]
    [InlineData("""{"prompt": "x", "description": "d", "run_in_background": true}""")]
    [InlineData("""{"prompt": "x", "description": "d", "subagent_type": "Explore", "run_in_background": true}""")]
    public async Task ValidateInput_ValidInput_ReturnsNoIssue(string inputJson)
    {
        var tool = new SubagentTool(new FakeSubagentPort());

        var issue = await tool.ValidateInputAsync(Parse(inputJson), CancellationToken.None);

        Assert.Null(issue);
    }

    [Fact]
    public async Task Execute_Foreground_ForwardsFieldsAndFormatsResultEnvelope()
    {
        using var workspace = new TempWorkspace();
        var started = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var result = SubagentTestResults.Completed(
            output: "the child findings",
            toolUseCount: 3,
            startedAt: started,
            endedAt: started.AddMilliseconds(1500));
        var port = new FakeSubagentPort { RunResult = result };
        var tool = new SubagentTool(port);

        var context = workspace.ContextFor(
            """{"prompt": "research X", "description": "research", "subagent_type": "Explore"}""");
        var execution = await tool.ExecuteAsync(context, CancellationToken.None);

        Assert.False(execution.IsError);
        Assert.Equal(
            $"""
            the child findings
            agentId: {result.State.Id.Value} (use SendMessage with to: '{result.State.Id.Value}' to continue this agent)
            <usage>tool_uses: 3
            duration_ms: 1500</usage>
            """,
            execution.SingleText());
        Assert.Equal("research", execution.DisplaySummary);

        Assert.Empty(port.BackgroundRequests);
        var request = port.RunRequests.Single();
        Assert.Equal(context.SessionId, request.ParentSessionId);
        Assert.Equal("research X", request.Prompt);
        Assert.Equal("research", request.Description);
        Assert.Equal("Explore", request.AgentType);
        Assert.Null(request.AllowedTools);
        Assert.Null(request.ModelSelection);
        Assert.False(request.Background);
    }

    [Fact]
    public async Task Execute_ForegroundWithoutType_DefaultsToGeneralPurpose()
    {
        using var workspace = new TempWorkspace();
        var port = new FakeSubagentPort { RunResult = SubagentTestResults.Completed() };
        var tool = new SubagentTool(port);

        await tool.ExecuteAsync(
            workspace.ContextFor("""{"prompt": "p", "description": "d"}"""),
            CancellationToken.None);

        var request = port.RunRequests.Single();
        Assert.Null(request.AgentType);
        Assert.False(request.Background);
    }

    [Fact]
    public async Task Execute_ForegroundEmptyOutput_ReturnsNoOutputPlaceholder()
    {
        using var workspace = new TempWorkspace();
        var port = new FakeSubagentPort { RunResult = SubagentTestResults.Completed(output: "  \n") };
        var tool = new SubagentTool(port);

        var execution = await tool.ExecuteAsync(
            workspace.ContextFor("""{"prompt": "p", "description": "d"}"""),
            CancellationToken.None);

        Assert.StartsWith("(Subagent completed but returned no output.)", execution.SingleText());
    }

    [Fact]
    public async Task Execute_ForegroundWithoutToolUseCount_OmitsToolUseLine()
    {
        using var workspace = new TempWorkspace();
        var started = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var port = new FakeSubagentPort
        {
            RunResult = SubagentTestResults.Completed(toolUseCount: null, startedAt: started, endedAt: started),
        };
        var tool = new SubagentTool(port);

        var execution = await tool.ExecuteAsync(
            workspace.ContextFor("""{"prompt": "p", "description": "d"}"""),
            CancellationToken.None);

        Assert.EndsWith("<usage>duration_ms: 0</usage>", execution.SingleText());
        Assert.DoesNotContain("tool_uses", execution.SingleText());
    }

    [Fact]
    public async Task Execute_ForegroundFailedRun_ReturnsErrorResultWithChildOutput()
    {
        using var workspace = new TempWorkspace();
        var port = new FakeSubagentPort { RunResult = SubagentTestResults.Completed(output: "child broke", status: SubagentStatus.Failed) };
        var tool = new SubagentTool(port);

        var execution = await tool.ExecuteAsync(
            workspace.ContextFor("""{"prompt": "p", "description": "d"}"""),
            CancellationToken.None);

        Assert.True(execution.IsError);
        Assert.StartsWith("child broke", execution.SingleText());
    }

    [Fact]
    public async Task Execute_Background_StartsSubagentAndReturnsLaunchText()
    {
        using var workspace = new TempWorkspace();
        var handle = new SubagentHandle(StrongId.NewSubagentId(), StrongId.NewSessionId(), "watch logs", Background: true);
        var port = new FakeSubagentPort { BackgroundHandle = handle };
        var tool = new SubagentTool(port);

        var context = workspace.ContextFor(
            """{"prompt": "watch the logs", "description": "watch", "run_in_background": true}""");
        var execution = await tool.ExecuteAsync(context, CancellationToken.None);

        Assert.False(execution.IsError);
        Assert.Equal(
            $"""
            Async agent launched successfully.
            agentId: {handle.Id.Value} (internal ID - do not mention to user. Use SendMessage with to: '{handle.Id.Value}' to continue this agent.)
            The agent is working in the background. You will be notified automatically when it completes.
            Do not duplicate this agent's work - avoid working with the same files or topics it is using. Work on non-overlapping tasks, or briefly tell the user what you launched and end your response.
            """,
            execution.SingleText());
        Assert.Equal("watch", execution.DisplaySummary);
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
        SubagentRequest? seenRequest = null;
        var tool = new SubagentTool(port, (request, result) =>
        {
            seenRequest = request;
            seen = result;
            return ValueTask.CompletedTask;
        });

        await tool.ExecuteAsync(
            workspace.ContextFor("""{"prompt": "p", "description": "d", "run_in_background": true}"""),
            CancellationToken.None);

        var (request, sink) = port.BackgroundRequests.Single();
        var sample = SubagentTestResults.Completed(output: "done");
        await sink(sample);

        Assert.Same(request, seenRequest);
        Assert.Same(sample, seen);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
