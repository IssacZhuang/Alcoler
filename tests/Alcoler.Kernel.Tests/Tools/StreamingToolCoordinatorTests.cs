using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class StreamingToolCoordinatorTests
{
    private static StreamingToolCoordinator Coordinator(
        TempWorkspace workspace,
        NotificationCollector notifications,
        CancellationToken cancellationToken,
        params ITool[] tools)
    {
        var registry = new ToolRegistry(tools);
        var executor = new ToolCallExecutor(registry, new ScriptedPermissionGate());
        return new StreamingToolCoordinator(registry, executor,
            new ToolBatchRunner(new ToolScheduler(registry), executor),
            workspace.ContextFactory(), notifications.Collect, cancellationToken);
    }

    [Fact]
    public async Task ReadyReadOnlyTool_StartsBeforeStreamCompletes_AndNeverRepeats()
    {
        using var workspace = new TempWorkspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new FakeTool("Read", execute: async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return ToolExecutionResult.Text("read result");
        });
        var write = new FakeTool("Write", FakeToolMetadata.Serial());
        var notifications = new NotificationCollector();
        await using var coordinator = Coordinator(workspace, notifications, CancellationToken.None, read, write);
        var readCall = read.Call();
        var writeCall = write.Call();
        try
        {
            await coordinator.AcceptAsync(readCall);
            await coordinator.AcceptAsync(readCall);
            await coordinator.AcceptAsync(writeCall);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(coordinator.HasAcceptedCalls);
            Assert.Equal([readCall.Id, writeCall.Id], coordinator.AcceptedCalls.Select(call => call.Id));
            Assert.Empty(write.ExecutedContexts);
            var completion = coordinator.CompleteAsync([readCall, writeCall]);
            Assert.False(completion.IsCompleted);
            release.TrySetResult();
            var result = await completion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal([readCall.Id, writeCall.Id], result.Outcomes.Select(outcome => outcome.Call.Id));
            Assert.Equal("read result", result.Outcomes[0].Result.SingleText());
            Assert.Single(read.ExecutedContexts);
            Assert.Single(write.ExecutedContexts);
            Assert.Equal(2, notifications.Of<ToolExecutionStarted>().Count);
            Assert.Equal(2, notifications.Of<ToolExecutionResultNotification>().Count);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task FailureRecovery_PreservesCompletedResultsAndAcceptedUnsafeCalls()
    {
        using var workspace = new TempWorkspace();
        var read = new FakeTool("Read", execute: (_, _) =>
            ValueTask.FromResult(ToolExecutionResult.Text("committed read")));
        var write = new FakeTool("Write", FakeToolMetadata.Serial());
        var notifications = new NotificationCollector();
        await using var coordinator = Coordinator(workspace, notifications, CancellationToken.None, read, write);
        var readCall = read.Call();
        var writeCall = write.Call();
        await coordinator.AcceptAsync(readCall);
        await coordinator.AcceptAsync(writeCall);
        await WaitForResultAsync(notifications, readCall.Id);

        var result = await coordinator.RecoverAsync([readCall, writeCall]);

        Assert.Equal([readCall.Id, writeCall.Id], result.Outcomes.Select(outcome => outcome.Call.Id));
        Assert.Equal("committed read", result.Outcomes[0].Result.SingleText());
        Assert.True(result.Outcomes[0].Ran);
        Assert.True(result.Outcomes[1].Result.IsError);
        Assert.False(result.Outcomes[1].Ran);
        Assert.Equal("Tool execution was interrupted during streaming recovery before this tool was executed. Treat this tool call as failed and do not retry blindly.",
            result.Outcomes[1].Result.SingleText());
        Assert.Single(read.ExecutedContexts);
        Assert.Empty(write.ExecutedContexts);
        Assert.Equal(2, notifications.Of<ToolExecutionResultNotification>().Count);
        Assert.Same(result, await coordinator.RecoverAsync([]));
    }

    [Fact]
    public async Task FailureRecovery_UnsafeAndUnknownAcceptedCallsNeverExecute()
    {
        using var workspace = new TempWorkspace();
        var write = new FakeTool("Write", FakeToolMetadata.Serial());
        var unknown = write.Call() with { Name = "NotRegistered" };
        var writeCall = write.Call();
        await using var coordinator = Coordinator(workspace, new NotificationCollector(), CancellationToken.None, write);
        await coordinator.AcceptAsync(writeCall);
        await coordinator.AcceptAsync(unknown);

        var result = await coordinator.RecoverAsync([]);

        Assert.Equal([writeCall.Id, unknown.Id], result.Outcomes.Select(outcome => outcome.Call.Id));
        Assert.All(result.Outcomes, outcome =>
        {
            Assert.True(outcome.Result.IsError);
            Assert.False(outcome.Ran);
        });
        Assert.Empty(write.ExecutedContexts);
    }

    [Fact]
    public async Task Recovery_CancelsUncooperativeExecutionWithoutLateResultOverwrite()
    {
        using var workspace = new TempWorkspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new FakeTool("Read", execute: async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            finished.TrySetResult();
            return ToolExecutionResult.Text("late result");
        });
        var notifications = new NotificationCollector();
        await using var coordinator = Coordinator(workspace, notifications, CancellationToken.None, read);
        var call = read.Call();
        try
        {
            await coordinator.AcceptAsync(call);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await coordinator.RecoverAsync([call]).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(result.Outcomes[0].Result.IsError);
            Assert.Equal("Tool execution cancelled", result.Outcomes[0].Result.SingleText());
            Assert.Single(notifications.Of<ToolExecutionResultNotification>());
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(notifications.Of<ToolExecutionResultNotification>());
            Assert.DoesNotContain("late result", result.Outcomes[0].Result.SingleText());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Complete_PreservesErrorTurnStopAndSkipsUnstartedGroups()
    {
        using var workspace = new TempWorkspace();
        var stop = new FakeTool("Read", execute: (_, _) =>
            ValueTask.FromResult(ToolExecutionResult.Text("stop", isError: true, stopTurnAfterResult: true)));
        var write = new FakeTool("Write", FakeToolMetadata.Serial());
        var notifications = new NotificationCollector();
        await using var coordinator = Coordinator(workspace, notifications, CancellationToken.None, stop, write);
        var stopCall = stop.Call();
        var writeCall = write.Call();
        await coordinator.AcceptAsync(stopCall);
        await coordinator.AcceptAsync(writeCall);

        var result = await coordinator.CompleteAsync([stopCall, writeCall]);

        Assert.True(result.StoppedAfterResult);
        Assert.True(result.Outcomes[0].Result.IsError);
        Assert.True(result.Outcomes[1].CancelledByStop);
        Assert.Empty(write.ExecutedContexts);
        Assert.Single(stop.ExecutedContexts);
    }

    [Fact]
    public async Task StreamExecution_RespectsMaximumConcurrency()
    {
        using var workspace = new TempWorkspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var read = new FakeTool("Read", execute: async (_, _) =>
        {
            var count = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximum, Math.Max(maximum, count));
            if (count == ToolScheduler.MaxConcurrency)
                entered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
            return ToolExecutionResult.Text("read");
        });
        var calls = Enumerable.Range(0, ToolScheduler.MaxConcurrency + 1).Select(_ => read.Call()).ToArray();
        await using var coordinator = Coordinator(workspace, new NotificationCollector(), CancellationToken.None, read);
        try
        {
            foreach (var call in calls)
                await coordinator.AcceptAsync(call);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ToolScheduler.MaxConcurrency, maximum);
            release.TrySetResult();
            var result = await coordinator.CompleteAsync(calls);
            Assert.Equal(calls.Length, result.Outcomes.Count);
            Assert.All(result.Outcomes, outcome => Assert.False(outcome.Result.IsError));
            Assert.InRange(maximum, 1, ToolScheduler.MaxConcurrency);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Abandon_CancelsEarlyReadAndNeverRunsPendingWrite()
    {
        using var workspace = new TempWorkspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new FakeTool("Read", execute: async (_, ct) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                return ToolExecutionResult.Text("unreachable");
            }
            finally
            {
                cancelled.TrySetResult();
            }
        });
        var write = new FakeTool("Write", FakeToolMetadata.Serial());
        await using var coordinator = Coordinator(workspace, new NotificationCollector(), CancellationToken.None, read, write);
        await coordinator.AcceptAsync(read.Call());
        await coordinator.AcceptAsync(write.Call());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.AbandonAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(write.ExecutedContexts);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.AcceptAsync(read.Call()));
    }

    private static async Task WaitForResultAsync(NotificationCollector notifications, ToolCallId id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!notifications.Of<ToolExecutionResultNotification>().Any(result => result.Call.Id == id))
            await Task.Delay(10, timeout.Token);
    }
}
