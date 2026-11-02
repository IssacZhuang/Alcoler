using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Subagents;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class RewindEndToEndTests
{
    private static IReadOnlyList<ModelStreamEvent> Reply(string text) =>
        [new ModelTextDelta(text), new ModelStreamFinished(ModelFinishReason.Stop)];

    private static string RequestText(ModelRequest request) =>
        string.Join("\n", request.Messages.SelectMany(message => message.Content.OfType<InputTextBlock>())
            .Select(block => block.Text));

    private static string[] UserTexts(IConversationProjection projection) =>
        [.. projection.Snapshot().Rows.OfType<UserMessageRow>().Select(row => row.Text)];

    [Fact]
    public async Task RewindMiddleUserMessage_RemovesLaterTurnsAndReplaysTheBranchAfterRestart()
    {
        await using var harness = ScriptedCallHarness.Create(
            Reply("answer one"),
            Reply("answer two"),
            Reply("answer three"),
            Reply("replacement answer"),
            Reply("resumed answer"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first question", expectedReplies: 1);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "second question", expectedReplies: 2);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "third question", expectedReplies: 3);

        MessageId targetId;
        MessageId firstUserId;
        MessageId firstAssistantId;
        MessageId lastStoredId;
        await using (var store = harness.OpenStore())
        {
            var stored = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
            Assert.Equal(6, stored.Count);
            var target = Assert.Single(stored, item => item.Message.Parts.OfType<TextPart>()
                .Any(part => part.Text == "second question"));
            targetId = target.Message.Id;
            firstUserId = stored[0].Message.Id;
            firstAssistantId = stored[1].Message.Id;
            lastStoredId = stored[^1].Message.Id;

            var ack = await runtime.SubmitAsync(
                new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, targetId),
                CancellationToken.None);

            Assert.True(ack.Accepted);
        }

        await WaitForAsync(
            () => UserTexts(projection).SequenceEqual(["first question"]),
            "the rewound message and later rows were not removed");
        Assert.All(projection.Snapshot().Rows.OfType<UserMessageRow>(), row => Assert.True(row.CanRewind));

        await using (var store = harness.OpenStore())
        {
            Assert.Equal(6, (await store.LoadMessagesAsync(session.Id, CancellationToken.None)).Count);
            var record = Assert.IsType<SessionRecord>(await store.GetSessionAsync(session.Id, CancellationToken.None));
            var branch = Assert.IsType<SessionBranch>(record.Branch);
            Assert.Equal(lastStoredId, branch.CutAfterMessageId);
            Assert.Equal([firstUserId, firstAssistantId], branch.KeptMessageIds);
            Assert.NotNull(branch.CutEventSequence);
        }

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "replacement question", expectedReplies: 2);
        var replacementRequest = RequestText(harness.Model.Requests[3]);
        Assert.Contains("first question", replacementRequest);
        Assert.Contains("replacement question", replacementRequest);
        Assert.DoesNotContain("second question", replacementRequest);
        Assert.DoesNotContain("third question", replacementRequest);
        Assert.DoesNotContain("answer two", replacementRequest);
        Assert.DoesNotContain("answer three", replacementRequest);

        await runtime.DisposeAsync();

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = resumed.Subscribe(session.Id);
        Assert.Equal(["first question", "replacement question"], UserTexts(resumedProjection));
        Assert.All(resumedProjection.Snapshot().Rows.OfType<UserMessageRow>(), row => Assert.True(row.CanRewind));

        await SendAndWaitIdleAsync(resumed, resumedProjection, session.Id, "after restart question", expectedReplies: 3);
        var afterRestartRequest = RequestText(harness.Model.Requests[4]);
        Assert.Contains("first question", afterRestartRequest);
        Assert.Contains("replacement question", afterRestartRequest);
        Assert.Contains("after restart question", afterRestartRequest);
        Assert.DoesNotContain("second question", afterRestartRequest);
        Assert.DoesNotContain("third question", afterRestartRequest);
        Assert.DoesNotContain("answer two", afterRestartRequest);
        Assert.DoesNotContain("answer three", afterRestartRequest);
    }

    [Fact]
    public async Task RewindRejectsAssistantAndUnknownTargets_AndCanEmptyTheActiveHistory()
    {
        await using var harness = ScriptedCallHarness.Create(
            Reply("answer one"),
            Reply("answer two"),
            Reply("fresh answer"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first question", expectedReplies: 1);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "second question", expectedReplies: 2);

        MessageId firstUserId;
        MessageId firstAssistantId;
        MessageId lastStoredId;
        await using (var store = harness.OpenStore())
        {
            var stored = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
            firstUserId = stored[0].Message.Id;
            firstAssistantId = stored[1].Message.Id;
            lastStoredId = stored[^1].Message.Id;
        }

        var assistantAck = await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, firstAssistantId),
            CancellationToken.None);
        Assert.False(assistantAck.Accepted);

        var unknownAck = await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, StrongId.NewMessageId()),
            CancellationToken.None);
        Assert.False(unknownAck.Accepted);

        var ack = await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, firstUserId),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        await WaitForAsync(
            () => projection.Snapshot().Rows.Count == 0,
            "rewinding to the first user message did not clear the conversation");
        Assert.Empty(UserTexts(projection));

        await using (var store = harness.OpenStore())
        {
            var record = Assert.IsType<SessionRecord>(await store.GetSessionAsync(session.Id, CancellationToken.None));
            var branch = Assert.IsType<SessionBranch>(record.Branch);
            Assert.Empty(branch.KeptMessageIds);
            Assert.Equal(lastStoredId, branch.CutAfterMessageId);
        }

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "fresh question", expectedReplies: 1);
        var request = RequestText(harness.Model.Requests[2]);
        Assert.Contains("fresh question", request);
        Assert.DoesNotContain("first question", request);
        Assert.DoesNotContain("second question", request);
        Assert.DoesNotContain("answer one", request);
        Assert.DoesNotContain("answer two", request);

        await runtime.DisposeAsync();
        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = resumed.Subscribe(session.Id);
        Assert.Equal(["fresh question"], UserTexts(resumedProjection));
    }

    [Fact]
    public async Task SecondRewindAfterANewTurn_DropsTheNewTurnAndKeepsOnlyTheEarlierPrefix()
    {
        await using var harness = ScriptedCallHarness.Create(
            Reply("answer one"),
            Reply("answer two"),
            Reply("answer three"),
            Reply("answer four"),
            Reply("answer five"),
            Reply("answer six"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first question", expectedReplies: 1);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "second question", expectedReplies: 2);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "third question", expectedReplies: 3);

        MessageId secondId;
        await using (var store = harness.OpenStore())
        {
            var stored = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
            secondId = Assert.Single(stored, item => item.Message.Parts.OfType<TextPart>()
                .Any(part => part.Text == "second question")).Message.Id;
        }
        Assert.True((await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, secondId),
            CancellationToken.None)).Accepted);
        await WaitForAsync(() => UserTexts(projection).SequenceEqual(["first question"]),
            "the first rewind did not remove later rows");

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "rewritten question", expectedReplies: 2);
        MessageId rewrittenId;
        await using (var store = harness.OpenStore())
        {
            var stored = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
            rewrittenId = Assert.Single(stored, item => item.Message.Parts.OfType<TextPart>()
                .Any(part => part.Text == "rewritten question")).Message.Id;
        }
        Assert.True((await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, rewrittenId),
            CancellationToken.None)).Accepted);
        await WaitForAsync(() => UserTexts(projection).SequenceEqual(["first question"]),
            "the second rewind did not remove the rewritten turn");

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "final question", expectedReplies: 2);
        var request = RequestText(harness.Model.Requests[4]);
        Assert.Contains("first question", request);
        Assert.Contains("final question", request);
        Assert.DoesNotContain("second question", request);
        Assert.DoesNotContain("third question", request);
        Assert.DoesNotContain("rewritten question", request);

        await runtime.DisposeAsync();
        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = resumed.Subscribe(session.Id);
        Assert.Equal(["first question", "final question"], UserTexts(resumedProjection));
    }

    [Fact]
    public async Task Rewind_DropsFailedTurnEventsFromTheRemovedBranchOnRestart()
    {
        const string failure = "rewound failure";
        await using var harness = ScriptedCallHarness.Create(
            Reply("answer one"),
            [new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest, failure))],
            Reply("fresh answer"));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first question", expectedReplies: 1);
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, "failing question"), CancellationToken.None);
        Assert.True(ack.Accepted);
        await WaitForAsync(
            () => projection.Snapshot().Rows.OfType<SystemNoticeRow>()
                .Any(row => row.Text.Contains(failure, StringComparison.Ordinal)),
            "the failed turn did not produce a notice");
        await WaitForAsync(
            () => projection.Snapshot().State.Phase == TurnPhase.Idle,
            "the failed turn did not become idle");

        MessageId failingId;
        await using (var store = harness.OpenStore())
        {
            var stored = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
            failingId = Assert.Single(stored, item => item.Message.Parts.OfType<TextPart>()
                .Any(part => part.Text == "failing question")).Message.Id;
        }
        Assert.True((await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, failingId),
            CancellationToken.None)).Accepted);
        await WaitForAsync(
            () => projection.Snapshot().Rows.OfType<SystemNoticeRow>()
                .All(row => !row.Text.Contains(failure, StringComparison.Ordinal)),
            "the failed turn notice was not removed by the rewind");

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "fresh question", expectedReplies: 2);
        var request = RequestText(harness.Model.Requests[2]);
        Assert.Contains("first question", request);
        Assert.Contains("fresh question", request);
        Assert.DoesNotContain("failing question", request);
        Assert.DoesNotContain(failure, request);

        await runtime.DisposeAsync();
        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = resumed.Subscribe(session.Id);
        Assert.Equal(["first question", "fresh question"], UserTexts(resumedProjection));
        Assert.DoesNotContain(resumedProjection.Snapshot().Rows.OfType<SystemNoticeRow>(),
            row => row.Text.Contains(failure, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task RewindLegacyTargetWithoutEnvelope_DropsDiscardedEventsAndPreservesArchivedPrefix(
        bool compactBeforeTarget, bool compactAfterRewind, bool missingPreservedHead)
    {
        var callId = StrongId.NewToolCallId();
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            [new ModelToolCallReady(new ToolCall(callId, "TodoRead", JsonSerializer.SerializeToElement(new { }))),
             new ModelStreamFinished(ModelFinishReason.ToolCalls)],
            [new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest, "discarded failure"))],
            Reply("replacement answer"),
        ];
        if (compactAfterRewind)
            calls.Add([new ModelTextDelta("replacement summary"), new ModelTextEnded(),
                new ModelStreamFinished(ModelFinishReason.Stop)]);
        await using var harness = new ScriptedCallHarness(calls);
        var sessionId = StrongId.NewSessionId();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        ConversationMessage Text(MessageRole role, string text, int seconds) => new(
            StrongId.NewMessageId(), role, [new TextPart(StrongId.NewPartId(), text)], now.AddSeconds(seconds));
        var archivedUser = Text(MessageRole.User, "archived question", 1);
        var archivedAssistant = Text(MessageRole.Assistant, "archived answer", 2);
        var target = Text(MessageRole.User, "legacy target", 6);
        var legacyAssistant = Text(MessageRole.Assistant, "legacy discarded answer", 7);
        var archivedTurn = StrongId.NewTurnId();
        var legacyTurn = StrongId.NewTurnId();
        var events = new List<SequencedEvent>
        {
            new(1, sessionId, now, new TurnStartedEvent(archivedTurn, StrongId.NewCommandId())),
            new(2, sessionId, archivedUser.CreatedAt, new UserMessageCommittedEvent(archivedUser)),
            new(3, sessionId, archivedAssistant.CreatedAt, new AssistantMessageCommittedEvent(archivedAssistant)),
            new(4, sessionId, now.AddSeconds(3), new TurnCompletedEvent(archivedTurn, TurnOutcome.Completed)),
        };
        var messages = new List<ConversationMessage>();
        if (compactBeforeTarget)
        {
            var compactAt = now.AddSeconds(missingPreservedHead ? 9 : 4);
            messages.Add(new ConversationMessage(StrongId.NewMessageId(), MessageRole.User,
                [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.CompactSummary, "archived summary", ProviderVisible: true)],
                compactAt));
            events.Add(new SequencedEvent(missingPreservedHead ? 8 : 5, sessionId, compactAt, new CompactBoundaryAppliedEvent(
                new CompactBoundary("legacy-prefix", missingPreservedHead ? CompactTrigger.Auto : CompactTrigger.Manual, 100, 10,
                    archivedAssistant.Id, missingPreservedHead ? target.Id : null,
                    missingPreservedHead ? legacyAssistant.Id : null, compactAt))));
        }
        else
        {
            messages.AddRange([archivedUser, archivedAssistant]);
        }
        messages.AddRange([target, legacyAssistant]);
        events.AddRange([
            new SequencedEvent(6, sessionId, now.AddSeconds(5), new TurnStartedEvent(legacyTurn, StrongId.NewCommandId())),
            new SequencedEvent(7, sessionId, now.AddSeconds(8), new TurnCompletedEvent(legacyTurn, TurnOutcome.Completed)),
        ]);
        await using (var store = harness.OpenStore())
        {
            await store.UpsertSessionAsync(new SessionRecord(sessionId, harness.Root, "Legacy rewind", PermissionMode.Yolo,
                new ModelSelection("scripted", "test-model"), now, now), CancellationToken.None);
            await store.AppendMessagesAsync(sessionId, messages, 0, CancellationToken.None);
            await store.AppendDurableEventsAsync(events, CancellationToken.None);
        }

        await using (var runtime = harness.BuildRuntime())
        {
            await runtime.ResumeSessionAsync(sessionId, CancellationToken.None);
            using var projection = runtime.Subscribe(sessionId);
            await SendAndWaitIdleAsync(runtime, projection, sessionId, "discarded question", expectedReplies: 3);
            Assert.Single(projection.Snapshot().Rows.OfType<ToolCallRow>());
            Assert.Contains(projection.Snapshot().Rows.OfType<SystemNoticeRow>(), row => row.Kind == "turn_error");

            await RewindWhenIdleAsync(runtime, sessionId, target.Id);
            await WaitForAsync(() => UserTexts(projection).SequenceEqual(["archived question"]),
                "the legacy rewind did not remove the discarded branch");
            await SendAndWaitIdleAsync(runtime, projection, sessionId, "replacement question", expectedReplies: 2);
            if (compactAfterRewind)
            {
                var ack = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), sessionId, 0), CancellationToken.None);
                Assert.True(ack.Accepted, ack.RejectionReason);
            }
        }

        for (var reopen = 0; reopen < 2; reopen++)
        {
            await using var runtime = harness.BuildRuntime();
            await runtime.ResumeSessionAsync(sessionId, CancellationToken.None);
            using var projection = runtime.Subscribe(sessionId);
            var snapshot = projection.Snapshot();
            Assert.Equal(TurnPhase.Idle, snapshot.State.Phase);
            Assert.Null(snapshot.State.ActiveTurn);
            Assert.Equal(["archived question", "replacement question"], UserTexts(projection));
            Assert.Equal(["archived answer", "replacement answer"], snapshot.Rows.OfType<AssistantMessageRow>()
                .Select(row => Assert.Single(row.Parts.OfType<TextPart>()).Text));
            Assert.All(snapshot.Rows.OfType<AssistantMessageRow>(), row => Assert.True(row.CanFork));
            Assert.Empty(snapshot.Rows.OfType<ToolCallRow>());
            Assert.DoesNotContain(snapshot.Rows.OfType<SystemNoticeRow>(), row => row.Kind == "turn_error");
            Assert.Equal((compactBeforeTarget && !missingPreservedHead ? 1 : 0) + (compactAfterRewind ? 1 : 0),
                snapshot.Rows.OfType<SystemNoticeRow>().Count(row => row.Kind == "compact"));
        }
    }

    [Fact]
    public async Task Rewind_IgnoresRunningSubagentsOwnedByOtherSessions_ButRejectsItsOwnRunningSubagents()
    {
        await using var harness = ScriptedCallHarness.Create(Reply("answer one"), Reply("answer two"));
        var tasks = new SubagentTaskRegistry();
        await using var runtime = harness.BuildRuntime(new SubagentTool(new UnusedSubagentPort(), tasks: tasks));
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        var other = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "first question", expectedReplies: 1);
        var firstUserId = await FindUserIdAsync(harness, session.Id, "first question");

        tasks.Register(RunningTask("sub_other", other.Id), _ => ValueTask.CompletedTask);
        var foreignAck = await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, firstUserId),
            CancellationToken.None);
        Assert.True(foreignAck.Accepted);
        await WaitForAsync(() => UserTexts(projection).Length == 0,
            "the rewind was not applied before the next turn");

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "second question", expectedReplies: 1);
        var secondUserId = await FindUserIdAsync(harness, session.Id, "second question");

        tasks.Register(RunningTask("sub_own", session.Id), _ => ValueTask.CompletedTask);
        var ownAck = await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, secondUserId),
            CancellationToken.None);
        Assert.False(ownAck.Accepted);
        Assert.Equal("stop running subagents before rewinding", ownAck.RejectionReason);

        tasks.Update(new SubagentId("sub_own"), info => info with { Status = SubagentStatus.Completed });
        var completedAck = await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), session.Id, 0, secondUserId),
            CancellationToken.None);
        Assert.True(completedAck.Accepted);
    }

    private static SubagentTaskInfo RunningTask(string id, SessionId parentSessionId) =>
        new(
            new SubagentId(id),
            new SessionId("sess_child_" + id),
            "general-purpose",
            "background work",
            "background work",
            OutputFile: null,
            SubagentStatus.Running,
            Result: null,
            Error: null,
            ToolUses: null,
            DateTimeOffset.UtcNow,
            EndedAt: null)
        {
            ParentSessionId = parentSessionId,
        };

    private static async Task<MessageId> FindUserIdAsync(ScriptedCallHarness harness, SessionId sessionId, string text)
    {
        await using var store = harness.OpenStore();
        var stored = await store.LoadMessagesAsync(sessionId, CancellationToken.None);
        return Assert.Single(stored, item => item.Message.Parts.OfType<TextPart>()
            .Any(part => part.Text == text)).Message.Id;
    }

    private sealed class UnusedSubagentPort : ISubagentPort
    {
        public ValueTask<SubagentResult> RunAsync(SubagentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SubagentHandle> StartBackgroundAsync(
            SubagentRequest request,
            Func<SubagentResult, ValueTask> onCompleted,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static async Task RewindWhenIdleAsync(AgentRuntime runtime, SessionId sessionId, MessageId target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var command = new RewindConversationCommand(StrongId.NewCommandId(), sessionId, 0, target);
        while (true)
        {
            var ack = await runtime.SubmitAsync(command, CancellationToken.None);
            if (ack.Accepted)
                return;
            Assert.Equal("wait for the current turn to finish before rewinding", ack.RejectionReason);
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("the completed turn did not release the rewind command");
            await Task.Delay(10);
        }
    }

    private static async Task SendAndWaitIdleAsync(
        AgentRuntime runtime,
        IConversationProjection projection,
        SessionId id,
        string text,
        int expectedReplies)
    {
        // A fresh submit can still observe the previous turn's Idle before this turn starts;
        // the committed user row is the monotonic proof the turn actually ran.
        var baselineUsers = projection.Snapshot().Rows.OfType<UserMessageRow>().Count();
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), id, 0, text), CancellationToken.None);
        Assert.True(ack.Accepted);
        await WaitForAsync(
            () => projection.Snapshot() is var snapshot
                && snapshot.State.Phase == TurnPhase.Idle
                && snapshot.Rows.OfType<UserMessageRow>().Count() > baselineUsers
                && snapshot.Rows.OfType<AssistantMessageRow>().Count() >= expectedReplies,
            $"turn for '{text}' did not become idle");
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(failure);
            }
            await Task.Delay(10);
        }
    }
}
