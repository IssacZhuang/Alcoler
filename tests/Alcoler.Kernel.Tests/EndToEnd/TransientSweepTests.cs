using System.Runtime.CompilerServices;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tests.Models;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class TransientSweepTests
{
    private static readonly TimeSpan Grace =
        TimeSpan.FromMilliseconds(SessionEventRetention.SealedTurnTransientGraceMs);

    private static ScriptStep Reply(string text) => new ScriptStep.Events(
        new ModelTextDelta(text), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop));

    // The store is not exposed on the runtime; recovery tests already reach internals this way.
    private static SessionEventStore EventStoreOf(SessionRuntime runtime) =>
        (SessionEventStore)typeof(SessionRuntime)
            .GetField("_events", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(runtime)!;

    [Fact]
    public async Task IdleSession_SweepAfterGraceDropsTransientTailAndKeepsDurableEvents()
    {
        var clock = new ManualTimeProvider();
        await using var harness = TestHarness.Create(Reply("streamed answer"));
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root,
            transform: options => options with { Clock = clock });
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var sessionRuntime = runtime.GetSession(session.Id);
        Assert.NotNull(sessionRuntime);
        var store = EventStoreOf(sessionRuntime);
        var settled = new TurnSignal();
        sessionRuntime.TurnFinished += (_, _) =>
        {
            settled.Signal();
            return ValueTask.CompletedTask;
        };
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "question", 1);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(store.TransientCount > 0);
        var durableBefore = store.DurableEvents();

        clock.Advance(Grace.Add(TimeSpan.FromSeconds(1)));

        Assert.Equal(0, store.TransientCount);
        var durableAfter = store.DurableEvents();
        Assert.Equal(durableBefore.Count, durableAfter.Count);
        Assert.Contains(durableAfter, e => e.Event is TurnStartedEvent);
        Assert.Contains(durableAfter, e => e.Event is TurnCompletedEvent);
        Assert.Contains(durableAfter, e => e.Event is UserMessageCommittedEvent);
        Assert.Contains(durableAfter, e => e.Event is AssistantMessageCommittedEvent);
    }

    [Fact]
    public async Task ActiveTurn_SuppressesSweepUntilThatTurnsOwnEndOfTurnEviction()
    {
        var clock = new ManualTimeProvider();
        await using var harness = TestHarness.Create(Reply("first answer"), Reply("second answer"));
        var gate = new GatedModel(harness.ScriptedModel);
        await using var runtime = TestHarness.BuildRuntimeFor(gate, harness.Root,
            transform: options => options with { Clock = clock });
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var sessionRuntime = runtime.GetSession(session.Id);
        Assert.NotNull(sessionRuntime);
        var store = EventStoreOf(sessionRuntime);
        var settled = new TurnSignal();
        sessionRuntime.TurnFinished += (_, _) =>
        {
            settled.Signal();
            return ValueTask.CompletedTask;
        };
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "one", 1);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(store.TransientCount > 0);

        clock.Advance(Grace.Subtract(TimeSpan.FromSeconds(1)));
        Assert.True(store.TransientCount > 0);

        settled.Arm();
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "two"), CancellationToken.None);
        Assert.True(ack.Accepted);
        await gate.SecondCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        clock.Advance(Grace.Add(TimeSpan.FromSeconds(2)));
        Assert.Contains(store.EventsFrom(0), e => e.Event is ModelStreamingTextEvent { Delta: "first answer" });

        gate.Release();
        await WaitIdleAsync(projection, 2);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // Turn two's own end-of-turn eviction is past the grace for turn one's tail; its own
        // fresh streaming tail survives until the next idle sweep.
        Assert.DoesNotContain(store.EventsFrom(0), e => e.Event is ModelStreamingTextEvent { Delta: "first answer" });
        Assert.True(store.TransientCount > 0);

        clock.Advance(Grace.Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, store.TransientCount);
        var durable = store.DurableEvents();
        Assert.Equal(2, durable.Count(e => e.Event is TurnStartedEvent));
        Assert.Equal(2, durable.Count(e => e.Event is TurnCompletedEvent));
        Assert.Equal(2, durable.Count(e => e.Event is UserMessageCommittedEvent));
        Assert.Equal(2, durable.Count(e => e.Event is AssistantMessageCommittedEvent));
    }

    private static async Task SendAndWaitIdleAsync(
        AgentRuntime runtime, IConversationProjection projection, SessionId id, string text, int expectedReplies)
    {
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), id, 0, text), CancellationToken.None);
        Assert.True(ack.Accepted);
        await WaitIdleAsync(projection, expectedReplies);
    }

    private static async Task WaitIdleAsync(IConversationProjection projection, int expectedReplies)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (projection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != TurnPhase.Idle || snapshot.Rows.OfType<AssistantMessageRow>().Count() < expectedReplies))
            await Task.Delay(10, timeout.Token);
    }

    // TurnFinished fires after the end-of-turn eviction and sweep arming, so it is the
    // deterministic signal that the timer exists.
    private sealed class TurnSignal
    {
        private TaskCompletionSource _source = New();

        public Task Task => _source.Task;

        public void Arm() => _source = New();

        public void Signal() => _source.TrySetResult();

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Holds the second model call open so the second turn is provably active while the clock
    // moves past the sweep deadline.
    private sealed class GatedModel(IChatModel inner) : IChatModel
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource SecondCallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelDescriptor Descriptor => inner.Descriptor;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 2)
            {
                SecondCallEntered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            await foreach (var streamEvent in inner.StreamAsync(request, cancellationToken).WithCancellation(cancellationToken))
                yield return streamEvent;
        }

        public void Release() => _release.TrySetResult();
    }
}
