using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Permissions;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Kernel.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class RuntimeRecoveryTests
{
    private static IReadOnlyList<ModelStreamEvent> StopText(string text) =>
        [new ModelTextDelta(text), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop)];

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

    private static IReadOnlyList<ModelStreamEvent> TodoReadCall(int index) =>
    [
        new ModelToolCallReady(new ToolCall(new ToolCallId($"todo-{index}"), "TodoRead",
            JsonSerializer.SerializeToElement(new { }))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls),
    ];

    private static IReadOnlyList<ModelStreamEvent> Overflow() =>
        Failure(new ModelFailure(ModelFailureKind.ContextOverflow, "prompt too long"));

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

        Assert.Single(harness.Model.Requests);
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

    [Theory]
    [InlineData("invalid request: `thinking` block signature cannot be modified")]
    [InlineData("signature in thinking block does not match")]
    [InlineData("invalid request: thinking block is too large")]
    public async Task GenericProviderSignatureRejection_FailsWithoutRetryOrChangingReasoning(string rejection)
    {
        await using var harness = ScriptedCallHarness.Create(
            ReasoningAnswer("internal thoughts", "answer"),
            Failure(SignatureRejection(rejection)),
            StopText("later answer"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "turn one");
        await using var store = harness.OpenStore();
        var before = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var original = Assert.Single(before.SelectMany(m => m.Message.Parts).OfType<ReasoningPart>());

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "turn two", expectedReplies: 0);

        Assert.Equal(2, harness.Model.Requests.Count);
        Assert.Equal(TurnOutcome.Failed, runtime.GetSessionActivity(session.Id)!.LastOutcome);
        var after = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        Assert.Equal(original, Assert.Single(after.SelectMany(m => m.Message.Parts).OfType<ReasoningPart>()));
        Assert.Single(after, m => m.Message.Role == MessageRole.Assistant);
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal(rejection, Assert.Single(events.Select(e => e.Event).OfType<TurnFailedEvent>()).Error);
        Assert.Equal(TurnOutcome.Failed, events.Select(e => e.Event).OfType<TurnCompletedEvent>().Last().Outcome);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "turn three", expectedReplies: 2);

        Assert.Equal(3, harness.Model.Requests.Count);
        var reasoning = Assert.Single(harness.Model.Requests[2].Messages.SelectMany(m => m.Content).OfType<InputReasoningBlock>());
        Assert.Equal(original.Text, reasoning.Text);
        Assert.Equal(original.ProviderSignature, reasoning.Signature);
    }

    [Fact]
    public async Task FailedTurnReason_RemainsVisibleAfterResumeWithoutLeavingSessionBusy()
    {
        const string failure = "invalid request: signature in thinking block does not match";
        await using var harness = ScriptedCallHarness.Create(Failure(SignatureRejection(failure)));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "fail", expectedReplies: 0);
        await runtime.DisposeAsync();

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var reopened = resumed.Subscribe(session.Id);

        Assert.Equal(TurnPhase.Idle, reopened.Snapshot().State.Phase);
        Assert.Contains(reopened.Snapshot().Rows.OfType<SystemNoticeRow>(), row => row.Text.Contains(failure, StringComparison.Ordinal));
        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal(failure, Assert.Single(events.Select(e => e.Event).OfType<TurnFailedEvent>()).Error);
    }

    [Fact]
    public async Task EmptyLengthFinish_SendsContinueWithoutSuspiciousEmptyRetry()
    {
        await using var harness = ScriptedCallHarness.Create(
            [new ModelStreamFinished(ModelFinishReason.Length)],
            StopText("continued answer"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "keep writing");

        Assert.Equal(2, harness.Model.Requests.Count);
        Assert.DoesNotContain(harness.Model.Requests[0].Messages.SelectMany(m => m.Content).OfType<InputTextBlock>(),
            p => p.Text == OutputContinuation.ContinuePromptText);
        Assert.Single(harness.Model.Requests[1].Messages.SelectMany(m => m.Content).OfType<InputTextBlock>(),
            p => p.Text == OutputContinuation.ContinuePromptText);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var assistant = Assert.Single(messages, m => m.Message.Role == MessageRole.Assistant).Message;
        Assert.Equal("continued answer", Assert.Single(assistant.Parts.OfType<TextPart>()).Text);
    }

    [Fact]
    public async Task AssistantOriginAndOpaqueReasoning_SurvivePersistenceAndResume()
    {
        await using var harness = ScriptedCallHarness.Create(
            [new ModelReasoningEnded("signed-only"),
                new ModelReasoningEnded(RedactedData: "redacted-data"),
                new ModelReasoningEnded(EncryptedContent: "encrypted-content", ProviderItemId: "reasoning-item"),
                new ModelTextDelta("answer"), new ModelStreamFinished(ModelFinishReason.Stop)],
            StopText("resumed answer"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first");
        await runtime.DisposeAsync();

        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var original = Assert.Single(messages, m => m.Message.Role == MessageRole.Assistant).Message;
        Assert.Equal("scripted", original.ProviderId);
        Assert.Equal("test-model", original.ModelId);
        var reasoning = original.Parts.OfType<ReasoningPart>().ToArray();
        Assert.Equal(3, reasoning.Length);
        Assert.Equal("signed-only", reasoning[0].ProviderSignature);
        Assert.Equal("redacted-data", reasoning[1].RedactedData);
        Assert.Equal("encrypted-content", reasoning[2].EncryptedContent);
        Assert.Equal("reasoning-item", reasoning[2].ProviderItemId);

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = resumed.Subscribe(session.Id);
        await SendAndWaitIdleAsync(resumed, resumedProjection, session.Id, "second", expectedReplies: 2);

        var historyAssistant = Assert.Single(harness.Model.Requests[1].Messages, m =>
            m.Role == MessageRole.Assistant && m.Content.OfType<InputReasoningBlock>().Any());
        Assert.Equal("scripted", historyAssistant.ProviderId);
        Assert.Equal("test-model", historyAssistant.ModelId);
        var projected = historyAssistant.Content.OfType<InputReasoningBlock>().ToArray();
        Assert.Equal(3, projected.Length);
        Assert.Equal("signed-only", projected[0].Signature);
        Assert.Equal("redacted-data", projected[1].RedactedData);
        Assert.Equal("encrypted-content", projected[2].EncryptedContent);
        Assert.Equal("reasoning-item", projected[2].ProviderItemId);
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
        Assert.DoesNotContain(CompactionSelector.TruncationMarkerText, summary.Content);
        Assert.Contains(CompactionPlanner.SuppressFollowupInstruction, summary.Content);
        Assert.Contains("summary text", summary.Content);
        var firstSummaryRequest = harness.Model.Requests[1];
        Assert.Equal(MessageRole.System, firstSummaryRequest.Messages[0].Role);
        Assert.NotEmpty(firstSummaryRequest.Tools);
        Assert.Contains(firstSummaryRequest.Messages, m => m.Role == MessageRole.Assistant
            && m.Content.OfType<InputTextBlock>().Any(p => p.Text == "answer"));
        Assert.Contains(harness.Model.Requests[2].Messages.SelectMany(m => m.Content).OfType<InputTextBlock>(),
            p => p.Text == CompactionSelector.TruncationMarkerText);
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
    public async Task ReactiveOverflow_RepeatedForSameStep_DoesNotCompactEndlessly()
    {
        await using var harness = ScriptedCallHarness.Create(
            StopText("previous answer"), TodoReadCall(0), Overflow(), StopText("summary"), Overflow());
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first");

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "second", expectedReplies: 0);

        Assert.Equal(5, harness.Model.Requests.Count);
        Assert.Equal(TurnOutcome.Failed, runtime.GetSessionActivity(session.Id)!.LastOutcome);
        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Single(events.Select(e => e.Event).OfType<CompactBoundaryAppliedEvent>());
    }

    [Fact]
    public async Task ReactiveOverflow_NewToolStepsEachAllowRecoveryBeyondThreeTotalCompactions()
    {
        var calls = new List<IReadOnlyList<ModelStreamEvent>> { StopText("previous answer"), TodoReadCall(0) };
        for (var cycle = 0; cycle < 4; cycle++)
        {
            calls.Add(Overflow());
            calls.Add(StopText($"summary {cycle}"));
            if (cycle < 3)
            {
                for (var tool = 0; tool < 3; tool++)
                    calls.Add(TodoReadCall(cycle * 3 + tool + 1));
            }
            else
            {
                calls.Add(StopText("completed after four recoveries"));
            }
        }
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first");

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "second", expectedReplies: 0);

        Assert.Equal(calls.Count, harness.Model.Requests.Count);
        Assert.Equal(TurnOutcome.Completed, runtime.GetSessionActivity(session.Id)!.LastOutcome);
        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal(4, events.Select(e => e.Event).OfType<CompactBoundaryAppliedEvent>().Count());
    }

    [Fact]
    public async Task ReactiveOverflow_AfterLengthContinuation_AllowsRecoveryForNewStep()
    {
        await using var harness = ScriptedCallHarness.Create(
            StopText("previous answer"), TodoReadCall(0), Overflow(), StopText("first summary"),
            LengthText("truncated answer"), Overflow(), StopText("second summary"), StopText("completed answer"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first");

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "second", expectedReplies: 0);

        Assert.Equal(8, harness.Model.Requests.Count);
        Assert.Equal(TurnOutcome.Completed, runtime.GetSessionActivity(session.Id)!.LastOutcome);
        Assert.Contains(harness.Model.Requests[5].Messages.SelectMany(m => m.Content).OfType<InputTextBlock>(),
            p => p.Text == OutputContinuation.ContinuePromptText);
        await using var store = harness.OpenStore();
        var events = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal(2, events.Select(e => e.Event).OfType<CompactBoundaryAppliedEvent>().Count());
    }

    [Theory]
    [InlineData(ModelFinishReason.Length, false)]
    [InlineData(ModelFinishReason.Other, true)]
    public async Task ManualCompaction_CompletedContextPressureReselectsWithoutPersistingPartial(ModelFinishReason reason, bool exceeded)
    {
        await using var harness = ScriptedCallHarness.Create(StopText("answer"),
            [new ModelStreamFinished(reason, ContextWindowExceeded: exceeded)], StopText("summary"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "question");

        var ack = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);

        Assert.True(ack.Accepted);
        Assert.Equal(3, harness.Model.Requests.Count);
        Assert.Contains(harness.Model.Requests[2].Messages.SelectMany(message => message.Content).OfType<InputTextBlock>(),
            block => block.Text == CompactionSelector.TruncationMarkerText);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    public async Task ManualCompaction_RecentPartialReadPreservesInputRangeAndNumberedOutput(int offset, int limit)
    {
        var input = JsonSerializer.SerializeToElement(new { file_path = "partial.txt", offset, limit });
        IReadOnlyList<ModelStreamEvent> read = [
            new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), "Read", input)),
            new ModelStreamFinished(ModelFinishReason.ToolCalls)];
        await using var harness = ScriptedCallHarness.Create(read, StopText("read completed"), StopText("summary"));
        await using var runtime = harness.BuildRuntime(new ReadTool());
        await File.WriteAllTextAsync(Path.Combine(harness.Root, "partial.txt"), "first\nsecond\nthird\nfourth\nfifth");
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "read part of the file", expectedReplies: 2);
        var readOutput = Assert.Single(harness.Model.Requests[1].Messages.SelectMany(message => message.Content)
            .OfType<InputToolResultBlock>()).Content;

        var ack = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);

        Assert.True(ack.Accepted);
        Assert.Equal(3, harness.Model.Requests.Count);
        await using var store = harness.OpenStore();
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var reminder = Assert.Single(messages.SelectMany(message => message.Message.Parts).OfType<AttachmentPart>(),
            part => part.Kind == AttachmentKind.ResumeReferencedContext);
        Assert.Equal($"<system-reminder>\nCalled the Read tool with the following input: {input.GetRawText()}\nResult of calling the Read tool:\n{readOutput}\n</system-reminder>", reminder.Content);
        Assert.Contains(offset == 0 ? "0\tfirst" : "3\tthird", reminder.Content);
        Assert.DoesNotContain("fifth", reminder.Content);
    }

    [Fact]
    public async Task AutoCompaction_TransientFailureRetriesOriginalSelectionUpToThreeTimes()
    {
        var transient = new ModelFailure(ModelFailureKind.NetworkError, "connection interrupted");
        await using var fixture = await CompactFixture.CreateAsync([
            [new ModelTextDelta("discard one"), new ModelStreamFailed(transient)],
            [new ModelTextDelta("discard two"), new ModelStreamFailed(transient)], StopText("summary")]);

        Assert.True(await fixture.CompactAsync(CompactTrigger.Auto));
        Assert.Equal(3, fixture.Model.Requests.Count);
        Assert.Equal(JsonSerializer.Serialize(fixture.Model.Requests[0]), JsonSerializer.Serialize(fixture.Model.Requests[1]));
        Assert.Equal(JsonSerializer.Serialize(fixture.Model.Requests[0]), JsonSerializer.Serialize(fixture.Model.Requests[2]));
        Assert.DoesNotContain(fixture.Model.Requests[2].Messages.SelectMany(message => message.Content).OfType<InputTextBlock>(), block => block.Text.Contains("discard"));
    }

    [Fact]
    public async Task AutoCompaction_ThreeTransientFailuresStopWithoutMutation()
    {
        var failure = new ModelFailure(ModelFailureKind.NetworkError, "connection interrupted");
        IReadOnlyList<ModelStreamEvent> broken = [new ModelTextDelta("discard"), new ModelStreamFailed(failure)];
        await using var fixture = await CompactFixture.CreateAsync([broken, broken, broken]);

        Assert.False(await fixture.CompactAsync(CompactTrigger.Auto));
        Assert.Equal(3, fixture.Model.Requests.Count);
        Assert.Equal(JsonSerializer.Serialize(fixture.Original), JsonSerializer.Serialize(fixture.History));
        Assert.Equal(JsonSerializer.Serialize(fixture.Original), JsonSerializer.Serialize(
            (await fixture.Store.LoadMessagesAsync(fixture.Id, CancellationToken.None)).Select(message => message.Message)));
    }

    [Fact]
    public async Task AutoCompaction_ContextExhaustionAndToolDenialDoNotOuterRetry()
    {
        await using (var context = await CompactFixture.CreateAsync([Overflow()]))
        {
            Assert.False(await context.CompactAsync(CompactTrigger.Auto));
            Assert.Single(context.Model.Requests);
        }
        await using var tools = await CompactFixture.CreateAsync([TodoReadCall(99)]);
        Assert.False(await tools.CompactAsync(CompactTrigger.Auto));
        Assert.Single(tools.Model.Requests);
        Assert.Equal("Tool use is not allowed during compaction", tools.Error);
    }

    [Fact]
    public async Task Compaction_MediaSizeFailureRetriesOnceWithRequestLocalPlaceholders()
    {
        await using var fixture = await CompactFixture.CreateAsync([
            [new ModelTextDelta("discarded partial"), new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest,
                "image too large", Phase: ModelFailurePhase.ResponseBody))], StopText("summary")], media: true);

        Assert.True(await fixture.CompactAsync(CompactTrigger.Manual));

        Assert.Equal(2, fixture.Model.Requests.Count);
        Assert.Contains(fixture.Model.Requests[0].Messages.SelectMany(message => message.Content), block => block is InputImageBlock);
        Assert.DoesNotContain(fixture.Model.Requests[1].Messages.SelectMany(message => message.Content), block => block is InputImageBlock);
        Assert.Contains(fixture.Model.Requests[1].Messages.SelectMany(message => message.Content).OfType<InputTextBlock>(), block => block.Text == "[image]");
        Assert.DoesNotContain(fixture.History.SelectMany(message => message.Parts).OfType<AttachmentPart>(), part => part.Content.Contains("discarded partial"));
    }

    [Theory]
    [InlineData(CompactTrigger.Manual, ModelFailureKind.InvalidRequest)]
    [InlineData(CompactTrigger.Auto, ModelFailureKind.InvalidRequest)]
    [InlineData(CompactTrigger.Auto, ModelFailureKind.ProviderError)]
    public async Task Compaction_SecondMediaFailureStopsWithoutMutatingHistory(CompactTrigger trigger, ModelFailureKind kind)
    {
        var mediaFailure = Failure(new ModelFailure(kind, "media payload rejected", "media_payload_too_large"));
        await using var fixture = await CompactFixture.CreateAsync([mediaFailure, mediaFailure], media: true);

        Assert.False(await fixture.CompactAsync(trigger));
        Assert.Equal(2, fixture.Model.Requests.Count);
        Assert.Contains(fixture.Model.Requests[0].Messages.SelectMany(message => message.Content), block => block is InputImageBlock);
        Assert.DoesNotContain(fixture.Model.Requests[1].Messages.SelectMany(message => message.Content), block => block is InputImageBlock);
        Assert.Equal(JsonSerializer.Serialize(fixture.Original), JsonSerializer.Serialize(fixture.History));
        Assert.Equal(JsonSerializer.Serialize(fixture.Original), JsonSerializer.Serialize(
            (await fixture.Store.LoadMessagesAsync(fixture.Id, CancellationToken.None)).Select(message => message.Message)));
    }

    [Theory]
    [InlineData(null, 20000)]
    [InlineData(8000, 8000)]
    [InlineData(64000, 20000)]
    public async Task Compaction_OutputCapUsesModelLimitNotUserOrLocalBudget(int? modelLimit, int expected)
    {
        await using var fixture = await CompactFixture.CreateAsync([StopText("summary")], modelLimit);

        Assert.True(await fixture.CompactAsync(CompactTrigger.Manual));
        Assert.Equal(expected, Assert.Single(fixture.Model.Requests).Options.MaxOutputTokens);
    }

    [Fact]
    public async Task Compaction_FailedPersistenceDoesNotReplaceActiveHistory()
    {
        await using var fixture = await CompactFixture.CreateAsync([StopText("summary")]);
        fixture.Failing.FailReplace = true;

        Assert.False(await fixture.CompactAsync(CompactTrigger.Manual));
        Assert.Equal(JsonSerializer.Serialize(fixture.Original), JsonSerializer.Serialize(fixture.History));
        Assert.Equal(JsonSerializer.Serialize(fixture.Original), JsonSerializer.Serialize(
            (await fixture.Store.LoadMessagesAsync(fixture.Id, CancellationToken.None)).Select(message => message.Message)));
    }

    [Fact]
    public async Task AutoCompaction_PostPersistenceEventFailureDoesNotRegenerateStaleHistory()
    {
        await using var fixture = await CompactFixture.CreateAsync([StopText("summary")]);
        fixture.Failing.FailBoundary = true;

        Assert.True(await fixture.CompactAsync(CompactTrigger.Auto));
        Assert.Single(fixture.Model.Requests);
        Assert.Contains(fixture.History.SelectMany(message => message.Parts).OfType<AttachmentPart>(), part => part.Kind == AttachmentKind.CompactSummary);
        Assert.Equal(JsonSerializer.Serialize(fixture.History), JsonSerializer.Serialize(
            (await fixture.Store.LoadMessagesAsync(fixture.Id, CancellationToken.None)).Select(message => message.Message)));
    }

    private sealed class CompactFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "alcoler-compact-" + Guid.NewGuid().ToString("N"));
        private SessionRuntime _runtime = null!;
        public SqliteSessionStore Store { get; private set; } = null!;
        public FailingStore Failing { get; private set; } = null!;
        public ScriptedCallModel Model { get; private set; } = null!;
        public SessionId Id { get; } = StrongId.NewSessionId();
        public IReadOnlyList<ConversationMessage> Original { get; private set; } = [
            Context.TestMessages.UserText("first"), Context.TestMessages.AssistantText("one"),
            Context.TestMessages.UserText("second"), Context.TestMessages.AssistantText("two")];
        public IReadOnlyList<ConversationMessage> History => ((Alcoler.Kernel.History.MessageHistory)typeof(SessionRuntime)
            .GetField("_history", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_runtime)!).Snapshot();
        public string? Error => (string?)typeof(SessionRuntime).GetProperty("LastTurnError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(_runtime);

        public static async Task<CompactFixture> CreateAsync(IReadOnlyList<IReadOnlyList<ModelStreamEvent>> calls, int? modelLimit = null, bool media = false)
        {
            var fixture = new CompactFixture();
            Directory.CreateDirectory(fixture._root);
            fixture.Store = new SqliteSessionStore(Path.Combine(fixture._root, "db.sqlite"));
            await fixture.Store.InitializeAsync(CancellationToken.None);
            fixture.Failing = new FailingStore(fixture.Store);
            fixture.Model = new ScriptedCallModel(calls);
            var model = new DescriptorModel(fixture.Model, modelLimit);
            var record = new SessionRecord(fixture.Id, fixture._root, "test", PermissionMode.Build,
                new ModelSelection("scripted", "test-model", new ModelCallOptions(MaxOutputTokens: 17)), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await fixture.Store.UpsertSessionAsync(record, CancellationToken.None);
            var artifacts = new FileArtifactStore(Path.Combine(fixture._root, "artifacts"));
            if (media)
            {
                var artifact = await artifacts.SaveAsync(fixture.Id, "image.png", "image/png", new byte[] { 1, 2, 3 }, CancellationToken.None);
                fixture.Original = [fixture.Original[0] with
                {
                    Parts = [.. fixture.Original[0].Parts, new FilePart(StrongId.NewPartId(), artifact.Uri, "image/png", "image.png")],
                }, .. fixture.Original.Skip(1)];
            }
            await fixture.Store.AppendMessagesAsync(fixture.Id, fixture.Original, 0, CancellationToken.None);
            fixture._runtime = await SessionRuntime.CreateAsync(record, new SessionRuntimeDeps(fixture.Failing, _ => Task.FromResult<IChatModel>(model),
                new ToolRegistry([]), new WorkspaceToolEnvironment(fixture._root, artifacts), PermissionRuleset.Empty,
                TimeProvider.System, NullLogger<SessionRuntime>.Instance, new SessionRuntimeOptions(), artifacts), CancellationToken.None);
            return fixture;
        }

        public async Task<bool> CompactAsync(CompactTrigger trigger)
        {
            var method = typeof(SessionRuntime).GetMethod("ExecuteCompactionAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            return await (Task<bool>)method.Invoke(_runtime, [trigger, null, CancellationToken.None, null, null, null])!;
        }

        public async ValueTask DisposeAsync()
        {
            await _runtime.DisposeAsync();
            await Store.DisposeAsync();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private sealed class DescriptorModel(ScriptedCallModel inner, int? limit) : IChatModel
        {
            public ModelDescriptor Descriptor { get; } = inner.Descriptor with { Properties = new ModelProperties(ContextWindowTokens: 2000, DefaultMaxOutputTokens: limit) };
            public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken) => inner.StreamAsync(request, cancellationToken);
        }
    }

    private sealed class FailingStore(ISessionStore inner) : ISessionStore
    {
        public bool FailReplace { get; set; }
        public bool FailBoundary { get; set; }
        public ValueTask InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public ValueTask UpsertSessionAsync(SessionRecord record, CancellationToken ct) => inner.UpsertSessionAsync(record, ct);
        public ValueTask<bool> TryUpdateSessionTitleAsync(SessionId id, string title, SessionTitleSource source, CancellationToken ct) => inner.TryUpdateSessionTitleAsync(id, title, source, ct);
        public ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken ct) => inner.GetSessionAsync(id, ct);
        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? root, int limit, CancellationToken ct) => inner.ListSessionsAsync(root, limit, ct);
        public ValueTask DeleteSessionAsync(SessionId id, CancellationToken ct) => inner.DeleteSessionAsync(id, ct);
        public ValueTask AppendMessagesAsync(SessionId id, IReadOnlyList<ConversationMessage> messages, long start, CancellationToken ct) => inner.AppendMessagesAsync(id, messages, start, ct);
        public ValueTask ReplaceMessagesAsync(SessionId id, IReadOnlyList<ConversationMessage> messages, CancellationToken ct) => FailReplace
            ? ValueTask.FromException(new IOException("replacement failed")) : inner.ReplaceMessagesAsync(id, messages, ct);
        public ValueTask<IReadOnlyList<StoredMessage>> LoadMessagesAsync(SessionId id, CancellationToken ct) => inner.LoadMessagesAsync(id, ct);
        public ValueTask AppendDurableEventsAsync(IReadOnlyList<SequencedEvent> events, CancellationToken ct) => FailBoundary && events.Any(entry => entry.Event is CompactBoundaryAppliedEvent)
            ? ValueTask.FromException(new IOException("boundary event failed")) : inner.AppendDurableEventsAsync(events, ct);
        public ValueTask<IReadOnlyList<SequencedEvent>> LoadDurableEventsAsync(SessionId id, CancellationToken ct) => inner.LoadDurableEventsAsync(id, ct);
        public ValueTask<long> GetNextMessageSequenceAsync(SessionId id, CancellationToken ct) => inner.GetNextMessageSequenceAsync(id, ct);
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
        // A fresh submit can still observe the previous turn's Idle before this turn starts;
        // the committed user row is the monotonic proof the turn actually ran.
        var baselineUsers = projection.Snapshot().Rows.OfType<UserMessageRow>().Count();
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), id, 0, text), CancellationToken.None);
        Assert.True(ack.Accepted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (projection.Snapshot() is var snapshot
               && (snapshot.State.Phase != TurnPhase.Idle
                   || snapshot.Rows.OfType<UserMessageRow>().Count() <= baselineUsers
                   || (expectedReplies > 0 && snapshot.Rows.OfType<AssistantMessageRow>().Count() < expectedReplies)))
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
