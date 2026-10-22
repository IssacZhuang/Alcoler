using System.Collections.Concurrent;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
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

    private AgentRuntime CreateRuntime(SqliteSessionStore store, IReadOnlyList<ITool> extraTools, params ScriptStep[] script)
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
