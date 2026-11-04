using System.Collections.Concurrent;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tests.EndToEnd;
using Alcoler.Kernel.Tests.Models;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionActivityRuntimeTests
{
    [Fact]
    public void ActiveTasks_DefaultImplementationRemainsEmpty()
    {
        ISessionActivityRuntime activity = new LegacyActivityRuntime();
        Assert.Empty(activity.GetActiveSessionTasks());
    }

    [Fact]
    public async Task ActiveTasks_OnlyReadResidentWorkAndKeepTheActualTurnStart()
    {
        var clock = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new FakeTool("WaitForRelease", execute: async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ToolExecutionResult.Text("released");
        });
        await using var harness = TestHarness.Create(CallTool("WaitForRelease", new { }), Reply());
        CountingSessionStore? store = null;
        await using var runtime = harness.BuildRuntime(options => options with
        {
            Clock = clock,
            Store = store = new CountingSessionStore(options.Store),
        }, gate);
        var cold = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await runtime.UnloadIdleSessionsAsync(TimeSpan.FromHours(1), null, CancellationToken.None));
        Assert.Null(runtime.GetSession(cold.Id));
        await runtime.CreateSessionAsync(new SessionOptions(harness.Root, "idle"), CancellationToken.None);
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root, "working"), CancellationToken.None);
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);
        Assert.Empty(activity.GetActiveSessionTasks());
        using var projection = runtime.Subscribe(session.Id);
        var startedAt = clock.GetUtcNow();
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "work"), CancellationToken.None);
        Assert.True(ack.Accepted);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase == TurnPhase.ExecutingTools);
            clock.Advance(TimeSpan.FromMinutes(3));
            var rename = await runtime.SubmitAsync(
                new RenameSessionCommand(StrongId.NewCommandId(), session.Id, 0, "renamed"), CancellationToken.None);
            Assert.True(rename.Accepted);
            Assert.NotNull(store);
            var reads = store.ReadCount;
            for (var poll = 0; poll < 3; poll++)
            {
                var task = Assert.Single(activity.GetActiveSessionTasks());
                Assert.Equal(session.Id, task.SessionId);
                Assert.Equal("renamed", task.Title);
                Assert.Equal(harness.Root, task.WorkspaceRoot);
                Assert.Equal(TurnPhase.ExecutingTools, task.Phase);
                Assert.True(task.HasActiveWork);
                Assert.Null(task.LastOutcome);
                Assert.Equal(clock.GetUtcNow(), task.LastActivityUtc);
                Assert.Equal(startedAt, task.StartedAt);
                Assert.Equal(0, task.RunningJobCount);
                Assert.Equal(0, task.RunningSubagentCount);
                Assert.False(task.IsAwaitingConfirmation);
                clock.Advance(TimeSpan.FromSeconds(1));
                if (poll < 2)
                    await runtime.SubmitAsync(new RenameSessionCommand(
                        StrongId.NewCommandId(), session.Id, 0, "renamed"), CancellationToken.None);
            }
            Assert.Equal(reads, store.ReadCount);
            Assert.Null(runtime.GetSession(cold.Id));
            release.TrySetResult();
            await WaitForRepliesAsync(projection, 1);
            await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase == TurnPhase.Idle);
            Assert.Empty(activity.GetActiveSessionTasks());
        }
        finally
        {
            release.TrySetResult();
        }
        await runtime.DisposeAsync();
        Assert.Empty(activity.GetActiveSessionTasks());
    }

    [Fact]
    public async Task ActiveTasks_IdleHeldInputRemainsVisibleWithoutAStaleTurnStart()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new FakeTool("WaitForStop", execute: async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolExecutionResult.Text("released");
        });
        await using var harness = TestHarness.Create(CallTool("WaitForStop", new { }));
        await using var runtime = harness.BuildRuntime(gate);
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        Assert.True((await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "work"), CancellationToken.None)).Accepted);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "later", Delivery: SendTextDelivery.Queue);
        Assert.True((await runtime.SubmitAsync(queued, CancellationToken.None)).Accepted);
        Assert.True((await runtime.SubmitAsync(
            new StopCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None)).Accepted);
        await WaitUntilAsync(() => runtime.GetSessionActivity(session.Id) is
            { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Cancelled });

        var task = Assert.Single(runtime.GetActiveSessionTasks());
        Assert.Equal(session.Id, task.SessionId);
        Assert.Equal(TurnPhase.Idle, task.Phase);
        Assert.True(task.HasActiveWork);
        Assert.Equal(TurnOutcome.Cancelled, task.LastOutcome);
        Assert.Null(task.StartedAt);
        Assert.Equal(0, task.RunningJobCount);
        Assert.Equal(0, task.RunningSubagentCount);
        Assert.False(task.IsAwaitingConfirmation);
        Assert.True((await runtime.SubmitAsync(new RemoveQueuedInputCommand(
            StrongId.NewCommandId(), session.Id, 0, queued.CommandId), CancellationToken.None)).Accepted);
        Assert.Empty(runtime.GetActiveSessionTasks());
    }

    [Fact]
    public async Task ActiveTasks_KeepFailedOutcomeWhileBackgroundWorkRemains()
    {
        var jobs = new ConcurrentDictionary<SessionId, SyntheticJob>();
        await using var harness = TestHarness.Create(StartJob(), new ScriptStep.Fail(
            new ModelFailure(ModelFailureKind.AuthenticationFailed, "bad key")));
        await using var runtime = harness.BuildRuntime(CreateJobTool(jobs));
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: Contracts.Permissions.PermissionMode.Yolo), CancellationToken.None);

        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None)).Accepted);
        await WaitUntilAsync(() => runtime.GetSessionActivity(session.Id) is
            { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Failed });

        var task = Assert.Single(runtime.GetActiveSessionTasks());
        Assert.Equal(TurnPhase.Idle, task.Phase);
        Assert.True(task.HasActiveWork);
        Assert.Equal(TurnOutcome.Failed, task.LastOutcome);
        Assert.Equal(1, task.RunningJobCount);
        Assert.Equal(BackgroundJobStatus.Running, Assert.Single(runtime.GetBackgroundJobs(session.Id)).Status);

        jobs[session.Id].Complete.TrySetResult();
        await WaitForJobStatusAsync(runtime, session.Id, BackgroundJobStatus.Completed);
        await WaitUntilAsync(() => runtime.GetActiveSessionTasks().Count == 0);
    }

    [Fact]
    public async Task ActiveTasks_CompletedChildStillContributesItsRunningJob()
    {
        var clock = new ManualTimeProvider();
        var jobs = new ConcurrentDictionary<SessionId, SyntheticJob>();
        await using var harness = TestHarness.Create(
            CallTool("Agent", new { prompt = "start work", description = "worker" }),
            StartJob(), Reply(), Reply(), Reply(), Reply());
        await using var runtime = harness.BuildRuntime(options => options with { Clock = clock },
            CreateJobTool(jobs, () => clock.Advance(TimeSpan.FromMinutes(1))));
        var parent = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: Contracts.Permissions.PermissionMode.Yolo), CancellationToken.None);
        await runtime.CreateSessionAsync(new SessionOptions(harness.Root, "unrelated"), CancellationToken.None);
        using var parentProjection = runtime.Subscribe(parent.Id);
        await StartAndWaitAsync(runtime, parent.Id, parentProjection);
        var state = Assert.Single(parentProjection.Snapshot().Rows.OfType<SystemNoticeRow>(),
            row => row.Kind == "subagent").Subagent;
        Assert.NotNull(state);
        Assert.Equal(SubagentStatus.Completed, state.Status);
        using var childProjection = runtime.TryObserveSubagent(parent.Id, state.ChildSessionId);
        Assert.NotNull(childProjection);
        await WaitForRepliesAsync(childProjection, 1);
        var job = jobs[state.ChildSessionId];

        var task = Assert.Single(runtime.GetActiveSessionTasks());
        Assert.Equal(parent.Id, task.SessionId);
        Assert.Equal(TurnPhase.Idle, task.Phase);
        Assert.True(task.HasActiveWork);
        Assert.Equal(TurnOutcome.Completed, task.LastOutcome);
        Assert.Equal(1, task.RunningJobCount);
        Assert.Equal(0, task.RunningSubagentCount);
        Assert.False(task.IsAwaitingConfirmation);
        Assert.Equal(job.Info.StartedAt, task.StartedAt);
        Assert.True(task.StartedAt > state.StartedAt);
        Assert.Empty(runtime.GetBackgroundJobs(parent.Id));
        Assert.Null(runtime.GetSession(state.ChildSessionId));

        job.Complete.TrySetResult();
        await WaitForRepliesAsync(childProjection, 2);
        await WaitForRepliesAsync(parentProjection, 2);
        Assert.Empty(runtime.GetActiveSessionTasks());
    }

    [Fact]
    public async Task ActiveTasks_RecursivelyAggregateDescendantWaitsAndDeduplicateAgentTasks()
    {
        await using var harness = TestHarness.Create(CallTool("AskUserQuestion", new
        {
            questions = new[]
            {
                new
                {
                    question = "Which approach?", header = "Approach",
                    options = new[]
                    {
                        new { label = "First", description = "First choice" },
                        new { label = "Second", description = "Second choice" },
                    },
                },
            },
        }), Reply());
        await using var runtime = harness.BuildRuntime();
        await using var childRuntime = harness.BuildRuntime();
        await using var grandchildRuntime = harness.BuildRuntime();
        var parent = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var unrelated = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var child = await childRuntime.CreateSessionAsync(
            new SessionOptions(harness.Root, ParentSessionId: parent.Id), CancellationToken.None);
        var grandchild = await grandchildRuntime.CreateSessionAsync(
            new SessionOptions(harness.Root, ParentSessionId: child.Id), CancellationToken.None);
        runtime.RegisterSubagentRuntime(parent.Id, child.Id, childRuntime);
        childRuntime.RegisterSubagentRuntime(child.Id, grandchild.Id, grandchildRuntime);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var id = new SubagentId("shared-task");
        RegisterTask(runtime, parent.Id, child.Id, id, startedAt);
        RegisterTask(childRuntime, child.Id, grandchild.Id, id, startedAt);
        var unrelatedId = new SubagentId("unrelated-task");
        RegisterTask(runtime, unrelated.Id, StrongId.NewSessionId(), unrelatedId, startedAt.AddMinutes(-10));
        using var projection = grandchildRuntime.Subscribe(grandchild.Id);
        Assert.True((await grandchildRuntime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), grandchild.Id, 0, "ask"), CancellationToken.None)).Accepted);
        await WaitUntilAsync(() => grandchildRuntime.GetSessionActivity(grandchild.Id)?.Phase == TurnPhase.AwaitingInteraction);

        var tasks = runtime.GetActiveSessionTasks();
        Assert.Equal(2, tasks.Count);
        var task = Assert.Single(tasks, task => task.SessionId == parent.Id);
        Assert.Equal(TurnPhase.Idle, task.Phase);
        Assert.True(task.HasActiveWork);
        Assert.True(task.IsAwaitingConfirmation);
        Assert.Equal(0, task.RunningJobCount);
        Assert.Equal(1, task.RunningSubagentCount);
        Assert.Equal(startedAt, task.StartedAt);
        Assert.DoesNotContain(tasks, task => task.SessionId == child.Id || task.SessionId == grandchild.Id);
        Assert.Equal(1, Assert.Single(tasks, task => task.SessionId == unrelated.Id).RunningSubagentCount);
        var interaction = projection.Snapshot().State.PendingInteraction;
        Assert.NotNull(interaction);
        Assert.True((await grandchildRuntime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), grandchild.Id, 0, interaction.InteractionId,
            InteractionAnswer: new InteractionAnswer(new Dictionary<string, string> { ["Approach"] = "First" })),
            CancellationToken.None)).Accepted);
        await WaitForRepliesAsync(projection, 1);
        CompleteTask(runtime, id);
        CompleteTask(childRuntime, id);
        CompleteTask(runtime, unrelatedId);
        Assert.Empty(runtime.GetActiveSessionTasks());
    }

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
        var tasks = activity.GetActiveSessionTasks();
        Assert.Equal(2, tasks.Count);
        foreach (var task in tasks)
        {
            Assert.Equal(TurnPhase.Idle, task.Phase);
            Assert.Equal(1, task.RunningJobCount);
            Assert.Equal(0, task.RunningSubagentCount);
            Assert.False(task.IsAwaitingConfirmation);
            Assert.Equal(jobs[task.SessionId].Info.StartedAt, task.StartedAt);
        }

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
        Assert.Equal(second.Id, Assert.Single(activity.GetActiveSessionTasks()).SessionId);
        Assert.Equal(2, tasks.Count);
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
        Assert.Empty(activity.GetActiveSessionTasks());
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
            var task = Assert.Single(activity.GetActiveSessionTasks());
            Assert.Equal(parent.Id, task.SessionId);
            Assert.Equal(TurnPhase.ExecutingTools, task.Phase);
            Assert.Equal(1, task.RunningSubagentCount);
            Assert.Equal(0, task.RunningJobCount);
            Assert.False(task.IsAwaitingConfirmation);
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

    [Fact]
    public async Task SubagentStop_StopsTheTargetedRunningChildAndReportsCancelledState()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new FakeTool("WaitForRelease", execute: async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ToolExecutionResult.Text("released");
        });
        // The stopped child never makes a second model call, so the parent's single
        // post-tool-call response is the terminal reply.
        await using var harness = TestHarness.Create(
            new ScriptStep.Events(
                new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), "Agent",
                    JsonSerializer.SerializeToElement(new { prompt = "Read the workspace", description = "reader" }))),
                new ModelStreamFinished(ModelFinishReason.ToolCalls)),
            new ScriptStep.Events(
                new ModelTextDelta("Current child text"),
                new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), "WaitForRelease", JsonSerializer.SerializeToElement(new { }))),
                new ModelStreamFinished(ModelFinishReason.ToolCalls)),
            Reply());
        await using var runtime = harness.BuildRuntime(gate);
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);
        var parent = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: Contracts.Permissions.PermissionMode.Yolo), CancellationToken.None);
        using var parentProjection = runtime.Subscribe(parent.Id);
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), parent.Id, 0, "delegate"), CancellationToken.None);
        Assert.True(ack.Accepted);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            SubagentState CurrentState()
            {
                using var projection = runtime.Subscribe(parent.Id);
                var state = Assert.Single(projection.Snapshot().Rows.OfType<SystemNoticeRow>(),
                    row => row.Kind == "subagent").Subagent;
                Assert.NotNull(state);
                return state;
            }

            var state = CurrentState();
            Assert.False(await activity.TryStopSubagentAsync(StrongId.NewSessionId(), state.Id.Value));
            Assert.False(await activity.TryStopSubagentAsync(parent.Id, "missing-task"));
            Assert.True(await activity.TryStopSubagentAsync(parent.Id, state.Id.Value));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (CurrentState().Status != Contracts.Subagents.SubagentStatus.Cancelled)
                await Task.Delay(10, timeout.Token);
            await WaitForRepliesAsync(parentProjection, 1);
            Assert.False(await activity.TryStopSubagentAsync(parent.Id, state.Id.Value));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static FakeTool CreateJobTool(ConcurrentDictionary<SessionId, SyntheticJob> jobs, Action? beforeStart = null) => new(
        "StartSyntheticJob",
        execute: (context, _) =>
        {
            beforeStart?.Invoke();
            var service = Assert.IsAssignableFrom<IBackgroundJobService>(context.BackgroundJobs);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var info = service.Start("synthetic job", Path.Combine(context.WorkspaceRoot, "job-output.txt"), async ct =>
            {
                try
                {
                    await complete.Task.WaitAsync(ct);
                    return new BackgroundJobResult(0);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            });
            jobs[context.SessionId] = new SyntheticJob(info, cancelled, complete);
            return ValueTask.FromResult(ToolExecutionResult.Text(info.Id.Value));
        });

    private static ScriptStep StartJob() => CallTool("StartSyntheticJob", new { });

    private static ScriptStep CallTool(string name, object input) => new ScriptStep.Events(
        new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), name, JsonSerializer.SerializeToElement(input))),
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static SubagentTaskRegistry Registry(AgentRuntime runtime) =>
        Assert.IsType<SubagentTaskRegistry>(Assert.Single(runtime.Options.Tools.OfType<SubagentTool>()).Tasks);

    private static void RegisterTask(
        AgentRuntime runtime, SessionId parent, SessionId child, SubagentId id, DateTimeOffset startedAt) =>
        Registry(runtime).Register(new SubagentTaskInfo(
            id, child, "general-purpose", "worker", "work", null, SubagentStatus.Running,
            null, null, null, startedAt, null) { ParentSessionId = parent }, _ => ValueTask.CompletedTask);

    private static void CompleteTask(AgentRuntime runtime, SubagentId id) =>
        Registry(runtime).Update(id, task => task with { Status = SubagentStatus.Completed, EndedAt = DateTimeOffset.UtcNow });

    private sealed record SyntheticJob(BackgroundJobInfo Info, TaskCompletionSource Cancelled, TaskCompletionSource Complete);

    private sealed class LegacyActivityRuntime : ISessionActivityRuntime
    {
        public IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId) => [];
        public bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId) => false;
    }

    private sealed class CountingSessionStore(ISessionStore inner) : ISessionStore
    {
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public ValueTask UpsertSessionAsync(SessionRecord record, CancellationToken ct) => inner.UpsertSessionAsync(record, ct);
        public ValueTask<bool> TryUpdateSessionTitleAsync(SessionId id, string title, SessionTitleSource source, CancellationToken ct) =>
            inner.TryUpdateSessionTitleAsync(id, title, source, ct);
        public ValueTask DeleteSessionAsync(SessionId id, CancellationToken ct) => inner.DeleteSessionAsync(id, ct);
        public ValueTask AppendMessagesAsync(SessionId id, IReadOnlyList<ConversationMessage> messages, long startingSequence, CancellationToken ct) =>
            inner.AppendMessagesAsync(id, messages, startingSequence, ct);
        public ValueTask ReplaceMessagesAsync(SessionId id, IReadOnlyList<ConversationMessage> messages, CancellationToken ct) =>
            inner.ReplaceMessagesAsync(id, messages, ct);
        public ValueTask AppendDurableEventsAsync(IReadOnlyList<SequencedEvent> events, CancellationToken ct) =>
            inner.AppendDurableEventsAsync(events, ct);

        public ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCount);
            return inner.GetSessionAsync(id, ct);
        }

        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCount);
            return inner.ListSessionsAsync(workspaceRoot, limit, ct);
        }

        public ValueTask<IReadOnlyList<StoredMessage>> LoadMessagesAsync(SessionId id, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCount);
            return inner.LoadMessagesAsync(id, ct);
        }

        public ValueTask<IReadOnlyList<SequencedEvent>> LoadDurableEventsAsync(SessionId id, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCount);
            return inner.LoadDurableEventsAsync(id, ct);
        }

        public ValueTask<long> GetNextMessageSequenceAsync(SessionId id, CancellationToken ct)
        {
            Interlocked.Increment(ref _readCount);
            return inner.GetNextMessageSequenceAsync(id, ct);
        }
    }
}
