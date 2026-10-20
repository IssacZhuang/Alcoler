using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Xunit;
using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class ToolCallExecutorTests
{
    private static ToolCallExecutor Executor(
        ToolRegistry registry,
        ScriptedPermissionGate? gate = null,
        TempWorkspace? workspace = null)
    {
        workspace ??= new TempWorkspace();
        return new ToolCallExecutor(registry, gate ?? new ScriptedPermissionGate());
    }

    private static async Task<ToolExecutionOutcome> RunAsync(
        ToolCallExecutor executor,
        ToolCall call,
        TempWorkspace workspace,
        NotificationCollector? notifications = null,
        CancellationToken cancellationToken = default)
    {
        notifications ??= new NotificationCollector();
        return await executor.ExecuteAsync(
            call,
            workspace.ContextFactory(),
            notifications.Collect,
            cancellationToken);
    }

    [Fact]
    public async Task UnknownTool_ReturnsPairedToolUseError()
    {
        using var workspace = new TempWorkspace();
        var registry = new ToolRegistry([new FakeTool("Read")]);
        var executor = Executor(registry, workspace: workspace);
        var call = new ToolCall(StrongId.NewToolCallId(), "Nope", JsonDocument.Parse("{}").RootElement.Clone());

        var notifications = new NotificationCollector();
        var outcome = await RunAsync(executor, call, workspace, notifications);

        Assert.True(outcome.Result.IsError);
        Assert.StartsWith("<tool_use_error>", outcome.Result.SingleText());
        Assert.Contains("Unknown tool 'Nope'", outcome.Result.SingleText());
        Assert.Single(notifications.Of<ToolExecutionStarted>());
        Assert.Single(notifications.Of<ToolExecutionResultNotification>());
        Assert.Empty(notifications.Of<ToolExecutionPermissionAsked>());
    }

    [Fact]
    public async Task SchemaInvalidInput_ReturnsToolUseError_WithoutExecuting()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool("Picky", schemaJson: TestSchemas.RequiresValue);
        var executor = Executor(new ToolRegistry([tool]), workspace: workspace);

        var outcome = await RunAsync(executor, tool.Call("""{"wrong": true}"""), workspace);

        Assert.True(outcome.Result.IsError);
        Assert.StartsWith("<tool_use_error>", outcome.Result.SingleText());
        Assert.Contains("schema validation", outcome.Result.SingleText());
        Assert.Empty(tool.ExecutedContexts);
    }

    [Fact]
    public async Task SemanticValidationIssue_ReturnsToolUseError()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool(
            "Guarded",
            validationIssue: new ToolInputIssue("bad_input", "value must be positive"));
        var executor = Executor(new ToolRegistry([tool]), workspace: workspace);

        var outcome = await RunAsync(executor, tool.Call(), workspace);

        Assert.True(outcome.Result.IsError);
        Assert.Contains("value must be positive", outcome.Result.SingleText());
        Assert.Empty(tool.ExecutedContexts);
    }

    [Fact]
    public async Task PermissionDenied_ReturnsPairedErrorResult_WithoutExecuting()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool("Write", FakeToolMetadata.Serial());
        var gate = new ScriptedPermissionGate(PermissionDecision.Deny);
        var executor = Executor(new ToolRegistry([tool]), gate, workspace);
        var call = tool.Call();

        var notifications = new NotificationCollector();
        var outcome = await RunAsync(executor, call, workspace, notifications);

        Assert.True(outcome.Result.IsError);
        Assert.Equal("Permission denied for Write", outcome.Result.SingleText());
        Assert.Empty(tool.ExecutedContexts);
        Assert.Single(gate.Requests);
        Assert.Equal("Write", gate.Requests[0].ToolName);
        Assert.Equal(tool.Metadata, gate.Requests[0].Tool);
        var asked = Assert.Single(notifications.Of<ToolExecutionPermissionAsked>());
        Assert.Equal(call.Id, asked.Call.Id);
        Assert.Equal("Write", asked.Request.ToolName);
    }

    [Fact]
    public async Task SuccessfulEmptyOutput_IsReplacedWithPlaceholder()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool("Quiet", execute: (_, _) => ValueTask.FromResult(ToolExecutionResult.Text("   ")));
        var executor = Executor(new ToolRegistry([tool]), workspace: workspace);

        var outcome = await RunAsync(executor, tool.Call(), workspace);

        Assert.False(outcome.Result.IsError);
        Assert.Equal("(Quiet completed with no output)", outcome.Result.SingleText());
    }

    [Fact]
    public async Task EmptyErrorOutput_IsKeptAsIs()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool("Failing", execute: (_, _) => ValueTask.FromResult(ToolExecutionResult.Text("", isError: true)));
        var executor = Executor(new ToolRegistry([tool]), workspace: workspace);

        var outcome = await RunAsync(executor, tool.Call(), workspace);

        Assert.True(outcome.Result.IsError);
        Assert.Equal(string.Empty, outcome.Result.SingleText());
    }

    [Fact]
    public async Task ExitPlanMode_OutsidePlanMode_ReturnsGuardMessageWithoutExecuting()
    {
        using var workspace = new TempWorkspace();
        var tool = new ExitPlanModeTool();
        var executor = new ToolCallExecutor(new ToolRegistry([tool]), new ScriptedPermissionGate());
        var call = new ToolCall(
            StrongId.NewToolCallId(),
            "ExitPlanMode",
            JsonDocument.Parse("""{"plan": "1. Do the work."}""").RootElement.Clone());

        var outcome = await RunAsync(executor, call, workspace);

        Assert.True(outcome.Result.IsError);
        Assert.Equal(
            "You are not in plan mode. This tool is only for exiting plan mode after writing a plan. If your plan was already approved, continue with implementation.",
            outcome.Result.SingleText());
    }

    [Fact]
    public async Task ExitPlanMode_DeniedInPlanMode_ReportsPlanNotApproved()
    {
        using var workspace = new TempWorkspace();
        var tool = new ExitPlanModeTool();
        var gate = new ScriptedPermissionGate(PermissionDecision.Deny);
        var executor = new ToolCallExecutor(
            new ToolRegistry([tool]), gate, static _ => PermissionMode.Plan);
        var call = new ToolCall(
            StrongId.NewToolCallId(),
            "ExitPlanMode",
            JsonDocument.Parse("""{"plan": "1. Do the work."}""").RootElement.Clone());

        var outcome = await RunAsync(executor, call, workspace);

        Assert.True(outcome.Result.IsError);
        Assert.Equal("The plan was not approved by the user.", outcome.Result.SingleText());
    }

    [Fact]
    public async Task Skill_LegacyNameInput_PassesSchemaAndValidates()
    {
        using var workspace = new TempWorkspace();
        var tool = new SkillTool();
        var executor = new ToolCallExecutor(new ToolRegistry([tool]), new ScriptedPermissionGate());
        var call = new ToolCall(
            StrongId.NewToolCallId(),
            "Skill",
            JsonDocument.Parse("""{"name": "report", "args": "quarterly"}""").RootElement.Clone());

        var outcome = await RunAsync(executor, call, workspace);

        // The legacy shape survives schema and input validation; execution only fails
        // because the test context has no skill provider.
        Assert.True(outcome.Result.IsError);
        Assert.Equal("Skill tool is not available in this session", outcome.Result.SingleText());
    }

    [Fact]
    public async Task PermissionModified_RevalidatesSchemaAndExecutesModifiedInput()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool("Mutable", schemaJson: TestSchemas.RequiresValue);
        var gate = new ScriptedPermissionGate(new PermissionDecision(
            PermissionOutcome.Modified,
            JsonDocument.Parse("""{"value": "changed"}""").RootElement.Clone()));
        var executor = Executor(new ToolRegistry([tool]), gate, workspace);

        var outcome = await RunAsync(executor, tool.Call("""{"value": "original"}"""), workspace);

        Assert.False(outcome.Result.IsError);
        var executed = Assert.Single(tool.ExecutedContexts);
        Assert.Equal("changed", executed.Input.GetProperty("value").GetString());
    }

    [Fact]
    public async Task PermissionModified_WithSchemaInvalidInput_ReturnsToolUseError()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool("Mutable", schemaJson: TestSchemas.RequiresValue);
        var gate = new ScriptedPermissionGate(new PermissionDecision(
            PermissionOutcome.Modified,
            JsonDocument.Parse("""{"value": 42}""").RootElement.Clone()));
        var executor = Executor(new ToolRegistry([tool]), gate, workspace);

        var outcome = await RunAsync(executor, tool.Call("""{"value": "original"}"""), workspace);

        Assert.True(outcome.Result.IsError);
        Assert.StartsWith("<tool_use_error>", outcome.Result.SingleText());
        Assert.Empty(tool.ExecutedContexts);
    }

    [Fact]
    public async Task DefaultTimeout_CancelsExecution()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool(
            "Slow",
            FakeToolMetadata.ParallelSafe() with { DefaultTimeoutMs = 150 },
            execute: async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return ToolExecutionResult.Text("unreachable");
            });
        var executor = Executor(new ToolRegistry([tool]), workspace: workspace);

        var outcome = await RunAsync(executor, tool.Call(), workspace);

        Assert.Equal("Tool execution cancelled", outcome.Result.SingleText());
        Assert.False(outcome.Result.IsError);
        Assert.True(outcome.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ExternalCancellation_CancelsExecution()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool(
            "Slow",
            execute: async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return ToolExecutionResult.Text("unreachable");
            });
        var executor = Executor(new ToolRegistry([tool]), workspace: workspace);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var outcome = await RunAsync(executor, tool.Call(), workspace, cancellationToken: cts.Token);

        Assert.Equal("Tool execution cancelled", outcome.Result.SingleText());
    }

    [Fact]
    public async Task ToolThrows_ErrorResultCarriesMessage()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool(
            "Broken",
            execute: (_, _) => ValueTask.FromException<ToolExecutionResult>(new InvalidOperationException("boom")));
        var executor = Executor(new ToolRegistry([tool]), workspace: workspace);

        var outcome = await RunAsync(executor, tool.Call(), workspace);

        Assert.True(outcome.Result.IsError);
        Assert.Contains("boom", outcome.Result.SingleText());
        Assert.StartsWith("<tool_use_error>", outcome.Result.SingleText());
    }

    [Fact]
    public async Task Success_ReturnsToolResultAndRunsPipelineInOrder()
    {
        using var workspace = new TempWorkspace();
        var tool = new FakeTool("Read");
        var executor = Executor(new ToolRegistry([tool]), workspace: workspace);
        var call = tool.Call("""{"file_path": "a.txt"}""");

        var notifications = new NotificationCollector();
        var outcome = await RunAsync(executor, call, workspace, notifications);

        Assert.False(outcome.Result.IsError);
        Assert.Equal("ok", outcome.Result.SingleText());
        Assert.Equal(
            [typeof(ToolExecutionStarted), typeof(ToolExecutionPermissionAsked), typeof(ToolExecutionResultNotification)],
            notifications.Notifications.Select(static n => n.GetType()).ToArray());
    }
}
