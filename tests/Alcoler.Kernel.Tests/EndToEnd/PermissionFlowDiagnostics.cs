using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class PermissionFlowDiagnostics(ITestOutputHelper output)
{
    [Fact]
    public async Task Trace_PermissionFlow_EventSequence()
    {
        await using var harness = TestHarness.Create(
            new ScriptStep.Events(
                new Contracts.Models.ModelTextDelta("Working on it."),
                new Contracts.Models.ModelToolInputStarted(new ToolCallId("call-1"), "Write"),
                new Contracts.Models.ModelToolInputDelta(new ToolCallId("call-1"), JsonSerializer.Serialize(new { file_path = "notes.txt", content = "Hello" })),
                new Contracts.Models.ModelToolInputEnded(new ToolCallId("call-1")),
                new Contracts.Models.ModelToolCallReady(new Contracts.Models.ToolCall(new ToolCallId("call-1"), "Write", JsonSerializer.SerializeToElement(new { file_path = "notes.txt", content = "Hello" }))),
                new Contracts.Models.ModelStreamFinished(Contracts.Models.ModelFinishReason.ToolCalls)),
            new ScriptStep.Events(
                new Contracts.Models.ModelTextDelta("Done."),
                new Contracts.Models.ModelStreamFinished(Contracts.Models.ModelFinishReason.Stop)));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Build),
            CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        var trace = new List<string>();
        var pump = Task.Run(async () =>
        {
            await foreach (var delta in projection.Deltas(CancellationToken.None))
            {
                lock (trace)
                {
                    trace.Add($"seq={delta.Sequence} ops=[{string.Join(", ", delta.Ops.Select(o => OpSummary(o)))}]");
                }
            }
        });

        var submitAck = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "create the file"), CancellationToken.None);
        output.WriteLine($"submit ack: accepted={submitAck.Accepted} revision={submitAck.Revision}");

        PermissionRequest? permission = null;
        for (var i = 0; i < 500 && permission is null; i++)
        {
            permission = projection.Snapshot().State.PendingPermission;
            await Task.Delay(20);
        }
        output.WriteLine($"permission seen: {permission is not null} id={permission?.InteractionId.Value}");
        Assert.NotNull(permission);

        var resolveAck = await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, submitAck.Revision, permission!.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        output.WriteLine($"resolve ack: accepted={resolveAck.Accepted} reason={resolveAck.RejectionReason}");

        TurnPhase lastPhase = TurnPhase.Idle;
        for (var i = 0; i < 500; i++)
        {
            lastPhase = projection.Snapshot().State.Phase;
            if (lastPhase == TurnPhase.Idle)
            {
                break;
            }
            await Task.Delay(20);
        }

        lock (trace)
        {
            foreach (var line in trace)
            {
                output.WriteLine(line);
            }
        }
        output.WriteLine($"final phase: {lastPhase}");
        Assert.Equal(TurnPhase.Idle, lastPhase);
        output.WriteLine($"file exists: {File.Exists(Path.Combine(harness.Root, "notes.txt"))}");
    }

    private static string OpSummary(RowOp op) => op switch
    {
        RowAppended a => $"append:{a.Row.GetType().Name}",
        RowUpserted u => $"upsert:{u.Row.GetType().Name}",
        RowRemoved r => $"remove:{r.RowId}",
        StatePatched s => $"state:{s.State.Phase}",
        _ => op.GetType().Name,
    };
}
