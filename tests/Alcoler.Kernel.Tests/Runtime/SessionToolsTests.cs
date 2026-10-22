using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionToolsTests
{
    [Fact]
    public async Task SessionToolsProvider_SnapshotsToolsPerSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "alcoler-session-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteSessionStore(Path.Combine(root, "sessions.db"));
            await store.InitializeAsync(CancellationToken.None);
            var artifacts = new FileArtifactStore(Path.Combine(root, "artifacts"));
            var firstTool = new FakeTool("marker");
            var secondTool = new FakeTool("marker");
            var snapshots = new Queue<IReadOnlyList<ITool>>([
                new ITool[] { firstTool },
                new ITool[] { secondTool },
            ]);
            var calls = 0;
            var model = new SequencedChatModel([
                ToolCall("marker"),
                Reply(),
                ToolCall("marker"),
                Reply(),
            ]);
            await using var runtime = new AgentRuntime(new AgentRuntimeOptions
            {
                Store = store,
                Artifacts = artifacts,
                Providers = [new ScriptedModelProvider(model)],
                Tools = [],
                SessionToolsProvider = _ =>
                {
                    Interlocked.Increment(ref calls);
                    return ValueTask.FromResult(snapshots.Dequeue());
                },
                WorkspaceFactory = workspaceRoot => new WorkspaceToolEnvironment(workspaceRoot, artifacts),
                DefaultModel = new ModelSelection("scripted", "scripted-model"),
            });

            var firstSession = await runtime.CreateSessionAsync(new SessionOptions(root), CancellationToken.None);
            var secondSession = await runtime.CreateSessionAsync(new SessionOptions(root), CancellationToken.None);
            Assert.Equal(2, calls);

            using var firstProjection = runtime.Subscribe(firstSession.Id);
            await runtime.SubmitAsync(
                new SendTextCommand(StrongId.NewCommandId(), firstSession.Id, 0, "first"), CancellationToken.None);
            await WaitForTurn(firstProjection, expectedCompletedAssistantMessages: 1);
            Assert.Single(firstTool.ExecutedContexts);
            Assert.Empty(secondTool.ExecutedContexts);

            using var secondProjection = runtime.Subscribe(secondSession.Id);
            await runtime.SubmitAsync(
                new SendTextCommand(StrongId.NewCommandId(), secondSession.Id, 0, "second"), CancellationToken.None);
            await WaitForTurn(secondProjection, expectedCompletedAssistantMessages: 1);
            Assert.Single(firstTool.ExecutedContexts);
            Assert.Single(secondTool.ExecutedContexts);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static ScriptStep ToolCall(string name) => new ScriptStep.Events(
        new ModelTextDelta("Working on it."),
        new ModelToolInputStarted(new ToolCallId("call-1"), name),
        new ModelToolInputDelta(new ToolCallId("call-1"), "{}"),
        new ModelToolInputEnded(new ToolCallId("call-1")),
        new ModelToolCallReady(new ToolCall(
            new ToolCallId("call-1"),
            name,
            JsonSerializer.SerializeToElement(new { }))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls));

    private static ScriptStep Reply() => new ScriptStep.Events(
        new ModelTextDelta("done"),
        new ModelStreamFinished(ModelFinishReason.Stop));

    private static async Task WaitForTurn(IConversationProjection projection, int expectedCompletedAssistantMessages)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (projection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != TurnPhase.Idle ||
                snapshot.Rows.OfType<AssistantMessageRow>().Count(row => !row.IsStreaming) < expectedCompletedAssistantMessages))
            await Task.Delay(10, timeout.Token);
    }
}
