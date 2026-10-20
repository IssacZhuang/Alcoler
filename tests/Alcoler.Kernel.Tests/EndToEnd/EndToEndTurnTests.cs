using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class EndToEndTurnTests
{
    private static ScriptStep Text(params string[] texts) =>
        new ScriptStep.Events(texts.Select(t => new Alcoler.Contracts.Models.ModelTextDelta(t)).ToArray());

    private static ScriptStep ToolCall(string name, object input) => new ScriptStep.Events(
        new Alcoler.Contracts.Models.ModelTextDelta("Working on it."),
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
    public async Task FullTurn_WithToolCall_PermissionAsk_PersistsAndResumes()
    {
        await using var harness = TestHarness.Create(
            ToolCall("WriteFile", new { file_path = "notes.txt", content = "Hello Alcoler" }),
            FinalText("File written successfully."));

        File.WriteAllText(Path.Combine(harness.Root, "README.md"), "# workspace");

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Build),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var revision = projection.Snapshot().Revision;

        var submitTask = runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, revision, "Create notes.txt with Hello Alcoler"), CancellationToken.None);

        var permission = await WaitForPermissionAsync(projection);
        var ack = await submitTask;
        Assert.True(ack.Accepted);

        var resolveAck = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(),
            session.Id,
            ack.Revision,
            permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        Assert.True(resolveAck.Accepted);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        Assert.Contains(snapshot.Rows, r => r is UserMessageRow u && u.Text.Contains("Create notes.txt"));
        Assert.Contains(snapshot.Rows, r => r is AssistantMessageRow a && a.Parts.OfType<Contracts.Conversation.TextPart>().Any(p => p.Text.Contains("File written")));
        Assert.Contains(snapshot.Rows, r => r is ToolCallRow t && t.ToolName == "WriteFile" && t.Status == ToolCallStatus.Succeeded);
        Assert.True(File.Exists(Path.Combine(harness.Root, "notes.txt")));

        // Resume: new runtime over the same database, full timeline survives.
        await runtime.DisposeAsync();
        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var projection2 = resumed.Subscribe(session.Id);
        var snapshot2 = projection2.Snapshot();
        Assert.Contains(snapshot2.Rows, r => r is UserMessageRow u && u.Text.Contains("Create notes.txt"));
        Assert.Contains(snapshot2.Rows, r => r is ToolCallRow t && t.ToolName == "WriteFile" && t.Status == ToolCallStatus.Succeeded);
        Assert.Equal(TurnPhase.Idle, snapshot2.State.Phase);
    }

    [Fact]
    public async Task PermissionDenied_ProducesErrorToolResult_TurnStillCompletes()
    {
        await using var harness = TestHarness.Create(
            ToolCall("Bash", new { command = "echo nope" }),
            FinalText("Understood, command was denied."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Build),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var submitAck = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "run echo"), CancellationToken.None);
        Assert.True(submitAck.Accepted);

        var permission = await WaitForPermissionAsync(projection);
        await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, submitAck.Revision,
            permission.InteractionId, PermissionDecision: PermissionDecision.Deny), CancellationToken.None);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        var toolRow = snapshot.Rows.OfType<ToolCallRow>().Single(r => r.ToolName == "Bash");
        Assert.Equal(ToolCallStatus.Failed, toolRow.Status);
        Assert.True(toolRow.IsError);
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
