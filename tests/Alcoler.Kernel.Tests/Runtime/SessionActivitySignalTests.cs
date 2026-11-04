using System.Collections.Concurrent;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tests.EndToEnd;
using Alcoler.Kernel.Tests.Models;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionActivitySignalTests
{
    [Fact]
    public async Task ActivitySnapshot_TracksBusyAndTerminalOutcomes_AndRaisesChangeNotifications()
    {
        await using var harness = TestHarness.Create(
            ToolCall("Gate", new { }),
            Reply(),
            new ScriptStep.Fail(new ModelFailure(ModelFailureKind.AuthenticationFailed, "bad key")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);
        var unknown = StrongId.NewSessionId();
        Assert.Null(activity.GetSessionActivity(unknown));

        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        var notifications = new ConcurrentQueue<SessionId>();
        activity.SessionActivityChanged += notifications.Enqueue;
        var initial = Assert.IsType<SessionActivitySnapshot>(activity.GetSessionActivity(session.Id));
        Assert.Equal(TurnPhase.Idle, initial.Phase);
        Assert.Null(initial.LastOutcome);
        Assert.Empty(notifications);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "run the gate tool"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var running = Assert.IsType<SessionActivitySnapshot>(activity.GetSessionActivity(session.Id));
        Assert.Equal(TurnPhase.ExecutingTools, running.Phase);
        Assert.Null(running.LastOutcome);
        Assert.Contains(session.Id, notifications);
        Assert.Equal(session.Id, Assert.Single(notifications.Distinct()));

        release.TrySetResult();
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id) is
        { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Completed });
        await WaitUntilAsync(() => notifications.Count >= 2);
        var completed = Assert.IsType<SessionActivitySnapshot>(activity.GetSessionActivity(session.Id));
        Assert.NotNull(completed.CompletedTurnId);
        Assert.NotNull(completed.StartedAt);
        Assert.NotNull(completed.FinishedAt);
        Assert.Null(completed.ActiveWorkStartedAt);

        ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "fail"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id) is
        { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Failed });
        Assert.False(Assert.Single(activity.GetSessionActivities()).HasActiveWork);
        await WaitUntilAsync(() => notifications.Count >= 4);
        Assert.All(notifications, id => Assert.Equal(session.Id, id));
    }

    [Fact]
    public async Task BackgroundJobSignals_PublishRunningBeforeSynchronousCompletionAndBeforeDelivery()
    {
        var states = new List<BackgroundJobStatus>();
        var delivering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BackgroundJobService? service = null;
        service = new BackgroundJobService(TimeProvider.System, async _ =>
        {
            delivering.TrySetResult();
            await delivered.Task;
        }, () => states.Add(Assert.Single(service!.List()).Status));
        var started = service.Start("immediate", null, _ => Task.FromResult(new BackgroundJobResult(0)));
        await delivering.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { BackgroundJobStatus.Running, BackgroundJobStatus.Completed }, states);
        Assert.Equal(BackgroundJobStatus.Running, started.Status);
        Assert.Equal(BackgroundJobStatus.Completed, service.Find(started.Id)!.Status);
        delivered.TrySetResult();
    }

    [Fact]
    public async Task BackgroundJobSignals_StopStaysRunningUntilTheRunnerActuallyFinishes()
    {
        var states = new ConcurrentQueue<BackgroundJobStatus>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BackgroundJobService? service = null;
        service = new BackgroundJobService(TimeProvider.System, _ =>
        {
            completed.TrySetResult();
            return ValueTask.CompletedTask;
        }, () => states.Enqueue(Assert.Single(service!.List()).Status));
        var started = service.Start("cooperative", null, async ct =>
        {
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await finish.Task;
            ct.ThrowIfCancellationRequested();
            return new BackgroundJobResult(0);
        });
        Assert.True(service.Stop(started.Id));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BackgroundJobStatus.Running, service.Find(started.Id)!.Status);
        Assert.Equal(BackgroundJobStatus.Running, Assert.Single(states));
        finish.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { BackgroundJobStatus.Running, BackgroundJobStatus.Stopped }, states.ToArray());
    }

    [Fact]
    public async Task ActivitySignals_RegistrationRenameUnloadAndResumePublishValidResidentState()
    {
        await using var harness = TestHarness.Create();
        var clock = new ManualTimeProvider();
        await using var runtime = harness.BuildRuntime(options => options with { Clock = clock });
        var changes = new ConcurrentQueue<(SessionId Id, SessionActivitySnapshot? Activity)>();
        runtime.SessionActivityChanged += id => changes.Enqueue((id, runtime.GetSessionActivity(id)));
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var registered = Assert.Single(changes);
        Assert.Equal(session.Id, registered.Id);
        Assert.NotNull(registered.Activity);
        Assert.Equal(session.Title, registered.Activity.Title);
        Assert.False(registered.Activity.HasActiveWork);
        Assert.Null(registered.Activity.CompletedTurnId);

        clock.Advance(TimeSpan.FromMinutes(2));
        changes.Clear();
        Assert.True((await runtime.SubmitAsync(new RenameSessionCommand(
            StrongId.NewCommandId(), session.Id, 0, "new title"), CancellationToken.None)).Accepted);
        Assert.Contains(changes, change => change.Activity is { Title: "new title" } activity
            && activity.LastActivityUtc == clock.GetUtcNow());
        changes.Clear();
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await runtime.UnloadIdleSessionsAsync(TimeSpan.FromHours(1), null, CancellationToken.None));
        var unloaded = Assert.Single(changes);
        Assert.Equal(session.Id, unloaded.Id);
        Assert.Null(unloaded.Activity);
        Assert.Empty(runtime.GetSessionActivities());

        changes.Clear();
        await runtime.ResumeSessionAsync(session.Id, CancellationToken.None);
        var resumed = Assert.Single(changes);
        Assert.Equal(session.Id, resumed.Id);
        Assert.NotNull(resumed.Activity);
        Assert.Equal("new title", resumed.Activity.Title);
        Assert.False(resumed.Activity.HasActiveWork);
        Assert.Null(resumed.Activity.LastOutcome);
        Assert.Null(resumed.Activity.CompletedTurnId);
    }

    [Fact]
    public async Task SessionDeleted_IsRaisedOnlyForPersistentlyDeletedSessionsNeverUnload()
    {
        await using var harness = TestHarness.Create();
        var clock = new ManualTimeProvider();
        await using var runtime = harness.BuildRuntime(options => options with { Clock = clock });
        IAgentRuntime contract = runtime;
        var deleted = new ConcurrentQueue<SessionId>();
        contract.SessionDeleted += deleted.Enqueue;
        var first = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var retained = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(2, await runtime.UnloadIdleSessionsAsync(TimeSpan.FromHours(1), null, CancellationToken.None));
        Assert.Empty(deleted);
        await runtime.SetSessionArchivedAsync(first.Id, true, CancellationToken.None);
        Assert.Empty(deleted);
        Assert.Equal(1, await runtime.DeleteArchivedSessionsAsync(CancellationToken.None));
        Assert.Equal(first.Id, Assert.Single(deleted));
        Assert.Equal(0, await runtime.DeleteArchivedSessionsAsync(CancellationToken.None));
        Assert.Single(deleted);
        var sessions = await runtime.ListSessionsAsync(null, 10, CancellationToken.None);
        Assert.Equal(retained.Id, Assert.Single(sessions).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivitySignals_ChildWorkInvalidatesTerminalRootWithoutReplacingItsCompletion(bool separateRuntime)
    {
        await using var harness = TestHarness.Create(Reply(), ToolCall("Gate", new { }), Reply());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new GateTool(entered, release);
        await using var runtime = harness.BuildRuntime(gate);
        await using var external = harness.BuildRuntime(gate);
        var childOwner = separateRuntime ? external : runtime;
        var parent = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), parent.Id, 0, "finish parent"), CancellationToken.None)).Accepted);
        await WaitUntilAsync(() => runtime.GetSessionActivity(parent.Id) is { HasActiveWork: false, CompletedTurnId: not null });
        var completed = Assert.IsType<SessionActivitySnapshot>(runtime.GetSessionActivity(parent.Id));
        var changes = new ConcurrentQueue<SessionActivitySnapshot>();
        runtime.SessionActivityChanged += id =>
        {
            if (id == parent.Id && runtime.GetSessionActivity(id) is { } snapshot)
                changes.Enqueue(snapshot);
        };
        var child = await childOwner.CreateSessionAsync(
            new SessionOptions(harness.Root, ParentSessionId: parent.Id), CancellationToken.None);
        if (separateRuntime)
            runtime.RegisterSubagentRuntime(parent.Id, child.Id, childOwner);
        Assert.Contains(changes, snapshot => !snapshot.HasActiveWork);
        changes.Clear();
        Assert.True((await childOwner.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), child.Id, 0, "work"), CancellationToken.None)).Accepted);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(changes, snapshot => snapshot.HasActiveWork && snapshot.ActiveWorkStartedAt is not null);
        var working = Assert.Single(runtime.GetSessionActivities());
        Assert.True(working.HasActiveWork);
        Assert.Equal(TurnPhase.Idle, working.Phase);
        Assert.Equal(completed.CompletedTurnId, working.CompletedTurnId);
        Assert.Equal(completed.StartedAt, working.StartedAt);
        Assert.Equal(completed.FinishedAt, working.FinishedAt);
        Assert.Equal(TurnOutcome.Completed, working.LastOutcome);
        changes.Clear();
        release.TrySetResult();
        await WaitUntilAsync(() => changes.Any(snapshot => !snapshot.HasActiveWork));
        var idle = Assert.IsType<SessionActivitySnapshot>(runtime.GetSessionActivity(parent.Id));
        Assert.False(idle.HasActiveWork);
        Assert.Null(idle.ActiveWorkStartedAt);
        Assert.Equal(completed.CompletedTurnId, idle.CompletedTurnId);
        Assert.Equal(completed.StartedAt, idle.StartedAt);
        Assert.Equal(completed.FinishedAt, idle.FinishedAt);
    }

    [Fact]
    public async Task ActivitySnapshot_ReportsAwaitingPermissionUntilResolved()
    {
        await using var harness = TestHarness.Create(
            ToolCall("Write", new { file_path = "notes.txt", content = "Hello Alcoler" }),
            Reply());
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Build), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);
        var notifications = new ConcurrentQueue<SessionId>();
        activity.SessionActivityChanged += notifications.Enqueue;

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "write notes"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);

        var permission = await WaitForPermissionAsync(projection);
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase == TurnPhase.AwaitingPermission);
        var task = Assert.Single(activity.GetSessionActivities());
        Assert.Equal(session.Id, task.SessionId);
        Assert.Equal(TurnPhase.AwaitingPermission, task.Phase);
        Assert.True(task.IsAwaitingConfirmation);
        Assert.NotNull(task.ActiveWorkStartedAt);
        Assert.Contains(session.Id, notifications);

        var resolve = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        Assert.True(resolve.Accepted, resolve.RejectionReason);

        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase
            is not (TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction));
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id) is
        { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Completed });
        Assert.False(Assert.Single(activity.GetSessionActivities()).HasActiveWork);
    }

    [Fact]
    public async Task ActivitySnapshot_ReportsAwaitingInteractionUntilAnswered()
    {
        await using var harness = TestHarness.Create(
            ToolCall("AskUserQuestion", new
            {
                questions = new[]
                {
                    new
                    {
                        question = "Which library should we use?",
                        header = "Library",
                        options = new[]
                        {
                            new { label = "NodaTime", description = "Most precise" },
                            new { label = "DateTime", description = "Built in" },
                        },
                    },
                },
            }),
            Reply());
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "ask me"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);

        var interaction = await WaitForInteractionAsync(projection);
        Assert.Null(projection.Snapshot().State.PendingPermission);
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase == TurnPhase.AwaitingInteraction);
        var task = Assert.Single(activity.GetSessionActivities());
        Assert.Equal(session.Id, task.SessionId);
        Assert.Equal(TurnPhase.AwaitingInteraction, task.Phase);
        Assert.True(task.IsAwaitingConfirmation);
        Assert.NotNull(task.ActiveWorkStartedAt);

        var resolve = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, interaction.InteractionId,
            InteractionAnswer: new InteractionAnswer(
                new Dictionary<string, string> { ["Library"] = "NodaTime" })), CancellationToken.None);
        Assert.True(resolve.Accepted, resolve.RejectionReason);

        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase
            is not (TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction));
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id) is
        { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Completed });
        Assert.False(Assert.Single(activity.GetSessionActivities()).HasActiveWork);
    }

    [Fact]
    public async Task ExitPlanModeApproval_ClearsAwaitingPermissionAndPlanEnabled()
    {
        await using var harness = TestHarness.Create(
            ToolCall("ExitPlanMode", new { plan = "1. Do the work." }),
            Reply());
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo, PlanEnabled: true),
            CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "present the plan"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);

        var permission = await WaitForPermissionAsync(projection);
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase == TurnPhase.AwaitingPermission);

        var resolve = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        Assert.True(resolve.Accepted, resolve.RejectionReason);

        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase
            is not (TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction));
        await WaitUntilAsync(() => projection.Snapshot().State.PlanEnabled is false);
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id) is
        { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Completed });
    }

    [Fact]
    public async Task PermissionTimeout_ClearsPendingStateAndLeavesAwaitingPermission()
    {
        await using var harness = TestHarness.Create(
            ToolCall("Write", new { file_path = "notes.txt", content = "Hello Alcoler" }),
            Reply());
        var clock = new ManualTimeProvider();
        await using var runtime = harness.BuildRuntime(
            options => options with { Clock = clock, InteractionTimeout = TimeSpan.FromSeconds(1) });
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Build), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "write notes"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);

        await WaitForPermissionAsync(projection);
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase == TurnPhase.AwaitingPermission);

        // Pending state is visible before the broker registers its timeout timer.
        await WaitUntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return projection.Snapshot().State.PendingPermission is null
                && activity.GetSessionActivity(session.Id)?.Phase
                    is not (TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction);
        });
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id) is
        { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Completed });

        await using var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        var resolved = Assert.Single(events.Select(sequenced => sequenced.Event).OfType<PermissionResolvedEvent>());
        Assert.Equal(PermissionOutcome.Denied, resolved.Decision.Outcome);
    }

    [Fact]
    public async Task InteractionTimeout_ClearsPendingStateAndLeavesAwaitingInteraction()
    {
        await using var harness = TestHarness.Create(
            ToolCall("AskUserQuestion", new
            {
                questions = new[]
                {
                    new
                    {
                        question = "Which library should we use?",
                        header = "Library",
                        options = new[]
                        {
                            new { label = "NodaTime", description = "Most precise" },
                            new { label = "DateTime", description = "Built in" },
                        },
                    },
                },
            }),
            Reply());
        var clock = new ManualTimeProvider();
        await using var runtime = harness.BuildRuntime(
            options => options with { Clock = clock, InteractionTimeout = TimeSpan.FromSeconds(2) });
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var activity = Assert.IsAssignableFrom<ISessionActivityRuntime>(runtime);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "ask me"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);

        await WaitForInteractionAsync(projection);
        Assert.Null(projection.Snapshot().State.PendingPermission);
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id)?.Phase == TurnPhase.AwaitingInteraction);

        // Pending state is visible before the broker registers its timeout timer.
        await WaitUntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return projection.Snapshot().State.PendingInteraction is null
                && activity.GetSessionActivity(session.Id)?.Phase
                    is not (TurnPhase.AwaitingPermission or TurnPhase.AwaitingInteraction);
        });
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id) is
        { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Completed });

        await using var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        var resolved = Assert.Single(events.Select(sequenced => sequenced.Event).OfType<InteractionResolvedEvent>());
        Assert.Empty(resolved.Answer.Answers);
    }

    private static async Task<PermissionRequest> WaitForPermissionAsync(IConversationProjection projection)
    {
        await WaitUntilAsync(() => projection.Snapshot().State.PendingPermission is not null);
        return projection.Snapshot().State.PendingPermission!;
    }

    private static async Task<InteractionRequest> WaitForInteractionAsync(IConversationProjection projection)
    {
        await WaitUntilAsync(() => projection.Snapshot().State.PendingInteraction is not null);
        return projection.Snapshot().State.PendingInteraction!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ScriptStep ToolCall(string name, object input) => new ScriptStep.Events(
        new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), name, JsonSerializer.SerializeToElement(input))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls));

    private static ScriptStep Reply() => new ScriptStep.Events(
        new ModelTextDelta("done"), new ModelStreamFinished(ModelFinishReason.Stop));

    private sealed class GateTool(TaskCompletionSource entered, TaskCompletionSource release) : ITool
    {
        public ToolContract Contract { get; } = new(
            "Gate",
            "Test-only tool that blocks until released.",
            JsonDocument.Parse("""{"type": "object", "properties": {}, "additionalProperties": false}""").RootElement.Clone());

        public ToolMetadata Metadata { get; } = new(
            ReadOnly: true,
            Destructive: false,
            ConcurrentSafe: false,
            RequiresUserInteraction: false,
            SideEffectScope: SideEffectScope.None,
            RiskLevel: RiskLevel.Low,
            NeedsApproval: false,
            AllowedInPlanMode: true);

        public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ToolInputIssue?>(null);

        public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult(input);

        public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ToolExecutionResult.Text("gate opened");
        }
    }
}
