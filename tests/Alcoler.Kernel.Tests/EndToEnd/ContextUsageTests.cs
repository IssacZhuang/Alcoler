using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.Runtime;
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
    public async Task Compaction_ResetsProviderAnchorAndCacheTotals()
    {
        await using var harness = TestHarness.Create(
            Reply("answer", new TokenUsage(ContextUsageTokens: 5000, CacheReadTokens: 4500)),
            Reply("summary"), Reply("after compact"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitAsync(runtime, projection, session.Id, "question", 1);
        var ack = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
        Assert.True(ack.Accepted);
        using var compactedProjection = runtime.Subscribe(session.Id);
        var compacted = Assert.IsType<ContextUsageState>(compactedProjection.Snapshot().State.ContextUsage);
        Assert.Equal(TokenEstimator.EstimateSource, compacted.TokenSource);
        Assert.Equal(0, compacted.CacheReadTokens);
        Assert.Equal(0, compacted.CacheInputTokens);
        Assert.Null(compacted.LastUsage);
        Assert.NotEqual(5000, compacted.UsedTokens);
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
