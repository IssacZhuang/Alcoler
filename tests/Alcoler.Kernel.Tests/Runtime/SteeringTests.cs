using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tests.EndToEnd;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SteeringTests
{
    // Holds the turn inside the tool-execution phase until the test has queued the steer,
    // so the mid-turn drain window is reached deterministically instead of by timing luck.
    private sealed class GateTool(TaskCompletionSource entered, TaskCompletionSource release) : ITool
    {
        public ToolContract Contract { get; } = new(
            "Gate",
            "Test-only tool that blocks until released.",
            ParseSchema("""{"type": "object", "properties": {}, "additionalProperties": false}"""));

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

        private static JsonElement ParseSchema(string rawJson) =>
            JsonDocument.Parse(rawJson).RootElement.Clone();
    }

    private static ScriptStep ToolCall(string name, object input) => new ScriptStep.Events(
        new ModelTextDelta("Working on it."),
        new ModelToolInputStarted(new ToolCallId("call-1"), name),
        new ModelToolInputDelta(new ToolCallId("call-1"), JsonSerializer.Serialize(input)),
        new ModelToolInputEnded(new ToolCallId("call-1")),
        new ModelToolCallReady(new ToolCall(
            new ToolCallId("call-1"),
            name,
            JsonSerializer.SerializeToElement(input))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls));

    private static ScriptStep FinalText(params string[] texts) => new ScriptStep.Events(
        [.. texts.Select(t => new ModelTextDelta(t)),
        new ModelStreamFinished(ModelFinishReason.Stop)]);

    [Fact]
    public async Task MidTurnUserInput_JoinsRunningTurn_BeforeFinalAnswer()
    {
        await using var harness = TestHarness.Create(
            ToolCall("Gate", new { }),
            FinalText("done after steer"));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo),
            CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        var submitTask = runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start"),
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var steerAck = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "change of plans"),
            CancellationToken.None);
        Assert.True(steerAck.Accepted);
        release.TrySetResult();

        await submitTask;
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        var steerTexts = snapshot.Rows
            .OfType<UserMessageRow>()
            .Select(static row => row.Text)
            .Where(static text => text.Contains("change of plans"))
            .ToList();
        Assert.Single(steerTexts);
        Assert.StartsWith("<system-reminder>", steerTexts[0]);
        Assert.Contains("The user sent a new message while you were working:", steerTexts[0]);

        // The scripted model's single final response proves the steer landed inside the same
        // turn instead of spawning a second one.
        var finalAnswers = snapshot.Rows
            .OfType<AssistantMessageRow>()
            .SelectMany(static row => row.Parts.OfType<TextPart>().Select(static part => part.Text))
            .Where(static text => text.Contains("done after steer"))
            .ToList();
        Assert.Single(finalAnswers);
    }

    private static async Task WaitForPhaseAsync(IConversationProjection projection, TurnPhase phase)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.Phase != phase)
        {
            if (deadline.IsCompleted)
                throw new TimeoutException($"Phase never reached {phase}.");
            await Task.Delay(20);
        }
    }
}
