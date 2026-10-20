using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class SubagentEndToEndTests
{
    private const string ChildAnswer = "The workspace contains a single README.";

    private static ScriptStep ToolCall(string name, object input) => new ScriptStep.Events(
        new Alcoler.Contracts.Models.ModelTextDelta("Delegating."),
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
    public async Task ForegroundSubagent_RunsChildSession_MirrorsTimeline_ReturnsOutput()
    {
        // The scripted model is shared by parent and child runtimes, so script order is
        // parent Task call -> child final answer -> parent final answer.
        await using var harness = TestHarness.Create(
            ToolCall("Task", new { prompt = "Summarize the workspace.", description = "summarizer" }),
            FinalText(ChildAnswer),
            FinalText("All done."));

        File.WriteAllText(Path.Combine(harness.Root, "README.md"), "# workspace");

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Edit),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Delegate a summary"),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        var taskRow = snapshot.Rows.OfType<ToolCallRow>().Single(r => r.ToolName == "Task");
        Assert.Equal(ToolCallStatus.Succeeded, taskRow.Status);
        Assert.False(taskRow.IsError);
        Assert.Contains(taskRow.Output ?? [], b => b is ToolResultText t && t.Text.Contains(ChildAnswer));

        var notice = snapshot.Rows.OfType<SystemNoticeRow>().Single(r => r.Kind == "subagent");
        Assert.Contains("Completed", notice.Text);

        Assert.Contains(snapshot.Rows, r => r is AssistantMessageRow a
            && a.Parts.OfType<TextPart>().Any(p => p.Text.Contains("All done.")));

        // Both sessions are persisted; the child survives as a first-class session.
        await runtime.DisposeAsync();
        await using var reopened = harness.BuildRuntime();
        var sessions = await reopened.ListSessionsAsync(harness.Root, 10, CancellationToken.None);
        Assert.Equal(2, sessions.Count);

        var childId = sessions.Single(s => s.Id != session.Id).Id;
        await reopened.ResumeSessionAsync(childId, CancellationToken.None);
        using var childProjection = reopened.Subscribe(childId);
        var childSnapshot = childProjection.Snapshot();
        Assert.Contains(childSnapshot.Rows, r => r is UserMessageRow u && u.Text.Contains("Summarize the workspace."));
        Assert.Contains(childSnapshot.Rows, r => r is AssistantMessageRow a
            && a.Parts.OfType<TextPart>().Any(p => p.Text.Contains(ChildAnswer)));
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
