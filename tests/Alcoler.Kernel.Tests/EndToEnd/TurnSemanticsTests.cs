using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

// Turn-loop semantics aligned with the ZCode reference: steering pacing, queue hold,
// notification channel, partial-commit on cancel, stream recovery, and reminder cadence.
public class TurnSemanticsTests
{
    private static readonly ToolCallId ToolCallId = new("call-1");

    private static IReadOnlyList<ModelStreamEvent> Text(params string[] texts) =>
        [.. texts.Select(t => new ModelTextDelta(t)), new ModelStreamFinished(ModelFinishReason.Stop)];

    private static IReadOnlyList<ModelStreamEvent> ToolCall(string name, object input, ToolCallId? id = null) =>
    [
        new ModelTextDelta("Working on it."),
        new ModelToolInputStarted(id ?? ToolCallId, name),
        new ModelToolInputDelta(id ?? ToolCallId, JsonSerializer.Serialize(input)),
        new ModelToolInputEnded(id ?? ToolCallId),
        new ModelToolCallReady(new ToolCall(id ?? ToolCallId, name, JsonSerializer.SerializeToElement(input))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls),
    ];

    private static string RequestTexts(ModelRequest request) =>
        string.Join("\n", request.Messages.SelectMany(m => m.Content.OfType<InputTextBlock>()).Select(b => b.Text));

    private static bool RequestsContain(IReadOnlyList<ModelRequest> requests, string needle) =>
        requests.Any(r => RequestTexts(r).Contains(needle, StringComparison.Ordinal));

    private sealed class GateTool(
        TaskCompletionSource entered,
        TaskCompletionSource release,
        string output = "gate opened",
        bool stopTurnAfterResult = false,
        bool concurrentSafe = false) : ITool
    {
        private int _executions;
        public int Executions => Volatile.Read(ref _executions);

        public ToolContract Contract { get; } = new(
            "Gate",
            "Test-only tool that blocks until released.",
            ParseSchema("""{"type": "object", "properties": {}, "additionalProperties": false}"""));

        public ToolMetadata Metadata { get; } = new(
            ReadOnly: true, Destructive: false, ConcurrentSafe: concurrentSafe, RequiresUserInteraction: false,
            SideEffectScope: SideEffectScope.None, RiskLevel: RiskLevel.Low, NeedsApproval: false,
            AllowedInPlanMode: true);

        public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ToolInputIssue?>(null);

        public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult(input);

        public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ToolExecutionResult.Text(output, stopTurnAfterResult: stopTurnAfterResult);
        }

        private static JsonElement ParseSchema(string rawJson) =>
            JsonDocument.Parse(rawJson).RootElement.Clone();
    }

    // Starts a background job through the session's job service; the job body is gated by
    // the test so notification delivery timing is deterministic.
    private sealed class BackgroundNotifyTool(TaskCompletionSource jobRelease) : ITool
    {
        public ToolContract Contract { get; } = new(
            "BackgroundNotify",
            "Test-only tool that starts a gated background job.",
            ParseSchema("""{"type": "object", "properties": {}, "additionalProperties": false}"""));

        public ToolMetadata Metadata { get; } = new(
            ReadOnly: true, Destructive: false, ConcurrentSafe: true, RequiresUserInteraction: false,
            SideEffectScope: SideEffectScope.Session, RiskLevel: RiskLevel.Low, NeedsApproval: false,
            AllowedInPlanMode: true);

        public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ToolInputIssue?>(null);

        public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult(input);

        public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            context.BackgroundJobs?.Start(
                "probe job",
                null,
                async ct =>
                {
                    await jobRelease.Task.WaitAsync(ct);
                    return new BackgroundJobResult(0);
                });
            return ToolExecutionResult.Text("job started");
        }

        private static JsonElement ParseSchema(string rawJson) =>
            JsonDocument.Parse(rawJson).RootElement.Clone();
    }

    private sealed class HangingStreamModel : IChatModel
    {
        public TaskCompletionSource DeltaEmitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelDescriptor Descriptor { get; } = new(
            "scripted", "hanging-model", "Hanging Model",
            new ModelProperties(ContextWindowTokens: 200_000));

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ModelTextDelta("partial answer");
            DeltaEmitted.TrySetResult();
            await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
            yield return new ModelStreamFinished(ModelFinishReason.Stop);
        }
    }

    private sealed class TinyWindowModel(IReadOnlyList<IReadOnlyList<ModelStreamEvent>> calls) : IChatModel
    {
        private int _index;

        public List<ModelRequest> Requests { get; } = [];

        public ModelDescriptor Descriptor { get; } = new(
            "scripted", "tiny-window-model", "Tiny Window Model",
            new ModelProperties(ContextWindowTokens: 1_000));

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            foreach (var streamEvent in calls[Math.Min(_index++, calls.Count - 1)])
                yield return streamEvent;
        }
    }

    private sealed class FailedStreamAfterToolModel(Task toolResultObserved) : IChatModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public ModelDescriptor Descriptor { get; } = new(
            "scripted", "test-model", "Failed stream model", new ModelProperties(ContextWindowTokens: 200_000));

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                yield return new ModelTextDelta("uncommitted stream prose");
                var call = new ToolCall(ToolCallId, " Gate ", JsonSerializer.SerializeToElement(new { }));
                yield return new ModelToolCallReady(call);
                yield return new ModelToolCallReady(call);
                await toolResultObserved.WaitAsync(cancellationToken);
                yield return new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest, "stream rejected after ready tool"));
            }
            else
            {
                yield return new ModelTextDelta("recovered answer");
                yield return new ModelStreamFinished(ModelFinishReason.Stop);
            }
        }
    }

    private static async Task SendAndWaitIdleAsync(
        AgentRuntime runtime,
        IConversationProjection projection,
        SessionId id,
        string text,
        PermissionMode? mode = null,
        int expectedReplies = 1,
        bool expectFailure = false)
    {
        // A fresh submit can still observe the previous turn's Idle before this turn starts;
        // the committed user row is the monotonic proof the turn actually ran.
        var baselineUsers = projection.Snapshot().Rows.OfType<UserMessageRow>().Count();
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), id, 0, text, Mode: mode), CancellationToken.None);
        Assert.True(ack.Accepted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (projection.Snapshot() is var snapshot
               && (snapshot.State.Phase != TurnPhase.Idle
                   || snapshot.Rows.OfType<UserMessageRow>().Count() <= baselineUsers
                   || (!expectFailure && expectedReplies > 0
                       && snapshot.Rows.OfType<AssistantMessageRow>().Count() < expectedReplies)))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task<ConversationSnapshot> WaitForProjectionAsync(
        IConversationProjection projection, Func<ConversationSnapshot, bool> condition)
    {
        var snapshot = projection.Snapshot();
        if (condition(snapshot))
            return snapshot;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var _ in projection.Deltas(timeout.Token))
        {
            snapshot = projection.Snapshot();
            if (condition(snapshot))
                return snapshot;
        }
        throw new InvalidOperationException("Projection ended before the expected state was observed.");
    }

    [Fact]
    public async Task TextOnlyStep_WithExplicitGuides_ContinuesSameTurn()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            ToolCall("Gate", new { }),
            Text("first answer"),
            Text("second answer"),
        ];
        await using var harness = new ScriptedCallHarness(calls);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "steer one", Delivery: SendTextDelivery.Guide), CancellationToken.None);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "steer two", Delivery: SendTextDelivery.Guide), CancellationToken.None);
        release.TrySetResult();

        await WaitForAsync(() =>
        {
            var snapshot = projection.Snapshot();
            return snapshot.State.Phase == TurnPhase.Idle
                && snapshot.Rows.OfType<AssistantMessageRow>().Any(r => !r.IsStreaming
                    && r.Parts.OfType<TextPart>().Any(p => p.Text == "second answer"));
        });

        // One steer per boundary: the tool boundary takes "steer one", the text-only finish
        // takes "steer two" and continues the SAME turn for the closing answer.
        Assert.Equal(3, harness.Model.Requests.Count);
        Assert.Contains("steer one", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("steer two", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
        Assert.Contains("steer two", RequestTexts(harness.Model.Requests[2]), StringComparison.Ordinal);
        Assert.Equal(2, projection.Snapshot().Rows.OfType<AssistantMessageRow>().Count(r =>
            r.Parts.OfType<TextPart>().Any(p => p.Text.Contains("answer"))));
    }

    [Fact]
    public async Task DefaultQueue_RemainsSeparateTurnWhileGuideJoinsActiveTurn()
    {
        await using var harness = ScriptedCallHarness.Create(
            ToolCall("Gate", new { }), Text("guided answer"), Text("queued answer"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var opening = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start");
        var queued = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "queued later");
        var guide = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "guide now", Delivery: SendTextDelivery.Guide);
        await runtime.SubmitAsync(opening, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await runtime.SubmitAsync(queued, CancellationToken.None);
        await runtime.SubmitAsync(guide, CancellationToken.None);
        release.TrySetResult();

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle && harness.Model.Requests.Count == 3);

        Assert.Contains("guide now", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("queued later", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
        Assert.Contains(harness.Model.Requests[2].Messages, m => m.Role == MessageRole.User
            && m.Content.OfType<InputTextBlock>().Any(p => p.Text == "queued later"));
        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal(new[] { opening.CommandId, queued.CommandId },
            events.Select(e => e.Event).OfType<TurnStartedEvent>().Select(e => e.CommandId));
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var canonicalGuide = Assert.Single(messages, m => m.Message.Presentation == MessagePresentation.UserSteer).Message;
        Assert.Equal(MessageSource.RealUser, canonicalGuide.Source);
        Assert.Equal("guide now", Assert.Single(canonicalGuide.Parts.OfType<TextPart>()).Text);
        Assert.Contains("The user sent a new message while you were working:", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveQueuedInput_DeletesOnlyTargetAndSurvivesReplay()
    {
        await using var harness = ScriptedCallHarness.Create(
            ToolCall("Gate", new { }), Text("active answer"), Text("kept answer"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var kept = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "kept queue");
        var removed = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "removed queue");
        Assert.True((await runtime.SubmitAsync(kept, CancellationToken.None)).Accepted);
        Assert.True((await runtime.SubmitAsync(removed, CancellationToken.None)).Accepted);
        await WaitForProjectionAsync(projection, snapshot => snapshot.State.Queue.Count == 2);

        var ack = await runtime.SubmitAsync(new RemoveQueuedInputCommand(
            StrongId.NewCommandId(), session.Id, 0, removed.CommandId), CancellationToken.None);
        Assert.True(ack.Accepted);
        var afterRemove = await WaitForProjectionAsync(projection, snapshot => snapshot.State.Queue.Count == 1);
        Assert.Equal(kept.CommandId, Assert.Single(afterRemove.State.Queue).CommandId);
        var duplicate = await runtime.SubmitAsync(new RemoveQueuedInputCommand(
            StrongId.NewCommandId(), session.Id, 0, removed.CommandId), CancellationToken.None);
        Assert.False(duplicate.Accepted);

        release.TrySetResult();
        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle && harness.Model.Requests.Count == 3);

        Assert.False(RequestsContain(harness.Model.Requests, "removed queue"));
        Assert.Contains("kept queue", RequestTexts(harness.Model.Requests[2]), StringComparison.Ordinal);
        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        var discarded = Assert.Single(events.Select(entry => entry.Event).OfType<InputDiscardedEvent>());
        Assert.Equal(removed.CommandId, discarded.CommandId);

        await runtime.DisposeAsync();
        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var replay = resumed.Subscribe(session.Id);
        Assert.Empty(replay.Snapshot().State.Queue);
    }

    [Theory]
    [InlineData(SendTextDelivery.Queue)]
    [InlineData(SendTextDelivery.Guide)]
    public async Task PendingImageOnlyInput_PreservesArtifactAndReachesItsModelRequest(SendTextDelivery delivery)
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        await using var harness = ScriptedCallHarness.Create(
            ToolCall("Gate", new { }), Text("active answer"), Text("queued answer"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var command = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "", Delivery: delivery,
            Images: [new("image/png", png)]);
        Assert.True((await runtime.SubmitAsync(command, CancellationToken.None)).Accepted);
        var queuedSnapshot = await WaitForProjectionAsync(projection,
            snapshot => snapshot.State.Queue.Any(input => input.CommandId == command.CommandId));
        var queued = Assert.Single(queuedSnapshot.State.Queue);
        var artifact = Assert.Single(queued.Images!);
        Assert.Equal(command.CommandId, queued.CommandId);
        Assert.Equal(delivery, queued.Delivery);
        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        var durable = Assert.Single(events.Select(entry => entry.Event).OfType<InputQueuedEvent>()).Input;
        Assert.Equal(artifact, Assert.Single(durable.Images!));
        Assert.DoesNotContain(png, Contracts.Serialization.AlcolerJson.Serialize(durable), StringComparison.Ordinal);
        release.TrySetResult();
        var expectedReply = delivery == SendTextDelivery.Queue ? "queued answer" : "active answer";
        var completed = await WaitForProjectionAsync(projection, snapshot => snapshot.State.Phase == TurnPhase.Idle
            && snapshot.Rows.OfType<AssistantMessageRow>().Any(row => !row.IsStreaming
                && row.Parts.OfType<TextPart>().Any(part => part.Text == expectedReply)));
        Assert.Equal(delivery == SendTextDelivery.Queue ? 3 : 2, harness.Model.Requests.Count);
        Assert.Empty(harness.Model.Requests[0].Messages.SelectMany(message => message.Content).OfType<InputImageBlock>());
        if (delivery == SendTextDelivery.Queue)
            Assert.Empty(harness.Model.Requests[1].Messages.SelectMany(message => message.Content).OfType<InputImageBlock>());
        var imageMessage = Assert.Single(harness.Model.Requests[^1].Messages,
            message => message.Content.OfType<InputImageBlock>().Any());
        Assert.Equal(png, Assert.Single(imageMessage.Content.OfType<InputImageBlock>()).Base64Data);
        if (delivery == SendTextDelivery.Guide)
            Assert.Contains("The user sent a new message while you were working:",
                Assert.Single(imageMessage.Content.OfType<InputTextBlock>()).Text, StringComparison.Ordinal);
        var message = Assert.Single(await store.LoadMessagesAsync(session.Id, CancellationToken.None),
            stored => stored.Message.Parts.OfType<FilePart>().Any()).Message;
        Assert.Equal(artifact, Assert.Single(message.Parts.OfType<FilePart>()));
        Assert.Equal(delivery == SendTextDelivery.Guide ? MessagePresentation.UserSteer : null, message.Presentation);
        Assert.Empty(completed.State.Queue);
        await runtime.DisposeAsync();
        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var replay = resumed.Subscribe(session.Id);
        Assert.Empty(replay.Snapshot().State.Queue);
        Assert.Equal(artifact, Assert.Single(replay.Snapshot().Rows.OfType<UserMessageRow>()
            .SelectMany(row => row.Images ?? [])));
    }

    [Fact]
    public async Task Resume_RestoresCancelledPendingImageQueueAndDeliversOldestBeforeNewInput()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        await using var harness = ScriptedCallHarness.Create(
            ToolCall("Gate", new { }), Text("queued image seen"), Text("fresh answered"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var selection = new ModelSelection("scripted", "test-model", new ModelCallOptions(MaxOutputTokens: 1234));
        var command = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "", Mode: PermissionMode.Edit,
            ModelSelection: selection, Delivery: SendTextDelivery.Guide, Images: [new("image/png", png)],
            PlanEnabled: true);
        Assert.True((await runtime.SubmitAsync(command, CancellationToken.None)).Accepted);
        var queuedSnapshot = await WaitForProjectionAsync(projection,
            snapshot => snapshot.State.Queue.Any(input => input.CommandId == command.CommandId));
        var artifact = Assert.Single(Assert.Single(queuedSnapshot.State.Queue).Images!);
        await runtime.SubmitAsync(new StopCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
        await WaitForProjectionAsync(projection, snapshot => snapshot.State.Phase == TurnPhase.Idle);
        await runtime.DisposeAsync();

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var replay = resumed.Subscribe(session.Id);
        var restored = Assert.Single(replay.Snapshot().State.Queue);
        Assert.Equal(command.CommandId, restored.CommandId);
        Assert.Equal(artifact, Assert.Single(restored.Images!));
        Assert.Equal(PermissionMode.Edit, restored.Mode);
        Assert.Equal(selection, restored.ModelSelection);
        Assert.Equal(SendTextDelivery.Guide, restored.Delivery);
        Assert.Single(harness.Model.Requests);
        Assert.True((await resumed.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0,
            "fresh input"), CancellationToken.None)).Accepted);
        var completed = await WaitForProjectionAsync(replay, snapshot => snapshot.State.Phase == TurnPhase.Idle
            && snapshot.Rows.OfType<AssistantMessageRow>().Any(row => !row.IsStreaming
                && row.Parts.OfType<TextPart>().Any(part => part.Text == "fresh answered")));
        Assert.Equal(3, harness.Model.Requests.Count);
        var imageRequest = harness.Model.Requests[1];
        Assert.Equal(png, Assert.Single(imageRequest.Messages.SelectMany(message => message.Content).OfType<InputImageBlock>()).Base64Data);
        Assert.Equal(1234, imageRequest.Options.MaxOutputTokens);
        Assert.Contains("Plan mode is active.", RequestTexts(imageRequest), StringComparison.Ordinal);
        Assert.DoesNotContain("fresh input", RequestTexts(imageRequest), StringComparison.Ordinal);
        Assert.Contains("fresh input", RequestTexts(harness.Model.Requests[2]), StringComparison.Ordinal);
        Assert.Empty(completed.State.Queue);
        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal(command.CommandId, events.Select(entry => entry.Event).OfType<TurnStartedEvent>().ElementAt(1).CommandId);
    }

    [Fact]
    public async Task QueuedModeIntent_AppliesOnlyWhenItsOwnTurnStarts()
    {
        await using var harness = ScriptedCallHarness.Create(
            ToolCall("Gate", new { }), Text("active answer"), Text("planned answer"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var queued = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "plan next", Mode: PermissionMode.Yolo,
            PlanEnabled: true), CancellationToken.None);
        Assert.True(queued.Accepted);
        await using var store = harness.OpenStore();
        var beforeStart = await store.GetSessionAsync(session.Id, CancellationToken.None);
        Assert.Equal(PermissionMode.Yolo, beforeStart!.PermissionMode);
        Assert.False(beforeStart.PlanEnabled);
        Assert.Equal(PermissionMode.Yolo, projection.Snapshot().State.Mode);
        Assert.False(projection.Snapshot().State.PlanEnabled);
        release.TrySetResult();

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle && harness.Model.Requests.Count == 3);

        Assert.DoesNotContain("Plan mode is active.", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
        Assert.Contains("Plan mode is active.", RequestTexts(harness.Model.Requests[2]), StringComparison.Ordinal);
        var stored = await store.GetSessionAsync(session.Id, CancellationToken.None);
        Assert.Equal(PermissionMode.Yolo, stored!.PermissionMode);
        Assert.True(stored.PlanEnabled);
    }

    [Theory]
    [InlineData(SendTextDelivery.Queue)]
    [InlineData(SendTextDelivery.Guide)]
    public async Task PendingModelIntent_IsDeferredUntilConsumedAndGuideChangesNextModelStep(SendTextDelivery delivery)
    {
        var first = new ScriptedCallModel([ToolCall("Gate", new { }), Text("first model closing")]);
        var next = new ScriptedCallModel([Text("selected model answer")]);
        await using var harness = TestHarness.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(options => options with
        {
            Providers = [new ScriptedModelProvider(first), new ScriptedModelProvider(next, "next")],
        }, new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var selection = new ModelSelection("next", "test-model", new ModelCallOptions(MaxOutputTokens: 1234));
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "use selected model", ModelSelection: selection, Delivery: delivery), CancellationToken.None)).Accepted);
        await using var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        Assert.Equal("scripted", (await store.GetSessionAsync(session.Id, CancellationToken.None))!.ModelSelection!.ProviderId);
        Assert.Empty(next.Requests);
        release.TrySetResult();

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle && next.Requests.Count == 1);

        Assert.Equal(delivery == SendTextDelivery.Guide ? 1 : 2, first.Requests.Count);
        var selectedRequest = Assert.Single(next.Requests);
        Assert.Equal(1234, selectedRequest.Options.MaxOutputTokens);
        Assert.Contains("use selected model", RequestTexts(selectedRequest), StringComparison.Ordinal);
        Assert.Equal(selection, (await store.GetSessionAsync(session.Id, CancellationToken.None))!.ModelSelection);
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal(delivery == SendTextDelivery.Guide ? 1 : 2, events.Select(e => e.Event).OfType<TurnStartedEvent>().Count());
    }

    [Fact]
    public async Task StopTurnToolResult_DoesNotConsumeGuideIntoCompletedTurn()
    {
        await using var harness = ScriptedCallHarness.Create(ToolCall("Gate", new { }), Text("guide answered"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release, stopTurnAfterResult: true));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var opening = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start");
        var guide = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "guide after stop", Delivery: SendTextDelivery.Guide);
        await runtime.SubmitAsync(opening, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await runtime.SubmitAsync(guide, CancellationToken.None);
        release.TrySetResult();

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle && harness.Model.Requests.Count == 2);

        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal(new[] { opening.CommandId, guide.CommandId },
            events.Select(e => e.Event).OfType<TurnStartedEvent>().Select(e => e.CommandId));
        Assert.Contains(harness.Model.Requests[1].Messages, m => m.Role == MessageRole.User
            && m.Content.OfType<InputTextBlock>().Any(p => p.Text == guide.Text));
    }

    [Fact]
    public async Task ToolOutputLargerThanSixtyThousandCharacters_IsNotTruncatedByRuntime()
    {
        var output = "BEGIN-" + new string('x', 70_000) + "-END";
        await using var harness = ScriptedCallHarness.Create(ToolCall("Gate", new { }), Text("done"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release.TrySetResult();
        var tool = new GateTool(entered, release, output);
        await using var runtime = harness.BuildRuntime(tool);
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "read large output", expectedReplies: 2);

        Assert.Equal(2, harness.Model.Requests.Count);
        Assert.Equal(output, Assert.Single(harness.Model.Requests[1].Messages.SelectMany(m => m.Content).OfType<InputToolResultBlock>()).Content);
        Assert.Equal(1, tool.Executions);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Equal(output, Assert.Single(messages.SelectMany(m => m.Message.Parts).OfType<ToolResultPart>()
            .SelectMany(p => p.Content).OfType<ToolResultText>()).Text);
    }

    [Fact]
    public async Task ContinuationPrompts_StayInterleavedWithAssistantOutputsAndToolResults()
    {
        await using var harness = ScriptedCallHarness.Create(
            [new ModelTextDelta("part A"), new ModelStreamFinished(ModelFinishReason.Length)],
            [new ModelTextDelta("part B"), new ModelStreamFinished(ModelFinishReason.Length)],
            ToolCall("Gate", new { }), Text("done"), Text("next turn"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release.TrySetResult();
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "write then read", expectedReplies: 4);

        var third = harness.Model.Requests[2].Messages.SelectMany(m => m.Content).OfType<InputTextBlock>()
            .Select(p => p.Text).Where(t => t is "part A" or "part B" || t == OutputContinuation.ContinuePromptText);
        Assert.Equal(new[] { "part A", OutputContinuation.ContinuePromptText, "part B", OutputContinuation.ContinuePromptText }, third);
        var afterTools = harness.Model.Requests[3].Messages.SelectMany(m => m.Content).Where(b =>
            b is InputToolResultBlock || b is InputTextBlock t && (t.Text is "part A" or "part B" || t.Text == OutputContinuation.ContinuePromptText)).ToArray();
        Assert.IsType<InputToolResultBlock>(afterTools[^1]);
        Assert.Equal(2, afterTools.OfType<InputTextBlock>().Count(p => p.Text == OutputContinuation.ContinuePromptText));
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "new turn", expectedReplies: 5);
        Assert.DoesNotContain(harness.Model.Requests[4].Messages.SelectMany(m => m.Content).OfType<InputTextBlock>(),
            p => p.Text == OutputContinuation.ContinuePromptText);
    }

    [Fact]
    public async Task Steering_EmptyOrTooLargeInput_IsRejected()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls = [Text("hi")];
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);

        var empty = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "   "), CancellationToken.None);
        Assert.False(empty.Accepted);
        Assert.Equal("empty_input", empty.RejectionReason);

        var tooLarge = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, new string('x', 200_001)), CancellationToken.None);
        Assert.False(tooLarge.Accepted);
        Assert.Equal("input_too_large", tooLarge.RejectionReason);

        var boundary = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, new string('y', 200_000)), CancellationToken.None);
        Assert.True(boundary.Accepted);
    }

    [Fact]
    public async Task CancelledTurn_HoldsQueue_AndNextSendResumesOldestFirst()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            ToolCall("Gate", new { }),
            ToolCall("Gate", new { }),
            Text("held-a answer"),
            Text("held-a closing"),
            Text("fresh answer"),
        ];
        await using var harness = new ScriptedCallHarness(calls);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "held-a"), CancellationToken.None);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "held-b"), CancellationToken.None);
        await runtime.SubmitAsync(new StopCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);

        await WaitForAsync(() => runtime.GetSessionActivity(session.Id) is
            { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Cancelled });
        Assert.Single(harness.Model.Requests);
        release.TrySetResult();

        // The held queue is untouched until new input arrives; FIFO makes the OLDEST entry
        // drive the turn and the newcomer waits behind it.
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "fresh"), CancellationToken.None);
        await WaitForAsync(() => harness.Model.Requests.Count >= 2);
        Assert.Contains("held-a", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("fresh", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle && harness.Model.Requests.Count == 5);
        Assert.DoesNotContain("held-b", RequestTexts(harness.Model.Requests[2]), StringComparison.Ordinal);
        Assert.Contains("held-b", RequestTexts(harness.Model.Requests[3]), StringComparison.Ordinal);
        Assert.DoesNotContain("fresh", RequestTexts(harness.Model.Requests[3]), StringComparison.Ordinal);
        Assert.Contains("fresh", RequestTexts(harness.Model.Requests[4]), StringComparison.Ordinal);
        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal(4, events.Select(e => e.Event).OfType<TurnStartedEvent>().Count());
    }

    [Fact]
    public async Task CancelledStream_CommitsPartialAssistantBeforeTurnEnd()
    {
        var model = new HangingStreamModel();
        var harness = new ScriptedCallHarness([[new ModelStreamFinished(ModelFinishReason.Stop)]]) { OverrideModel = model };
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "long answer please"), CancellationToken.None);
        await model.DeltaEmitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        await runtime.SubmitAsync(new StopCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle);

        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Contains(messages, m => m.Message.Role == MessageRole.Assistant
            && m.Message.Parts.OfType<TextPart>().Any(p => p.Text.Contains("partial answer")));
    }

    [Fact]
    public async Task FailedStreamAfterReadyReadTool_PersistsMatchedAnchorAndReusesResultWithoutPartialProse()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release.TrySetResult();
        var model = new FailedStreamAfterToolModel(observed.Task);
        await using var harness = new ScriptedCallHarness([Text("unused")]) { OverrideModel = model };
        var tool = new GateTool(entered, release, concurrentSafe: true);
        await using var runtime = harness.BuildRuntime(tool);
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "recover streamed read"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForAsync(() => projection.Snapshot().Rows.OfType<ToolCallRow>().Any(r => r.Id == ToolCallId && r.Output is not null));
        observed.TrySetResult();

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(1, tool.Executions);
        var request = model.Requests[1];
        Assert.DoesNotContain("uncommitted stream prose", RequestTexts(request), StringComparison.Ordinal);
        var call = Assert.Single(request.Messages.SelectMany(m => m.Content).OfType<InputToolCallBlock>());
        var result = Assert.Single(request.Messages.SelectMany(m => m.Content).OfType<InputToolResultBlock>());
        Assert.Equal(ToolCallId, call.Id);
        Assert.Equal("Gate", call.Name);
        Assert.Equal(call.Id, result.Id);
        Assert.Equal("gate opened", result.Content);
        Assert.False(result.IsError);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var anchor = Assert.Single(messages, m => m.Message.Parts.OfType<ToolCallPart>().Any()).Message;
        Assert.Empty(anchor.Parts.OfType<TextPart>());
        Assert.Equal("scripted", anchor.ProviderId);
        Assert.Equal("test-model", anchor.ModelId);
        Assert.Single(messages.SelectMany(m => m.Message.Parts).OfType<ToolResultPart>());
        Assert.Equal(TurnOutcome.Completed, runtime.GetSessionActivity(session.Id)!.LastOutcome);
    }

    [Fact]
    public async Task RetryableFailureAfterBoundary_DiscardsPartialAndRecoversFresh()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            [new ModelTextDelta("partial "), new ModelStreamFailed(new ModelFailure(ModelFailureKind.Timeout, "stream stalled"))],
            Text("fresh full answer"),
        ];
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "work");

        Assert.Equal(2, harness.Model.Requests.Count);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var answer = messages.SelectMany(m => m.Message.Parts).OfType<TextPart>()
            .Single(p => p.Text.Contains("answer"));
        Assert.Equal("fresh full answer", answer.Text);
        Assert.DoesNotContain(messages, m => m.Message.Parts.OfType<TextPart>().Any(p => p.Text.Contains("partial ")));
    }

    [Fact]
    public async Task NonRetryableFailureAfterBoundary_CommitsPartialAndFailsTurn()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            [new ModelTextDelta("partial "), new ModelStreamFailed(new ModelFailure(ModelFailureKind.AuthenticationFailed, "bad key"))],
        ];
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "work", expectFailure: true);

        Assert.Single(harness.Model.Requests);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Contains(messages, m => m.Message.Parts.OfType<TextPart>().Any(p => p.Text.Contains("partial ")));
    }

    [Fact]
    public async Task NearWindow_KeepsBaselineMaxOutputTokens()
    {
        var model = new TinyWindowModel([Text("ok")]);
        await using var harness = new ScriptedCallHarness([Text("unused")]) { OverrideModel = model };
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "hello", expectedReplies: 0);

        var request = Assert.Single(model.Requests);
        // No positive headroom in a 1000-token window: the baseline stays unclamped so a
        // provider 400 can drive reactive compaction.
        Assert.Equal(32_000, request.Options.MaxOutputTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundJobCompletion_AfterCancelledOrFailedTurn_DoesNotResumeHeldQueue(bool fail)
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            ToolCall("BackgroundNotify", new { }, new ToolCallId("call-job")),
            ToolCall("Gate", new { }),
        ];
        if (fail)
            calls.Add([new ModelStreamFailed(new ModelFailure(ModelFailureKind.AuthenticationFailed, "bad key"))]);
        calls.AddRange([Text("held answer"), Text("notification answer"), Text("fresh answer")]);
        await using var harness = new ScriptedCallHarness(calls);
        var jobRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new BackgroundNotifyTool(jobRelease), new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var held = new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "held input");
        await runtime.SubmitAsync(held, CancellationToken.None);
        if (fail)
            release.TrySetResult();
        else
            await runtime.SubmitAsync(new StopCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
        var expectedOutcome = fail ? TurnOutcome.Failed : TurnOutcome.Cancelled;
        await WaitForAsync(() => runtime.GetSessionActivity(session.Id) is { Phase: TurnPhase.Idle } activity
            && activity.LastOutcome == expectedOutcome);
        var requestCount = harness.Model.Requests.Count;

        jobRelease.TrySetResult();
        await WaitForAsync(() => runtime.GetBackgroundJobs(session.Id).Single().Status == BackgroundJobStatus.Completed);
        await Task.Delay(200);

        Assert.Equal(requestCount, harness.Model.Requests.Count);
        Assert.Equal(expectedOutcome, runtime.GetSessionActivity(session.Id)!.LastOutcome);
        Assert.Equal(held.CommandId, Assert.Single(projection.Snapshot().State.Queue).CommandId);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "fresh input"), CancellationToken.None);
        await WaitForAsync(() => runtime.GetSessionActivity(session.Id) is { Phase: TurnPhase.Idle, LastOutcome: TurnOutcome.Completed }
            && harness.Model.Requests.Count == requestCount + 3);
        Assert.Contains("held input", RequestTexts(harness.Model.Requests[requestCount]), StringComparison.Ordinal);
        Assert.DoesNotContain("fresh input", RequestTexts(harness.Model.Requests[requestCount]), StringComparison.Ordinal);
        Assert.Contains("<task-notification>", RequestTexts(harness.Model.Requests[requestCount + 1]), StringComparison.Ordinal);
        Assert.Contains("fresh input", RequestTexts(harness.Model.Requests[requestCount + 2]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TurnOutcome.Cancelled, ToolCallStatus.Cancelled)]
    [InlineData(TurnOutcome.Failed, ToolCallStatus.Failed)]
    public void TurnCompleted_ClosesOpenToolRows(TurnOutcome outcome, ToolCallStatus expected)
    {
        var builder = new ProjectionBuilder();
        var sessionId = StrongId.NewSessionId();
        var turnId = StrongId.NewTurnId();
        var startedAt = DateTimeOffset.UtcNow;
        builder.Apply(new SequencedEvent(1, sessionId, startedAt,
            new ToolCallStartedEvent(turnId, ToolCallId, "Bash", JsonSerializer.SerializeToElement(new { }))));
        builder.Apply(new SequencedEvent(2, sessionId, startedAt.AddSeconds(1),
            new TurnCompletedEvent(turnId, outcome)));

        var row = Assert.Single(builder.Rows().OfType<ToolCallRow>());
        Assert.Equal(expected, row.Status);
        Assert.Equal(outcome == TurnOutcome.Failed, row.IsError);
        Assert.Equal(TurnPhase.Idle, builder.State.Phase);
    }

    [Fact]
    public void TurnFailed_ClosesOpenToolRows()
    {
        var builder = new ProjectionBuilder();
        var sessionId = StrongId.NewSessionId();
        var turnId = StrongId.NewTurnId();
        var startedAt = DateTimeOffset.UtcNow;
        builder.Apply(new SequencedEvent(1, sessionId, startedAt,
            new ToolCallStartedEvent(turnId, ToolCallId, "Bash", JsonSerializer.SerializeToElement(new { }))));
        builder.Apply(new SequencedEvent(2, sessionId, startedAt.AddSeconds(1),
            new TurnFailedEvent(turnId, "runtime crashed")));

        var row = Assert.Single(builder.Rows().OfType<ToolCallRow>());
        Assert.Equal(ToolCallStatus.Failed, row.Status);
        Assert.True(row.IsError);
        Assert.Equal(TurnPhase.Error, builder.State.Phase);
    }

    [Theory]
    [InlineData(ToolCallStatus.Running)]
    [InlineData(ToolCallStatus.Succeeded)]
    [InlineData(ToolCallStatus.Failed)]
    public void AssistantToolDeclaration_PreservesEarlyExecutionState(ToolCallStatus status)
    {
        var builder = new ProjectionBuilder();
        var sessionId = StrongId.NewSessionId();
        var turnId = StrongId.NewTurnId();
        var startedAt = DateTimeOffset.UtcNow;
        var input = JsonSerializer.SerializeToElement(new { file_path = "a.txt" });
        builder.Apply(new SequencedEvent(1, sessionId, startedAt,
            new ToolCallStartedEvent(turnId, ToolCallId, "Read", input)));
        if (status != ToolCallStatus.Running)
        {
            builder.Apply(new SequencedEvent(2, sessionId, startedAt.AddSeconds(1),
                new ToolCallResultEvent(turnId, ToolCallId, "Read",
                    ToolExecutionResult.Text("early result", isError: status == ToolCallStatus.Failed), TimeSpan.FromSeconds(1))));
        }
        var early = Assert.Single(builder.Rows().OfType<ToolCallRow>());

        builder.Apply(new SequencedEvent(3, sessionId, startedAt.AddSeconds(2),
            new AssistantMessageCommittedEvent(new ConversationMessage(
                StrongId.NewMessageId(), MessageRole.Assistant,
                [new ToolCallPart(StrongId.NewPartId(), ToolCallId, "Read", input)], startedAt.AddSeconds(2)))));

        Assert.Equal(early, Assert.Single(builder.Rows().OfType<ToolCallRow>()));
        Assert.Equal(status, early.Status);
    }

    [Fact]
    public async Task BackgroundJobCompletion_Idle_StartsNotificationTurnWithWrappedBody()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            ToolCall("BackgroundNotify", new { }),
            Text("launched"),
            Text("noted the notification"),
        ];
        await using var harness = new ScriptedCallHarness(calls);
        var jobRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new BackgroundNotifyTool(jobRelease));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "run the job", expectedReplies: 1);
        Assert.Equal(2, harness.Model.Requests.Count);
        jobRelease.TrySetResult();

        await WaitForAsync(() => harness.Model.Requests.Count == 3);
        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle);

        var notificationTurn = harness.Model.Requests[2];
        var notificationText = RequestTexts(notificationTurn);
        Assert.Contains("[SYSTEM NOTIFICATION - NOT USER INPUT]", notificationText, StringComparison.Ordinal);
        Assert.Contains("<task-notification>", notificationText, StringComparison.Ordinal);
        Assert.DoesNotContain("The user sent a new message while you were working:", notificationText, StringComparison.Ordinal);
        var notificationBlock = notificationTurn.Messages
            .SelectMany(m => m.Content.OfType<InputTextBlock>())
            .Single(b => b.Text.Contains("[SYSTEM NOTIFICATION - NOT USER INPUT]", StringComparison.Ordinal));
        Assert.StartsWith("<system-reminder>\n[SYSTEM NOTIFICATION - NOT USER INPUT]", notificationBlock.Text, StringComparison.Ordinal);

        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var notificationMessage = messages.SelectMany(m => m.Message.Parts)
            .OfType<AttachmentPart>()
            .Single(p => p.Kind == AttachmentKind.SystemReminder && p.Content.Contains("probe job"));
        Assert.True(notificationMessage.ProviderVisible);
    }

    [Fact]
    public async Task BackgroundJobCompletion_MidTurn_IsDrainedAtToolBoundary()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            ToolCall("BackgroundNotify", new { }, new ToolCallId("call-job")),
            ToolCall("Gate", new { }, new ToolCallId("call-gate")),
            Text("done after notification"),
        ];
        await using var harness = new ScriptedCallHarness(calls);
        var jobRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = harness.BuildRuntime(new BackgroundNotifyTool(jobRelease), new GateTool(entered, gateRelease));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "run then wait"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        jobRelease.TrySetResult();
        await Task.Delay(50);
        gateRelease.TrySetResult();

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle);

        Assert.Equal(3, harness.Model.Requests.Count);
        var boundaryText = RequestTexts(harness.Model.Requests[2]);
        Assert.Contains("[SYSTEM NOTIFICATION - NOT USER INPUT]", boundaryText, StringComparison.Ordinal);
        Assert.Contains("<task-notification>", boundaryText, StringComparison.Ordinal);
        // The notification joins the SAME turn: the final answer is its model step.
        Assert.Contains("done after notification", string.Join("\n", projection.Snapshot().Rows
            .OfType<AssistantMessageRow>().SelectMany(r => r.Parts.OfType<TextPart>().Select(p => p.Text))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundSubagentCompletion_NotifiesParentThroughChannel()
    {
        var agentCall = ToolCall("Agent", new
        {
            prompt = "count the files",
            description = "counting",
            run_in_background = true,
        });
        // Parent, child, and notification turn share one scripted model, so every step
        // after the launch carries the same deterministic body.
        List<ScriptStep> steps =
        [
            new ScriptStep.Events([.. agentCall]),
            new ScriptStep.Events([.. Text("child done")]),
            new ScriptStep.Events([.. Text("child done")]),
            new ScriptStep.Events([.. Text("child done")]),
        ];
        var root = Path.Combine(Path.GetTempPath(), "alcoler-subagent-notify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteSessionStore(Path.Combine(root, "db.sqlite"));
            await store.InitializeAsync(CancellationToken.None);
            var model = new Alcoler.Adapters.Models.SequencedChatModel(
                steps,
                new ModelDescriptor("scripted", "test-model", "Scripted Test Model",
                    new ModelProperties(ContextWindowTokens: 200_000)));
            var runtime = TestHarness.BuildRuntimeFor(model, root, store);
            var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(root), CancellationToken.None);
            using var projection = runtime.Subscribe(session.Id);

            await SendAndWaitIdleAsync(
                runtime, projection, session.Id, "launch the counter", expectedReplies: 1);

            // The child turn runs detached; its completion lands as a parent notification.
            AttachmentPart? notification = null;
            await WaitForAsync(() =>
            {
                notification = store.LoadMessagesAsync(session.Id, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult()
                    .SelectMany(m => m.Message.Parts)
                    .OfType<AttachmentPart>()
                    .SingleOrDefault(p => p.Content.Contains("Agent general-purpose task", StringComparison.Ordinal));
                return notification is not null;
            });

            Assert.Contains("<task-notification>", notification!.Content, StringComparison.Ordinal);
            var state = Assert.Single(projection.Snapshot().Rows.OfType<SystemNoticeRow>()
                .Select(row => row.Subagent).OfType<SubagentState>());
            var artifactDirectory = Path.Combine(root, "agent-output", session.Id.Value, state.Id.Value);
            var outputFile = Path.Combine(artifactDirectory, "output.txt");
            var taskOutputFile = Path.Combine(artifactDirectory, "task.output");
            var metadataFile = Path.Combine(artifactDirectory, "metadata.json");
            Assert.Equal(outputFile, state.OutputFile);
            Assert.True(File.Exists(outputFile));
            Assert.True(File.Exists(taskOutputFile));
            Assert.True(File.Exists(metadataFile));
            Assert.Equal("child done", File.ReadAllText(outputFile));
            Assert.Equal(File.ReadAllText(outputFile), File.ReadAllText(taskOutputFile));
            Assert.Contains($"<output-file>{outputFile}</output-file>", notification.Content, StringComparison.Ordinal);
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataFile));
            Assert.Equal("completed", metadata.RootElement.GetProperty("status").GetString());
            Assert.Equal("call-1", metadata.RootElement.GetProperty("parentToolUseId").GetString());
            Assert.Equal(state.Id.Value, metadata.RootElement.GetProperty("agentId").GetString());
            Assert.Equal(session.Id.Value, metadata.RootElement.GetProperty("parentSessionId").GetString());
            // The summary is XML-escaped per the reference's escapeXml, so quotes arrive as &quot;.
            Assert.Contains("Agent general-purpose task &quot;counting&quot; completed.", notification.Content, StringComparison.Ordinal);
            Assert.Contains("<result>child done</result>", notification.Content, StringComparison.Ordinal);
            Assert.Contains("<tool_uses>0</tool_uses>", notification.Content, StringComparison.Ordinal);
            Assert.Contains("<duration_ms>", notification.Content, StringComparison.Ordinal);
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

    [Fact]
    public async Task Resume_WithDanglingToolCall_SynthesizesInterruptedResult()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls = [Text("ok")];
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        await runtime.DisposeAsync();

        await using var store = harness.OpenStore();
        var danglingCall = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [new ToolCallPart(StrongId.NewPartId(), new ToolCallId("call_dangling"), "Read",
                JsonSerializer.SerializeToElement(new { file_path = "a.txt" }))],
            DateTimeOffset.UtcNow);
        var sequence = await store.GetNextMessageSequenceAsync(session.Id, CancellationToken.None);
        await store.AppendMessagesAsync(session.Id, [danglingCall], sequence, CancellationToken.None);

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);

        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var repair = messages.SelectMany(m => m.Message.Parts)
            .OfType<ToolResultPart>()
            .SingleOrDefault(p => p.ToolCallId == new ToolCallId("call_dangling"));
        Assert.NotNull(repair);
        Assert.Equal("[Tool execution was interrupted before resume]", Assert.Single(repair.Content.OfType<ToolResultText>()).Text);
        Assert.False(repair.IsError);
    }

    [Fact]
    public async Task Resume_AfterCrashDuringToolCall_ClosesInterruptedTurn()
    {
        await using var harness = ScriptedCallHarness.Create(Text("unused"));
        var sessionId = StrongId.NewSessionId();
        var now = DateTimeOffset.UtcNow;
        var turnId = StrongId.NewTurnId();
        var callId = new ToolCallId("call_crashed");
        var input = JsonSerializer.SerializeToElement(new { command = "sleep 30" });
        var assistant = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.Assistant,
            [new ToolCallPart(StrongId.NewPartId(), callId, "Bash", input)],
            now);

        await using (var store = harness.OpenStore())
        {
            await store.UpsertSessionAsync(new SessionRecord(
                sessionId, harness.Root, "Interrupted", PermissionMode.Yolo, null, now, now), CancellationToken.None);
            await store.AppendMessagesAsync(sessionId, [assistant], 0, CancellationToken.None);
            await store.AppendDurableEventsAsync(
            [
                new SequencedEvent(10, sessionId, now, new TurnStartedEvent(turnId, StrongId.NewCommandId())),
                new SequencedEvent(11, sessionId, now.AddSeconds(1),
                    new ToolCallStartedEvent(turnId, callId, "Bash", input)),
            ], CancellationToken.None);
        }

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(sessionId, CancellationToken.None);
        using var projection = resumed.Subscribe(sessionId);
        var snapshot = projection.Snapshot();

        Assert.Equal(TurnPhase.Idle, snapshot.State.Phase);
        Assert.Null(snapshot.State.ActiveTurn);
        var row = Assert.Single(snapshot.Rows.OfType<ToolCallRow>());
        Assert.NotEqual(ToolCallStatus.Running, row.Status);
        Assert.NotEqual(ToolCallStatus.Pending, row.Status);
    }

    [Fact]
    public async Task OutputContinuation_IsRequestLocalAndNeverPersisted()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            [new ModelTextDelta("part one "), new ModelStreamFinished(ModelFinishReason.Length)],
            [new ModelTextDelta("part two"), new ModelStreamFinished(ModelFinishReason.Length)],
            [new ModelTextDelta("part three"), new ModelStreamFinished(ModelFinishReason.Length)],
            [new ModelTextDelta("part four"), new ModelStreamFinished(ModelFinishReason.Length)],
        ];
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "keep writing", expectedReplies: 4);

        Assert.Equal(4, harness.Model.Requests.Count);
        Assert.Contains(OutputContinuation.ContinuePromptText, RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
        // Accumulated prompts stay visible to later requests within the turn...
        Assert.Equal(2, RequestTexts(harness.Model.Requests[2]).Split(OutputContinuation.ContinuePromptText).Length - 1);
        Assert.Equal(3, RequestTexts(harness.Model.Requests[3]).Split(OutputContinuation.ContinuePromptText).Length - 1);

        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(messages, m => m.Message.Parts.OfType<TextPart>()
            .Any(p => p.Text == OutputContinuation.ContinuePromptText));
    }

    [Fact]
    public async Task RepeatedIdenticalToolCall_WarnsOnThirdConsecutive_RequestLocalOnly()
    {
        var todoInput = new { todos = new[] { new { content = "same", status = "pending", priority = "medium" } } };
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            ToolCall("TodoWrite", todoInput, new ToolCallId("call-a")),
            ToolCall("TodoWrite", todoInput, new ToolCallId("call-b")),
            ToolCall("TodoWrite", todoInput, new ToolCallId("call-c")),
            Text("done"),
        ];
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "same calls", expectedReplies: 4);

        Assert.DoesNotContain("You have called TodoWrite", RequestTexts(harness.Model.Requests[2]), StringComparison.Ordinal);
        var warned = RequestTexts(harness.Model.Requests[3]);
        Assert.Contains("You have called TodoWrite with the same input 3 times in a row.", warned, StringComparison.Ordinal);
        Assert.Contains("Do not repeat the exact same tool call again", warned, StringComparison.Ordinal);

        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(messages, m => m.Message.Parts.OfType<AttachmentPart>()
            .Any(p => p.Content.Contains("You have called TodoWrite")));
    }

    [Fact]
    public async Task PlanMode_AttachesFullReminderThenSparseEveryFiveHumanTurns()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls = Enumerable.Range(0, 6)
            .Select(_ => Text("planning"))
            .ToList();
        await using var harness = new ScriptedCallHarness([.. calls]);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Edit,
                PlanEnabled: true), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        for (var i = 1; i <= 5; i++)
        {
            await SendAndWaitIdleAsync(runtime, projection, session.Id, $"question {i}", expectedReplies: i);
        }

        Assert.Contains("Plan mode is active.", RequestTexts(harness.Model.Requests[0]), StringComparison.Ordinal);
        for (var i = 1; i < 5; i++)
        {
            // Exactly the turn-1 full reminder carried by history; no fresh attachment yet.
            Assert.Equal(1, RequestTexts(harness.Model.Requests[i]).Split("Plan mode is active.").Length - 1);
            Assert.DoesNotContain("Plan mode still active", RequestTexts(harness.Model.Requests[i]), StringComparison.Ordinal);
        }
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "question 6", expectedReplies: 6);
        var sixth = RequestTexts(harness.Model.Requests[5]);
        Assert.Contains("Plan mode still active", sixth, StringComparison.Ordinal);
        Assert.Equal(1, sixth.Split("## Plan Workflow").Length - 1);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(messages.SelectMany(m => m.Message.Parts).OfType<AttachmentPart>(),
            p => RuntimeReminders.IsPlanModeReminder(p.Content));
    }

    [Fact]
    public async Task PlanModeExit_KeepsOneShotExitedReminderInMemoryOnly()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            ToolCall("ExitPlanMode", new { plan = "1. Write." }, new ToolCallId("call-plan")),
            Text("implementing"),
        ];
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Edit,
                PlanEnabled: true), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "plan it"), CancellationToken.None);
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.PendingPermission is null)
        {
            Assert.False(deadline.IsCompleted, "permission never arrived");
            await Task.Delay(20);
        }
        var permission = projection.Snapshot().State.PendingPermission!;
        await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, 0, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle);

        Assert.Equal(2, harness.Model.Requests.Count);
        Assert.Contains("## Exited Plan Mode", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(messages.SelectMany(m => m.Message.Parts).OfType<AttachmentPart>(),
            p => p.Content.Contains("## Exited Plan Mode"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPlanModeExit_InjectsMemoryOnlyReminderUnlessPlanIsReentered(bool reenterPlan)
    {
        await using var harness = ScriptedCallHarness.Create(Text("planning"), Text("next answer"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Edit,
                PlanEnabled: true), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first");
        Assert.True((await runtime.SubmitAsync(new SetModeCommand(
            StrongId.NewCommandId(), session.Id, 0, PermissionMode.Yolo, PlanEnabled: false),
            CancellationToken.None)).Accepted);
        if (reenterPlan)
        {
            Assert.True((await runtime.SubmitAsync(new SetModeCommand(
                StrongId.NewCommandId(), session.Id, 0, PlanEnabled: true),
                CancellationToken.None)).Accepted);
        }

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "second", expectedReplies: 2);

        Assert.Equal(!reenterPlan, RequestTexts(harness.Model.Requests[1]).Contains("## Exited Plan Mode", StringComparison.Ordinal));
        Assert.Equal(PermissionMode.Yolo, projection.Snapshot().State.Mode);
        Assert.Equal(reenterPlan, projection.Snapshot().State.PlanEnabled);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(messages.SelectMany(m => m.Message.Parts).OfType<AttachmentPart>(),
            p => p.Content.Contains("## Exited Plan Mode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TodoReminder_IsDurableButSuppressedUntilLengthContinuationEnds()
    {
        var calls = Enumerable.Range(0, 9).Select(_ => Text("answer")).ToList();
        calls.Add([new ModelTextDelta("truncated"), new ModelStreamFinished(ModelFinishReason.Length)]);
        calls.Add(ToolCall("Gate", new { }));
        calls.Add(Text("finished"));
        await using var harness = new ScriptedCallHarness(calls);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release.TrySetResult();
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        for (var turn = 0; turn < 9; turn++)
            await SendAndWaitIdleAsync(runtime, projection, session.Id, $"question {turn}", expectedReplies: turn + 1);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "long question", expectedReplies: 12);

        Assert.DoesNotContain(TodoReminderPolicy.ReminderBody, RequestTexts(harness.Model.Requests[10]), StringComparison.Ordinal);
        Assert.Contains(TodoReminderPolicy.ReminderBody, RequestTexts(harness.Model.Requests[11]), StringComparison.Ordinal);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Single(messages.SelectMany(m => m.Message.Parts).OfType<AttachmentPart>(),
            p => TodoReminderPolicy.IsTodoReminder(p.Content));
    }

    [Fact]
    public async Task NonLengthToolStep_ResetsOutputContinuationCountWithinTurn()
    {
        IReadOnlyList<ModelStreamEvent> Truncated(string text) =>
            [new ModelTextDelta(text), new ModelStreamFinished(ModelFinishReason.Length)];
        await using var harness = ScriptedCallHarness.Create(
            Truncated("A"), Truncated("B"), ToolCall("Gate", new { }),
            Truncated("C"), Truncated("D"), Text("done"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release.TrySetResult();
        await using var runtime = harness.BuildRuntime(new GateTool(entered, release));
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "continue across tools", expectedReplies: 0);
        using var outcomeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (runtime.GetSessionActivity(session.Id)!.LastOutcome is null)
            await Task.Delay(10, outcomeTimeout.Token);

        Assert.Equal(6, harness.Model.Requests.Count);
        Assert.Equal(TurnOutcome.Completed, runtime.GetSessionActivity(session.Id)!.LastOutcome);
        Assert.Equal(4, harness.Model.Requests[5].Messages.SelectMany(m => m.Content).OfType<InputTextBlock>()
            .Count(p => p.Text == OutputContinuation.ContinuePromptText));
    }

    [Fact]
    public async Task RapidRefill_ThirdConsecutiveCompact_TurnFailsWithStructuredError()
    {
        var tinyWindow = new ModelDescriptor(
            "scripted", "tiny-model", "Tiny Model", new ModelProperties(ContextWindowTokens: 2_000));
        var steps = new List<IReadOnlyList<ModelStreamEvent>>
        {
            Text("answer one"),
            Text("answer two"),
            new ModelStreamEvent[] { new ModelTextDelta("summary one"), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop) },
            Text("answer three"),
            new ModelStreamEvent[] { new ModelTextDelta("summary two"), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop) },
            Text("answer four"),
        };
        var root = Path.Combine(Path.GetTempPath(), "alcoler-rapid-refill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = new PerCallModel(steps, tinyWindow);
            var store = new SqliteSessionStore(Path.Combine(root, "db.sqlite"));
            var runtime = TestHarness.BuildRuntimeFor(model, root, store);
            var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(root), CancellationToken.None);
            using var projection = runtime.Subscribe(session.Id);

            for (var i = 1; i <= 5; i++)
            {
                var ack = await runtime.SubmitAsync(
                    new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, $"q{i}"), CancellationToken.None);
                Assert.True(ack.Accepted);
            }

            await WaitForAsync(() => store
                .LoadDurableEventsAsync(session.Id, CancellationToken.None).AsTask().GetAwaiter().GetResult()
                .Any(d => d.Event is TurnFailedEvent f && f.Error.Contains("Autocompact stopped", StringComparison.Ordinal)));

            var durable = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
            var failure = durable.Select(d => d.Event).OfType<TurnFailedEvent>().LastOrDefault();
            Assert.NotNull(failure);
            Assert.Equal(
                "Autocompact stopped because the context refilled within fewer than 3 tool turns after compaction 3 times in a row. A file or tool output may be too large. Read it in smaller chunks, or start a new session.",
                failure.Error);
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

    private sealed class PerCallModel(IReadOnlyList<IReadOnlyList<ModelStreamEvent>> calls, ModelDescriptor descriptor) : IChatModel
    {
        private int _index;

        public ModelDescriptor Descriptor => descriptor;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (_index >= calls.Count)
            {
                throw new InvalidOperationException($"script exhausted after {calls.Count} call(s)");
            }
            foreach (var streamEvent in calls[_index++])
                yield return streamEvent;
        }
    }
}
