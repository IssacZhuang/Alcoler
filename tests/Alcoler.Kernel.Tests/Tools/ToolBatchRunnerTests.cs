using Alcoler.Contracts.Tools;
using Xunit;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class ToolBatchRunnerTests
{
    private static ToolBatchRunner Runner(params FakeTool[] tools) =>
        new(
            new ToolScheduler(new ToolRegistry(tools)),
            new ToolCallExecutor(new ToolRegistry(tools), new ScriptedPermissionGate()));

    [Fact]
    public async Task ParallelGroup_ExecutesCallsConcurrently()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        var gateTool = new FakeTool("Read", execute: async (_, _) =>
        {
            if (Interlocked.Increment(ref enteredCount) == 2)
                entered.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return ToolExecutionResult.Text("done");
        });

        using var workspace = new TempWorkspace();
        var runner = Runner(gateTool);
        var result = await runner.ExecuteAsync(
            [new ScheduledToolCall(gateTool.Call()), new ScheduledToolCall(gateTool.Call())],
            workspace.ContextFactory(),
            new NotificationCollector().Collect,
            CancellationToken.None);

        Assert.All(result.Outcomes, outcome =>
        {
            Assert.True(outcome.Ran);
            Assert.False(outcome.CancelledByStop);
            Assert.Equal("done", outcome.Result.SingleText());
        });
    }

    [Fact]
    public async Task StopTurnAfterResult_CancelsRemainingGroups()
    {
        var stopTool = new FakeTool("Grep", execute: (_, _) =>
            ValueTask.FromResult(ToolExecutionResult.Text("found it")));
        var laterTool = new FakeTool("Write", FakeToolMetadata.Serial());

        using var workspace = new TempWorkspace();
        var runner = Runner(stopTool, laterTool);
        var result = await runner.ExecuteAsync(
            [
                new ScheduledToolCall(stopTool.Call(), StopTurnAfterResult: true),
                new ScheduledToolCall(laterTool.Call()),
            ],
            workspace.ContextFactory(),
            new NotificationCollector().Collect,
            CancellationToken.None);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.True(result.Outcomes[0].Ran);
        Assert.Equal("found it", result.Outcomes[0].Result.SingleText());
        Assert.False(result.Outcomes[1].Ran);
        Assert.True(result.Outcomes[1].CancelledByStop);
        Assert.Equal("Tool cancelled because a previous tool result requested a turn stop.", result.Outcomes[1].Result.SingleText());
        Assert.Empty(laterTool.ExecutedContexts);
    }

    [Fact]
    public async Task StopTurnAfterResult_OnFailedResult_DoesNotStopRemainingGroups()
    {
        var failing = new FakeTool("Grep", execute: (_, _) =>
            ValueTask.FromResult(ToolExecutionResult.Text("nope", isError: true)));
        var laterTool = new FakeTool("Write", FakeToolMetadata.Serial());

        using var workspace = new TempWorkspace();
        var runner = Runner(failing, laterTool);
        var result = await runner.ExecuteAsync(
            [
                new ScheduledToolCall(failing.Call(), StopTurnAfterResult: true),
                new ScheduledToolCall(laterTool.Call()),
            ],
            workspace.ContextFactory(),
            new NotificationCollector().Collect,
            CancellationToken.None);

        Assert.True(result.Outcomes[1].Ran);
        Assert.False(result.Outcomes[1].CancelledByStop);
        Assert.Single(laterTool.ExecutedContexts);
    }

    [Fact]
    public async Task Outcomes_PreserveInputOrder()
    {
        var read = new FakeTool("Read");
        var write = new FakeTool("Write", FakeToolMetadata.Serial());
        var readCall = new ScheduledToolCall(read.Call());
        var writeCall = new ScheduledToolCall(write.Call());
        var secondReadCall = new ScheduledToolCall(read.Call());

        using var workspace = new TempWorkspace();
        var runner = Runner(read, write);
        var result = await runner.ExecuteAsync(
            [readCall, writeCall, secondReadCall],
            workspace.ContextFactory(),
            new NotificationCollector().Collect,
            CancellationToken.None);

        Assert.Equal([readCall.Call.Id, writeCall.Call.Id, secondReadCall.Call.Id],
            result.Outcomes.Select(static outcome => outcome.Call.Id).ToArray());
        Assert.All(result.Outcomes, outcome => Assert.True(outcome.Ran));
    }

    [Fact]
    public async Task CancelledBeforeStart_MarksAllCallsNotRan()
    {
        var tool = new FakeTool("Read");
        using var workspace = new TempWorkspace();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = Runner(tool);

        var result = await runner.ExecuteAsync(
            [new ScheduledToolCall(tool.Call()), new ScheduledToolCall(tool.Call())],
            workspace.ContextFactory(),
            new NotificationCollector().Collect,
            cts.Token);

        Assert.All(result.Outcomes, outcome =>
        {
            Assert.False(outcome.Ran);
            Assert.False(outcome.CancelledByStop);
            Assert.Equal("Tool execution cancelled", outcome.Result.SingleText());
        });
        Assert.Empty(tool.ExecutedContexts);
    }
}
