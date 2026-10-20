using System.Diagnostics;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tools;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Models;
using Xunit;
using Xunit.Abstractions;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class MemorySpikeProbe
{
    private readonly ITestOutputHelper _output;

    public MemorySpikeProbe(ITestOutputHelper output) => _output = output;

    private static ScriptStep FinalText(string text) => new ScriptStep.Events(
        new Alcoler.Contracts.Models.ModelTextDelta(text),
        new Alcoler.Contracts.Models.ModelStreamFinished(Alcoler.Contracts.Models.ModelFinishReason.Stop));

    private static string Measure(string label)
    {
        var managed = GC.GetTotalMemory(forceFullCollection: true) / (1024.0 * 1024.0);
        var workingSet = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
        return $"{label}: managed={managed:F1} MB, workingSet={workingSet:F1} MB";
    }

    [Fact]
    public async Task LongSession_MemoryStaysBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "alcoler-mem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var steps = Enumerable.Range(0, 900)
                .Select(i => FinalText($"Answer number {i} with some filler text to approximate a real assistant reply of typical length. " +
                                       "It mentions code, files and reasoning so the string sizes resemble production."))
                .ToArray();
            var model = new SequencedChatModel(steps, new ModelDescriptor("scripted", "test-model", "Scripted", new ModelProperties(200_000)));

            var store = new SqliteSessionStore(Path.Combine(root, "db.sqlite"));
            await store.InitializeAsync(CancellationToken.None);
            var artifacts = new FileArtifactStore(Path.Combine(root, "artifacts"));
            AgentRuntime? runtime = null;
            var todoStore = new InMemoryTodoStore();
            var tools = new List<ITool>
            {
                new ReadTool(), new WriteTool(), new EditTool(), new GlobTool(), new GrepTool(), new BashTool(),
                new TodoWriteTool(todoStore), new TodoReadTool(todoStore), new ExitPlanModeTool(),
                SubagentWiring.CreateTool(() => runtime!),
            };
            runtime = new AgentRuntime(new AgentRuntimeOptions
            {
                Store = store,
                Artifacts = artifacts,
                Providers = [new ScriptedModelProvider(model)],
                Tools = tools,
                WorkspaceFactory = r => new WorkspaceToolEnvironment(r, artifacts),
                DefaultModel = new ModelSelection("scripted", "test-model"),
            });

            _output.WriteLine(Measure("baseline (runtime only)"));

            var session = await runtime.CreateSessionAsync(
                new Contracts.Sessions.SessionOptions(root, PermissionMode: PermissionMode.Edit), CancellationToken.None);
            using var projection = runtime.Subscribe(session.Id);
            var revision = projection.Snapshot().Revision;

            for (var i = 0; i < 400; i++)
            {
                var ack = await runtime.SubmitAsync(
                    new SendTextCommand(StrongId.NewCommandId(), session.Id, revision, $"question {i}"),
                    CancellationToken.None);
                if (!ack.Accepted)
                {
                    throw new InvalidOperationException($"rejected at {i}: {ack.RejectionReason}");
                }
                revision = ack.Revision;
                await WaitForIdleAsync(projection);
            }

            _output.WriteLine(Measure("after 400-turn session (events resident + projection live)"));

            var rows = projection.Snapshot().Rows.Count;
            _output.WriteLine($"timeline rows: {rows}");

            for (var s = 0; s < 9; s++)
            {
                var other = await runtime.CreateSessionAsync(
                    new Contracts.Sessions.SessionOptions(root, PermissionMode: PermissionMode.Edit), CancellationToken.None);
                using var p2 = runtime.Subscribe(other.Id);
                var rev = p2.Snapshot().Revision;
                for (var i = 0; i < 50; i++)
                {
                    var ack = await runtime.SubmitAsync(
                        new SendTextCommand(StrongId.NewCommandId(), other.Id, rev, $"q {i}"), CancellationToken.None);
                    rev = ack.Revision;
                    await WaitForIdleAsync(p2);
                }
            }

            _output.WriteLine(Measure("after 10 resident sessions (400 + 9x50 turns)"));
            await runtime.DisposeAsync();
            _output.WriteLine(Measure("after runtime dispose"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task WaitForIdleAsync(IConversationProjection projection)
    {
        var deadline = Task.Delay(TimeSpan.FromMinutes(2));
        while (projection.Snapshot().State.Phase is not TurnPhase.Idle)
        {
            if (deadline.IsCompleted
                || projection.Snapshot().State.Phase is TurnPhase.Error)
            {
                throw new TimeoutException(
                    $"turn never reached idle (phase: {projection.Snapshot().State.Phase}).");
            }
            await Task.Delay(5);
        }
    }
}
