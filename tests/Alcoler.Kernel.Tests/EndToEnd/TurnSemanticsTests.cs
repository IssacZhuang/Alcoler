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

    private sealed class GateTool(TaskCompletionSource entered, TaskCompletionSource release) : ITool
    {
        public ToolContract Contract { get; } = new(
            "Gate",
            "Test-only tool that blocks until released.",
            ParseSchema("""{"type": "object", "properties": {}, "additionalProperties": false}"""));

        public ToolMetadata Metadata { get; } = new(
            ReadOnly: true, Destructive: false, ConcurrentSafe: false, RequiresUserInteraction: false,
            SideEffectScope: SideEffectScope.None, RiskLevel: RiskLevel.Low, NeedsApproval: false,
            AllowedInPlanMode: true);

        public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ToolInputIssue?>(null);

        public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
            ValueTask.FromResult(input);

        public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ToolExecutionResult.Text("gate opened");
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

    private static async Task SendAndWaitIdleAsync(
        AgentRuntime runtime,
        IConversationProjection projection,
        SessionId id,
        string text,
        PermissionMode? mode = null,
        int expectedReplies = 1,
        bool expectFailure = false)
    {
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), id, 0, text, Mode: mode), CancellationToken.None);
        Assert.True(ack.Accepted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (projection.Snapshot() is var snapshot
               && (snapshot.State.Phase != TurnPhase.Idle
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

    [Fact]
    public async Task TextOnlyStep_WithQueuedSteer_ContinuesSameTurn()
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
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "steer one"), CancellationToken.None);
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "steer two"), CancellationToken.None);
        release.TrySetResult();

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle);

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

        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle);
        Assert.Single(harness.Model.Requests);
        release.TrySetResult();

        // The held queue is untouched until new input arrives; FIFO makes the OLDEST entry
        // drive the turn and the newcomer waits behind it.
        await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "fresh"), CancellationToken.None);
        await WaitForAsync(() => harness.Model.Requests.Count >= 2);
        Assert.Contains("held-a", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("fresh", RequestTexts(harness.Model.Requests[1]), StringComparison.Ordinal);

        // held-b joins the held-a turn at the tool boundary; fresh continues the same turn
        // at the text-only finish boundary.
        await WaitForAsync(() => harness.Model.Requests.Count >= 3);
        Assert.Contains("held-b", RequestTexts(harness.Model.Requests[2]), StringComparison.Ordinal);
        await WaitForAsync(() => projection.Snapshot().State.Phase == TurnPhase.Idle && harness.Model.Requests.Count >= 4);
        Assert.Contains("fresh", RequestTexts(harness.Model.Requests[3]), StringComparison.Ordinal);
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
            Assert.DoesNotContain("<output-file>", notification.Content, StringComparison.Ordinal);
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
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Plan), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        for (var i = 1; i <= 5; i++)
        {
            await SendAndWaitIdleAsync(runtime, projection, session.Id, $"question {i}", mode: PermissionMode.Plan, expectedReplies: i);
        }

        Assert.Contains("Plan mode is active.", RequestTexts(harness.Model.Requests[0]), StringComparison.Ordinal);
        for (var i = 1; i < 5; i++)
        {
            // Exactly the turn-1 full reminder carried by history; no fresh attachment yet.
            Assert.Equal(1, RequestTexts(harness.Model.Requests[i]).Split("Plan mode is active.").Length - 1);
            Assert.DoesNotContain("Plan mode still active", RequestTexts(harness.Model.Requests[i]), StringComparison.Ordinal);
        }
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "question 6", mode: PermissionMode.Plan, expectedReplies: 6);
        var sixth = RequestTexts(harness.Model.Requests[5]);
        Assert.Contains("Plan mode still active", sixth, StringComparison.Ordinal);
        // The 6th attachment is sparse: history's full reminder stays the only Plan Workflow copy.
        Assert.Equal(1, sixth.Split("## Plan Workflow").Length - 1);
    }

    [Fact]
    public async Task PlanModeExit_CommitsOneShotExitedReminder()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            ToolCall("ExitPlanMode", new { plan = "1. Write." }, new ToolCallId("call-plan")),
            Text("implementing"),
        ];
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Plan), CancellationToken.None);
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
        Assert.Contains(messages.SelectMany(m => m.Message.Parts).OfType<AttachmentPart>(),
            p => p.Content.Contains("## Exited Plan Mode"));
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
            Text("summary one"),
            Text("answer three"),
            Text("summary two"),
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
