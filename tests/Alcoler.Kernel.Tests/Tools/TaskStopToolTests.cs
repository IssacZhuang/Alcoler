using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
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
    public async Task RunningTask_IsStoppedAndReported()
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
        Assert.Equal($"Successfully stopped task: {started.Id.Value} (ping -n 20 127.0.0.1)", result.SingleText());
        await WaitForStatusAsync(jobs, started.Id, BackgroundJobStatus.Stopped);
    }

    private static ToolExecutionContext Context(TempWorkspace workspace, BackgroundJobService jobs, string json) =>
        new(
            new SessionId("sess_test"),
            workspace.Root,
            JsonDocument.Parse(json).RootElement.Clone(),
            workspace.Workspace,
            TimeProvider.System,
            BackgroundJobs: jobs);

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
