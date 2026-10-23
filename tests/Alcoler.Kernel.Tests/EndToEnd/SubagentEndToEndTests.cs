using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Subagents;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Subagents;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class SubagentEndToEndTests
{
    private const string ChildAnswer = "The workspace contains a single README.";

    private static ScriptStep ToolCall(string name, object input) => new ScriptStep.Events(
        new Alcoler.Contracts.Models.ModelTextDelta("Delegating."),
        new Alcoler.Contracts.Models.ModelToolInputStarted(new ToolCallId("call-1"), name),
        new Alcoler.Contracts.Models.ModelToolInputDelta(
            new ToolCallId("call-1"),
            JsonSerializer.Serialize(input)),
        new Alcoler.Contracts.Models.ModelToolInputEnded(new ToolCallId("call-1")),
        new Alcoler.Contracts.Models.ModelToolCallReady(new Alcoler.Contracts.Models.ToolCall(
            new ToolCallId("call-1"),
            name,
            JsonSerializer.SerializeToElement(input))),
        new Alcoler.Contracts.Models.ModelStreamFinished(Alcoler.Contracts.Models.ModelFinishReason.ToolCalls));

    private static ScriptStep FinalText(params string[] texts) => new ScriptStep.Events(
        [.. texts.Select(t => new Alcoler.Contracts.Models.ModelTextDelta(t)),
        new Alcoler.Contracts.Models.ModelStreamFinished(Alcoler.Contracts.Models.ModelFinishReason.Stop)]);

    [Fact]
    public async Task ForegroundSubagent_RunsChildSession_MirrorsTimeline_ReturnsOutput()
    {
        // Parent and child share a scripted model, so responses must follow execution order.
        await using var harness = TestHarness.Create(
            ToolCall("Agent", new { prompt = "Summarize the workspace.", description = "summarizer" }),
            FinalText(ChildAnswer),
            FinalText("All done."));

        File.WriteAllText(Path.Combine(harness.Root, "README.md"), "# workspace");

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Edit),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Delegate a summary"),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        var taskRow = snapshot.Rows.OfType<ToolCallRow>().Single(r => r.ToolName == "Agent");
        Assert.Equal(ToolCallStatus.Succeeded, taskRow.Status);
        Assert.False(taskRow.IsError);
        Assert.Contains(taskRow.Output ?? [], b => b is ToolResultText t && t.Text.Contains(ChildAnswer));

        var notice = snapshot.Rows.OfType<SystemNoticeRow>().Single(r => r.Kind == "subagent");
        Assert.Contains("Completed", notice.Text);

        var finishedState = Assert.IsType<SubagentState>(notice.Subagent);
        Assert.Equal(taskRow.Id, finishedState.ParentToolCallId);
        Assert.Equal("general-purpose", finishedState.AgentType);
        Assert.NotNull(finishedState.OutputFile);
        Assert.Equal(Path.Combine(harness.Root, "agent-output", session.Id.Value, finishedState.Id.Value, "output.txt"),
            finishedState.OutputFile);
        Assert.Equal(ChildAnswer, File.ReadAllText(finishedState.OutputFile!));
        Assert.Equal(File.ReadAllBytes(finishedState.OutputFile!),
            File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(finishedState.OutputFile!)!, "task.output")));
        using (var metadata = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(finishedState.OutputFile!)!, "metadata.json"))))
        {
            Assert.Equal("completed", metadata.RootElement.GetProperty("status").GetString());
            Assert.Equal(finishedState.Id.Value, metadata.RootElement.GetProperty("agentId").GetString());
        }

        Assert.Contains(snapshot.Rows, r => r is AssistantMessageRow a
            && a.Parts.OfType<TextPart>().Any(p => p.Text.Contains("All done.")));

        var childId = finishedState.ChildSessionId;
        await runtime.DisposeAsync();
        await using var reopened = harness.BuildRuntime();
        await reopened.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var parentProjection = reopened.Subscribe(session.Id);
        Assert.Equal(snapshot.Rows.Select(RowIdentity), parentProjection.Snapshot().Rows.Select(RowIdentity));
        var sessions = await reopened.ListSessionsAsync(harness.Root, 10, CancellationToken.None);
        Assert.Equal(session.Id, Assert.Single(sessions).Id);


        await reopened.ResumeSessionAsync(childId, CancellationToken.None);
        using var childProjection = reopened.Subscribe(childId);
        var childSnapshot = childProjection.Snapshot();
        Assert.Contains(childSnapshot.Rows, r => r is UserMessageRow u && u.Text.Contains("Summarize the workspace."));
        Assert.Contains(childSnapshot.Rows, r => r is AssistantMessageRow a
            && a.Parts.OfType<TextPart>().Any(p => p.Text.Contains(ChildAnswer)));
    }

    [Fact]
    public async Task ForegroundSubagent_InheritsParentSessionModel_NotRuntimeDefault()
    {
        var parentModel = new SequencedChatModel(
            [
                ToolCall("Agent", new { prompt = "Summarize the workspace.", description = "summarizer" }),
                FinalText(ChildAnswer),
                FinalText("All done."),
            ],
            new ModelDescriptor("parent", "parent-model", "Parent Model",
                new ModelProperties(ContextWindowTokens: 200_000)));
        var defaultModel = new SequencedChatModel(
            [FinalText("default model response")],
            new ModelDescriptor("default", "default-model", "Default Model",
                new ModelProperties(ContextWindowTokens: 200_000)));
        var parentCapture = new TestHarness.RequestCapturingModel(parentModel);
        var defaultCapture = new TestHarness.RequestCapturingModel(defaultModel);

        await using var harness = TestHarness.Create();
        await using var runtime = harness.BuildRuntime(options => options with
        {
            Providers =
            [
                new ScriptedModelProvider(defaultCapture, "default"),
                new ScriptedModelProvider(parentCapture, "parent"),
            ],
            DefaultModel = new ModelSelection("default", "default-model"),
        });
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Edit,
                ModelSelection: new ModelSelection("parent", "parent-model")),
            CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Delegate a summary"),
            CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        Assert.Empty(defaultCapture.Requests);
        Assert.Equal(3, parentCapture.Requests.Count);
        Assert.Contains(projection.Snapshot().Rows.OfType<AssistantMessageRow>(), row =>
            row.Parts.OfType<TextPart>().Any(part => part.Text.Contains("All done.")));
    }

    [Fact]
    public async Task ForegroundSubagent_WithoutParentModelSelection_FailsInsteadOfUsingRuntimeDefault()
    {
        await using var harness = TestHarness.Create(
            ToolCall("Agent", new { prompt = "Summarize the workspace.", description = "summarizer" }),
            FinalText("Parent finished."));
        var store = new SqliteSessionStore(Path.Combine(harness.Root, "db.sqlite"));
        await store.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var record = new SessionRecord(
            StrongId.NewSessionId(), harness.Root, "Legacy session", PermissionMode.Yolo, null, now, now);
        await store.UpsertSessionAsync(record, CancellationToken.None);

        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
        var session = await runtime.ResumeSessionAsync(record.Id, CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Delegate a summary"),
            CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var taskRow = Assert.Single(projection.Snapshot().Rows.OfType<ToolCallRow>());
        Assert.Equal(ToolCallStatus.Failed, taskRow.Status);
        Assert.Contains(taskRow.Output ?? [], block => block is ToolResultText text
            && text.Text.Contains("No model is selected", StringComparison.Ordinal));
        Assert.Equal(session.Id, Assert.Single(
            await runtime.ListSessionsAsync(harness.Root, 10, CancellationToken.None)).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourSubagents_StartTogether_KeepStableIds_AndStayOutOfSessionList(bool background)
    {
        await using var harness = TestHarness.Create();
        var model = new ConcurrentSubagentModel(4, background);
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root);
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Edit), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "Delegate four independent tasks"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);

        try
        {
            await model.AllChildrenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => Subagents(projection).Count == 4);
            var running = Subagents(projection);
            Assert.Equal(4, running.Count);
            Assert.All(running, state => Assert.Equal(SubagentStatus.Running, state.Status));
            Assert.Equal(4, running.Select(state => state.Id).Distinct().Count());
            Assert.Equal(4, running.Select(state => state.ChildSessionId).Distinct().Count());
            Assert.Equal(4, model.ChildPrompts.Distinct().Count());
            var listed = await runtime.ListSessionsAsync(harness.Root, 1, CancellationToken.None);
            Assert.Equal(session.Id, Assert.Single(listed).Id);
            foreach (var child in running)
            {
                using var childProjection = runtime.TryObserveSubagent(session.Id, child.ChildSessionId);
                Assert.NotNull(childProjection);
            }
            if (background)
            {
                await WaitForPhaseAsync(projection, TurnPhase.Idle);
                Assert.All(projection.Snapshot().Rows.OfType<ToolCallRow>(),
                    row => Assert.Equal(ToolCallStatus.Succeeded, row.Status));
            }
        }
        finally
        {
            model.ReleaseChildren.TrySetResult();
        }

        await WaitForAsync(() => Subagents(projection) is { Count: 4 } states
            && states.All(state => state.Status == SubagentStatus.Completed));
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        var finished = Subagents(projection);
        var calls = projection.Snapshot().Rows.OfType<ToolCallRow>().ToArray();
        Assert.Equal(4, calls.Length);
        Assert.All(calls, row => Assert.Equal(ToolCallStatus.Succeeded, row.Status));
        foreach (var state in finished)
        {
            var call = calls.Single(row => row.Input.GetProperty("description").GetString() == state.Description);
            Assert.Equal(call.Id, state.ParentToolCallId);
            Assert.Equal("general-purpose", state.AgentType);
            var output = string.Join("\n", (call.Output ?? []).OfType<ToolResultText>().Select(text => text.Text));
            Assert.Contains($"agentId: {state.Id.Value}", output);
            Assert.NotNull(state.EndedAt);
        }
        if (background)
        {
            await WaitForAsync(() => finished.All(state => model.Notifications.Any(text =>
                text.Contains($"<task-id>{state.Id.Value}</task-id>", StringComparison.Ordinal)
                && text.Contains($"<tool-use-id>{calls.Single(call => call.Input.GetProperty("description").GetString() == state.Description).Id.Value}</tool-use-id>", StringComparison.Ordinal))));
            await WaitForPhaseAsync(projection, TurnPhase.Idle);
        }
        else
        {
            Assert.Equal(4, model.ParentToolResults.Count);
            foreach (var state in finished)
                Assert.Contains(model.ParentToolResults, result => result.Contains(state.Id.Value, StringComparison.Ordinal));
        }

        var liveOrder = projection.Snapshot().Rows.Select(RowIdentity).ToArray();
        await runtime.DisposeAsync();
        await using var reopened = TestHarness.BuildRuntimeFor(model, harness.Root);
        Assert.Equal(session.Id, Assert.Single(
            await reopened.ListSessionsAsync(null, 1, CancellationToken.None)).Id);
        await reopened.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var restored = reopened.Subscribe(session.Id);
        Assert.Equal(4, Subagents(restored).Count);
        Assert.Equal(liveOrder, restored.Snapshot().Rows.Select(RowIdentity));
        foreach (var state in finished)
        {
            await reopened.ResumeSessionAsync(state.ChildSessionId, CancellationToken.None);
            using var child = reopened.Subscribe(state.ChildSessionId);
            Assert.Contains(child.Snapshot().Rows, row => row is AssistantMessageRow message
                && message.Parts.OfType<TextPart>().Any(part => part.Text.StartsWith("child-result:", StringComparison.Ordinal)));
        }
        Assert.Single(await reopened.ListSessionsAsync(null, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedChild_FinishesParentCall_AndReportsMatchingFailedState(bool background)
    {
        await using var harness = TestHarness.Create();
        var model = new ConcurrentSubagentModel(1, background, failChildren: true);
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root);
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "Delegate a failing task"), CancellationToken.None)).Accepted);
        try
        {
            await model.AllChildrenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            model.ReleaseChildren.TrySetResult();
        }
        await WaitForAsync(() => Subagents(projection) is [ { Status: SubagentStatus.Failed } ]);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        var child = Assert.Single(Subagents(projection));
        var call = Assert.Single(projection.Snapshot().Rows.OfType<ToolCallRow>());
        var output = string.Join("\n", (call.Output ?? []).OfType<ToolResultText>().Select(text => text.Text));
        Assert.Contains(child.Id.Value, output);
        if (background)
        {
            await WaitForAsync(() => model.Notifications.Any(text =>
                text.Contains($"<task-id>{child.Id.Value}</task-id>", StringComparison.Ordinal)
                && text.Contains("<status>failed</status>", StringComparison.Ordinal)));
        }
        else
        {
            Assert.True(call.IsError);
            Assert.Equal(ToolCallStatus.Failed, call.Status);
            Assert.Contains("child rejected request", output);
        }
    }

    [Fact]
    public async Task StopParent_CancelsAllRunningForegroundChildren()
    {
        await using var harness = TestHarness.Create();
        var model = new ConcurrentSubagentModel(4, background: false);
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root);
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "Delegate four tasks then stop"), CancellationToken.None)).Accepted);
        try
        {
            await model.AllChildrenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var ack = await runtime.SubmitAsync(new StopCommand(
                StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
            Assert.True(ack.Accepted, ack.RejectionReason);
            await WaitForAsync(() => Subagents(projection) is { Count: 4 } states
                && states.All(state => state.Status == SubagentStatus.Cancelled));
            await WaitForPhaseAsync(projection, TurnPhase.Idle);
            Assert.All(Subagents(projection), state => Assert.NotNull(state.EndedAt));
        }
        finally
        {
            model.ReleaseChildren.TrySetResult();
        }
    }

    [Fact]
    public async Task SendMessage_ResumesForegroundChild_WithSameIdentityAndCompletionNotification()
    {
        await using var harness = TestHarness.Create();
        var model = new ConcurrentSubagentModel(1, background: false, resumeChild: true);
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root);
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "Delegate then follow up"), CancellationToken.None)).Accepted);
        try
        {
            await model.AllChildrenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => Subagents(projection).Count == 1);
            var original = Assert.Single(Subagents(projection));
            model.ReleaseChildren.TrySetResult();
            await model.ChildResumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var resumed = Assert.Single(Subagents(projection));
            Assert.Equal(original.Id, resumed.Id);
            Assert.Equal(original.ChildSessionId, resumed.ChildSessionId);
            Assert.Equal(new ToolCallId("delegate-0"), original.ParentToolCallId);
            Assert.Equal(original.ParentToolCallId, resumed.ParentToolCallId);
            Assert.Equal(original.AgentType, resumed.AgentType);
            Assert.Equal(SubagentStatus.Running, resumed.Status);
            Assert.Null(resumed.EndedAt);
            await WaitForPhaseAsync(projection, TurnPhase.Idle);
            var send = projection.Snapshot().Rows.OfType<ToolCallRow>().Single(row => row.ToolName == "SendMessage");
            Assert.Equal(ToolCallStatus.Succeeded, send.Status);
            Assert.False(send.IsError);
            Assert.Contains("was stopped (completed)", string.Join("\n", (send.Output ?? []).OfType<ToolResultText>().Select(text => text.Text)));
            model.ReleaseResumedChild.TrySetResult();
            await WaitForAsync(() => model.Notifications.Any(text =>
                text.Contains($"<task-id>{original.Id.Value}</task-id>", StringComparison.Ordinal)
                && text.Contains("<tool-use-id>followup</tool-use-id>", StringComparison.Ordinal)
                && text.Contains("Continue the child task", StringComparison.Ordinal)
                && text.Contains("followup-result", StringComparison.Ordinal)));
            await WaitForAsync(() => Subagents(projection) is [ { Status: SubagentStatus.Completed } ]);
            var completed = Assert.Single(Subagents(projection));
            Assert.Equal(original.Id, completed.Id);
            Assert.Equal(original.ParentToolCallId, completed.ParentToolCallId);
            Assert.Equal(original.AgentType, completed.AgentType);
            Assert.NotNull(completed.EndedAt);
            Assert.Single(await runtime.ListSessionsAsync(null, 10, CancellationToken.None));
        }
        finally
        {
            model.ReleaseChildren.TrySetResult();
            model.ReleaseResumedChild.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildResponse_IsDeliveredToCoordinator_WithoutBecomingUserInput(bool background)
    {
        await using var harness = TestHarness.Create();
        var model = new ConcurrentSubagentModel(1, background, respondToCoordinator: true);
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root);
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "Delegate and request progress"), CancellationToken.None)).Accepted);
        model.ReleaseChildren.TrySetResult();

        await WaitForAsync(() => model.ChildResponses.Any());
        var reply = model.ChildResponses.First();
        var state = Assert.Single(Subagents(projection));
        Assert.Contains($"<agent-id>{state.Id.Value}</agent-id>", reply);
        Assert.Contains("<agent-type>general-purpose</agent-type>", reply);
        Assert.Contains("<summary>Progress update</summary>", reply);
        Assert.Contains("<message>Found &lt;entry&gt; &amp; continuing.</message>", reply);
        Assert.DoesNotContain("[SYSTEM NOTIFICATION - NOT USER INPUT]", reply);
        Assert.DoesNotContain(projection.Snapshot().Rows.OfType<UserMessageRow>(), row => row.Text.Contains("<subagent-message>"));
        await WaitForAsync(() => Subagents(projection) is [ { Status: SubagentStatus.Completed } ]);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        using var childProjection = runtime.TryObserveSubagent(session.Id, state.ChildSessionId);
        Assert.NotNull(childProjection);
        var responseCall = Assert.Single(childProjection.Snapshot().Rows.OfType<ToolCallRow>());
        Assert.Equal("RespondToCoordinator", responseCall.ToolName);
        Assert.Equal(ToolCallStatus.Succeeded, responseCall.Status);
        Assert.Contains("was queued for the coordinator", string.Join("\n", (responseCall.Output ?? []).OfType<ToolResultText>().Select(text => text.Text)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedChild_WithoutNewOutput_DoesNotDeliverPreviousRunOutput(bool failed)
    {
        await using var harness = TestHarness.Create();
        var model = new ConcurrentSubagentModel(1, background: false, resumeChild: true,
            emptyResumedOutput: true, failResumed: failed);
        await using var runtime = TestHarness.BuildRuntimeFor(model, harness.Root);
        var session = await runtime.CreateSessionAsync(
            new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "Delegate and follow up"), CancellationToken.None)).Accepted);
        model.ReleaseChildren.TrySetResult();
        await model.ChildResumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        model.ReleaseResumedChild.TrySetResult();
        await WaitForAsync(() => model.Notifications.Any(text => text.Contains("<tool-use-id>followup</tool-use-id>", StringComparison.Ordinal)));
        var notification = model.Notifications.First(text => text.Contains("<tool-use-id>followup</tool-use-id>", StringComparison.Ordinal));
        Assert.DoesNotContain("child-result:", notification);
        Assert.Contains(failed ? "<status>failed</status>" : "<status>completed</status>", notification);
        if (failed)
            Assert.Contains("resumed child rejected request", notification);
        else
            Assert.DoesNotContain("<result>", notification);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SqliteReplay_ReconstructsLegacyMixedAndExactTimeline_WithoutComparingSequenceDomains(int format)
    {
        await using var harness = TestHarness.Create();
        var sessionId = StrongId.NewSessionId();
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var turn = StrongId.NewTurnId();
        var call1 = new ToolCallId("first-call");
        var call2 = new ToolCallId("parallel-call");
        var input = JsonSerializer.SerializeToElement(new { prompt = "research" });
        var first = new SubagentState(StrongId.NewSubagentId(), StrongId.NewSessionId(), "first",
            SubagentStatus.Running, null, now, null, ParentToolCallId: call1, AgentType: "Explore");
        var parallel = first with { Id = StrongId.NewSubagentId(), ChildSessionId = StrongId.NewSessionId(),
            Description = "parallel", ParentToolCallId = call2 };
        var messages = new List<ConversationMessage>();
        var events = new List<SequencedEvent>();
        var expected = new ProjectionBuilder();
        long sequence = 1000;
        var offset = 0;
        void Add(SessionEvent sessionEvent)
        {
            var at = format == 2 ? now : now.AddSeconds(offset++);
            if (format == 4 && sessionEvent is (UserMessageCommittedEvent or AssistantMessageCommittedEvent)
                && messages.Count % 2 == 1)
                at = messages.Count == 1 ? now.AddDays(1) : now.AddDays(-1);
            var message = sessionEvent switch
            {
                UserMessageCommittedEvent user => user.Message,
                AssistantMessageCommittedEvent assistant => assistant.Message,
                _ => null,
            };
            if (message is not null)
            {
                message = message with { CreatedAt = at };
                messages.Add(message);
                sessionEvent = message.Role == MessageRole.User
                    ? new UserMessageCommittedEvent(message) : new AssistantMessageCommittedEvent(message);
            }
            var entry = new SequencedEvent(sequence += 7, sessionId, at, sessionEvent);
            expected.Apply(entry);
            if (format == 3 && sessionEvent is ToolCallStartedEvent or ToolCallResultEvent)
                return;
            if (message is null || format is 2 or 4 || format == 1 && messages.Count % 2 == 0)
                events.Add(entry);
        }
        ConversationMessage Text(MessageRole role, string text) => new(StrongId.NewMessageId(), role,
            [new TextPart(StrongId.NewPartId(), text)], now);
        Add(new UserMessageCommittedEvent(Text(MessageRole.User, "turn one")));
        Add(new AssistantMessageCommittedEvent(new ConversationMessage(StrongId.NewMessageId(), MessageRole.Assistant,
            [new ToolCallPart(StrongId.NewPartId(), call1, "Agent", input),
             new ToolCallPart(StrongId.NewPartId(), call2, "Agent", input)], now)));
        Add(new ToolCallStartedEvent(turn, call1, "Agent", input));
        Add(new SubagentStartedEvent(first));
        Add(new ToolCallStartedEvent(turn, call2, "Agent", input));
        Add(new SubagentStartedEvent(parallel));
        Add(new UserMessageCommittedEvent(Text(MessageRole.User, "progress during turn")));
        Add(new SubagentStatusChangedEvent(first with { Status = SubagentStatus.Completed, ResultSummary = "done" }));
        Add(new ToolCallResultEvent(turn, call1, "Agent", ToolExecutionResult.Text("first result"), TimeSpan.FromSeconds(2)));
        Add(new SubagentStatusChangedEvent(parallel with { Status = SubagentStatus.Failed, ResultSummary = "child failed" }));
        Add(new ToolCallResultEvent(turn, call2, "Agent", ToolExecutionResult.Text("parallel result", isError: true), TimeSpan.FromSeconds(3)));
        Add(new UserMessageCommittedEvent(new ConversationMessage(StrongId.NewMessageId(), MessageRole.User,
            [new ToolResultPart(StrongId.NewPartId(), call1, [new ToolResultText("first result")]),
             new ToolResultPart(StrongId.NewPartId(), call2, [new ToolResultText("parallel result")], IsError: true)], now)));
        Add(new AssistantMessageCommittedEvent(Text(MessageRole.Assistant, "turn one done")));
        Add(new TurnCompletedEvent(turn, TurnOutcome.Completed));
        Add(new UserMessageCommittedEvent(Text(MessageRole.User, "turn two")));
        Add(new TurnFailedEvent(StrongId.NewTurnId(), "parent failed"));
        Add(new TurnCompletedEvent(turn, TurnOutcome.Failed));
        Add(new UserMessageCommittedEvent(Text(MessageRole.User, "turn three")));
        Add(new AssistantMessageCommittedEvent(Text(MessageRole.Assistant, "turn three done")));
        Add(new SubagentStatusChangedEvent(first with { Status = SubagentStatus.Running }));
        Add(new SubagentStatusChangedEvent(first with { Status = SubagentStatus.Completed, ResultSummary = "resumed" }));
        Add(new TurnCompletedEvent(turn, TurnOutcome.Completed));

        await using (var store = new SqliteSessionStore(harness.DatabasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await store.UpsertSessionAsync(new SessionRecord(sessionId, harness.Root, "Replay", PermissionMode.Yolo,
                null, now, now), CancellationToken.None);
            await store.AppendMessagesAsync(sessionId, messages, 50_000, CancellationToken.None);
            await store.AppendDurableEventsAsync(events.AsEnumerable().Reverse().ToArray(), CancellationToken.None);
        }
        for (var reopen = 0; reopen < 2; reopen++)
        {
            await using var store = new SqliteSessionStore(harness.DatabasePath);
            await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, store);
            await runtime.ResumeSessionAsync(sessionId, CancellationToken.None);
            using var projection = runtime.Subscribe(sessionId);
            var restored = projection.Snapshot();
            Assert.Equal(expected.Rows().Select(RowIdentity), restored.Rows.Select(RowIdentity));
            Assert.Equal(TurnPhase.Idle, restored.State.Phase);
            Assert.Equal(first.ParentToolCallId, Subagents(projection)[0].ParentToolCallId);
            Assert.Equal("Explore", Subagents(projection)[0].AgentType);
            Assert.Equal("resumed", Subagents(projection)[0].ResultSummary);
            Assert.Equal(SubagentStatus.Failed, Subagents(projection)[1].Status);
            Assert.Equal(format == 3 ? null : TimeSpan.FromSeconds(2), restored.Rows.OfType<ToolCallRow>().First().Duration);
            Assert.Equal(events.Count, (await store.LoadDurableEventsAsync(sessionId, CancellationToken.None)).Count);
        }
    }

    [Fact]
    public async Task SqliteReplay_UsesCanonicalMessages_WithoutResurrectingCompactedEnvelopesOrTools()
    {
        await using var harness = TestHarness.Create();
        var now = DateTimeOffset.UtcNow;
        var sessionId = StrongId.NewSessionId();
        var callId = new ToolCallId("removed-call");
        var stale = new ConversationMessage(StrongId.NewMessageId(), MessageRole.Assistant,
            [new ToolCallPart(StrongId.NewPartId(), callId, "Read", JsonSerializer.SerializeToElement(new { }))], now);
        var retained = new ConversationMessage(StrongId.NewMessageId(), MessageRole.Assistant,
            [new TextPart(StrongId.NewPartId(), "old content")], now);
        var canonical = retained with { Parts = [new TextPart(StrongId.NewPartId(), "canonical content")] };
        await using (var store = new SqliteSessionStore(harness.DatabasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await store.UpsertSessionAsync(new SessionRecord(sessionId, harness.Root, "Compacted", PermissionMode.Yolo,
                null, now, now), CancellationToken.None);
            await store.AppendMessagesAsync(sessionId, [canonical], 0, CancellationToken.None);
            await store.AppendDurableEventsAsync([
                new SequencedEvent(10, sessionId, now, new AssistantMessageCommittedEvent(stale)),
                new SequencedEvent(11, sessionId, now, new ToolCallStartedEvent(StrongId.NewTurnId(), callId, "Read",
                    JsonSerializer.SerializeToElement(new { }))),
                new SequencedEvent(20, sessionId, now, new AssistantMessageCommittedEvent(retained)),
                new SequencedEvent(21, sessionId, now, new AssistantMessageCommittedEvent(retained))], CancellationToken.None);
        }
        await using var reopenedStore = new SqliteSessionStore(harness.DatabasePath);
        await using var runtime = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, reopenedStore);
        await runtime.ResumeSessionAsync(sessionId, CancellationToken.None);
        using var projection = runtime.Subscribe(sessionId);
        var row = Assert.IsType<AssistantMessageRow>(Assert.Single(projection.Snapshot().Rows));
        Assert.Equal(canonical.Id, row.Id);
        Assert.Equal("canonical content", Assert.IsType<TextPart>(Assert.Single(row.Parts)).Text);
    }

    [Fact]
    public async Task ActualTurns_WithTiedTimestampsAndFailure_PreserveRowChronologyAcrossSqliteRestart()
    {
        await using var harness = TestHarness.Create(
            ToolCall("Agent", new { prompt = "child task", description = "reader", subagent_type = "Explore" }),
            FinalText("child answer"),
            FinalText("first answer"),
            new ScriptStep.Events(new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest, "second turn failed"))),
            FinalText("third answer"));
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        await using var runtime = harness.BuildRuntime(options => options with { Clock = clock });
        var session = await runtime.CreateSessionAsync(new SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);
        for (var turn = 1; turn <= 3; turn++)
        {
            Assert.True((await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), session.Id, 0, $"turn {turn}"), CancellationToken.None)).Accepted);
            await WaitForAsync(() => projection.Snapshot().Rows.OfType<UserMessageRow>().Count() == turn
                && projection.Snapshot().State.Phase == TurnPhase.Idle);
        }
        var expected = projection.Snapshot().Rows.Select(RowIdentity).ToArray();
        Assert.Contains(expected, identity => identity == "turn_error:second turn failed");
        Assert.Equal("Explore", Assert.Single(Subagents(projection)).AgentType);
        await runtime.DisposeAsync();
        await using var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        var persisted = await store.LoadDurableEventsAsync(session.Id, CancellationToken.None);
        Assert.Equal((await store.LoadMessagesAsync(session.Id, CancellationToken.None)).Count,
            persisted.Count(entry => entry.Event is UserMessageCommittedEvent or AssistantMessageCommittedEvent));
        Assert.All(persisted, entry => Assert.Equal(clock.GetUtcNow(), entry.OccurredAt));
        for (var reopen = 0; reopen < 2; reopen++)
        {
            await using var reopenedStore = new SqliteSessionStore(harness.DatabasePath);
            await using var reopened = TestHarness.BuildRuntimeFor(harness.ScriptedModel, harness.Root, reopenedStore);
            await reopened.ResumeSessionAsync(session.Id, CancellationToken.None);
            using var restored = reopened.Subscribe(session.Id);
            Assert.Equal(expected, restored.Snapshot().Rows.Select(RowIdentity));
        }
    }

    private static string RowIdentity(ConversationRow row) => row switch
    {
        SystemNoticeRow { Kind: "turn_error" } notice => $"turn_error:{notice.Text}",
        _ => $"{row.GetType().Name}:{row.RowId}",
    };

    private static IReadOnlyList<SubagentState> Subagents(IConversationProjection projection) =>
        projection.Snapshot().Rows.OfType<SystemNoticeRow>()
            .Select(row => row.Subagent).OfType<SubagentState>().ToArray();

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class ConcurrentSubagentModel(
        int childCount, bool background, bool failChildren = false, bool resumeChild = false,
        bool respondToCoordinator = false, bool emptyResumedOutput = false, bool failResumed = false) : IChatModel
    {
        private int _parentRequests;
        private int _childrenStarted;

        public ModelDescriptor Descriptor { get; } = new(
            "scripted", "test-model", "Concurrent Subagents", new ModelProperties(ContextWindowTokens: 200_000));

        public TaskCompletionSource AllChildrenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseChildren { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ChildResumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseResumedChild { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> ChildPrompts { get; } = new();

        public ConcurrentQueue<string> ParentToolResults { get; } = new();

        public ConcurrentQueue<string> Notifications { get; } = new();

        public ConcurrentQueue<string> ChildResponses { get; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (request.Tools.Any(tool => tool.Name == "Agent"))
            {
                foreach (var reply in request.Messages.Where(message => message.Role == MessageRole.User)
                    .SelectMany(message => message.Content).OfType<InputTextBlock>()
                    .Where(block => block.Text.Contains("<subagent-message>", StringComparison.Ordinal)))
                    ChildResponses.Enqueue(reply.Text);
                foreach (var notification in request.Messages.Where(message => message.Role == MessageRole.User)
                    .SelectMany(message => message.Content).OfType<InputTextBlock>()
                    .Where(block => block.Text.Contains("<task-notification>", StringComparison.Ordinal)))
                    Notifications.Enqueue(notification.Text);
                var parentRequest = Interlocked.Increment(ref _parentRequests);
                if (parentRequest == 1)
                {
                    for (var index = 0; index < childCount; index++)
                    {
                        var id = new ToolCallId($"delegate-{index}");
                        var input = JsonSerializer.SerializeToElement(new
                        {
                            description = $"child-{index}",
                            prompt = $"task-{index}",
                            run_in_background = background,
                        });
                        yield return new ModelToolInputStarted(id, "Agent");
                        yield return new ModelToolInputDelta(id, input.GetRawText());
                        yield return new ModelToolInputEnded(id);
                        yield return new ModelToolCallReady(new ToolCall(id, "Agent", input));
                    }
                    yield return new ModelStreamFinished(ModelFinishReason.ToolCalls);
                    yield break;
                }
                foreach (var result in request.Messages.SelectMany(message => message.Content).OfType<InputToolResultBlock>())
                    ParentToolResults.Enqueue(result.Content);
                if (resumeChild && parentRequest == 2)
                {
                    var result = request.Messages.SelectMany(message => message.Content).OfType<InputToolResultBlock>().Single();
                    var idLine = result.Content.Split('\n').Single(line => line.StartsWith("agentId: ", StringComparison.Ordinal));
                    var agentId = idLine["agentId: ".Length..].Split(' ')[0];
                    var callId = new ToolCallId("followup");
                    var input = JsonSerializer.SerializeToElement(new
                    {
                        to = agentId,
                        summary = "Continue the child task",
                        message = "followup-task",
                    });
                    yield return new ModelToolInputStarted(callId, "SendMessage");
                    yield return new ModelToolInputDelta(callId, input.GetRawText());
                    yield return new ModelToolInputEnded(callId);
                    yield return new ModelToolCallReady(new ToolCall(callId, "SendMessage", input));
                    yield return new ModelStreamFinished(ModelFinishReason.ToolCalls);
                    yield break;
                }
                yield return new ModelTextDelta("Parent finished.");
                yield return new ModelStreamFinished(ModelFinishReason.Stop);
                yield break;
            }

            if (resumeChild && request.Messages.Where(message => message.Role == MessageRole.User)
                .SelectMany(message => message.Content).OfType<InputTextBlock>().Any(block => block.Text == "followup-task"))
            {
                ChildResumed.TrySetResult();
                await ReleaseResumedChild.Task.WaitAsync(cancellationToken);
                if (failResumed)
                {
                    yield return new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest, "resumed child rejected request"));
                    yield break;
                }
                if (!emptyResumedOutput)
                    yield return new ModelTextDelta("followup-result");
                yield return new ModelStreamFinished(ModelFinishReason.Stop);
                yield break;
            }
            if (respondToCoordinator
                && !request.Messages.SelectMany(message => message.Content).OfType<InputToolResultBlock>().Any())
            {
                var callId = new ToolCallId("child-response");
                var input = JsonSerializer.SerializeToElement(new
                {
                    summary = "Progress update",
                    message = "Found <entry> & continuing.",
                });
                yield return new ModelToolInputStarted(callId, "RespondToCoordinator");
                yield return new ModelToolInputDelta(callId, input.GetRawText());
                yield return new ModelToolInputEnded(callId);
                yield return new ModelToolCallReady(new ToolCall(callId, "RespondToCoordinator", input));
                yield return new ModelStreamFinished(ModelFinishReason.ToolCalls);
                yield break;
            }
            var prompt = request.Messages.Where(message => message.Role == MessageRole.User)
                .SelectMany(message => message.Content).OfType<InputTextBlock>()
                .Last(block => block.Text.StartsWith("task-", StringComparison.Ordinal)).Text;
            ChildPrompts.Enqueue(prompt);
            if (Interlocked.Increment(ref _childrenStarted) == childCount)
                AllChildrenStarted.TrySetResult();
            await ReleaseChildren.Task.WaitAsync(cancellationToken);
            if (failChildren)
            {
                yield return new ModelStreamFailed(new ModelFailure(ModelFailureKind.InvalidRequest, "child rejected request"));
                yield break;
            }
            yield return new ModelTextDelta($"child-result:{prompt}");
            yield return new ModelStreamFinished(ModelFinishReason.Stop);
        }
    }

    private static async Task WaitForPhaseAsync(IConversationProjection projection, TurnPhase phase)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != phase || !snapshot.Rows.OfType<UserMessageRow>().Any()))
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException($"Phase never reached {phase} (current: {projection.Snapshot().State.Phase}).");
            }
            await Task.Delay(20);
        }
    }
}
