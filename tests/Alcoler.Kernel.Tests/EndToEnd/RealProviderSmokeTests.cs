using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tools;
using Alcoler.Contracts.Models;
using Xunit;
using Xunit.Abstractions;

namespace Alcoler.Kernel.Tests.EndToEnd;

/// <summary>
/// Live-provider smoke. Runs only when the environment supplies the endpoint
/// (ALCOLER_SMOKE_BASE_URL, ALCOLER_SMOKE_API_KEY, ALCOLER_SMOKE_MODEL), so no
/// credential ever lives in this repository.
/// </summary>
public class RealProviderSmokeTests
{
    private readonly ITestOutputHelper _output;

    public RealProviderSmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RealProvider_FullTurnWithToolCall_CreatesFile()
    {
        var baseUrl = Environment.GetEnvironmentVariable("ALCOLER_SMOKE_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("ALCOLER_SMOKE_API_KEY");
        var model = Environment.GetEnvironmentVariable("ALCOLER_SMOKE_MODEL");
        if (baseUrl is null || apiKey is null || model is null)
        {
            _output.WriteLine("smoke endpoint not configured; skipping");
            return;
    }

        var root = Path.Combine(Path.GetTempPath(), "alcoler-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "README.md"), "# smoke workspace");
            var store = new SqliteSessionStore(Path.Combine(root, "db.sqlite"));
            await store.InitializeAsync(CancellationToken.None);
            var artifacts = new FileArtifactStore(Path.Combine(root, "artifacts"));
            var workspaceFactory = (string r) => new WorkspaceToolEnvironment(r, artifacts);
            var runtime = new AgentRuntime(new AgentRuntimeOptions
            {
                Store = store,
                Artifacts = artifacts,
                Providers = [new OpenAiCompatibleModelProvider(
                    baseUrl,
                    apiKey,
                    modelProperties: new Dictionary<string, ModelProperties>
                    {
                        [model] = new(ContextWindowTokens: 1_048_576),
                    })],
                Tools = [.. DefaultToolSet.Create()],
                WorkspaceFactory = workspaceFactory,
                DefaultModel = new ModelSelection("openai-compatible", model),
            });
            await using (runtime.ConfigureAwait(false))
            {
                var session = await runtime.CreateSessionAsync(
                    new Contracts.Sessions.SessionOptions(root, PermissionMode: PermissionMode.Edit),
                    CancellationToken.None);
                using var projection = runtime.Subscribe(session.Id);
                var ack = await runtime.SubmitAsync(new SendTextCommand(
                    StrongId.NewCommandId(),
                    session.Id,
                    projection.Snapshot().Revision,
                    "Create a file named smoke.txt whose only content is the line deepseek-smoke-ok (use the WriteFile tool). After the tool call succeeds, reply with the single word DONE."),
                    CancellationToken.None);
                Assert.True(ack.Accepted, ack.RejectionReason);

                await WaitForTerminalAsync(projection);

                var snapshot = projection.Snapshot();
                var error = snapshot.Rows.OfType<SystemNoticeRow>().FirstOrDefault(r => r.Kind == "turn_error");
                Assert.True(error is null, $"turn failed: {error?.Text}");

                Assert.True(File.Exists(Path.Combine(root, "smoke.txt")), "smoke.txt was not created");
                Assert.Contains("deepseek-smoke-ok", File.ReadAllText(Path.Combine(root, "smoke.txt")));

                var writeRow = snapshot.Rows.OfType<ToolCallRow>()
                    .SingleOrDefault(r => r.ToolName == "WriteFile");
                Assert.NotNull(writeRow);
                Assert.Equal(ToolCallStatus.Succeeded, writeRow.Status);

                Assert.Contains(snapshot.Rows, r => r is AssistantMessageRow a
                    && a.Parts.OfType<Contracts.Conversation.TextPart>().Any(p => p.Text.Contains("DONE", StringComparison.OrdinalIgnoreCase)));

                _output.WriteLine($"context usage: {snapshot.State.ContextUsageTokens} tokens");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private async Task WaitForTerminalAsync(IConversationProjection projection)
    {
        var deadline = Task.Delay(TimeSpan.FromMinutes(3));
        while (projection.Snapshot().State.Phase is not (TurnPhase.Idle or TurnPhase.Error))
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException(
                    $"turn never finished (phase: {projection.Snapshot().State.Phase}).");
            }
            await Task.Delay(50);
        }
    }
}
