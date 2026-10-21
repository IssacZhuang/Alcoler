using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class TaskStopToolTests
{
    [Fact]
    public async Task MissingTaskId_IsRejected()
    {
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new TaskStopTool();

        var result = await tool.ExecuteAsync(Context(workspace, jobs, "{}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Missing required parameter: task_id", result.SingleText());
    }

    [Fact]
    public async Task UnknownTask_ReportsNotFound()
    {
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new TaskStopTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, """{"task_id": "bg_missing"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("No task found with ID: bg_missing", result.SingleText());
    }

    [Fact]
    public async Task CompletedTask_IsNotRunning()
    {
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var started = jobs.Start("done", null, _ => Task.FromResult(new BackgroundJobResult(0)));
        await WaitForStatusAsync(jobs, started.Id, BackgroundJobStatus.Completed);
        var tool = new TaskStopTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, $$"""{"task_id": "{{started.Id.Value}}"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal($"Task {started.Id.Value} is not running (status: completed)", result.SingleText());
    }

    [Fact]
    public async Task RunningTask_IsStoppedAndReturnsReferenceJson()
    {
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var started = jobs.Start(
            "ping -n 20 127.0.0.1",
            null,
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new BackgroundJobResult(0);
            });
        var tool = new TaskStopTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, $$"""{"task_id": "{{started.Id.Value}}"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            $$"""{"message":"Successfully stopped task: {{started.Id.Value}} (ping -n 20 127.0.0.1)","task_id":"{{started.Id.Value}}","task_type":"local_bash","command":"ping -n 20 127.0.0.1"}""",
            result.SingleText());
        await WaitForStatusAsync(jobs, started.Id, BackgroundJobStatus.Stopped);
    }

    [Fact]
    public async Task DeprecatedShellId_IsAcceptedAsTaskId()
    {
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var started = jobs.Start(
            "node server.js",
            null,
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new BackgroundJobResult(0);
            });
        var tool = new TaskStopTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, jobs, $$"""{"shell_id": "{{started.Id.Value}}"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("\"task_id\":\"" + started.Id.Value + "\"", result.SingleText());
        await WaitForStatusAsync(jobs, started.Id, BackgroundJobStatus.Stopped);
    }

    [Fact]
    public void Contract_SchemaExposesDeprecatedShellId()
    {
        var contract = new TaskStopTool().Contract;

        Assert.Equal("TaskStop", contract.Name);
        var schema = contract.InputSchema.GetRawText();
        Assert.Contains("\"shell_id\"", schema);
        Assert.Contains("Deprecated: use task_id instead", schema);
        Assert.DoesNotContain("\"required\"", schema);
    }

    [Fact]
    public async Task Subagent_Running_StopsAndReturnsLocalAgentPayload()
    {
        using var workspace = new TempWorkspace();
        var registry = new SubagentTaskRegistry();
        var info = SubagentInfo(SubagentStatus.Running);
        var stopped = false;
        registry.Register(info, _ =>
        {
            stopped = true;
            return ValueTask.CompletedTask;
        });
        var tool = new TaskStopTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, null, """{"task_id": "sub_agent"}""", registry),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            """{"message":"Successfully stopped task: sub_agent (explore the workspace)","task_id":"sub_agent","task_type":"local_agent","command":"explore the workspace"}""",
            result.SingleText());
        Assert.True(stopped);
    }

    [Fact]
    public async Task Subagent_Terminal_ReportsNotRunning()
    {
        using var workspace = new TempWorkspace();
        var registry = new SubagentTaskRegistry();
        var stopped = false;
        registry.Register(SubagentInfo(SubagentStatus.Completed), _ =>
        {
            stopped = true;
            return ValueTask.CompletedTask;
        });
        var tool = new TaskStopTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, null, """{"task_id": "sub_agent"}""", registry),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Task sub_agent is not running (status: completed)", result.SingleText());
        Assert.False(stopped);
    }

    [Fact]
    public async Task Subagent_Unknown_ReportsNotFound()
    {
        using var workspace = new TempWorkspace();
        var registry = new SubagentTaskRegistry();
        var tool = new TaskStopTool();

        var result = await tool.ExecuteAsync(
            Context(workspace, null, """{"task_id": "sub_missing"}""", registry),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("No task found with ID: sub_missing", result.SingleText());
    }

    private static SubagentTaskInfo SubagentInfo(SubagentStatus status) =>
        new(
            new SubagentId("sub_agent"),
            new SessionId("sess_child"),
            "general-purpose",
            "explore the workspace",
            "explore",
            OutputFile: null,
            status,
            Result: null,
            Error: null,
            ToolUses: null,
            DateTimeOffset.UtcNow,
            status == SubagentStatus.Running ? null : DateTimeOffset.UtcNow);

    private static ToolExecutionContext Context(
        TempWorkspace workspace,
        BackgroundJobService? jobs,
        string json,
        ISubagentTasks? subagents = null) =>
        new(
            new SessionId("sess_test"),
            workspace.Root,
            JsonDocument.Parse(json).RootElement.Clone(),
            workspace.Workspace,
            TimeProvider.System,
            BackgroundJobs: jobs,
            Subagents: subagents);

    private static async Task WaitForStatusAsync(BackgroundJobService jobs, BackgroundJobId id, BackgroundJobStatus status)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (jobs.Find(id)?.Status != status)
        {
            if (deadline.IsCompleted)
                throw new TimeoutException($"job never reached {status}");
            await Task.Delay(20);
        }
    }
}
