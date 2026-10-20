using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class PlanModeEndToEndTests
{
    private static ScriptStep ToolCall(string name, object input) => new ScriptStep.Events(
        new Alcoler.Contracts.Models.ModelTextDelta("Presenting the plan."),
        new Alcoler.Contracts.Models.ModelToolInputStarted(new ToolCallId("call-1"), name),
        new Alcoler.Contracts.Models.ModelToolInputDelta(
            new ToolCallId("call-1"),
            JsonSerializer.Serialize(input)),
        new Alcoler.Contracts.Models.ModelToolInputEnded(new ToolCallId("call-1")),
        new Alcoler.Contracts.Models.ModelToolCallReady(new Alcoler.Contracts.Models.ToolCall(
            new ToolCallId("call-1"),
            name,
            JsonSerializer.SerializeToElement(input))),
        new Alcoler.Contracts.Models.ModelStreamFinished(Alcoler.Contracts.Models.ModelFinishReason.ToolCalls));

    private static ScriptStep FinalText(params string[] texts) => new ScriptStep.Events(
        [.. texts.Select(t => new Alcoler.Contracts.Models.ModelTextDelta(t)),
        new Alcoler.Contracts.Models.ModelStreamFinished(Alcoler.Contracts.Models.ModelFinishReason.Stop)]);

    [Fact]
    public async Task PlanApproval_SwitchesModeToEdit()
    {
        await using var harness = TestHarness.Create(
            ToolCall("ExitPlanMode", new { plan = "1. Write the file.\n2. Verify." }),
            FinalText("Implementing now."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Plan),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Plan writing notes.txt"),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        var permission = await WaitForPermissionAsync(projection);
        Assert.Equal("ExitPlanMode", permission.ToolName);
        var resolveAck = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        Assert.True(resolveAck.Accepted);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        Assert.Equal(PermissionMode.Edit, snapshot.State.Mode);
        Assert.Contains(snapshot.Rows, r => r is ToolCallRow t
            && t.ToolName == "ExitPlanMode"
            && t.Status == ToolCallStatus.Succeeded
            && t.Output!.OfType<Contracts.Conversation.ToolResultText>().Any(b => b.Text.Contains("User has approved your plan")));
        Assert.Contains(snapshot.Rows, r => r is AssistantMessageRow a
            && a.Parts.OfType<Contracts.Conversation.TextPart>().Any(p => p.Text.Contains("Implementing now.")));
    }

    [Fact]
    public async Task PlanDenial_KeepsPlanMode_ToolNeverRuns()
    {
        await using var harness = TestHarness.Create(
            ToolCall("ExitPlanMode", new { plan = "1. Write the file." }),
            FinalText("Staying in plan mode."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Plan),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "plan something"),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        var permission = await WaitForPermissionAsync(projection);
        await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Deny), CancellationToken.None);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        Assert.Equal(PermissionMode.Plan, snapshot.State.Mode);
        Assert.Contains(snapshot.Rows, r => r is ToolCallRow t
            && t.ToolName == "ExitPlanMode" && t.Status == ToolCallStatus.Failed);
    }

    private static async Task<PermissionRequest> WaitForPermissionAsync(IConversationProjection projection)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.PendingPermission is null)
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException("Permission request never arrived.");
            }
            await Task.Delay(20);
        }
        return projection.Snapshot().State.PendingPermission!;
    }

    private static async Task WaitForPhaseAsync(IConversationProjection projection, TurnPhase phase)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.Phase != phase)
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException($"Phase never reached {phase} (current: {projection.Snapshot().State.Phase}).");
            }
            await Task.Delay(20);
        }
    }
}
