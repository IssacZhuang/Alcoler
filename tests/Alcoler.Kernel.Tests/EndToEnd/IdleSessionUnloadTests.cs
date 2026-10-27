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

public class IdleSessionUnloadTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromHours(1);

    private static ScriptStep Reply(string text) => new ScriptStep.Events(
        new ModelTextDelta(text), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop));

    private static SessionRuntime RuntimeOf(AgentRuntime runtime, SessionId sessionId)
    {
        var sessionRuntime = runtime.GetSession(sessionId);
        Assert.NotNull(sessionRuntime);
        return sessionRuntime;
    }

    // TurnFinished fires after the idle clock restarts at turn end, so awaiting it makes the
    // threshold comparison deterministic.
    private static async Task SendAndSettleAsync(
        AgentRuntime runtime,
        SessionRuntime sessionRuntime,
        IConversationProjection projection,
        SessionId id,
        string text,
        int expectedReplies)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionRuntime.TurnFinished += OnFinished;
        try
        {
            var ack = await runtime.SubmitAsync(
                new SendTextCommand(StrongId.NewCommandId(), id, 0, text), CancellationToken.None);
            Assert.True(ack.Accepted);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (projection.Snapshot() is var snapshot
                   && (snapshot.State.Phase != TurnPhase.Idle
                       || snapshot.Rows.OfType<AssistantMessageRow>().Count() < expectedReplies))
            {
                await Task.Delay(10, timeout.Token);
            }
            await settled.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            sessionRuntime.TurnFinished -= OnFinished;
        }

        return;

        ValueTask OnFinished(SessionRuntime _, TurnOutcome __)
        {
            settled.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task WaitIdleAsync(IConversationProjection projection, int expectedReplies)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (projection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != TurnPhase.Idle || snapshot.Rows.OfType<AssistantMessageRow>().Count() < expectedReplies))
            await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task IdleSessions_PastThresholdUnloadWhileKeepOpenAndRecentStayResident()
    {
        var clock = new ManualTimeProvider();
        await using var harness = TestHarness.Create(Reply("answer"));
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root,
            transform: options => options with { Clock = clock });
        var idle = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var kept = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var idleRuntime = RuntimeOf(runtime, idle.Id);
        using var projection = runtime.Subscribe(idle.Id);
        await SendAndSettleAsync(runtime, idleRuntime, projection, idle.Id, "question", 1);

        clock.Advance(Threshold.Add(TimeSpan.FromMinutes(1)));
        var recent = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);

        var unloaded = await runtime.UnloadIdleSessionsAsync(Threshold, kept.Id, CancellationToken.None);

        Assert.Equal(1, unloaded);
        Assert.Null(runtime.GetSession(idle.Id));
        Assert.Null(runtime.TryObserve(idle.Id));
        Assert.NotNull(runtime.GetSession(kept.Id));
        Assert.True(RuntimeOf(runtime, recent.Id).IsSafeToUnload);
        Assert.NotNull(runtime.GetSession(recent.Id));
    }

    [Fact]
    public async Task ActiveTurn_BlocksUnloadPastThreshold()
    {
        var clock = new ManualTimeProvider();
        await using var harness = TestHarness.Create(Reply("first answer"), Reply("second answer"));
        var gate = new GatedModel(harness.ScriptedModel);
        await using var runtime = TestHarness.BuildRuntimeFor(gate, harness.Root,
            transform: options => options with { Clock = clock });
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var sessionRuntime = RuntimeOf(runtime, session.Id);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndSettleAsync(runtime, sessionRuntime, projection, session.Id, "one", 1);

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "two"), CancellationToken.None);
        Assert.True(ack.Accepted);
        await gate.SecondCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        clock.Advance(Threshold.Add(TimeSpan.FromMinutes(1)));

        var unloaded = await runtime.UnloadIdleSessionsAsync(Threshold, null, CancellationToken.None);

        Assert.Equal(0, unloaded);
        Assert.False(sessionRuntime.IsSafeToUnload);
        Assert.NotNull(runtime.GetSession(session.Id));

        gate.Release();
        await WaitIdleAsync(projection, 2);
    }

    [Fact]
    public async Task UnloadedSession_ResumesAndServesNewTurns()
    {
        var clock = new ManualTimeProvider();
        await using var harness = TestHarness.Create(Reply("first answer"), Reply("resumed answer"));
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root,
            transform: options => options with { Clock = clock });
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var sessionRuntime = RuntimeOf(runtime, session.Id);
        using (var projection = runtime.Subscribe(session.Id))
        {
            await SendAndSettleAsync(runtime, sessionRuntime, projection, session.Id, "first", 1);
        }

        clock.Advance(Threshold.Add(TimeSpan.FromMinutes(1)));
        Assert.Equal(1, await runtime.UnloadIdleSessionsAsync(Threshold, null, CancellationToken.None));
        Assert.Null(runtime.GetSession(session.Id));

        await runtime.ResumeSessionAsync(session.Id, CancellationToken.None);
        Assert.NotNull(runtime.GetSession(session.Id));
        using var resumed = runtime.Subscribe(session.Id);
        await SendAndSettleAsync(runtime, RuntimeOf(runtime, session.Id), resumed, session.Id, "after resume", 2);
    }

    [Fact]
    public async Task SubagentChild_WithResidentParentIsSkipped()
    {
        var clock = new ManualTimeProvider();
        await using var harness = TestHarness.Create();
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root,
            transform: options => options with { Clock = clock });
        var parent = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var child = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, ParentSessionId: parent.Id), CancellationToken.None);
        clock.Advance(Threshold.Add(TimeSpan.FromMinutes(1)));

        var unloaded = await runtime.UnloadIdleSessionsAsync(Threshold, parent.Id, CancellationToken.None);

        Assert.Equal(0, unloaded);
        Assert.True(RuntimeOf(runtime, child.Id).IsSafeToUnload);
        Assert.NotNull(runtime.GetSession(child.Id));
    }

    // Holds the second model call open so the second turn is provably active while the clock
    // moves past the unload threshold.
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
