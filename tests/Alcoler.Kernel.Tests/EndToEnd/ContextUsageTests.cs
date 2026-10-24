using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tests.Models;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class ContextUsageTests
{
    private static ScriptStep Reply(string text, TokenUsage? usage = null) => new ScriptStep.Events(
        new ModelTextDelta(text), new ModelStreamFinished(ModelFinishReason.Stop, usage));

    [Fact]
    public async Task ProviderUsage_ReplacesEstimateAndCacheRateWeightsRequests()
    {
        await using var harness = TestHarness.Create(
            Reply("first", new TokenUsage(InputTokens: 1000, ContextUsageTokens: 1000, CacheReadTokens: 1000)),
            Reply("second", new TokenUsage(InputTokens: 9000, ContextUsageTokens: 9000, CacheReadTokens: 0)));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitAsync(runtime, projection, session.Id, "one", 1);
        var first = Assert.IsType<ContextUsageState>(projection.Snapshot().State.ContextUsage);
        Assert.Equal(1000, first.UsedTokens);
        Assert.Equal(TokenEstimator.ProviderUsageSource, first.TokenSource);
        Assert.Contains(first.Breakdown, category => category.Source == ContextUsageSources.SystemToolSchemas && category.Characters > 0);
        Assert.Contains(first.Breakdown, category => category.Source == ContextUsageSources.Messages && category.Characters > 0);

        await SendAndWaitAsync(runtime, projection, session.Id, "two", 2);
        var second = Assert.IsType<ContextUsageState>(projection.Snapshot().State.ContextUsage);
        Assert.Equal(9000, second.UsedTokens);
        Assert.Equal(1000, second.CacheReadTokens);
        Assert.Equal(10000, second.CacheInputTokens);
        Assert.Equal(.1, (double)second.CacheReadTokens / second.CacheInputTokens);
        Assert.Equal(9000, second.LastUsage!.InputTokens);
    }

    [Fact]
    public async Task Resume_RecomputesRequestBreakdownAndPreservesCacheTotals()
    {
        await using var harness = TestHarness.Create(
            Reply("answer", new TokenUsage(ContextUsageTokens: 2000, CacheReadTokens: 1600)),
            Reply("continued", new TokenUsage(ContextUsageTokens: 2500, CacheReadTokens: 2000)));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitAsync(runtime, projection, session.Id, "question", 1);
        for (var i = 0; i < 4; i++)
            await runtime.SubmitAsync(new RenameSessionCommand(StrongId.NewCommandId(), session.Id, 0, $"Renamed {i}"), CancellationToken.None);
        var priorSequence = runtime.Subscribe(session.Id);
        var highWater = priorSequence.Snapshot().Sequence;
        priorSequence.Dispose();
        await runtime.DisposeAsync();

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = resumed.Subscribe(session.Id);
        var context = Assert.IsType<ContextUsageState>(resumedProjection.Snapshot().State.ContextUsage);
        Assert.Equal(TokenEstimator.EstimateSource, context.TokenSource);
        Assert.True(context.UsedTokens > 0);
        Assert.Equal(1600, context.CacheReadTokens);
        Assert.Equal(2000, context.CacheInputTokens);
        Assert.Contains(context.Breakdown, category => category.Source == ContextUsageSources.Messages);
        Assert.True(resumedProjection.Snapshot().Sequence > highWater);
        await SendAndWaitAsync(resumed, resumedProjection, session.Id, "continue", 2);
        var continued = Assert.IsType<ContextUsageState>(resumedProjection.Snapshot().State.ContextUsage);
        Assert.Equal(2500, continued.UsedTokens);
        Assert.Equal(3600, continued.CacheReadTokens);
        Assert.Equal(4500, continued.CacheInputTokens);
    }

    [Fact]
    public async Task Compaction_ResetsProviderAnchorButPreservesCacheTotals()
    {
        await using var harness = TestHarness.Create(
            Reply("answer", new TokenUsage(ContextUsageTokens: 5000, CacheReadTokens: 4500)),
            new ScriptStep.Events(new ModelTextDelta("summary"), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop)),
            Reply("after compact", new TokenUsage(ContextUsageTokens: 1000, CacheReadTokens: 500)));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitAsync(runtime, projection, session.Id, "question", 1);
        var ack = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
        Assert.True(ack.Accepted);
        using var compactedProjection = runtime.Subscribe(session.Id);
        var compacted = Assert.IsType<ContextUsageState>(compactedProjection.Snapshot().State.ContextUsage);
        Assert.Equal(TokenEstimator.EstimateSource, compacted.TokenSource);
        Assert.Equal(4500, compacted.CacheReadTokens);
        Assert.Equal(5000, compacted.CacheInputTokens);
        Assert.Equal(.9, (double)compacted.CacheReadTokens / compacted.CacheInputTokens);
        Assert.Null(compacted.LastUsage);
        Assert.NotEqual(5000, compacted.UsedTokens);

        await SendAndWaitAsync(runtime, compactedProjection, session.Id, "continue", 2);
        var continued = Assert.IsType<ContextUsageState>(compactedProjection.Snapshot().State.ContextUsage);
        Assert.Equal(5000, continued.CacheReadTokens);
        Assert.Equal(6000, continued.CacheInputTokens);
    }

    [Fact]
    public void Projection_TurnCompletionDoesNotOverwriteNewerContextEstimate()
    {
        var builder = new ProjectionBuilder();
        var id = StrongId.NewSessionId();
        var context = new ContextUsageState(1234, 200000, TokenEstimator.EstimateSource, []);
        builder.Apply(new SequencedEvent(1, id, DateTimeOffset.UtcNow, new ContextUsageUpdatedEvent(context)));
        builder.Apply(new SequencedEvent(2, id, DateTimeOffset.UtcNow,
            new TurnCompletedEvent(StrongId.NewTurnId(), TurnOutcome.Completed, new TokenUsage(ContextUsageTokens: 9000))));
        Assert.Equal(1234, builder.State.ContextUsageTokens);
        Assert.Equal(context, builder.State.ContextUsage);
    }

    [Fact]
    public async Task Projection_HiddenStreamingEventsPreserveDeltaSequenceAndVisibleChanges()
    {
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        var turnId = StrongId.NewTurnId();
        store.Append(new TurnStartedEvent(turnId, StrongId.NewCommandId()));
        using var projection = new LiveConversationProjection(store, 0, () => 0);
        var initial = projection.Snapshot();
        SessionEvent[] events =
        [
            new ModelStreamingReasoningEvent(turnId, "Thinking"),
            new ModelStreamingToolInputEvent(turnId, new ToolCallId("call-1"), "{}"),
            new ModelStreamingTextEvent(turnId, "Hello"),
            new ModelStreamingReasoningEvent(turnId, "Still thinking"),
            new ModelStreamingTextEvent(turnId, " world"),
        ];
        foreach (var sessionEvent in events)
            store.Append(sessionEvent);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var deltas = projection.Deltas(timeout.Token).GetAsyncEnumerator();
        var received = new List<ConversationDelta>();
        for (var i = 0; i < events.Length; i++)
        {
            Assert.True(await deltas.MoveNextAsync());
            var delta = deltas.Current;
            Assert.Equal(initial.Sequence + i + 1, delta.Sequence);
            received.Add(delta);
        }

        Assert.Empty(received[0].Ops);
        Assert.Empty(received[1].Ops);
        Assert.Empty(received[3].Ops);
        var first = Assert.IsType<AssistantMessageRow>(Assert.Single(received[2].Ops.OfType<RowAppended>()).Row);
        Assert.True(first.IsStreaming);
        Assert.Equal("Hello", Assert.IsType<TextPart>(Assert.Single(first.Parts)).Text);
        var updated = Assert.IsType<AssistantMessageRow>(Assert.Single(received[4].Ops.OfType<RowUpserted>()).Row);
        Assert.Equal(first.RowId, updated.RowId);
        Assert.True(updated.IsStreaming);
        Assert.Equal("Hello world", Assert.IsType<TextPart>(Assert.Single(updated.Parts)).Text);
        Assert.Equal(TurnPhase.Streaming, Assert.Single(received[4].Ops.OfType<StatePatched>()).State.Phase);
        var snapshot = projection.Snapshot();
        Assert.Equal(store.LastSequence, snapshot.Sequence);
        Assert.Equal(updated, Assert.Single(snapshot.Rows));
        Assert.Equal(TurnPhase.Streaming, snapshot.State.Phase);
    }

    [Fact]
    public async Task CompactImmediatelyAfterProjectedIdle_IsAccepted()
    {
        // Regression: the projection can report Idle while the runtime is still releasing
        // the turn; a compact submitted in that window must not be rejected.
        const int rounds = 8;
        var summary = new ScriptStep.Events(
            new ModelTextDelta("summary"), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop));
        var script = Enumerable.Range(0, rounds)
            .SelectMany(_ => new ScriptStep[] { Reply("answer"), summary })
            .ToArray();
        await using var harness = TestHarness.Create(script);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        for (var round = 0; round < rounds; round++)
        {
            await SendAndWaitAsync(runtime, projection, session.Id, $"question {round}", round + 1);
            var ack = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
            Assert.True(ack.Accepted);
        }
    }

    [Fact]
    public async Task OutputSpeed_ExcludesToolTimeAndSurvivesResume()
    {
        var root = Path.Combine(Path.GetTempPath(), "alcoler-speed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new ManualTimeProvider();
            var store = new SqliteSessionStore(Path.Combine(root, "db.sqlite"));
            var tool = new ClockAdvanceTool(clock);
            SessionId sessionId;
            await using (var runtime = TestHarness.BuildRuntimeFor(new TimedModel(clock), root, store,
                             options => options with { Clock = clock }, tool))
            {
                var session = await runtime.CreateSessionAsync(
                    new Contracts.Sessions.SessionOptions(root), CancellationToken.None);
                sessionId = session.Id;
                var sessionRuntime = runtime.GetSession(session.Id);
                Assert.NotNull(sessionRuntime);
                var turnFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                sessionRuntime.TurnFinished += (_, _) =>
                {
                    turnFinished.TrySetResult();
                    return ValueTask.CompletedTask;
                };
                using var projection = runtime.Subscribe(session.Id);
                await SendAndWaitAsync(runtime, projection, session.Id, "go", 2);
                await turnFinished.Task.WaitAsync(TimeSpan.FromSeconds(20));

                var usage = Assert.IsType<ContextUsageState>(projection.Snapshot().State.ContextUsage);
                Assert.Equal(150, usage.OutputTokens);
                Assert.Equal(3000, usage.GenerationDurationMs);

                var compactAck = await runtime.SubmitAsync(
                    new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
                Assert.True(compactAck.Accepted, compactAck.RejectionReason);
                using var compactedProjection = runtime.Subscribe(session.Id);
                var compacted = Assert.IsType<ContextUsageState>(compactedProjection.Snapshot().State.ContextUsage);
                Assert.Equal(150, compacted.OutputTokens);
                Assert.Equal(3000, compacted.GenerationDurationMs);
            }

            await using (var resumed = TestHarness.BuildRuntimeFor(new TimedModel(clock), root, store,
                             options => options with { Clock = clock }, tool))
            {
                await resumed.ResumeSessionAsync(sessionId, CancellationToken.None);
                using var projection = resumed.Subscribe(sessionId);
                var usage = Assert.IsType<ContextUsageState>(projection.Snapshot().State.ContextUsage);
                Assert.Equal(150, usage.OutputTokens);
                Assert.Equal(3000, usage.GenerationDurationMs);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class TimedModel(ManualTimeProvider clock) : IChatModel
    {
        private static readonly ToolCallId ToolCallId = new("clock-call");
        private int _calls;

        public ModelDescriptor Descriptor { get; } = new(
            "scripted", "timed-model", "Timed Model",
            new ModelProperties(ContextWindowTokens: 200_000));

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                yield return new ModelTextStarted();
                yield return new ModelTextDelta("Working on it.");
                yield return new ModelTextEnded();
                yield return new ModelToolInputStarted(ToolCallId, ClockAdvanceTool.ToolName);
                yield return new ModelToolInputDelta(ToolCallId, "{}");
                yield return new ModelToolInputEnded(ToolCallId);
                yield return new ModelToolCallReady(new ToolCall(ToolCallId, ClockAdvanceTool.ToolName,
                    JsonSerializer.SerializeToElement(new { })));
                yield return new ModelStreamFinished(ModelFinishReason.ToolCalls, new TokenUsage(OutputTokens: 100));
                yield break;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
            yield return new ModelTextStarted();
            yield return new ModelTextDelta("done");
            yield return new ModelTextEnded();
            yield return new ModelStreamFinished(ModelFinishReason.Stop, new TokenUsage(OutputTokens: 50));
        }
    }

    private sealed class ClockAdvanceTool(ManualTimeProvider clock) : ITool
    {
        public const string ToolName = "ClockAdvance";

        public ToolContract Contract { get; } = new(
            ToolName,
            "Test-only tool that advances the manual clock.",
            JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")
                .RootElement.Clone());

        public ToolMetadata Metadata { get; } = new(
            ReadOnly: true, Destructive: false, ConcurrentSafe: false, RequiresUserInteraction: false,
            SideEffectScope: SideEffectScope.None, RiskLevel: RiskLevel.Low, NeedsApproval: false,
            AllowedInPlanMode: true);

        public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ToolInputIssue?>(null);

        public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult(input);

        public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            return ValueTask.FromResult(ToolExecutionResult.Text("advanced"));
        }
    }

    private static async Task SendAndWaitAsync(AgentRuntime runtime, IConversationProjection projection, SessionId id, string text, int expectedReplies)
    {
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), id, 0, text), CancellationToken.None);
        Assert.True(ack.Accepted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (projection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != TurnPhase.Idle || snapshot.Rows.OfType<AssistantMessageRow>().Count() < expectedReplies))
            await Task.Delay(10, timeout.Token);
    }
}
