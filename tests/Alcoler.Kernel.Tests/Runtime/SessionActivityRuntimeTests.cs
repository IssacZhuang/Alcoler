using System.Collections.Concurrent;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tests.EndToEnd;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionActivityRuntimeTests
{
    [Fact]
    public async Task BackgroundJobs_ListAndStopOnlyTheSelectedSessionRegistry()
    {
        var jobs = new ConcurrentDictionary<SessionId, SyntheticJob>();
        await using var harness = TestHarness.Create(StartJob(), Reply(), StartJob(), Reply(), Reply());
        await using var runtime = harness.BuildRuntime(CreateJobTool(jobs));
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);
        var unknown = StrongId.NewSessionId();
        Assert.Empty(activity.GetBackgroundJobs(unknown));
        Assert.False(activity.StopBackgroundJob(unknown, StrongId.NewBackgroundJobId()));

        var first = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var second = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var firstProjection = runtime.Subscribe(first.Id);
        using var secondProjection = runtime.Subscribe(second.Id);
        await StartAndWaitAsync(runtime, first.Id, firstProjection);
        await StartAndWaitAsync(runtime, second.Id, secondProjection);

        var firstJob = jobs[first.Id];
        var secondJob = jobs[second.Id];
        var firstSnapshot = activity.GetBackgroundJobs(first.Id);
        Assert.Equal(firstJob.Info, Assert.Single(firstSnapshot));
        Assert.Equal(secondJob.Info, Assert.Single(activity.GetBackgroundJobs(second.Id)));
        Assert.Equal(BackgroundJobStatus.Running, firstJob.Info.Status);
        Assert.NotEqual(firstJob.Info.Id, secondJob.Info.Id);

        Assert.False(activity.StopBackgroundJob(first.Id, secondJob.Info.Id));
        Assert.False(activity.StopBackgroundJob(second.Id, firstJob.Info.Id));
        Assert.False(activity.StopBackgroundJob(unknown, firstJob.Info.Id));
        Assert.False(activity.StopBackgroundJob(first.Id, StrongId.NewBackgroundJobId()));
        Assert.False(firstJob.Cancelled.Task.IsCompleted);
        Assert.False(secondJob.Cancelled.Task.IsCompleted);

        var stopTurn = await runtime.SubmitAsync(
            new StopCommand(StrongId.NewCommandId(), first.Id, 0), CancellationToken.None);
        Assert.True(stopTurn.Accepted);
        Assert.Equal(BackgroundJobStatus.Running, Assert.Single(activity.GetBackgroundJobs(first.Id)).Status);
        Assert.False(firstJob.Cancelled.Task.IsCompleted);

        Assert.True(activity.StopBackgroundJob(first.Id, firstJob.Info.Id));
        await firstJob.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForJobStatusAsync(activity, first.Id, BackgroundJobStatus.Stopped);
        await WaitForRepliesAsync(firstProjection, 2);
        var stopped = Assert.Single(activity.GetBackgroundJobs(first.Id));
        Assert.Equal(firstJob.Info.Id, stopped.Id);
        Assert.Equal(firstJob.Info.OutputPath, stopped.OutputPath);
        Assert.Null(stopped.ExitCode);
        Assert.Equal("Job was stopped", stopped.Error);
        Assert.False(activity.StopBackgroundJob(first.Id, stopped.Id));
        Assert.Equal(BackgroundJobStatus.Running, Assert.Single(firstSnapshot).Status);
        Assert.Equal(secondJob.Info, Assert.Single(activity.GetBackgroundJobs(second.Id)));
        Assert.False(secondJob.Cancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task BackgroundJobs_DisposedAndInactiveSessionsReturnEmptyAndFalse()
    {
        var jobs = new ConcurrentDictionary<SessionId, SyntheticJob>();
        await using var harness = TestHarness.Create(StartJob(), Reply());
        await using var runtime = harness.BuildRuntime(CreateJobTool(jobs));
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await StartAndWaitAsync(runtime, session.Id, projection);
        var job = jobs[session.Id];
        Assert.Equal(job.Info, Assert.Single(activity.GetBackgroundJobs(session.Id)));

        await runtime.DisposeAsync();
        await job.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(activity.GetBackgroundJobs(session.Id));
        Assert.False(activity.StopBackgroundJob(session.Id, job.Info.Id));

        await using var reopened = harness.BuildRuntime();
        var reopenedActivity = Assert.IsAssignableFrom<ISessionActivityRuntime>(reopened);
        Assert.Empty(reopenedActivity.GetBackgroundJobs(session.Id));
        Assert.False(reopenedActivity.StopBackgroundJob(session.Id, job.Info.Id));
        await reopened.ResumeSessionAsync(session.Id, CancellationToken.None);
        Assert.Empty(reopenedActivity.GetBackgroundJobs(session.Id));
        Assert.False(reopenedActivity.StopBackgroundJob(session.Id, job.Info.Id));
    }

    [Fact]
    public async Task SubagentProjection_RealWiringResolvesLiveChildOnlyForItsParentWithoutLoadingSessions()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new FakeTool("WaitForRelease", execute: async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ToolExecutionResult.Text("released");
        });
        await using var harness = TestHarness.Create(
            new ScriptStep.Events(
                new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), "Agent",
                    JsonSerializer.SerializeToElement(new { prompt = "Read the workspace", description = "reader" }))),
                new ModelStreamFinished(ModelFinishReason.ToolCalls)),
            new ScriptStep.Events(
                new ModelTextDelta("Current child text"),
                new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), "WaitForRelease", JsonSerializer.SerializeToElement(new { }))),
                new ModelStreamFinished(ModelFinishReason.ToolCalls)),
            new ScriptStep.Events(new ModelTextDelta("Child complete"), new ModelStreamFinished(ModelFinishReason.Stop)),
            Reply());
        await using var runtime = harness.BuildRuntime(gate);
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);
        var parent = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: Contracts.Permissions.PermissionMode.Yolo), CancellationToken.None);
        var unrelated = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var parentProjection = runtime.Subscribe(parent.Id);
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), parent.Id, 0, "delegate"), CancellationToken.None);
        Assert.True(ack.Accepted);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var currentParent = runtime.Subscribe(parent.Id);
            var state = Assert.Single(currentParent.Snapshot().Rows.OfType<SystemNoticeRow>(), row => row.Kind == "subagent").Subagent;
            Assert.NotNull(state);
            Assert.Equal(Contracts.Subagents.SubagentStatus.Running, state.Status);
            Assert.Null(runtime.TryObserve(state.ChildSessionId));
            var sessionsBefore = await runtime.ListSessionsAsync(harness.Root, 10, CancellationToken.None);
            Assert.Equal(2, sessionsBefore.Count);
            Assert.DoesNotContain(sessionsBefore, session => session.Id == state.ChildSessionId);

            Assert.Null(activity.TryObserveSubagent(unrelated.Id, state.ChildSessionId));
            Assert.Null(activity.TryObserveSubagent(StrongId.NewSessionId(), state.ChildSessionId));
            Assert.Null(activity.TryObserveSubagent(parent.Id, unrelated.Id));
            Assert.Null(activity.TryObserveSubagent(parent.Id, StrongId.NewSessionId()));
            Assert.Null(activity.TryObserveSubagent(state.ChildSessionId, parent.Id));
            using var childProjection = activity.TryObserveSubagent(parent.Id, state.ChildSessionId);
            Assert.NotNull(childProjection);
            var snapshot = childProjection.Snapshot();
            Assert.Contains(snapshot.Rows, row => row is UserMessageRow { Text: "Read the workspace" });
            Assert.Contains(snapshot.Rows, row => row is AssistantMessageRow message &&
                message.Parts.OfType<Contracts.Conversation.TextPart>().Any(part => part.Text == "Current child text"));
            Assert.Equal(TurnPhase.ExecutingTools, snapshot.State.Phase);
            using var observedAgain = activity.TryObserveSubagent(parent.Id, state.ChildSessionId);
            Assert.NotNull(observedAgain);
            Assert.Equal(snapshot.Sequence, observedAgain.Snapshot().Sequence);
            var sessionsAfter = await runtime.ListSessionsAsync(harness.Root, 10, CancellationToken.None);
            Assert.Equal(sessionsBefore.ToArray(), sessionsAfter.ToArray());
            Assert.Null(runtime.TryObserve(state.ChildSessionId));

            release.TrySetResult();
            await WaitForRepliesAsync(parentProjection, 1);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!childProjection.Snapshot().Rows.OfType<AssistantMessageRow>().Any(message =>
                       message.Parts.OfType<Contracts.Conversation.TextPart>().Any(part => part.Text == "Child complete")))
                await Task.Delay(10, timeout.Token);

            await runtime.DisposeAsync();
            Assert.Null(activity.TryObserveSubagent(parent.Id, state.ChildSessionId));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static FakeTool CreateJobTool(ConcurrentDictionary<SessionId, SyntheticJob> jobs) => new(
        "StartSyntheticJob",
        execute: (context, _) =>
        {
            var service = Assert.IsAssignableFrom<IBackgroundJobService>(context.BackgroundJobs);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var info = service.Start("synthetic job", Path.Combine(context.WorkspaceRoot, "job-output.txt"), async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return new BackgroundJobResult(0);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            });
            jobs[context.SessionId] = new SyntheticJob(info, cancelled);
            return ValueTask.FromResult(ToolExecutionResult.Text(info.Id.Value));
        });

    private static ScriptStep StartJob() => new ScriptStep.Events(
        new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), "StartSyntheticJob", JsonSerializer.SerializeToElement(new { }))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls));

    private static ScriptStep Reply() => new ScriptStep.Events(
        new ModelTextDelta("done"), new ModelStreamFinished(ModelFinishReason.Stop));

    private static async Task StartAndWaitAsync(AgentRuntime runtime, SessionId sessionId, IConversationProjection projection)
    {
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), sessionId, 0, "start synthetic job"), CancellationToken.None);
        Assert.True(ack.Accepted);
        await WaitForRepliesAsync(projection, 1);
    }

    private static async Task WaitForRepliesAsync(IConversationProjection projection, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (projection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != TurnPhase.Idle ||
                snapshot.Rows.OfType<AssistantMessageRow>().Count(row => !row.IsStreaming &&
                    row.Parts.OfType<Contracts.Conversation.TextPart>().Any(part => part.Text == "done")) < count))
            await Task.Delay(10, timeout.Token);
    }

    private static async Task WaitForJobStatusAsync(ISessionActivityRuntime activity, SessionId sessionId, BackgroundJobStatus status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (Assert.Single(activity.GetBackgroundJobs(sessionId)).Status != status)
            await Task.Delay(10, timeout.Token);
    }

    private sealed record SyntheticJob(BackgroundJobInfo Info, TaskCompletionSource Cancelled);
}
