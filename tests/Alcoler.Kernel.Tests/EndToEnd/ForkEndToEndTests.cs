using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class ForkEndToEndTests
{
    private static IReadOnlyList<ModelStreamEvent> Reply(string text) =>
        [new ModelTextDelta(text), new ModelStreamFinished(ModelFinishReason.Stop)];

    private static string RequestText(ModelRequest request) =>
        string.Join("\n", request.Messages.SelectMany(message => message.Content.OfType<InputTextBlock>())
            .Select(block => block.Text));

    private static string[] UserTexts(IConversationProjection projection) =>
        [.. projection.Snapshot().Rows.OfType<UserMessageRow>().Select(row => row.Text)];

    private static string[] AssistantTexts(IConversationProjection projection) =>
        [.. projection.Snapshot().Rows.OfType<AssistantMessageRow>()
            .Select(row => string.Join("", row.Parts.OfType<TextPart>().Select(part => part.Text)))];

    [Fact]
    public async Task ForkCopiesTheSelectedCompletedTurn_AndTheChildContinuesIndependently()
    {
        await using var harness = ScriptedCallHarness.Create(
            Reply("answer one"),
            Reply("answer two"),
            Reply("child answer"));
        await using var runtime = harness.BuildRuntime();
        var parent = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var parentProjection = runtime.Subscribe(parent.Id);
        await SendAndWaitIdleAsync(runtime, parentProjection, parent.Id, "first question", expectedReplies: 1);
        await SendAndWaitIdleAsync(runtime, parentProjection, parent.Id, "second question", expectedReplies: 2);

        MessageId firstAssistantId;
        await using (var store = harness.OpenStore())
        {
            var stored = await store.LoadMessagesAsync(parent.Id, CancellationToken.None);
            firstAssistantId = stored[1].Message.Id;
        }

        var child = await runtime.ForkSessionAsync(parent.Id, firstAssistantId, CancellationToken.None);

        Assert.NotEqual(parent.Id, child.Id);
        Assert.Equal("Fork of first question", child.Title);
        Assert.Equal(harness.Root, child.WorkspaceRoot);
        using var childProjection = runtime.Subscribe(child.Id);
        Assert.Equal(["first question"], UserTexts(childProjection));
        Assert.Equal(["answer one"], AssistantTexts(childProjection));
        Assert.True(Assert.Single(childProjection.Snapshot().Rows.OfType<AssistantMessageRow>()).CanFork);

        await SendAndWaitIdleAsync(runtime, childProjection, child.Id, "child question", expectedReplies: 1);
        var childRequest = RequestText(harness.Model.Requests[^1]);
        Assert.Contains("first question", childRequest);
        Assert.Contains("answer one", childRequest);
        Assert.Contains("child question", childRequest);
        Assert.DoesNotContain("second question", childRequest);
        Assert.DoesNotContain("answer two", childRequest);

        Assert.Equal(["first question", "second question"], UserTexts(parentProjection));
        Assert.Equal(["answer one", "answer two"], AssistantTexts(parentProjection));

        await using (var store = harness.OpenStore())
        {
            var childRecord = Assert.IsType<SessionRecord>(await store.GetSessionAsync(child.Id, CancellationToken.None));
            Assert.Equal(parent.Id, childRecord.ForkedFromSessionId);
            Assert.Equal(firstAssistantId, childRecord.ForkedFromMessageId);
            Assert.Null(childRecord.ParentSessionId);
            Assert.Null(childRecord.Branch);
            Assert.Equal(4, (await store.LoadMessagesAsync(child.Id, CancellationToken.None)).Count);

            var parentRecord = Assert.IsType<SessionRecord>(await store.GetSessionAsync(parent.Id, CancellationToken.None));
            Assert.Null(parentRecord.ForkedFromSessionId);
            Assert.Null(parentRecord.Branch);
        }

        var sessions = await runtime.ListSessionsAsync(harness.Root, 20, CancellationToken.None);
        Assert.Contains(sessions, summary => summary.Id == parent.Id);
        Assert.Contains(sessions, summary => summary.Id == child.Id);

        await runtime.DisposeAsync();
        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(child.Id, CancellationToken.None);
        using var resumedChild = resumed.Subscribe(child.Id);
        Assert.Equal(["first question", "child question"], UserTexts(resumedChild));
        Assert.Equal(["answer one", "child answer"], AssistantTexts(resumedChild));
        Assert.Equal(2, resumedChild.Snapshot().Rows.OfType<AssistantMessageRow>().Count(row => row.CanFork));
    }

    [Fact]
    public async Task ForkCanTargetAnEarlierCompletedTurn_AndRejectsUserOrUnknownBoundaries()
    {
        await using var harness = ScriptedCallHarness.Create(
            Reply("answer one"),
            Reply("answer two"),
            Reply("forked answer"));
        await using var runtime = harness.BuildRuntime();
        var parent = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(parent.Id);
        await SendAndWaitIdleAsync(runtime, projection, parent.Id, "first question", expectedReplies: 1);
        await SendAndWaitIdleAsync(runtime, projection, parent.Id, "second question", expectedReplies: 2);

        MessageId firstUserId;
        MessageId firstAssistantId;
        await using (var store = harness.OpenStore())
        {
            var stored = await store.LoadMessagesAsync(parent.Id, CancellationToken.None);
            firstUserId = stored[0].Message.Id;
            firstAssistantId = stored[1].Message.Id;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ForkSessionAsync(parent.Id, firstUserId, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ForkSessionAsync(parent.Id, StrongId.NewMessageId(), CancellationToken.None).AsTask());

        var child = await runtime.ForkSessionAsync(parent.Id, firstAssistantId, CancellationToken.None);
        using var childProjection = runtime.Subscribe(child.Id);
        Assert.Equal(["first question"], UserTexts(childProjection));
        Assert.Equal(["answer one"], AssistantTexts(childProjection));

        await SendAndWaitIdleAsync(runtime, childProjection, child.Id, "forked question", expectedReplies: 1);
        var request = RequestText(harness.Model.Requests[^1]);
        Assert.DoesNotContain("second question", request);
        Assert.DoesNotContain("answer two", request);
    }

    [Fact]
    public async Task ForkUsesTheActiveBranchAfterRewind_AndKeepsEarlierTailsForkableOnRestart()
    {
        await using var harness = ScriptedCallHarness.Create(
            Reply("answer one"),
            Reply("answer two"),
            Reply("answer three"),
            Reply("fresh answer"));
        await using var runtime = harness.BuildRuntime();
        var parent = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(parent.Id);
        await SendAndWaitIdleAsync(runtime, projection, parent.Id, "first question", expectedReplies: 1);
        await SendAndWaitIdleAsync(runtime, projection, parent.Id, "second question", expectedReplies: 2);
        await SendAndWaitIdleAsync(runtime, projection, parent.Id, "third question", expectedReplies: 3);

        MessageId secondUserId;
        MessageId firstAssistantId;
        await using (var store = harness.OpenStore())
        {
            var stored = await store.LoadMessagesAsync(parent.Id, CancellationToken.None);
            firstAssistantId = stored[1].Message.Id;
            secondUserId = stored[2].Message.Id;
        }

        Assert.True((await runtime.SubmitAsync(
            new RewindConversationCommand(StrongId.NewCommandId(), parent.Id, 0, secondUserId),
            CancellationToken.None)).Accepted);
        await WaitForAsync(() => UserTexts(projection).SequenceEqual(["first question"]),
            "the rewind did not remove later rows");

        var child = await runtime.ForkSessionAsync(parent.Id, firstAssistantId, CancellationToken.None);
        using var childProjection = runtime.Subscribe(child.Id);
        Assert.Equal(["first question"], UserTexts(childProjection));
        Assert.Equal(["answer one"], AssistantTexts(childProjection));

        await runtime.DisposeAsync();
        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(parent.Id, CancellationToken.None);
        using var resumedParent = resumed.Subscribe(parent.Id);
        Assert.Equal(["first question"], UserTexts(resumedParent));
        Assert.True(Assert.Single(resumedParent.Snapshot().Rows.OfType<AssistantMessageRow>()).CanFork);
    }

    [Fact]
    public async Task ForkOfAToolUsingTurnCopiesTheWholeTurnAndMarksOnlyTheFinalAssistantTail()
    {
        var callId = new ToolCallId("call-todo");
        await using var harness = ScriptedCallHarness.Create(
            [
                new ModelTextDelta("working"),
                new ModelToolCallReady(new ToolCall(callId, "TodoWrite", JsonSerializer.SerializeToElement(new
                {
                    todos = new[] { new { content = "keep going", status = "pending", priority = "medium" } },
                }))),
                new ModelStreamFinished(ModelFinishReason.ToolCalls),
            ],
            Reply("finished"));
        await using var runtime = harness.BuildRuntime();
        var parent = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(parent.Id);
        await SendAndWaitIdleAsync(runtime, projection, parent.Id, "use a tool", expectedReplies: 2);

        MessageId finalAssistantId;
        await using (var parentStore = harness.OpenStore())
        {
            var stored = await parentStore.LoadMessagesAsync(parent.Id, CancellationToken.None);
            Assert.Equal(4, stored.Count);
            finalAssistantId = stored[^1].Message.Id;
        }
        var parentAssistantRows = projection.Snapshot().Rows.OfType<AssistantMessageRow>().ToArray();
        Assert.Equal(2, parentAssistantRows.Length);
        Assert.False(parentAssistantRows[0].CanFork);
        Assert.True(parentAssistantRows[1].CanFork);

        var child = await runtime.ForkSessionAsync(parent.Id, finalAssistantId, CancellationToken.None);
        using var childProjection = runtime.Subscribe(child.Id);
        Assert.Equal(["use a tool"], UserTexts(childProjection));
        Assert.Equal(["working", "finished"], AssistantTexts(childProjection));

        await using var childStore = harness.OpenStore();
        Assert.Equal(4, (await childStore.LoadMessagesAsync(child.Id, CancellationToken.None)).Count);
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
