using System.Collections.Concurrent;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tools;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Tests;

public sealed class SidebarActivityFlowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("alcoler-sidebar-activity-").FullName;

    private string DatabasePath => Path.Combine(_root, "sessions.sqlite");

    [Fact]
    public async Task RunningCompletionAndFailure_ReachTheSidebarOfNonActiveSessions()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store, [new GateTool(entered, release)],
            ToolCall("Gate", new { }),
            Reply("gate finished"),
            new ScriptStep.Fail(new ModelFailure(ModelFailureKind.AuthenticationFailed, "bad key")));

        var watched = await runtime.CreateSessionAsync(new SessionOptions(_root, "Watched"), CancellationToken.None);
        var active = await runtime.CreateSessionAsync(new SessionOptions(_root, "Active"), CancellationToken.None);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(active.Id);
        scheduler.RunPending();
        Assert.Equal(active.Id, workbench.Sidebar.ActiveSessionId);
        var watchedVm = FindSession(workbench, watched.Id);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), watched.Id, 0, "run the gate"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => watchedVm.IsRunning, scheduler);
        Assert.False(watchedVm.ShowActiveDot);

        release.TrySetResult();
        await WaitUntilAsync(() => watchedVm.IsCompleted, scheduler);
        Assert.False(watchedVm.IsRunning);

        ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), watched.Id, 0, "fail"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitUntilAsync(() => watchedVm.IsFailed, scheduler);
        Assert.False(watchedVm.IsCompleted);
    }

    [Fact]
    public async Task AwaitingPermission_ReachesTheSidebarOfNonActiveSessions()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store, [new WriteTool()],
            ToolCall("Write", new { file_path = "notes.txt", content = "Hello Alcoler" }),
            Reply("written"));

        var watched = await runtime.CreateSessionAsync(
            new SessionOptions(_root, "Watched", PermissionMode: PermissionMode.Build), CancellationToken.None);
        var active = await runtime.CreateSessionAsync(new SessionOptions(_root, "Active"), CancellationToken.None);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(active.Id);
        scheduler.RunPending();
        var watchedVm = FindSession(workbench, watched.Id);
        using var projection = runtime.Subscribe(watched.Id);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), watched.Id, 0, "write notes"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitUntilAsync(() => projection.Snapshot().State.PendingPermission is not null, scheduler);
        await WaitUntilAsync(() => watchedVm.IsAwaitingConfirmation, scheduler);
        Assert.False(watchedVm.IsRunning);
        Assert.False(watchedVm.ShowActiveDot);

        var permission = projection.Snapshot().State.PendingPermission!;
        var resolve = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), watched.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        Assert.True(resolve.Accepted, resolve.RejectionReason);

        await WaitUntilAsync(() => watchedVm.IsCompleted, scheduler);
        Assert.False(watchedVm.IsAwaitingConfirmation);
    }

    [Fact]
    public async Task ExitPlanModeApproval_ClearsAwaitingConfirmationOfNonActiveSession()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store, [new ExitPlanModeTool()],
            ToolCall("ExitPlanMode", new { plan = "1. Implement the change." }),
            Reply("implemented"));

        var watched = await runtime.CreateSessionAsync(
            new SessionOptions(_root, "Watched", PermissionMode: PermissionMode.Yolo, PlanEnabled: true),
            CancellationToken.None);
        var active = await runtime.CreateSessionAsync(new SessionOptions(_root, "Active"), CancellationToken.None);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(active.Id);
        scheduler.RunPending();
        var watchedVm = FindSession(workbench, watched.Id);
        using var projection = runtime.Subscribe(watched.Id);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), watched.Id, 0, "present the plan"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitUntilAsync(() => projection.Snapshot().State.PendingPermission is not null, scheduler);
        await WaitUntilAsync(() => watchedVm.IsAwaitingConfirmation, scheduler);

        var permission = projection.Snapshot().State.PendingPermission!;
        var resolve = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), watched.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        Assert.True(resolve.Accepted, resolve.RejectionReason);

        await WaitUntilAsync(() => !watchedVm.IsAwaitingConfirmation, scheduler);
        await WaitUntilAsync(() => watchedVm.IsCompleted, scheduler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AskUserQuestionAnswerAndDismiss_ClearAwaitingConfirmation(bool dismiss)
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store, [new AskUserQuestionTool()],
            ToolCall("AskUserQuestion", new
            {
                questions = new[]
                {
                    new
                    {
                        question = "Which target should be used?",
                        header = "Target",
                        options = new[]
                        {
                            new { label = "main", description = "Main branch" },
                            new { label = "branch", description = "Feature branch" },
                        },
                        multiSelect = false,
                    },
                },
            }),
            Reply("answered"));

        var watched = await runtime.CreateSessionAsync(
            new SessionOptions(_root, "Watched", PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        var active = await runtime.CreateSessionAsync(new SessionOptions(_root, "Active"), CancellationToken.None);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(active.Id);
        scheduler.RunPending();
        var watchedVm = FindSession(workbench, watched.Id);
        using var projection = runtime.Subscribe(watched.Id);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), watched.Id, 0, "ask me"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitUntilAsync(() => projection.Snapshot().State.PendingPermission is not null, scheduler);
        var permission = projection.Snapshot().State.PendingPermission!;
        var permissionResolve = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), watched.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        Assert.True(permissionResolve.Accepted, permissionResolve.RejectionReason);

        await WaitUntilAsync(() => projection.Snapshot().State.PendingInteraction is not null, scheduler);
        await WaitUntilAsync(() => watchedVm.IsAwaitingConfirmation, scheduler);
        var interaction = projection.Snapshot().State.PendingInteraction!;
        var answer = dismiss
            ? new InteractionAnswer(new Dictionary<string, string>())
            : new InteractionAnswer(new Dictionary<string, string> { ["Target"] = "main" });
        var resolve = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), watched.Id, projection.Snapshot().Revision, interaction.InteractionId,
            InteractionAnswer: answer), CancellationToken.None);
        Assert.True(resolve.Accepted, resolve.RejectionReason);

        await WaitUntilAsync(() => !watchedVm.IsAwaitingConfirmation, scheduler);
        await WaitUntilAsync(() => watchedVm.IsCompleted, scheduler);
    }

    [Fact]
    public async Task PermissionTimeout_ClearsAwaitingConfirmationOfNonActiveSession()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store, [new WriteTool()], TimeSpan.FromSeconds(1),
            ToolCall("Write", new { file_path = "notes.txt", content = "Hello Alcoler" }),
            Reply("written"));

        var watched = await runtime.CreateSessionAsync(
            new SessionOptions(_root, "Watched", PermissionMode: PermissionMode.Build), CancellationToken.None);
        var active = await runtime.CreateSessionAsync(new SessionOptions(_root, "Active"), CancellationToken.None);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(active.Id);
        scheduler.RunPending();
        var watchedVm = FindSession(workbench, watched.Id);
        using var projection = runtime.Subscribe(watched.Id);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), watched.Id, 0, "write notes"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitUntilAsync(() => projection.Snapshot().State.PendingPermission is not null, scheduler);
        await WaitUntilAsync(() => watchedVm.IsAwaitingConfirmation, scheduler);

        await WaitUntilAsync(() => !watchedVm.IsAwaitingConfirmation, scheduler);
    }

    private static SessionSummaryViewModel FindSession(WorkbenchViewModel workbench, SessionId id) =>
        workbench.Sidebar.Groups.SelectMany(group => group.Sessions).Single(session => session.Id == id);

    private static async Task WaitUntilAsync(Func<bool> condition, QueuedUiScheduler scheduler)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            scheduler.RunPending();
            if (condition())
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, "Sidebar activity did not arrive within 10 seconds.");
            await Task.Delay(10);
        }
    }

    private AgentRuntime CreateRuntime(SqliteSessionStore store, IReadOnlyList<ITool> extraTools, params ScriptStep[] script) =>
        CreateRuntime(store, extraTools, null, script);

    private AgentRuntime CreateRuntime(
        SqliteSessionStore store, IReadOnlyList<ITool> extraTools, TimeSpan? interactionTimeout, params ScriptStep[] script)
    {
        var artifacts = new FileArtifactStore(Path.Combine(_root, "artifacts"));
        return new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(new SequencedChatModel(script))],
            Tools = extraTools,
            WorkspaceFactory = root => new WorkspaceToolEnvironment(root, artifacts),
            DefaultModel = new ModelSelection("scripted", "scripted-model"),
            InteractionTimeout = interactionTimeout ?? TimeSpan.FromMinutes(10),
        });
    }

    private static ScriptStep ToolCall(string name, object input) => new ScriptStep.Events(
        new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), name, JsonSerializer.SerializeToElement(input))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls));

    private static ScriptStep Reply(string text) => new ScriptStep.Events(
        new ModelTextDelta(text), new ModelStreamFinished(ModelFinishReason.Stop));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class QueuedUiScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> _pending = new();

        public void Post(Action action) => _pending.Enqueue(action);

        public void RunPending()
        {
            while (_pending.TryDequeue(out var action))
            {
                action();
            }
        }
    }

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
