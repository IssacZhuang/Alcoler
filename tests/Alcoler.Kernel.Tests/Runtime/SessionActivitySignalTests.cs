using System.Text.Json;
using Alcoler.Adapters.Models;
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
        var notifications = new List<SessionId>();
        activity.SessionActivityChanged += id => notifications.Add(id);
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
        Assert.Equal(2, notifications.Count);

        ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "fail"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitUntilAsync(() => activity.GetSessionActivity(session.Id) is
            { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Failed });
        Assert.True(notifications.Count >= 4);
        Assert.All(notifications, id => Assert.Equal(session.Id, id));
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
