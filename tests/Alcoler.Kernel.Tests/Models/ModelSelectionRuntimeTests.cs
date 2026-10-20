using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public class ModelSelectionRuntimeTests
{
    [Fact]
    public async Task ProvidersWithSameKind_ResolveByIdentityAndEnforceSuppliedCatalog()
    {
        await using var fixture = await Fixture.Create();
        var first = new OpenAiCompatibleModelProvider("https://one/v1", "key", modelProperties: new Dictionary<string, ModelProperties> { ["same"] = new(DefaultMaxOutputTokens: 100) }, providerId: "first");
        var second = new OpenAiCompatibleModelProvider("https://two/v1", "key", modelProperties: new Dictionary<string, ModelProperties> { ["same"] = new(DefaultMaxOutputTokens: 200) }, providerId: "second");
        var a = await first.BindAsync(new ModelSelection("first", "same"), CancellationToken.None);
        var b = await second.BindAsync(new ModelSelection("second", "same"), CancellationToken.None);
        Assert.NotSame(a, b);
        Assert.Equal("first/same", a.Descriptor.FullId);
        Assert.Equal("second/same", b.Descriptor.FullId);
        Assert.Equal(100, a.Descriptor.Properties.DefaultMaxOutputTokens);
        Assert.Equal(200, b.Descriptor.Properties.DefaultMaxOutputTokens);
        await using var runtime = fixture.Runtime([first, second], new ModelSelection("first", "same"));
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        var switched = await runtime.SubmitAsync(new SwitchModelCommand(StrongId.NewCommandId(), session.Id, 0, new ModelSelection("second", "same")), CancellationToken.None);
        Assert.True(switched.Accepted);
        Assert.Equal(new ModelSelection("second", "same"), (await fixture.Store.GetSessionAsync(session.Id, CancellationToken.None))!.ModelSelection);
        using var projection = runtime.Subscribe(session.Id);
        Assert.Equal("second", projection.Snapshot().State.ModelSelection!.ProviderId);
        var invalid = await runtime.SubmitAsync(new SwitchModelCommand(StrongId.NewCommandId(), session.Id, 0, new ModelSelection("first", "missing")), CancellationToken.None);
        Assert.False(invalid.Accepted);
        var uncatalogued = new OpenAiCompatibleModelProvider("https://one/v1", "key");
        Assert.Equal("arbitrary", (await uncatalogued.BindAsync(new ModelSelection(uncatalogued.Id, "arbitrary"), CancellationToken.None)).Descriptor.ModelId);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai-compatible")]
    [InlineData("openai-responses")]
    public async Task AllCataloguedProviders_RejectUnknownModelIds(string kind)
    {
        var catalog = new Dictionary<string, ModelProperties> { ["allowed"] = new() };
        IModelProvider provider = kind switch
        {
            "anthropic" => new AnthropicModelProvider("key", modelProperties: catalog, providerId: "custom"),
            "openai-responses" => new OpenAiResponsesModelProvider("https://test/v1", "key", catalog, "custom"),
            _ => new OpenAiCompatibleModelProvider("https://test/v1", "key", modelProperties: catalog, providerId: "custom"),
        };
        Assert.Equal("custom", (await provider.BindAsync(new ModelSelection("custom", "allowed"), CancellationToken.None)).Descriptor.ProviderId);
        await Assert.ThrowsAsync<ArgumentException>(async () => await provider.BindAsync(new ModelSelection("custom", "missing"), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidSwitch_IsRejectedBeforePersistingSelection()
    {
        await using var fixture = await Fixture.Create();
        var original = new ModelSelection("first", "same", new("high", 500));
        var provider = new OpenAiCompatibleModelProvider("https://test/v1", "key", modelProperties: new Dictionary<string, ModelProperties>
        {
            ["same"] = new(ReasoningLevels: ["high"], DefaultMaxOutputTokens: 1000),
            ["bad-map"] = new(MaxOutputTokensMapping: "{'model':'bad'}"),
        }, providerId: "first");
        await using var runtime = fixture.Runtime([provider], original);
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        foreach (var invalid in new[]
        {
            new ModelSelection("missing", "same"), new ModelSelection("first", "missing"),
            new ModelSelection("first", "same", new("low")), new ModelSelection("first", "same", new(MaxOutputTokens: 0)),
            new ModelSelection("first", "bad-map"),
        })
        {
            var ack = await runtime.SubmitAsync(new SwitchModelCommand(StrongId.NewCommandId(), session.Id, 0, invalid), CancellationToken.None);
            Assert.False(ack.Accepted);
            Assert.False(string.IsNullOrWhiteSpace(ack.RejectionReason));
            Assert.Equal(original, (await fixture.Store.GetSessionAsync(session.Id, CancellationToken.None))!.ModelSelection);
        }
    }

    [Fact]
    public async Task CommandOverrideAndMidTurnSwitch_UseFrozenModelAndOptionsForEveryStep()
    {
        await using var fixture = await Fixture.Create();
        var defaultModel = new CapturingModel("default");
        var overrideModel = new CapturingModel("override", blockFirst: true);
        var nextModel = new CapturingModel("next");
        await using var runtime = fixture.Runtime([
            new ScriptedModelProvider(defaultModel, "default"), new ScriptedModelProvider(overrideModel, "override"), new ScriptedModelProvider(nextModel, "next")],
            new ModelSelection("default", "model", new("low", 111)));
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var turnOptions = new ModelCallOptions("high", 222);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "first", ModelSelection: new ModelSelection("override", "model", turnOptions)), CancellationToken.None);
        await overrideModel.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var switched = await runtime.SubmitAsync(new SwitchModelCommand(StrongId.NewCommandId(), session.Id, 0, new ModelSelection("next", "model", new("medium", 333))), CancellationToken.None);
        Assert.True(switched.Accepted);
        overrideModel.Release.TrySetResult();
        await WaitForTurn(projection, expectedCompletedAssistantMessages: 2);
        Assert.Empty(defaultModel.Requests);
        Assert.Empty(nextModel.Requests);
        Assert.Equal(2, overrideModel.Requests.Count);
        Assert.All(overrideModel.Requests, r => Assert.Equal(turnOptions, r.Options));
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "second"), CancellationToken.None);
        await WaitForTurn(projection, expectedCompletedAssistantMessages: 3);
        Assert.Equal(new ModelCallOptions("medium", 333), Assert.Single(nextModel.Requests).Options);
    }

    [Fact]
    public async Task MidTurnSwitch_DoesNotChangeReactiveCompactionModelOrOptions()
    {
        await using var fixture = await Fixture.Create();
        var frozen = new CapturingModel("frozen", blockFirst: true, overflowFirst: true);
        var next = new CapturingModel("next");
        await using var runtime = fixture.Runtime([new ScriptedModelProvider(frozen, "frozen"), new ScriptedModelProvider(next, "next")],
            new ModelSelection("frozen", "model", new("high", 700)));
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "first"), CancellationToken.None);
        await frozen.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await runtime.SubmitAsync(new SwitchModelCommand(StrongId.NewCommandId(), session.Id, 0,
            new ModelSelection("next", "model", new("low", 900))), CancellationToken.None)).Accepted);
        frozen.Release.TrySetResult();
        await WaitForTurn(projection, expectedCompletedAssistantMessages: 1);
        Assert.Empty(next.Requests);
        Assert.Equal(3, frozen.Requests.Count);
        Assert.All(frozen.Requests, request => Assert.Equal(new ModelCallOptions("high", 700), request.Options));
    }

    [Fact]
    public async Task DefaultOptionsAreUsedAndOutputIsCappedByContextRemaining()
    {
        await using var fixture = await Fixture.Create();
        var model = new CapturingModel("default", context: 4000, output: 8000);
        await using var runtime = fixture.Runtime([new ScriptedModelProvider(model)], new ModelSelection("scripted", "model", new("high", 7000)));
        var session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "hello"), CancellationToken.None);
        await WaitForTurn(projection, expectedCompletedAssistantMessages: 1);
        var options = Assert.Single(model.Requests).Options;
        Assert.Equal("high", options.ReasoningLevel);
        Assert.InRange(options.MaxOutputTokens!.Value, 1, 3000);
    }

    private static async Task WaitForTurn(IConversationProjection projection, int expectedCompletedAssistantMessages)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (projection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != TurnPhase.Idle ||
                snapshot.Rows.OfType<AssistantMessageRow>().Count(row => !row.IsStreaming) < expectedCompletedAssistantMessages))
            await Task.Delay(10, timeout.Token);
    }

    private sealed class CapturingModel(string provider, bool blockFirst = false, int context = 200_000, int? output = null, bool overflowFirst = false) : IChatModel
    {
        public ModelDescriptor Descriptor { get; } = new(provider, "model", "model", new(ContextWindowTokens: context, DefaultMaxOutputTokens: output));
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            Started.TrySetResult();
            if (blockFirst && Requests.Count == 1)
            {
                await Release.Task.WaitAsync(cancellationToken);
                if (overflowFirst)
                {
                    yield return new ModelStreamFailed(new ModelFailure(ModelFailureKind.ContextOverflow, "context length exceeded"));
                    yield break;
                }
                yield return new ModelToolCallReady(new ToolCall(new ToolCallId("call_1"), "nonexistent", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelStreamFinished(ModelFinishReason.ToolCalls);
            }
            else
            {
                yield return new ModelTextDelta("done");
                yield return new ModelStreamFinished(ModelFinishReason.Stop);
            }
        }
    }

    private sealed class Fixture(string root, SqliteSessionStore store) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public SqliteSessionStore Store { get; } = store;
        public static async Task<Fixture> Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "alcoler-model-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new SqliteSessionStore(Path.Combine(root, "sessions.db"));
            await store.InitializeAsync(CancellationToken.None);
            return new Fixture(root, store);
        }

        public AgentRuntime Runtime(IReadOnlyList<IModelProvider> providers, ModelSelection selection)
        {
            var artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
            return new AgentRuntime(new AgentRuntimeOptions
            {
                Store = Store,
                Artifacts = artifacts,
                Providers = providers,
                Tools = [],
                WorkspaceFactory = root => new WorkspaceToolEnvironment(root, artifacts),
                DefaultModel = selection,
            });
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}
