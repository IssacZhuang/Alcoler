using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.EndToEnd;

public class PlanModeEndToEndTests
{
    private static ScriptStep ToolCall(string name, object input) => new ScriptStep.Events(
        new Alcoler.Contracts.Models.ModelTextDelta("Presenting the plan."),
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
    public async Task SetMode_WithOnlyMode_LeavesPlanEnabledUnchanged()
    {
        await using var harness = TestHarness.Create(FinalText("Still planning."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SetModeCommand(
            StrongId.NewCommandId(), session.Id, 0, Mode: PermissionMode.Edit), CancellationToken.None)).Accepted);
        await WaitForModeAsync(projection, PermissionMode.Edit);
        Assert.True(projection.Snapshot().State.PlanEnabled);
    }

    [Fact]
    public async Task SetMode_WithExplicitPlanDisabled_ExitsPlanWithoutChangingMode()
    {
        await using var harness = TestHarness.Create(FinalText("Implementing now."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SetModeCommand(
            StrongId.NewCommandId(), session.Id, 0, PlanEnabled: false), CancellationToken.None)).Accepted);
        await WaitForPlanEnabledAsync(projection, expected: false);
        Assert.Equal(PermissionMode.Yolo, projection.Snapshot().State.Mode);
        Assert.False(projection.Snapshot().State.PlanEnabled);
    }

    [Fact]
    public async Task PlanApproval_PreservesPermissionModeSelectedBeforePlanning()
    {
        await using var harness = TestHarness.Create(
            ToolCall("ExitPlanMode", new { plan = "1. Write the file.\n2. Verify." }),
            FinalText("Implementing now."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        Assert.Equal(PermissionMode.Yolo, projection.Snapshot().State.Mode);
        Assert.True(projection.Snapshot().State.PlanEnabled);

        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Plan writing notes.txt"),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        var permission = await WaitForPermissionAsync(projection);
        await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        Assert.Equal(PermissionMode.Yolo, snapshot.State.Mode);
        Assert.False(snapshot.State.PlanEnabled);
    }

    [Fact]
    public async Task PlanMode_RunsReadOnlyBashWithoutAsking()
    {
        await using var harness = TestHarness.Create(
            ToolCall("Bash", new { command = "ls -la" }),
            FinalText("Inspected the directory."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "inspect the directory"),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        await WaitForAssistantTextAsync(projection, "Inspected the directory.");
        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        var call = Assert.Single(projection.Snapshot().Rows.OfType<ToolCallRow>());
        Assert.Equal(ToolCallStatus.Succeeded, call.Status);
        Assert.False(call.IsError);
    }

    [Fact]
    public async Task PlanMode_DeniesSideEffectingBashWithoutAsking()
    {
        await using var harness = TestHarness.Create(
            ToolCall("Bash", new { command = "mkdir build" }),
            FinalText("The write was denied."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "create a directory"),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);
        var call = Assert.Single(projection.Snapshot().Rows.OfType<ToolCallRow>());
        Assert.Equal(ToolCallStatus.Failed, call.Status);
        Assert.True(call.IsError);
    }

    [Fact]
    public async Task PlanApproval_PreservesModeAndWritesPlanFile()
    {
        const string plan = "1. Write notes.txt.\n2. Verify the contents.";
        await using var harness = TestHarness.Create(
            ToolCall("ExitPlanMode", new { plan }),
            FinalText("Implementing now."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SetModeCommand(
            StrongId.NewCommandId(), session.Id, 0, PlanEnabled: true), CancellationToken.None)).Accepted);
        await WaitForPlanEnabledAsync(projection, expected: true);

        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Plan the work"), CancellationToken.None);
        Assert.True(ack.Accepted);

        var permission = await WaitForPermissionAsync(projection);
        await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        Assert.Equal(PermissionMode.Yolo, projection.Snapshot().State.Mode);
        Assert.False(projection.Snapshot().State.PlanEnabled);
        var planPath = Path.Combine(harness.Root, "plans", $"plan-{session.Id.Value}.md");
        Assert.True(File.Exists(planPath));
        Assert.Equal(plan, await File.ReadAllTextAsync(planPath));

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = resumed.Subscribe(session.Id);
        Assert.Equal(PermissionMode.Yolo, resumedProjection.Snapshot().State.Mode);
        Assert.False(resumedProjection.Snapshot().State.PlanEnabled);
    }

    [Fact]
    public async Task PlanApproval_UsesPermissionModeChangedWhilePlanning()
    {
        await using var harness = TestHarness.Create(
            ToolCall("ExitPlanMode", new { plan = "1. Write the file." }),
            FinalText("Implementing now."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        Assert.True((await runtime.SubmitAsync(new SetModeCommand(
            StrongId.NewCommandId(), session.Id, 0, Mode: PermissionMode.Edit, PlanEnabled: true),
            CancellationToken.None)).Accepted);
        await WaitForModeAsync(projection, PermissionMode.Edit);
        Assert.True(projection.Snapshot().State.PlanEnabled);

        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Plan the work"),
            CancellationToken.None);
        Assert.True(ack.Accepted);
        var permission = await WaitForPermissionAsync(projection);
        await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        Assert.Equal(PermissionMode.Edit, projection.Snapshot().State.Mode);
        Assert.False(projection.Snapshot().State.PlanEnabled);
    }

    [Fact]
    public async Task Resume_WhilePlanIsActive_PreservesModeAndPlanFlag()
    {
        await using var harness = TestHarness.Create(FinalText("Still planning."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Keep planning"),
            CancellationToken.None);
        Assert.True(ack.Accepted);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        await using var resumed = harness.BuildRuntime();
        await resumed.ResumeSessionAsync(session.Id, CancellationToken.None);
        using var resumedProjection = resumed.Subscribe(session.Id);
        Assert.Equal(PermissionMode.Yolo, resumedProjection.Snapshot().State.Mode);
        Assert.True(resumedProjection.Snapshot().State.PlanEnabled);
    }

    [Fact]
    public async Task PlanDenialWithFeedback_KeepsPlanningAndSteersFeedbackIntoSameTurn()
    {
        await using var harness = TestHarness.Create(
            ToolCall("ExitPlanMode", new { plan = "1. Write the file." }),
            FinalText("I will revise the plan."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "plan something"),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        var permission = await WaitForPermissionAsync(projection);
        await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: new PermissionDecision(PermissionOutcome.Denied, Feedback: "Use the existing parser instead.")),
            CancellationToken.None);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        Assert.Equal(PermissionMode.Yolo, snapshot.State.Mode);
        Assert.True(snapshot.State.PlanEnabled);
        Assert.Equal(
            ["plan something", "Use the existing parser instead."],
            snapshot.Rows.OfType<UserMessageRow>().Select(row => row.Text).ToArray());
        Assert.Contains(snapshot.Rows, r => r is AssistantMessageRow a
            && a.Parts.OfType<Contracts.Conversation.TextPart>().Any(p => p.Text.Contains("I will revise the plan.")));
    }

    [Fact]
    public async Task PlanApproval_ReinjectsThePlanFileAfterCompaction()
    {
        const string plan = "1. Add the parser.\n2. Verify the tests.";
        await using var harness = TestHarness.Create(
            ToolCall("ExitPlanMode", new { plan }),
            FinalText("Implementing now."),
            FinalText("Follow-up complete."),
            new ScriptStep.Events(
                new Alcoler.Contracts.Models.ModelTextDelta("summary"),
                new Alcoler.Contracts.Models.ModelTextEnded(),
                new Alcoler.Contracts.Models.ModelStreamFinished(Alcoler.Contracts.Models.ModelFinishReason.Stop)));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "Plan the parser"), CancellationToken.None);
        Assert.True(ack.Accepted);
        var permission = await WaitForPermissionAsync(projection);
        await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Allow), CancellationToken.None);
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        Assert.True((await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "implement it"), CancellationToken.None)).Accepted);
        await WaitForAssistantTextAsync(projection, "Follow-up complete.");
        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var compactAck = await runtime.SubmitAsync(
            new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
        Assert.True(compactAck.Accepted, compactAck.RejectionReason);

        await using var store = new SqliteSessionStore(harness.DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        var messages = await store.LoadMessagesAsync(session.Id, CancellationToken.None);
        var planReference = Assert.Single(
            messages.SelectMany(message => message.Message.Parts).OfType<AttachmentPart>(),
            part => part.Kind == AttachmentKind.ResumeReferencedContext);
        Assert.Contains(plan, planReference.Content);
        Assert.Contains($"plan-{session.Id.Value}.md", planReference.Content);
    }

    [Fact]
    public async Task PlanDenial_KeepsPlanMode_ToolNeverRuns()
    {
        await using var harness = TestHarness.Create(
            ToolCall("ExitPlanMode", new { plan = "1. Write the file." }),
            FinalText("Staying in plan mode."));

        await using var runtime = harness.BuildRuntime();
        var session = await runtime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo,
                PlanEnabled: true),
            CancellationToken.None);

        using var projection = runtime.Subscribe(session.Id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, "plan something"),
            CancellationToken.None);
        Assert.True(ack.Accepted);

        var permission = await WaitForPermissionAsync(projection);
        await runtime.SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), session.Id, ack.Revision, permission.InteractionId,
            PermissionDecision: PermissionDecision.Deny), CancellationToken.None);

        await WaitForPhaseAsync(projection, TurnPhase.Idle);

        var snapshot = projection.Snapshot();
        Assert.Equal(PermissionMode.Yolo, snapshot.State.Mode);
        Assert.True(snapshot.State.PlanEnabled);
        Assert.Contains(snapshot.Rows, r => r is ToolCallRow t
            && t.ToolName == "ExitPlanMode" && t.Status == ToolCallStatus.Failed);
    }

    private static async Task<PermissionRequest> WaitForPermissionAsync(IConversationProjection projection)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.PendingPermission is null)
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException("Permission request never arrived.");
            }
            await Task.Delay(20);
        }
        return projection.Snapshot().State.PendingPermission!;
    }

    private static async Task WaitForAssistantTextAsync(IConversationProjection projection, string text)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (!projection.Snapshot().Rows.OfType<AssistantMessageRow>().Any(row =>
                   row.Parts.OfType<Contracts.Conversation.TextPart>().Any(part => part.Text.Contains(text))))
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException($"Assistant text was never committed: {text}");
            }
            await Task.Delay(20);
        }
    }

    private static async Task WaitForModeAsync(IConversationProjection projection, PermissionMode expected)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.Mode != expected)
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException(
                    $"Mode never reached {expected} (current: {projection.Snapshot().State.Mode}).");
            }
            await Task.Delay(20);
        }
    }

    private static async Task WaitForPlanEnabledAsync(IConversationProjection projection, bool expected)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.PlanEnabled != expected)
        {
            if (deadline.IsCompleted)
            {
                throw new TimeoutException(
                    $"PlanEnabled never reached {expected} (current: {projection.Snapshot().State.PlanEnabled}).");
            }
            await Task.Delay(20);
        }
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
}
