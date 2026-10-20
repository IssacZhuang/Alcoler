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
        Assert.Equal(.5, viewModel.ContextUsage!.Fraction);
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
    public async Task ThoughtLevelChange_PreservesOtherCallOptionsAndUsesConfiguredLevels()
    {
        var (viewModel, runtime, store) = Create();
        using (store)
        using (viewModel)
        {
            var selection = new Alcoler.Contracts.Models.ModelSelection("test", "model",
                new Alcoler.Contracts.Models.ModelCallOptions(ReasoningLevel: "low", MaxOutputTokens: 4096));
            var state = TestSnapshots.State(modelSelection: selection);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
            viewModel.ConfigureModel(new ModelChoice("test", "model", "Model",
                new Alcoler.Contracts.Models.ModelProperties(ReasoningLevels: ["low", "high"])));
            Assert.Equal(new[] { "low", "high" }, viewModel.ThoughtLevels);
            Assert.Empty(runtime.Submitted);

            viewModel.ThoughtLevel = "high";
            var changed = Assert.IsType<SwitchModelCommand>(Assert.Single(runtime.Submitted));
            Assert.Equal(4096, changed.Selection.Options!.MaxOutputTokens);
            Assert.Equal("high", changed.Selection.Options.ReasoningLevel);

            await viewModel.SwitchModelAsync(viewModel.ApplyThoughtLevel(selection));
            Assert.Equal(4096, viewModel.CurrentSelection!.Options!.MaxOutputTokens);
        }
    }

    [Fact]
    public void ApplyThoughtLevel_PreservesConfiguredLiteralOffInConversationAndDraft()
    {
        var (viewModel, _, store) = Create();
        using (store)
        using (viewModel)
        {
            var choice = new ModelChoice("test", "model", "Model",
                new Alcoler.Contracts.Models.ModelProperties(ReasoningLevels: ["off", "high"],
                    ReasoningMapping: "{'reasoning_effort':value}"));
            viewModel.ConfigureModel(choice);
            var draft = new DraftConversationViewModel("C:/workspace", choice, _ => Task.CompletedTask);
            var selection = choice.Selection with { Options = new Alcoler.Contracts.Models.ModelCallOptions(MaxOutputTokens: 2048) };
            foreach (var host in new IComposerHost[] { viewModel, draft })
            {
                host.ThoughtLevel = "off";
                var applied = host.ApplyThoughtLevel(selection);
                Assert.Equal("off", applied.Options!.ReasoningLevel);
                Assert.Equal(2048, applied.Options.MaxOutputTokens);
                host.ConfigureModel(choice with { Properties = new Alcoler.Contracts.Models.ModelProperties() });
                Assert.Null(host.ApplyThoughtLevel(selection).Options!.ReasoningLevel);
            }
        }
    }

    [Fact]
    public void RestoredComposer_SurvivesStaleStatePatchWithoutCommands()
    {
        var (viewModel, runtime, store) = Create();
        using (store)
        using (viewModel)
        {
            var restored = new Alcoler.Contracts.Models.ModelSelection("other", "model",
                new Alcoler.Contracts.Models.ModelCallOptions(ReasoningLevel: "high", MaxOutputTokens: 2048));
            viewModel.RestoreComposerState("keep me", PermissionMode.Plan, restored);
            var stale = TestSnapshots.State();
            viewModel.ApplyUpdate(new ProjectionUpdate(1, stale, [new StatePatched(stale)], Reset: true));

            Assert.Equal("keep me", viewModel.Composer.Draft);
            Assert.Equal(PermissionMode.Plan, viewModel.Composer.Mode);
            Assert.Equal(restored, viewModel.CurrentSelection);
            Assert.Equal("high", viewModel.ThoughtLevel);
            Assert.Empty(runtime.Submitted);
        }
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
