using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Kernel.Subagents;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Alcoler.Kernel.Tests.Subagents;

public class SubagentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static SubagentRequest Request(
        string prompt = "research the topic",
        string? description = null,
        bool background = false) =>
        new(new SessionId("sess_parent"), prompt, Description: description, Background: background);

    [Fact]
    public async Task RunAsync_ForegroundRun_ReturnsCompletedResultWithFinalOutput()
    {
        var factory = new FakeSubagentFactory();
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now));
        var request = Request(description: "research");

        var runTask = service.RunAsync(request, CancellationToken.None).AsTask();
        var child = factory.Children.Single();
        child.Complete();
        var result = await runTask;

        Assert.Same(request, factory.Requests.Single());
        Assert.Equal(new[] { "research the topic" }, child.SubmittedPrompts);
        Assert.Equal(0, child.StopCalls);
        Assert.Equal(SubagentStatus.Completed, result.State.Status);
        Assert.Equal("child output", result.Output);
        Assert.Equal("research", result.State.Description);
        Assert.Equal(child.ChildSessionId, result.State.ChildSessionId);

        var state = service.ActiveStates.Single();
        Assert.Equal(result.State.Id, state.Id);
        Assert.Equal(result.State.Id, Assert.Single(factory.Ids));
        Assert.Equal(SubagentStatus.Completed, state.Status);
        Assert.Equal("child output", state.ResultSummary);
        Assert.Equal(Now, state.StartedAt);
        Assert.Equal(Now, state.EndedAt);
    }

    [Fact]
    public async Task RunAsync_ResultSummary_IsFirstTwoHundredCharsOnASingleLine()
    {
        var output = "a\r\nb   c\n\n" + new string('x', 300);
        var factory = new FakeSubagentFactory(_ => new FakeChildSession(output));
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now));

        var runTask = service.RunAsync(Request(), CancellationToken.None).AsTask();
        factory.Children.Single().Complete();
        var result = await runTask;

        Assert.Equal(("a b c " + new string('x', 300))[..200], result.State.ResultSummary);
    }

    [Fact]
    public async Task RunAsync_WithoutDescription_DerivesSingleLineDescriptionFromPrompt()
    {
        var request = Request(prompt: "fix\r\nthe   build\nand report back " + new string('y', 120));
        var factory = new FakeSubagentFactory();
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now));

        var runTask = service.RunAsync(request, CancellationToken.None).AsTask();
        factory.Children.Single().Complete();
        var result = await runTask;

        Assert.Equal(("fix the build and report back " + new string('y', 120))[..80], result.State.Description);
    }

    [Fact]
    public async Task RunAsync_ChildFaultsWithoutOutput_MapsToFailedWithExceptionMessage()
    {
        var factory = new FakeSubagentFactory(_ => new FakeChildSession(finalOutput: null));
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now));

        var runTask = service.RunAsync(Request(), CancellationToken.None).AsTask();
        var child = factory.Children.Single();
        child.Fail(new InvalidOperationException("kaboom"));
        var result = await runTask;

        Assert.Equal(SubagentStatus.Failed, result.State.Status);
        Assert.Equal("kaboom", result.Output);
        Assert.Equal("kaboom", result.State.ResultSummary);
        Assert.Equal(1, child.StopCalls);
        Assert.Equal(SubagentStatus.Failed, service.ActiveStates.Single().Status);
    }

    [Fact]
    public async Task RunAsync_ChildFaultsAfterPartialOutput_KeepsChildOutput()
    {
        var factory = new FakeSubagentFactory(_ => new FakeChildSession("partial findings"));
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now));

        var runTask = service.RunAsync(Request(), CancellationToken.None).AsTask();
        var child = factory.Children.Single();
        child.Fail(new InvalidOperationException("kaboom"));
        var result = await runTask;

        Assert.Equal(SubagentStatus.Failed, result.State.Status);
        Assert.Equal("partial findings", result.Output);
        Assert.Equal(1, child.StopCalls);
    }

    [Fact]
    public async Task RunAsync_ChildFaultsAndOutputUnreadable_FallsBackToExceptionMessage()
    {
        var factory = new FakeSubagentFactory(_ => new FakeChildSession("never read"));
        var logger = new RecordingLogger();
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now), logger);

        var runTask = service.RunAsync(Request(), CancellationToken.None).AsTask();
        var child = factory.Children.Single();
        child.ReadOutputThrows = true;
        child.Fail(new InvalidOperationException("kaboom"));
        var result = await runTask;

        Assert.Equal(SubagentStatus.Failed, result.State.Status);
        Assert.Equal("kaboom", result.Output);
        Assert.Contains(logger.Entries, e => e.Exception?.Message == "final output unreadable");
    }

    [Fact]
    public async Task RunAsync_CallerCancels_MapsToCancelledAndStopsChild()
    {
        var factory = new FakeSubagentFactory();
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now));
        using var cts = new CancellationTokenSource();

        var runTask = service.RunAsync(Request(), cts.Token).AsTask();
        var child = factory.Children.Single();
        cts.Cancel();
        var result = await runTask;

        Assert.Equal(SubagentStatus.Cancelled, result.State.Status);
        Assert.Equal(string.Empty, result.Output);
        Assert.Null(result.State.ResultSummary);
        Assert.Equal(1, child.StopCalls);
        Assert.Equal(SubagentStatus.Cancelled, service.ActiveStates.Single().Status);
    }

    [Fact]
    public async Task RunAsync_ChildCancelsItself_MapsToCancelled()
    {
        var factory = new FakeSubagentFactory();
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now));

        var runTask = service.RunAsync(Request(), CancellationToken.None).AsTask();
        var child = factory.Children.Single();
        child.Cancel();
        var result = await runTask;

        Assert.Equal(SubagentStatus.Cancelled, result.State.Status);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal(1, child.StopCalls);
    }

    [Fact]
    public async Task StartBackgroundAsync_ReturnsHandleImmediately_ThenNotifiesSinkOnceCompleted()
    {
        const string outputFile = @"C:\root\sess_parent\sub_logwatch\output.txt";
        var factory = new FakeSubagentFactory(_ => new FakeChildSession { OutputFile = outputFile });
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now));
        var sinkDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        SubagentResult? observed = null;

        var handle = await service.StartBackgroundAsync(
            Request("watch the logs", description: "log watch", background: true),
            result =>
            {
                Interlocked.Increment(ref calls);
                observed = result;
                sinkDone.TrySetResult();
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        var child = factory.Children.Single();
        Assert.True(handle.Background);
        Assert.Equal("log watch", handle.Description);
        Assert.Equal(child.ChildSessionId, handle.ChildSessionId);
        Assert.Equal(outputFile, handle.OutputFile);

        var running = service.ActiveStates.Single();
        Assert.Equal(handle.Id, running.Id);
        Assert.Equal(handle.Id, Assert.Single(factory.Ids));
        Assert.Equal(SubagentStatus.Running, running.Status);
        Assert.Null(running.EndedAt);

        child.Complete();
        await sinkDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.NotNull(observed);
        Assert.Equal(handle.Id, observed!.State.Id);
        Assert.Equal(SubagentStatus.Completed, observed.State.Status);
        Assert.Equal("child output", observed.Output);
        Assert.Equal(new[] { "watch the logs" }, child.SubmittedPrompts);

        var done = service.ActiveStates.Single();
        Assert.Equal(SubagentStatus.Completed, done.Status);
        Assert.NotNull(done.EndedAt);
        Assert.Equal("child output", done.ResultSummary);
    }

    [Fact]
    public async Task StartBackgroundAsync_SinkThrows_IsObservedInLogAndNeverPropagates()
    {
        var factory = new FakeSubagentFactory();
        var logger = new RecordingLogger();
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now), logger);
        var sinkRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        await service.StartBackgroundAsync(
            Request(background: true),
            _ =>
            {
                Interlocked.Increment(ref calls);
                sinkRan.TrySetResult();
                return ValueTask.FromException(new InvalidOperationException("sink blew up"));
            },
            CancellationToken.None);

        factory.Children.Single().Complete();
        await sinkRan.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref calls));
        await AsyncTestWait.UntilAsync(() => logger.Entries.Any(e => e.Level == LogLevel.Error));
        Assert.Contains(
            logger.Entries,
            e => e.Exception is InvalidOperationException { Message: "sink blew up" });
        Assert.Equal(SubagentStatus.Completed, service.ActiveStates.Single().Status);
    }

    [Fact]
    public async Task StartBackgroundAsync_CompletionRacingCancellation_InvokesSinkExactlyOnce()
    {
        var factory = new FakeSubagentFactory();
        var service = new SubagentService(factory.ToFactory(), new FixedTimeProvider(Now));
        using var cts = new CancellationTokenSource();
        var sinkDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        await service.StartBackgroundAsync(
            Request(background: true),
            _ =>
            {
                Interlocked.Increment(ref calls);
                sinkDone.TrySetResult();
                return ValueTask.CompletedTask;
            },
            cts.Token);

        var child = factory.Children.Single();
        child.Complete();
        cts.Cancel();

        await sinkDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref calls));
    }
}
