using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Workbench.Stores;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Xunit;

namespace Alcoler.Workbench.Tests;

public class ConversationViewModelTests
{
    private static (ConversationViewModel ViewModel, FakeAgentRuntime Runtime, ProjectionStore Store) Create()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = new SessionId(Guid.NewGuid().ToString("N"));
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var store = new ProjectionStore(
            () => projection,
            sessionId,
            InlineUiScheduler.Instance,
            TestSnapshots.State());
        var viewModel = new ConversationViewModel(runtime, store);
        return (viewModel, runtime, store);
    }

    private static ConversationState StateWithPermission(PermissionRequest permission, TurnPhase phase = TurnPhase.AwaitingPermission) =>
        new("Test session", PermissionMode.Build, null, 200_000, phase, null,
            100_000, "estimate", [], permission, null);

    private static PermissionRequest Permission(string tool = "WriteFile", RiskLevel risk = RiskLevel.Medium) => new(
        StrongId.NewInteractionId(),
        new SessionId(Guid.NewGuid().ToString("N")),
        tool,
        JsonSerializer.SerializeToElement(new { file_path = "a.txt" }),
        risk,
        "test reason");

    [Fact]
    public void ApplyOps_AppendsUpsertsAndRemoves()
    {
        var (viewModel, _, store) = Create();
        viewModel.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(),
        [
            new RowAppended(new UserMessageRow("u1", "hello", DateTimeOffset.UtcNow)),
            new RowAppended(new AssistantMessageRow("a1", StrongId.NewMessageId(), [], true, DateTimeOffset.UtcNow)),
        ], Reset: false));

        Assert.Equal(2, viewModel.Items.Count);

        viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(TurnPhase.Streaming),
        [
            new RowUpserted(new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), "world")], false, DateTimeOffset.UtcNow)),
            new RowRemoved("u1"),
        ], Reset: false));

        var assistant = Assert.IsType<AssistantMessageItemViewModel>(Assert.Single(viewModel.Items));
        Assert.Equal("world", assistant.MarkdownText);
        Assert.False(assistant.IsStreaming);
        store.Dispose();
    }

    [Fact]
    public void StatePatch_UpdatesComposerPendingInteractionAndContextUsage()
    {
        var (viewModel, _, store) = Create();
        var state = StateWithPermission(Permission());
        viewModel.ApplyUpdate(new ProjectionUpdate(2, state, [new StatePatched(state)], Reset: false));

        Assert.True(viewModel.Composer.IsTurnActive);
        Assert.False(viewModel.Composer.CanSend);
        Assert.Equal(50, viewModel.ContextUsagePercent);
        Assert.True(viewModel.HasPendingInteraction);
        Assert.Equal("WriteFile", viewModel.PendingInteraction!.ToolName);
        store.Dispose();
    }

    [Fact]
    public async Task SendAsync_SubmitsCommandAndClearsDraftOnAccept()
    {
        var (viewModel, runtime, store) = Create();
        viewModel.Composer.Draft = "do the thing";

        await viewModel.SendAsync();

        var command = Assert.IsType<SendTextCommand>(Assert.Single(runtime.Submitted));
        Assert.Equal("do the thing", command.Text);
        Assert.Equal(string.Empty, viewModel.Composer.Draft);
        store.Dispose();
    }

    [Fact]
    public async Task SendAsync_RejectionKeepsDraftAndSetsBanner()
    {
        var (viewModel, runtime, store) = Create();
        runtime.RejectNextCommand = true;
        viewModel.Composer.Draft = "do the thing";

        await viewModel.SendAsync();

        Assert.Equal("do the thing", viewModel.Composer.Draft);
        Assert.NotNull(viewModel.Banner);
        store.Dispose();
    }

    [Fact]
    public async Task SendAsync_BusyWithDifferentDraftMode_DoesNotChangeActivePolicy()
    {
        var (viewModel, runtime, store) = Create();
        using (store)
        using (viewModel)
        {
            var state = TestSnapshots.State(TurnPhase.Streaming);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
            viewModel.Composer.Draft = "next turn";
            viewModel.Composer.Mode = PermissionMode.Yolo;

            await viewModel.SendAsync();

            Assert.False(viewModel.Composer.CanSend);
            Assert.Empty(runtime.Submitted);
            Assert.Equal("next turn", viewModel.Composer.Draft);
        }
    }

    [Fact]
    public void StatePatch_PreservesDraftModeAndPendingAnswers()
    {
        var (viewModel, _, store) = Create();
        using (store)
        using (viewModel)
        {
            var question = new Alcoler.Contracts.Events.InteractionQuestion("Target", "Where?",
                [new Alcoler.Contracts.Events.InteractionOption("main")]);
            var request = new Alcoler.Contracts.Events.InteractionRequest(StrongId.NewInteractionId(), "Choose", [question]);
            var state = TestSnapshots.State(TurnPhase.AwaitingInteraction) with { PendingInteraction = request };
            viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
            viewModel.Composer.Mode = PermissionMode.Plan;
            viewModel.SelectInteractionAnswer("Target", "main");
            var pending = viewModel.PendingInteraction;

            viewModel.ApplyUpdate(new ProjectionUpdate(2, state, [new StatePatched(state)], Reset: false));

            Assert.Same(pending, viewModel.PendingInteraction);
            Assert.Equal("main", viewModel.PendingInteraction!.SelectedAnswers["Target"]);
            Assert.Equal(PermissionMode.Plan, viewModel.Composer.Mode);
            Assert.True(viewModel.PendingInteraction.CanSubmit);
        }
    }

    [Fact]
    public async Task SubmitInteractionAnswers_Incomplete_DoesNotSubmit()
    {
        var (viewModel, runtime, store) = Create();
        using (store)
        using (viewModel)
        {
            var question = new Alcoler.Contracts.Events.InteractionQuestion("Target", "Where?",
                [new Alcoler.Contracts.Events.InteractionOption("main")]);
            var state = TestSnapshots.State(TurnPhase.AwaitingInteraction) with
            {
                PendingInteraction = new Alcoler.Contracts.Events.InteractionRequest(StrongId.NewInteractionId(), "Choose", [question]),
            };
            viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));

            await viewModel.SubmitInteractionAnswersAsync();

            Assert.Empty(runtime.Submitted);
        }
    }

    [Fact]
    public async Task ResolvePermission_SubmitsResolveCommandWithDecision()
    {
        var (viewModel, runtime, store) = Create();
        var permission = Permission("Bash", RiskLevel.High);
        viewModel.ApplyUpdate(new ProjectionUpdate(
            1,
            StateWithPermission(permission),
            [new StatePatched(StateWithPermission(permission))],
            Reset: false));

        await viewModel.ResolvePermissionAsync(PermissionDecision.Allow);

        var command = Assert.IsType<ResolveInteractionCommand>(Assert.Single(runtime.Submitted));
        Assert.Equal(PermissionDecision.Allow, command.PermissionDecision);
        store.Dispose();
    }

    [Fact]
    public async Task ThoughtLevelChange_SubmitsSwitchModelWithReasoningLevel()
    {
        var (viewModel, runtime, store) = Create();
        var selection = new Alcoler.Contracts.Models.ModelSelection("anthropic", "claude-x");
        viewModel.ApplyUpdate(new ProjectionUpdate(
            1, TestSnapshots.State(modelSelection: selection),
            [new StatePatched(TestSnapshots.State(modelSelection: selection))], Reset: false));
        runtime.Submitted.Clear();

        viewModel.ThoughtLevel = "high";

        var command = Assert.IsType<SwitchModelCommand>(Assert.Single(runtime.Submitted));
        Assert.Equal("high", command.Selection.Options?.ReasoningLevel);
        Assert.Equal("claude-x", command.Selection.ModelId);
        store.Dispose();
    }

    [Fact]
    public void ThoughtLevelFromState_DoesNotResubmit()
    {
        var (viewModel, runtime, store) = Create();
        var selection = new Alcoler.Contracts.Models.ModelSelection(
            "anthropic", "claude-x", new Alcoler.Contracts.Models.ModelCallOptions(ReasoningLevel: "medium"));

        viewModel.ApplyUpdate(new ProjectionUpdate(
            1, TestSnapshots.State(modelSelection: selection),
            [new StatePatched(TestSnapshots.State(modelSelection: selection))], Reset: false));

        Assert.Equal("medium", viewModel.ThoughtLevel);
        Assert.Empty(runtime.Submitted);
        store.Dispose();
    }
}
