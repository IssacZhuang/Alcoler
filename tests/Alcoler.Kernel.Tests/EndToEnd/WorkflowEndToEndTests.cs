using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Workflows;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class WorkflowEndToEndTests
{
    private static ScriptStep FinalText(params string[] texts) => new ScriptStep.Events(
        [.. texts.Select(t => new ModelTextDelta(t)),
        new ModelStreamFinished(ModelFinishReason.Stop)]);

    private static string RequestText(ModelRequest request) =>
        string.Join("\n", request.Messages.SelectMany(m => m.Content.OfType<InputTextBlock>().Select(b => b.Text)));

    [Fact]
    public async Task ActivateOrchestratorViaCommand_InjectsSystemSectionAndFullReminder()
    {
        await using var harness = TestHarness.Create(
            FinalText("Orchestrating."),
            FinalText("Still orchestrating."));
        var capturing = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        await using var runtime = TestHarness.BuildRuntimeFor(capturing, harness.Root);

        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        var ack = await runtime.SubmitAsync(new SetWorkflowsCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, [OrchestratorWorkflow.Id]),
            CancellationToken.None);
        Assert.True(ack.Accepted);
        await WaitForSequenceAsync(projection, ack.LastEventSequence);
        var workflows = projection.Snapshot().State.ActiveWorkflows;
        Assert.NotNull(workflows);
        Assert.Contains(OrchestratorWorkflow.Id, workflows);

        var sendAck = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, "refactor the storage layer"),
            CancellationToken.None);
        Assert.True(sendAck.Accepted);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var requestText = RequestText(Assert.Single(capturing.Requests));
        Assert.Contains("# Orchestrator mode", requestText);
        Assert.Contains("Orchestrator mode is active.", requestText);
        Assert.Contains(WorkflowRegistry.ReminderMarker(OrchestratorWorkflow.Id), requestText);
    }

    [Fact]
    public async Task DeactivateOrchestrator_AttachesExitReminderAndDropsSection()
    {
        await using var harness = TestHarness.Create(
            FinalText("Orchestrating."),
            FinalText("Working directly."));
        var capturing = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        await using var runtime = TestHarness.BuildRuntimeFor(capturing, harness.Root);

        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, ActiveWorkflows: [OrchestratorWorkflow.Id]),
            CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        var sendAck = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "refactor the storage layer"),
            CancellationToken.None);
        Assert.True(sendAck.Accepted);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        await WaitForStateAsync(projection, state => state.ActiveWorkflows?.Contains(OrchestratorWorkflow.Id) == true);

        var clearAck = await runtime.SubmitAsync(new SetWorkflowsCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, []),
            CancellationToken.None);
        Assert.True(clearAck.Accepted);
        await WaitForSequenceAsync(projection, clearAck.LastEventSequence);
        Assert.Empty(projection.Snapshot().State.ActiveWorkflows ?? []);

        var secondAck = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, clearAck.Revision, "continue"),
            CancellationToken.None);
        Assert.True(secondAck.Accepted);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        Assert.Equal(2, capturing.Requests.Count);
        var requestText = RequestText(capturing.Requests[1]);
        Assert.Contains("Exited Orchestrator Mode", requestText);
        Assert.DoesNotContain("# Orchestrator mode", requestText);
        Assert.DoesNotContain("Orchestrator mode still active", requestText);
        // The first turn's reminder stays in history; deactivation must not re-attach one.
        Assert.Equal(1, CountOccurrences(requestText, WorkflowRegistry.ReminderMarker(OrchestratorWorkflow.Id)));
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }

    [Fact]
    public async Task SetWorkflows_RejectsUnknownWorkflow()
    {
        await using var harness = TestHarness.Create(FinalText("Unrelated."));
        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        var ack = await runtime.SubmitAsync(new SetWorkflowsCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, ["turbo"]),
            CancellationToken.None);
        Assert.False(ack.Accepted);
        Assert.Contains("unknown workflow", ack.RejectionReason);
    }

    [Fact]
    public async Task SendTextWithWorkflows_AppliesOnSend()
    {
        await using var harness = TestHarness.Create(FinalText("Orchestrating."));
        var capturing = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        await using var runtime = TestHarness.BuildRuntimeFor(capturing, harness.Root);

        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        using var projection = runtime.Subscribe(session.Id);

        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision,
            "refactor the storage layer", Workflows: [OrchestratorWorkflow.Id]),
            CancellationToken.None);
        Assert.True(ack.Accepted);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        await WaitForStateAsync(projection, state => state.ActiveWorkflows?.Contains(OrchestratorWorkflow.Id) == true);

        Assert.Contains("Orchestrator mode is active.", RequestText(Assert.Single(capturing.Requests)));
    }

    [Fact]
    public async Task ActiveWorkflows_SurviveRuntimeRestart()
    {
        await using var harness = TestHarness.Create(
            FinalText("Orchestrating."),
            FinalText("Resumed."));
        var capturing = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        var store = new Alcoler.Adapters.Storage.SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = TestHarness.BuildRuntimeFor(capturing, harness.Root, store);

        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root), CancellationToken.None);
        var setAck = await runtime.SubmitAsync(new SetWorkflowsCommand(
            StrongId.NewCommandId(), session.Id, 0, [OrchestratorWorkflow.Id]), CancellationToken.None);
        Assert.True(setAck.Accepted);
        var sendAck = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, setAck.Revision, "refactor the storage layer"),
            CancellationToken.None);
        Assert.True(sendAck.Accepted);
        using (var projection = runtime.Subscribe(session.Id))
        {
            await WaitForPhaseAsync(projection, TurnPhase.Idle);
        }
        await runtime.DisposeAsync();

        var resuming = new TestHarness.RequestCapturingModel(harness.ScriptedModel);
        await using var resumed = TestHarness.BuildRuntimeFor(resuming, harness.Root, store);
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = resumed.Subscribe(session.Id);
        Assert.Equal([OrchestratorWorkflow.Id], resumedProjection.Snapshot().State.ActiveWorkflows);

        var secondAck = await resumed.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, resumedProjection.Snapshot().Revision, "continue"),
            CancellationToken.None);
        Assert.True(secondAck.Accepted);
        await WaitForPhaseAsync(resumedProjection, TurnPhase.Idle);

        Assert.Contains("# Orchestrator mode", RequestText(Assert.Single(resuming.Requests)));
        // One human turn since the first reminder: below the cadence threshold, so no re-attach.
        Assert.DoesNotContain("Orchestrator mode still active", RequestText(Assert.Single(resuming.Requests)));
    }

    private static async Task WaitForPhaseAsync(IConversationProjection projection, TurnPhase phase)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.Phase != phase)
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException($"Phase never reached {phase} (current: {projection.Snapshot().State.Phase}).");
            }
            await Task.Delay(20);
        }
    }

    // The ack carries the store tail its effects were appended under; awaiting that
    // sequence in the projection is exact synchronization, no predicate polling.
    private static async Task WaitForSequenceAsync(IConversationProjection projection, long sequence)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await projection.WaitUntilSequenceAsync(sequence, timeout.Token);
    }

    // Projection state is pumped asynchronously from the event store, so it can lag the command ack.
    private static async Task WaitForStateAsync(IConversationProjection projection, Func<ConversationState, bool> predicate)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (!predicate(projection.Snapshot().State))
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException("Projection state never satisfied the expected condition.");
            }
            await Task.Delay(20);
        }
    }
}
