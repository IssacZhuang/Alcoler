using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class RuntimeRecoveryTests
{
    private static readonly ToolCallId TodoCallId = new("call-todo");

    private static IReadOnlyList<ModelStreamEvent> StopText(string text) =>
        [new ModelTextDelta(text), new ModelStreamFinished(ModelFinishReason.Stop)];

    private static IReadOnlyList<ModelStreamEvent> LengthText(string text) =>
        [new ModelTextDelta(text), new ModelStreamFinished(ModelFinishReason.Length)];

    private static IReadOnlyList<ModelStreamEvent> EmptyFinish() =>
        [new ModelStreamFinished(ModelFinishReason.Stop)];

    // ZCode isSuspiciousEmptyModelResult shape: non-stop finish, zero usage, no content.
    private static IReadOnlyList<ModelStreamEvent> SuspiciousEmptyFinish() =>
        [new ModelStreamFinished(ModelFinishReason.Other)];

    private static IReadOnlyList<ModelStreamEvent> Failure(ModelFailure failure) =>
        [new ModelStreamFailed(failure)];

    private static IReadOnlyList<ModelStreamEvent> ReasoningAnswer(string reasoning, string text) =>
    [
        new ModelReasoningDelta(reasoning),
        new ModelReasoningEnded("signature-1"),
        new ModelTextDelta(text),
        new ModelStreamFinished(ModelFinishReason.Stop),
    ];

    private static IReadOnlyList<ModelStreamEvent> TodoWriteCall() =>
    [
        new ModelTextDelta("Updating the todo list."),
        new ModelToolInputStarted(TodoCallId, "TodoWrite"),
        new ModelToolInputDelta(TodoCallId, JsonSerializer.Serialize(new { todos = new[] { new { content = "keep going" } } })),
        new ModelToolInputEnded(TodoCallId),
        new ModelToolCallReady(new ToolCall(TodoCallId, "TodoWrite",
            JsonSerializer.SerializeToElement(new { todos = new[] { new { content = "keep going" } } }))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls),
    ];

    private static ModelFailure SignatureRejection(string message) =>
        new(ModelFailureKind.InvalidRequest, message);

    [Fact]
    public async Task LengthFinish_ContinuesViaRequestLocalPromptAndResumesOutput()
    {
        await using var harness = ScriptedCallHarness.Create(
            LengthText("part one "),
            StopText("part two"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "write a long essay");

        Assert.Equal(2, harness.Model.Requests.Count);
        var continuation = harness.Model.Requests[1].Messages
            .SelectMany(m => m.Content.OfType<InputTextBlock>())
            .SingleOrDefault(t => t.Text == OutputContinuation.ContinuePromptText);
        Assert.NotNull(continuation);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Contains(messages, m => m.Message.Parts.OfType<TextPart>().Any(p => p.Text.Contains("part one")));
        Assert.Contains(messages, m => m.Message.Parts.OfType<TextPart>().Any(p => p.Text.Contains("part two")));
        // The Continue prompt is request-local and never persisted into history.
        Assert.DoesNotContain(messages, m =>
            m.Message.Role == MessageRole.User &&
            m.Message.Parts.OfType<TextPart>().Any(p => p.Text == OutputContinuation.ContinuePromptText));
    }

    [Fact]
    public async Task LengthFinish_FourthTruncation_FailsTurnWithOutputLimitError()
    {
        await using var harness = ScriptedCallHarness.Create(
            LengthText("one "), LengthText("two "), LengthText("three "), LengthText("four"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "keep writing", expectedReplies: 0);

        // Three continuations, then the fourth truncation fails the turn with the
        // output-limit error instead of a fifth request or a silent finish.
        Assert.Equal(4, harness.Model.Requests.Count);
        Assert.Equal(TurnPhase.Idle, projection.Snapshot().State.Phase);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Contains(messages, m => m.Message.Parts.OfType<TextPart>().Any(p => p.Text.Contains("four")));
    }

    [Fact]
    public async Task EmptyCompletion_StopFinish_CommitsEmptyAssistantAndCompletes()
    {
        await using var harness = ScriptedCallHarness.Create(EmptyFinish());
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "say something", expectedReplies: 1);

        Assert.Equal(1, harness.Model.Requests.Count);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Contains(messages, m => m.Message.Role == MessageRole.Assistant && m.Message.Parts.Count == 0);
    }

    [Fact]
    public async Task SuspiciousEmptyCompletion_IsRetriedOnceAndSecondAttemptCommits()
    {
        await using var harness = ScriptedCallHarness.Create(
            SuspiciousEmptyFinish(),
            StopText("recovered"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "say something");

        Assert.Equal(2, harness.Model.Requests.Count);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Contains(messages, m => m.Message.Parts.OfType<TextPart>().Any(p => p.Text.Contains("recovered")));
    }

    [Fact]
    public async Task SuspiciousEmptyCompletion_RetryAlsoEmpty_FailsTurnWithoutAssistant()
    {
        await using var harness = ScriptedCallHarness.Create(
            SuspiciousEmptyFinish(),
            SuspiciousEmptyFinish());
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "say something", expectedReplies: 0);

        Assert.Equal(2, harness.Model.Requests.Count);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(messages, m => m.Message.Role == MessageRole.Assistant);
    }

    [Fact]
    public async Task SignatureRejection_StripsReasoningFromHistoryAndRetries()
    {
        await using var harness = ScriptedCallHarness.Create(
            ReasoningAnswer("internal thoughts", "answer"),
            Failure(SignatureRejection("invalid request: `thinking` block signature cannot be modified")),
            StopText("repaired answer"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "turn one");
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "turn two", expectedReplies: 2);

        Assert.Equal(3, harness.Model.Requests.Count);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.DoesNotContain(messages, m => m.Message.Parts.OfType<ReasoningPart>().Any());
        Assert.Contains(messages, m => m.Message.Parts.OfType<TextPart>().Any(p => p.Text.Contains("repaired answer")));
    }

    [Fact]
    public async Task SignatureReparation_IsAllowedAgainInLaterTurns()
    {
        await using var harness = ScriptedCallHarness.Create(
            ReasoningAnswer("thoughts", "first"),
            Failure(SignatureRejection("signature in thinking block does not match")),
            StopText("second"),
            ReasoningAnswer("thoughts again", "third"),
            Failure(SignatureRejection("signature in thinking block does not match")),
            StopText("fourth"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "one");
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "two", expectedReplies: 2);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "three", expectedReplies: 3);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "four", expectedReplies: 4);

        Assert.Equal(6, harness.Model.Requests.Count);
    }

    [Fact]
    public async Task SignatureRejection_HappensAtMostOncePerTurn()
    {
        await using var harness = ScriptedCallHarness.Create(
            ReasoningAnswer("thoughts", "first"),
            Failure(SignatureRejection("signature in thinking block does not match")),
            TodoWriteCall(),
            Failure(SignatureRejection("signature in thinking block does not match")),
            StopText("must not be requested"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "one");
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "two", expectedReplies: 2);

        Assert.Equal(4, harness.Model.Requests.Count);
    }

    [Fact]
    public async Task SignatureRejection_WideMessage_DoesNotRepairHistory()
    {
        await using var harness = ScriptedCallHarness.Create(
            ReasoningAnswer("internal thoughts", "answer"),
            Failure(SignatureRejection("invalid request: thinking block is too large")));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "one");
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "two", expectedReplies: 1);

        Assert.Equal(2, harness.Model.Requests.Count);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Contains(messages, m => m.Message.Parts.OfType<ReasoningPart>().Any());
    }

    [Fact]
    public async Task ManualCompaction_PromptTooLong_ReselectsTruncatedSummaryAndRetries()
    {
        await using var harness = ScriptedCallHarness.Create(
            StopText("answer"),
            Failure(new ModelFailure(
                ModelFailureKind.ContextOverflow,
                "prompt is too long: 250000 tokens > 200000 maximum",
                DeclaredTokenGap: 4_000)),
            StopText("summary text"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "question");

        var ack = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);

        Assert.True(ack.Accepted);
        Assert.Equal(3, harness.Model.Requests.Count);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var summary = messages.SelectMany(m => m.Message.Parts)
            .OfType<AttachmentPart>()
            .Single(p => p.Kind == AttachmentKind.CompactSummary);
        Assert.StartsWith(CompactionPlanner.CompactContinuationHeader, summary.Content);
        Assert.Contains(CompactionSelector.TruncationMarkerText, summary.Content);
        Assert.Contains("summary text", summary.Content);
    }

    [Fact]
    public async Task ManualCompaction_NonOverflowFailure_DoesNotReselect()
    {
        await using var harness = ScriptedCallHarness.Create(
            StopText("answer"),
            Failure(new ModelFailure(ModelFailureKind.AuthenticationFailed, "bad key")));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "question");

        var ack = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);

        Assert.False(ack.Accepted);
        Assert.Equal(2, harness.Model.Requests.Count);
    }

    [Fact]
    public async Task MaxOutputTokens_BaselinesTo32KWhenUndeclared()
    {
        await using var harness = ScriptedCallHarness.Create(StopText("hi"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "hello");

        Assert.Equal(32_000, Assert.Single(harness.Model.Requests).Options.MaxOutputTokens);
    }

    private static async Task SendAndWaitIdleAsync(
        AgentRuntime runtime,
        IConversationProjection projection,
        SessionId id,
        string text,
        int expectedReplies = 1)
    {
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), id, 0, text), CancellationToken.None);
        Assert.True(ack.Accepted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (projection.Snapshot() is var snapshot
               && (snapshot.State.Phase != TurnPhase.Idle
                   || (expectedReplies > 0 && snapshot.Rows.OfType<AssistantMessageRow>().Count() < expectedReplies)))
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
