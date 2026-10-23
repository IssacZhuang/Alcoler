using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tests.Subagents;
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
    public async Task ForegroundAgentCalls_AllStartBeforeAnyChildCompletes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCount = 0;
        var service = new SubagentService((id, _, _) =>
        {
            return ValueTask.FromResult(new SubagentChildSession(
                StrongId.NewSessionId(),
                (_, _) =>
                {
                    if (Interlocked.Increment(ref enteredCount) == 4)
                        entered.TrySetResult();
                    return ValueTask.CompletedTask;
                },
                release.Task,
                () => Task.FromResult<string?>(id.Value),
                () => Task.FromResult(0),
                _ => ValueTask.CompletedTask));
        });
        var agent = new SubagentTool(service);
        var registry = new ToolRegistry([agent]);
        var runner = new ToolBatchRunner(new ToolScheduler(registry),
            new ToolCallExecutor(registry, new ScriptedPermissionGate()));
        var calls = Enumerable.Range(0, 4).Select(i => new ScheduledToolCall(new ToolCall(
            StrongId.NewToolCallId(), agent.Contract.Name,
            JsonSerializer.SerializeToElement(new { prompt = $"task {i}", description = $"child {i}" })))).ToArray();
        using var workspace = new TempWorkspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var execution = runner.ExecuteAsync(calls, workspace.ContextFactory(),
            new NotificationCollector().Collect, timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            Assert.False(execution.IsCompleted);
            Assert.Equal(4, service.ActiveStates.Count);
            Assert.All(service.ActiveStates, state => Assert.Equal(Contracts.Subagents.SubagentStatus.Running, state.Status));
        }
        finally
        {
            release.TrySetResult();
        }
        var result = await execution;
        Assert.Equal(calls.Select(call => call.Call.Id), result.Outcomes.Select(outcome => outcome.Call.Id));
        Assert.All(result.Outcomes, outcome => Assert.False(outcome.Result.IsError));
        Assert.All(service.ActiveStates, state => Assert.Equal(Contracts.Subagents.SubagentStatus.Completed, state.Status));
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
    public async Task ErrorResultTurnStop_CancelsRemainingGroupsAndNotifies()
    {
        var stopped = new FakeTool("Terminal", FakeToolMetadata.Serial(), execute: (_, _) =>
            ValueTask.FromResult(ToolExecutionResult.Text("denied", isError: true, stopTurnAfterResult: true)));
        var later = new FakeTool("Write", FakeToolMetadata.Serial());
        var notifications = new NotificationCollector();
        using var workspace = new TempWorkspace();

        var result = await Runner(stopped, later).ExecuteAsync(
            [new ScheduledToolCall(stopped.Call()), new ScheduledToolCall(later.Call())],
            workspace.ContextFactory(), notifications.Collect, CancellationToken.None);

        Assert.True(result.StoppedAfterResult);
        Assert.True(result.Outcomes[0].Result.IsError);
        Assert.False(result.Outcomes[1].Ran);
        Assert.True(result.Outcomes[1].CancelledByStop);
        Assert.True(result.Outcomes[1].Result.IsError);
        Assert.Empty(later.ExecutedContexts);
        Assert.Equal(2, notifications.Of<ToolExecutionResultNotification>().Count);
        Assert.Single(notifications.Of<ToolExecutionStarted>());
    }

    [Fact]
    public async Task DeniedPlanExit_StopsProductionBatchWithoutScheduledFlag()
    {
        var exit = new ExitPlanModeTool();
        var later = new FakeTool("Write", FakeToolMetadata.Serial());
        var registry = new ToolRegistry([exit, later]);
        var runner = new ToolBatchRunner(new ToolScheduler(registry),
            new ToolCallExecutor(registry, new ScriptedPermissionGate(Contracts.Permissions.PermissionDecision.Deny),
                static _ => new Contracts.Permissions.ExecutionState(
                    Contracts.Permissions.PermissionMode.Edit, PlanEnabled: true)));
        using var workspace = new TempWorkspace();

        var result = await runner.ExecuteAsync(
            [new ScheduledToolCall(new ToolCall(StrongId.NewToolCallId(), exit.Contract.Name,
                JsonSerializer.SerializeToElement(new { plan = "Implement it." }))), new ScheduledToolCall(later.Call())],
            workspace.ContextFactory(), new NotificationCollector().Collect, CancellationToken.None);

        Assert.True(result.StoppedAfterResult);
        Assert.True(result.Outcomes[0].Result.StopTurnAfterResult);
        Assert.True(result.Outcomes[0].Result.IsError);
        Assert.True(result.Outcomes[1].CancelledByStop);
        Assert.Empty(later.ExecutedContexts);
    }

    [Fact]
    public async Task CompletedOutcomes_AreNotRepeatedOrLostAfterStop()
    {
        var stop = new FakeTool("Terminal", FakeToolMetadata.Serial(), execute: (_, _) =>
            ValueTask.FromResult(ToolExecutionResult.Text("stop", stopTurnAfterResult: true)));
        var later = new FakeTool("Write", FakeToolMetadata.Serial());
        var read = new FakeTool("Read");
        var readCall = read.Call();
        var preserved = new ToolBatchOutcome(readCall, ToolExecutionResult.Text("already read"),
            TimeSpan.FromMilliseconds(10), Ran: true, CancelledByStop: false);
        using var workspace = new TempWorkspace();

        var result = await Runner(stop, later, read).ExecuteAsync(
            [new ScheduledToolCall(stop.Call()), new ScheduledToolCall(later.Call()), new ScheduledToolCall(readCall)],
            workspace.ContextFactory(), new NotificationCollector().Collect, CancellationToken.None, [preserved]);

        Assert.True(result.StoppedAfterResult);
        Assert.True(result.Outcomes[1].CancelledByStop);
        Assert.Equal(preserved, result.Outcomes[2]);
        Assert.Empty(read.ExecutedContexts);
        Assert.Empty(later.ExecutedContexts);
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
