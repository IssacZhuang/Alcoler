using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Adapters.Storage;
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
using Alcoler.Kernel.Subagents;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class SubagentTaskToolsTests
{
    [Fact]
    public async Task BackgroundRead_ConsumesTerminalResult_AndSuppressesCompletionNotification()
    {
        await using var harness = TestHarness.Create();
        var model = new TaskToolModel(TaskToolScenario.BackgroundRead);
        var store = new DelayingSessionStore(
            new SqliteSessionStore(Path.Combine(harness.Root, "db.sqlite")),
            TimeSpan.FromMilliseconds(500));
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root, store);
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "delegate two tasks"), CancellationToken.None)).Accepted);
        try
        {
            await WaitForAsync(() => projection.Snapshot().Rows.OfType<ToolCallRow>()
                .Any(row => row.ToolName == "TaskOutput" && row.Status == ToolCallStatus.Running));
            await model.SubjectStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            model.ReleaseSubject.TrySetResult();

            await WaitForAsync(() => projection.Snapshot().Rows.OfType<ToolCallRow>()
                .Any(row => row.ToolName == "TaskOutput" && row.Status == ToolCallStatus.Succeeded));
            await WaitForAsync(() => Subagents(projection).Count == 2);
            var states = Subagents(projection);
            var subject = states.Single(state => state.Description == "subject");
            var control = states.Single(state => state.Description == "control");

            var output = string.Join("\n", projection.Snapshot().Rows.OfType<ToolCallRow>()
                .Where(row => row.ToolName == "TaskOutput")
                .SelectMany(row => row.Output ?? [])
                .OfType<ToolResultText>()
                .Select(block => block.Text));
            Assert.Contains("<retrieval_status>success</retrieval_status>", output);
            Assert.Contains("<task_type>local_agent</task_type>", output);
            Assert.Contains("<status>completed</status>", output);
            Assert.Contains("subject-result", output);
            Assert.DoesNotContain("<exit_code>", output);

            await WaitForAsync(() => model.Notifications.Any(text =>
                text.Contains($"<task-id>{control.Id.Value}</task-id>", StringComparison.Ordinal)));
            await WaitForPhaseAsync(projection, TurnPhase.Idle);
            Assert.DoesNotContain(model.Notifications, text =>
                text.Contains($"<task-id>{subject.Id.Value}</task-id>", StringComparison.Ordinal));
        }
        finally
        {
            model.ReleaseSubject.TrySetResult();
        }
    }

    [Fact]
    public async Task TaskStop_StopsRunningBackgroundSubagent_AndWritesStoppedArtifacts()
    {
        await using var harness = TestHarness.Create();
        var model = new TaskToolModel(TaskToolScenario.Stop);
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root);
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "delegate then stop"), CancellationToken.None)).Accepted);
        try
        {
            await WaitForAsync(() => projection.Snapshot().Rows.OfType<ToolCallRow>()
                .Any(row => row.ToolName == "TaskOutput" && row.Status == ToolCallStatus.Succeeded));
            await WaitForAsync(() => Subagents(projection) is [ { Status: SubagentStatus.Cancelled } ]);
            await WaitForPhaseAsync(projection, TurnPhase.Idle);

            var state = Assert.Single(Subagents(projection));
            Assert.NotNull(state.OutputFile);
            Assert.Equal(SubagentArtifacts.StoppedText, await File.ReadAllTextAsync(state.OutputFile));

            var output = string.Join("\n", projection.Snapshot().Rows.OfType<ToolCallRow>()
                .Where(row => row.ToolName == "TaskOutput")
                .SelectMany(row => row.Output ?? [])
                .OfType<ToolResultText>()
                .Select(block => block.Text));
            Assert.Contains("<status>stopped</status>", output);

            var stop = projection.Snapshot().Rows.OfType<ToolCallRow>().Single(row => row.ToolName == "TaskStop");
            Assert.Equal(ToolCallStatus.Succeeded, stop.Status);
            Assert.False(stop.IsError);
            Assert.Contains("Successfully stopped task", string.Join("\n",
                (stop.Output ?? []).OfType<ToolResultText>().Select(block => block.Text)));
        }
        finally
        {
            model.ReleaseSubject.TrySetResult();
        }
    }

    [Fact]
    public async Task SendMessageResume_AfterTaskOutput_ReenablesCompletionNotification()
    {
        await using var harness = TestHarness.Create();
        var model = new TaskToolModel(TaskToolScenario.ForegroundResume);
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root);
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "delegate then follow up"), CancellationToken.None)).Accepted);

        await WaitForAsync(() => projection.Snapshot().Rows.OfType<ToolCallRow>()
            .Any(row => row.ToolName == "SendMessage" && row.Status == ToolCallStatus.Succeeded));
        var state = Assert.Single(Subagents(projection));
        await WaitForAsync(() => model.Notifications.Any(text =>
            text.Contains($"<task-id>{state.Id.Value}</task-id>", StringComparison.Ordinal)
            && text.Contains("<tool-use-id>followup</tool-use-id>", StringComparison.Ordinal)
            && text.Contains("followup-result", StringComparison.Ordinal)));
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        Assert.Equal(SubagentStatus.Completed, Assert.Single(Subagents(projection)).Status);
    }

    private static IReadOnlyList<SubagentState> Subagents(IConversationProjection projection) =>
        projection.Snapshot().Rows.OfType<SystemNoticeRow>()
            .Select(row => row.Subagent).OfType<SubagentState>().ToArray();

    private static async Task WaitForPhaseAsync(IConversationProjection projection, TurnPhase phase)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != phase || !snapshot.Rows.OfType<UserMessageRow>().Any()))
        {
            if (deadline.IsCompleted)
                throw new TimeoutException($"Phase never reached {phase} (current: {projection.Snapshot().State.Phase}).");
            await Task.Delay(20);
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private enum TaskToolScenario
    {
        BackgroundRead,
        Stop,
        ForegroundResume,
    }

    private static IReadOnlyList<ModelStreamEvent> ToolCalls(
        params (string Id, string Name, object Input)[] calls)
    {
        var events = new List<ModelStreamEvent>();
        foreach (var (id, name, input) in calls)
        {
            var callId = new ToolCallId(id);
            var element = JsonSerializer.SerializeToElement(input);
            events.Add(new ModelToolInputStarted(callId, name));
            events.Add(new ModelToolInputDelta(callId, element.GetRawText()));
            events.Add(new ModelToolInputEnded(callId));
            events.Add(new ModelToolCallReady(new ToolCall(callId, name, element)));
        }
        events.Add(new ModelStreamFinished(ModelFinishReason.ToolCalls));
        return events;
    }

    private static string AgentIdFor(ModelRequest request, string description)
    {
        var call = request.Messages.SelectMany(message => message.Content).OfType<InputToolCallBlock>()
            .Single(block => block.Name == "Agent"
                && block.Input.GetProperty("description").GetString() == description);
        var result = request.Messages.SelectMany(message => message.Content).OfType<InputToolResultBlock>()
            .Single(block => block.Id == call.Id);
        var idLine = result.Content.Split('\n').Single(line => line.StartsWith("agentId: ", StringComparison.Ordinal));
        return idLine["agentId: ".Length..].Split(' ')[0];
    }

    private sealed class TaskToolModel(TaskToolScenario scenario) : IChatModel
    {
        private int _parentRequests;

        public ModelDescriptor Descriptor { get; } = new(
            "scripted", "test-model", "Task Tool Model", new ModelProperties(ContextWindowTokens: 200_000));

        public TaskCompletionSource SubjectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSubject { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Notifications { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (request.Tools.Any(tool => tool.Name == "Agent"))
            {
                foreach (var text in request.Messages.SelectMany(message => message.Content)
                    .OfType<InputTextBlock>().Select(block => block.Text)
                    .Where(text => text.Contains("<task-notification>", StringComparison.Ordinal)))
                    Notifications.Enqueue(text);

                var turn = Interlocked.Increment(ref _parentRequests);
                if (turn == 1)
                {
                    var calls = new List<(string Id, string Name, object Input)>
                    {
                        ("launch-subject", "Agent", new
                        {
                            description = "subject",
                            prompt = "subject-task",
                            run_in_background = scenario != TaskToolScenario.ForegroundResume,
                        }),
                    };
                    if (scenario == TaskToolScenario.BackgroundRead)
                    {
                        calls.Add(("launch-control", "Agent", new
                        {
                            description = "control",
                            prompt = "control-task",
                            run_in_background = true,
                        }));
                    }
                    foreach (var streamEvent in ToolCalls(calls.ToArray()))
                        yield return streamEvent;
                    yield break;
                }

                if (turn == 2)
                {
                    var subjectId = AgentIdFor(request, "subject");
                    if (scenario == TaskToolScenario.Stop)
                    {
                        await SubjectStarted.Task.WaitAsync(cancellationToken);
                        foreach (var streamEvent in ToolCalls(
                            ("stop-subject", "TaskStop", new { task_id = subjectId })))
                            yield return streamEvent;
                    }
                    else
                    {
                        foreach (var streamEvent in ToolCalls(
                            ("read-subject", "TaskOutput", new
                            {
                                task_id = subjectId,
                                block = scenario == TaskToolScenario.BackgroundRead,
                                timeout = 10000,
                            })))
                            yield return streamEvent;
                    }
                    yield break;
                }

                if (turn == 3 && scenario == TaskToolScenario.Stop)
                {
                    var subjectId = AgentIdFor(request, "subject");
                    foreach (var streamEvent in ToolCalls(
                        ("read-stopped", "TaskOutput", new { task_id = subjectId, block = true, timeout = 10000 })))
                        yield return streamEvent;
                    yield break;
                }

                if (turn == 3 && scenario == TaskToolScenario.ForegroundResume)
                {
                    var subjectId = AgentIdFor(request, "subject");
                    foreach (var streamEvent in ToolCalls(
                        ("followup", "SendMessage", new
                        {
                            to = subjectId,
                            summary = "Continue the child task",
                            message = "followup-task",
                        })))
                        yield return streamEvent;
                    yield break;
                }

                yield return new ModelTextDelta("parent done");
                yield return new ModelStreamFinished(ModelFinishReason.Stop);
                yield break;
            }

            var prompt = request.Messages
                .Where(message => message.Role == MessageRole.User)
                .SelectMany(message => message.Content)
                .OfType<InputTextBlock>()
                .Select(block => block.Text)
                .LastOrDefault(text => text is "subject-task" or "control-task" or "followup-task");

            if (prompt == "subject-task")
            {
                SubjectStarted.TrySetResult();
                if (scenario != TaskToolScenario.ForegroundResume)
                    await ReleaseSubject.Task.WaitAsync(cancellationToken);
                yield return new ModelTextDelta("subject-result");
            }
            else if (prompt == "control-task")
            {
                yield return new ModelTextDelta("control-result");
            }
            else if (prompt == "followup-task")
            {
                yield return new ModelTextDelta("followup-result");
            }
            else
            {
                yield return new ModelTextDelta("child done");
            }

            yield return new ModelStreamFinished(ModelFinishReason.Stop);
        }
    }

    // Terminal subagent events are persisted before the completion notification is released,
    // so delaying that write gives TaskOutput a deterministic window to mark the task notified.
    private sealed class DelayingSessionStore(ISessionStore inner, TimeSpan delay) : ISessionStore
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            inner.InitializeAsync(cancellationToken);

        public ValueTask UpsertSessionAsync(SessionRecord record, CancellationToken cancellationToken) =>
            inner.UpsertSessionAsync(record, cancellationToken);

        public ValueTask<bool> TryUpdateSessionTitleAsync(
            SessionId sessionId,
            string title,
            SessionTitleSource source,
            CancellationToken cancellationToken) =>
            inner.TryUpdateSessionTitleAsync(sessionId, title, source, cancellationToken);

        public ValueTask<SessionRecord?> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            inner.GetSessionAsync(sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
            string? workspaceRoot,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ListSessionsAsync(workspaceRoot, limit, cancellationToken);

        public ValueTask DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            inner.DeleteSessionAsync(sessionId, cancellationToken);

        public ValueTask AppendMessagesAsync(
            SessionId sessionId,
            IReadOnlyList<ConversationMessage> messages,
            long startingSequence,
            CancellationToken cancellationToken) =>
            inner.AppendMessagesAsync(sessionId, messages, startingSequence, cancellationToken);

        public ValueTask ReplaceMessagesAsync(
            SessionId sessionId,
            IReadOnlyList<ConversationMessage> messages,
            CancellationToken cancellationToken) =>
            inner.ReplaceMessagesAsync(sessionId, messages, cancellationToken);

        public ValueTask<IReadOnlyList<StoredMessage>> LoadMessagesAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) =>
            inner.LoadMessagesAsync(sessionId, cancellationToken);

        public async ValueTask AppendDurableEventsAsync(
            IReadOnlyList<SequencedEvent> events,
            CancellationToken cancellationToken)
        {
            if (events.Any(evt => evt.Event is SubagentStatusChangedEvent changed
                && changed.State.Status != SubagentStatus.Running))
                await Task.Delay(delay, cancellationToken);
            await inner.AppendDurableEventsAsync(events, cancellationToken);
        }

        public ValueTask<IReadOnlyList<SequencedEvent>> LoadDurableEventsAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) =>
            inner.LoadDurableEventsAsync(sessionId, cancellationToken);

        public ValueTask<long> GetNextMessageSequenceAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) =>
            inner.GetNextMessageSequenceAsync(sessionId, cancellationToken);
    }
}
