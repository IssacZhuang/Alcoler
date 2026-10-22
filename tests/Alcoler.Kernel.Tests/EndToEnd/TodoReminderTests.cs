using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class TodoReminderTests
{
    private static readonly ToolCallId TodoCallId = new("call-todo");

    private static IReadOnlyList<ModelStreamEvent> OkReply() =>
        [new ModelTextDelta("ok"), new ModelStreamFinished(ModelFinishReason.Stop)];

    private static IReadOnlyList<ModelStreamEvent> TodoWriteCall() =>
    [
        new ModelToolInputStarted(TodoCallId, "TodoWrite"),
        new ModelToolInputDelta(TodoCallId, JsonSerializer.Serialize(new
        {
            todos = new[] { new { content = "keep going", status = "pending", priority = "medium" } }
        })),
        new ModelToolInputEnded(TodoCallId),
        new ModelToolCallReady(new ToolCall(TodoCallId, "TodoWrite",
            JsonSerializer.SerializeToElement(new
            {
                todos = new[] { new { content = "keep going", status = "pending", priority = "medium" } }
            }))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls),
    ];

    private static string? TodoReminderText(ModelRequest request) =>
        request.Messages
            .Where(m => m.Role == Contracts.Conversation.MessageRole.User)
            .SelectMany(m => m.Content.OfType<InputTextBlock>())
            .Select(b => b.Text)
            .FirstOrDefault(t => t.Contains("The TodoWrite tool hasn't been used recently."));

    private static bool HasTodoReminder(ModelRequest request) => TodoReminderText(request) is not null;

    private static int CountTodoReminders(ModelRequest request) =>
        request.Messages
            .Where(m => m.Role == Contracts.Conversation.MessageRole.User)
            .SelectMany(m => m.Content.OfType<InputTextBlock>())
            .Count(b => b.Text.Contains("The TodoWrite tool hasn't been used recently.", StringComparison.Ordinal));

    [Fact]
    public async Task Reminder_FiresOnEleventhRequestFromHistoryCountsAndRecycles()
    {
        var calls = Enumerable.Range(0, 21).Select(_ => OkReply()).ToArray();
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        for (var i = 1; i <= 21; i++)
            await SendAndWaitIdleAsync(runtime, projection, session.Id, $"message {i}", expectedReplies: i);

        // ZCode counts committed assistants, so the 11th request is the first to see 10.
        // The committed reminder rides along in later requests; the counter recycles and
        // fires again on the 21st request, which therefore carries two copies.
        Assert.Equal(21, harness.Model.Requests.Count);
        Assert.DoesNotContain(harness.Model.Requests.Take(10), HasTodoReminder);
        Assert.Equal(1, CountTodoReminders(harness.Model.Requests[10]));
        Assert.Equal(1, CountTodoReminders(harness.Model.Requests[19]));
        Assert.Equal(2, CountTodoReminders(harness.Model.Requests[20]));
    }

    [Fact]
    public async Task TodoWriteCall_ResetsStepCounterAndDefersReminder()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls =
        [
            OkReply(), OkReply(), TodoWriteCall(), OkReply(),
        ];
        calls.AddRange(Enumerable.Range(0, 10).Select(_ => OkReply()));
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        for (var i = 1; i <= 13; i++)
        {
            var replies = i >= 3 ? i + 1 : i; // turn 3 commits two assistant messages (tool round + follow-up)
            await SendAndWaitIdleAsync(runtime, projection, session.Id, $"message {i}", expectedReplies: replies);
        }

        // The counter resets on the turn-3 TodoWrite call and counts committed
        // assistants after it, so the reminder lands on the 14th request.
        Assert.Equal(14, harness.Model.Requests.Count);
        Assert.DoesNotContain(harness.Model.Requests.Take(13), HasTodoReminder);
        Assert.True(HasTodoReminder(harness.Model.Requests[13]));
    }

    [Fact]
    public async Task Reminder_IncludesCurrentTodoList_WhenTodosExist()
    {
        List<IReadOnlyList<ModelStreamEvent>> calls = [TodoWriteCall(), OkReply()];
        calls.AddRange(Enumerable.Range(0, 10).Select(_ => OkReply()));
        await using var harness = new ScriptedCallHarness(calls);
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        await SendAndWaitIdleAsync(runtime, projection, session.Id, "seed todos", expectedReplies: 2);
        for (var i = 2; i <= 11; i++)
            await SendAndWaitIdleAsync(runtime, projection, session.Id, $"message {i}", expectedReplies: i + 1);

        var reminder = TodoReminderText(harness.Model.Requests[^1]);
        Assert.NotNull(reminder);
        Assert.Contains("Here are the existing contents of your todo list:", reminder);
        Assert.Contains("[1. [pending] keep going]", reminder);
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
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), id, 0, text), CancellationToken.None);
        Assert.True(ack.Accepted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (projection.Snapshot() is var snapshot
               && (snapshot.State.Phase != TurnPhase.Idle
                   || snapshot.Rows.OfType<UserMessageRow>().Count() <= baselineUsers
                   || snapshot.Rows.OfType<AssistantMessageRow>().Count() < expectedReplies))
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
