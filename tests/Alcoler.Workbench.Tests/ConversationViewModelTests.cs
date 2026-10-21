using System.Collections.Specialized;
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

    private static ToolCallRow ToolRow(string rowId) => new(
        rowId, StrongId.NewToolCallId(), "Bash", JsonSerializer.SerializeToElement(new { command = "pwd" }),
        ToolCallStatus.Running, null, false, null, DateTimeOffset.UtcNow);

    private static void ApplyRows(ConversationViewModel viewModel, long sequence, bool reset, params ConversationRow[] rows) =>
        viewModel.ApplyUpdate(new ProjectionUpdate(sequence, TestSnapshots.State(),
            reset ? [] : rows.Select(row => (RowOp)new RowUpserted(row)).ToArray(),
            Reset: reset, ResetRows: reset ? rows : null));

    private static ConversationState StateWithPermission(PermissionRequest permission, TurnPhase phase = TurnPhase.AwaitingPermission) =>
        new("Test session", PermissionMode.Build, null, 200_000, phase, null,
            100_000, "estimate", [], permission, null);

    private static PermissionRequest Permission(string tool = "Write", RiskLevel risk = RiskLevel.Medium) => new(
        StrongId.NewInteractionId(),
        new SessionId(Guid.NewGuid().ToString("N")),
        tool,
        JsonSerializer.SerializeToElement(new { file_path = "a.txt" }),
        risk,
        "test reason");

    [Theory]
    [InlineData(TurnPhase.ProcessingInput, "Preparing request", true, true)]
    [InlineData(TurnPhase.AwaitingModel, "Waiting for model", true, true)]
    [InlineData(TurnPhase.Streaming, "Generating response", true, true)]
    [InlineData(TurnPhase.ExecutingTools, "Running tools", true, true)]
    [InlineData(TurnPhase.AwaitingPermission, "Waiting for permission", false, true)]
    [InlineData(TurnPhase.AwaitingInteraction, "Waiting for your answer", false, true)]
    [InlineData(TurnPhase.Completing, "Finishing up", true, true)]
    [InlineData(TurnPhase.Idle, "", false, false)]
    [InlineData(TurnPhase.Error, "", false, false)]
    public void ActivityPresentation_DistinguishesRunningFromWaitingForUser(
        TurnPhase phase, string text, bool isWorking, bool isTurnActive)
    {
        var (viewModel, _, store) = Create();
        using (store)
        using (viewModel)
        {
            var state = TestSnapshots.State(phase);
            viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));

            Assert.Equal(text, viewModel.ActivityText);
            Assert.Equal(isWorking, viewModel.IsWorking);
            Assert.Equal(isTurnActive, viewModel.Composer.IsTurnActive);
            Assert.Equal(phase.ToString(), viewModel.PhaseText);
        }
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyRows_UpdatesContentWithoutLosingExpansionOrIdentity(bool reset)
    {
        var (viewModel, _, store) = Create();
        using (store)
        using (viewModel)
        {
            var assistantRow = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), "answer"), new ReasoningPart(StrongId.NewPartId(), "reasoning")],
                true, DateTimeOffset.UtcNow);
            var toolRow = ToolRow("t1");
            ApplyRows(viewModel, 1, true, assistantRow, toolRow);
            var assistant = Assert.IsType<AssistantMessageItemViewModel>(viewModel.Items[0]);
            var tool = Assert.IsType<ToolCallItemViewModel>(viewModel.Items[1]);
            Assert.False(assistant.IsReasoningExpanded);
            Assert.False(tool.IsExpanded);
            assistant.IsReasoningExpanded = true;
            tool.IsExpanded = true;
            List<NotifyCollectionChangedAction> changes = [];
            viewModel.Items.CollectionChanged += (_, args) => changes.Add(args.Action);

            ApplyRows(viewModel, 2, reset, assistantRow with
            {
                Parts = [new TextPart(StrongId.NewPartId(), "updated answer"), new ReasoningPart(StrongId.NewPartId(), "updated reasoning")],
                IsStreaming = false,
            }, toolRow with
            {
                Input = JsonSerializer.SerializeToElement(new { command = "git status" }),
                Status = ToolCallStatus.Succeeded,
                Output = [new ToolResultText("updated output")],
            });

            Assert.Same(assistant, viewModel.Items[0]);
            Assert.Same(tool, viewModel.Items[1]);
            Assert.True(assistant.IsReasoningExpanded);
            Assert.True(tool.IsExpanded);
            Assert.Equal("updated answer", assistant.MarkdownText);
            Assert.Equal("updated reasoning", Assert.Single(assistant.ReasoningBlocks));
            Assert.False(assistant.IsStreaming);
            Assert.Equal("git status", tool.Summary);
            Assert.Equal("updated output", tool.OutputText);
            Assert.Equal(ToolCallStatus.Succeeded, tool.Status);
            Assert.Empty(changes);
        }
    }

    [Fact]
    public void Reset_ReconcilesRemovedReorderedNewAndChangedRowsWithoutClearing()
    {
        var (viewModel, _, store) = Create();
        using (store)
        using (viewModel)
        {
            var userRow = new UserMessageRow("u1", "hello", DateTimeOffset.UtcNow);
            var assistantRow = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new ReasoningPart(StrongId.NewPartId(), "reasoning")], true, DateTimeOffset.UtcNow);
            var toolRow = ToolRow("t1");
            var removedRow = ToolRow("removed");
            var noticeRow = new SystemNoticeRow("n1", "compact", "context compacted");
            var changedUserRow = userRow with { RowId = "changed-user" };
            var changedNoticeRow = noticeRow with { RowId = "changed-notice" };
            var changedTypeRow = ToolRow("changed-type");
            ApplyRows(viewModel, 1, true, userRow, assistantRow, toolRow, removedRow, noticeRow,
                changedUserRow, changedNoticeRow, changedTypeRow);
            var original = viewModel.Items.ToDictionary(item => item.RowId);
            var assistant = Assert.IsType<AssistantMessageItemViewModel>(original["a1"]);
            var tool = Assert.IsType<ToolCallItemViewModel>(original["t1"]);
            assistant.IsReasoningExpanded = true;
            tool.IsExpanded = true;
            Assert.IsType<ToolCallItemViewModel>(original["removed"]).IsExpanded = true;
            Assert.IsType<ToolCallItemViewModel>(original["changed-type"]).IsExpanded = true;
            List<NotifyCollectionChangedAction> changes = [];
            viewModel.Items.CollectionChanged += (_, args) => changes.Add(args.Action);
            var newToolRow = ToolRow("new-tool");

            ApplyRows(viewModel, 2, true, noticeRow with { }, toolRow with { Status = ToolCallStatus.Succeeded },
                newToolRow, userRow with { At = userRow.At.AddMinutes(1) }, assistantRow with { IsStreaming = false },
                changedUserRow with { Text = "edited" }, changedNoticeRow with { Kind = "turn_error", Text = "failed" },
                assistantRow with { RowId = "changed-type", Id = StrongId.NewMessageId() });

            Assert.Equal(new[] { "n1", "t1", "new-tool", "u1", "a1", "changed-user", "changed-notice", "changed-type" },
                viewModel.Items.Select(item => item.RowId));
            Assert.Same(original["n1"], viewModel.Items[0]);
            Assert.Same(tool, viewModel.Items[1]);
            Assert.False(Assert.IsType<ToolCallItemViewModel>(viewModel.Items[2]).IsExpanded);
            Assert.Same(original["u1"], viewModel.Items[3]);
            Assert.Same(assistant, viewModel.Items[4]);
            Assert.True(tool.IsExpanded);
            Assert.True(assistant.IsReasoningExpanded);
            Assert.Equal(ToolCallStatus.Succeeded, tool.Status);
            Assert.False(assistant.IsStreaming);
            Assert.NotSame(original["changed-user"], viewModel.Items[5]);
            Assert.Equal("edited", Assert.IsType<UserMessageItemViewModel>(viewModel.Items[5]).Text);
            Assert.NotSame(original["changed-notice"], viewModel.Items[6]);
            var changedNotice = Assert.IsType<SystemNoticeItemViewModel>(viewModel.Items[6]);
            Assert.Equal("turn_error", changedNotice.Kind);
            Assert.Equal("failed", changedNotice.Text);
            Assert.NotSame(original["changed-type"], viewModel.Items[7]);
            Assert.False(Assert.IsType<AssistantMessageItemViewModel>(viewModel.Items[7]).IsReasoningExpanded);
            Assert.DoesNotContain(viewModel.Items, item => item.RowId == "removed");
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
            Assert.Contains(NotifyCollectionChangedAction.Remove, changes);
            Assert.Contains(NotifyCollectionChangedAction.Move, changes);
            Assert.Contains(NotifyCollectionChangedAction.Add, changes);

            ApplyRows(viewModel, 3, false, toolRow with { Output = [new ToolResultText("after reset")] }, removedRow);

            Assert.Equal(9, viewModel.Items.Count);
            Assert.Same(tool, viewModel.Items[1]);
            Assert.True(tool.IsExpanded);
            Assert.Equal("after reset", tool.OutputText);
            var reinserted = Assert.IsType<ToolCallItemViewModel>(viewModel.Items[^1]);
            Assert.Equal("removed", reinserted.RowId);
            Assert.NotSame(original["removed"], reinserted);
            Assert.False(reinserted.IsExpanded);
        }
    }

    [Theory]
    [InlineData(false, "assistant-id")]
    [InlineData(true, "assistant-id")]
    [InlineData(false, "tool-id")]
    [InlineData(true, "tool-id")]
    [InlineData(false, "tool-name")]
    [InlineData(true, "tool-name")]
    [InlineData(false, "row-type")]
    [InlineData(true, "row-type")]
    [InlineData(false, "user-text")]
    [InlineData(true, "user-text")]
    [InlineData(false, "notice-kind")]
    [InlineData(true, "notice-kind")]
    [InlineData(false, "notice-text")]
    [InlineData(true, "notice-text")]
    public void ApplyRows_ReplacesIncompatibleRowsWithoutLeakingExpansion(bool reset, string change)
    {
        var (viewModel, _, store) = Create();
        using (store)
        using (viewModel)
        {
            var assistantRow = new AssistantMessageRow("row", StrongId.NewMessageId(),
                [new ReasoningPart(StrongId.NewPartId(), "reasoning")], false, DateTimeOffset.UtcNow);
            var toolRow = ToolRow("row");
            var userRow = new UserMessageRow("row", "before", DateTimeOffset.UtcNow);
            var noticeRow = new SystemNoticeRow("row", "compact", "before");
            ConversationRow initial = change switch
            {
                "assistant-id" or "row-type" => assistantRow,
                "user-text" => userRow,
                "notice-kind" or "notice-text" => noticeRow,
                _ => toolRow,
            };
            ConversationRow replacement = change switch
            {
                "assistant-id" => assistantRow with { Id = StrongId.NewMessageId() },
                "tool-id" => toolRow with { Id = StrongId.NewToolCallId() },
                "tool-name" => toolRow with { ToolName = "Read" },
                "row-type" => toolRow,
                "user-text" => userRow with { Text = "after" },
                "notice-kind" => noticeRow with { Kind = "turn_error" },
                "notice-text" => noticeRow with { Text = "after" },
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            };
            ApplyRows(viewModel, 1, true, initial);
            var original = Assert.Single(viewModel.Items);
            if (original is AssistantMessageItemViewModel originalAssistant)
                originalAssistant.IsReasoningExpanded = true;
            if (original is ToolCallItemViewModel originalTool)
                originalTool.IsExpanded = true;

            ApplyRows(viewModel, 2, reset, replacement);

            var replaced = Assert.Single(viewModel.Items);
            Assert.NotSame(original, replaced);
            Assert.Equal("row", replaced.RowId);
            switch (replacement)
            {
                case AssistantMessageRow assistant:
                    var item = Assert.IsType<AssistantMessageItemViewModel>(replaced);
                    Assert.Equal(assistant.Id, item.Id);
                    Assert.False(item.IsReasoningExpanded);
                    break;
                case ToolCallRow tool:
                    var toolItem = Assert.IsType<ToolCallItemViewModel>(replaced);
                    Assert.Equal(tool.Id, toolItem.Id);
                    Assert.Equal(tool.ToolName, toolItem.ToolName);
                    Assert.False(toolItem.IsExpanded);
                    break;
                case UserMessageRow user:
                    Assert.Equal(user.Text, Assert.IsType<UserMessageItemViewModel>(replaced).Text);
                    break;
                case SystemNoticeRow notice:
                    var noticeItem = Assert.IsType<SystemNoticeItemViewModel>(replaced);
                    Assert.Equal(notice.Kind, noticeItem.Kind);
                    Assert.Equal(notice.Text, noticeItem.Text);
                    break;
            }

            ApplyRows(viewModel, 3, false, replacement);

            Assert.Same(replaced, Assert.Single(viewModel.Items));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reset_EmptySnapshotForgetsExpansionBeforeRowsReturn(bool omitRows)
    {
        var (viewModel, _, store) = Create();
        using (store)
        using (viewModel)
        {
            var assistantRow = new AssistantMessageRow("a1", StrongId.NewMessageId(),
                [new ReasoningPart(StrongId.NewPartId(), "reasoning")], false, DateTimeOffset.UtcNow);
            var toolRow = ToolRow("t1");
            ApplyRows(viewModel, 1, true, assistantRow, toolRow);
            var assistant = Assert.IsType<AssistantMessageItemViewModel>(viewModel.Items[0]);
            var tool = Assert.IsType<ToolCallItemViewModel>(viewModel.Items[1]);
            assistant.IsReasoningExpanded = true;
            tool.IsExpanded = true;
            List<NotifyCollectionChangedAction> changes = [];
            viewModel.Items.CollectionChanged += (_, args) => changes.Add(args.Action);

            viewModel.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(), [], Reset: true,
                ResetRows: omitRows ? null : []));

            Assert.Empty(viewModel.Items);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove }, changes);

            ApplyRows(viewModel, 3, true, assistantRow, toolRow);

            Assert.Equal(2, viewModel.Items.Count);
            var restoredAssistant = Assert.IsType<AssistantMessageItemViewModel>(viewModel.Items[0]);
            var restoredTool = Assert.IsType<ToolCallItemViewModel>(viewModel.Items[1]);
            Assert.NotSame(assistant, restoredAssistant);
            Assert.NotSame(tool, restoredTool);
            Assert.False(restoredAssistant.IsReasoningExpanded);
            Assert.False(restoredTool.IsExpanded);
        }
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
        Assert.Equal("Write", viewModel.PendingInteraction!.ToolName);
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
    public async Task SubmitInteractionAnswers_Empty_SubmitsWithoutInventingAnswers()
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

            var command = Assert.IsType<ResolveInteractionCommand>(Assert.Single(runtime.Submitted));
            Assert.Empty(command.InteractionAnswer!.Answers);
        }
    }

    [Fact]
    public async Task SubmitInteractionAnswers_PartialOrDismissed_SendsOnlyIntendedAnswers()
    {
        foreach (var dismiss in new[] { false, true })
        {
            var (viewModel, runtime, store) = Create();
            using (store)
            using (viewModel)
            {
                var state = TestSnapshots.State(TurnPhase.AwaitingInteraction) with
                {
                    PendingInteraction = new Alcoler.Contracts.Events.InteractionRequest(StrongId.NewInteractionId(), "Choose",
                    [
                        new Alcoler.Contracts.Events.InteractionQuestion("Target", "Where?", [new Alcoler.Contracts.Events.InteractionOption("main")]),
                        new Alcoler.Contracts.Events.InteractionQuestion("Next", "Next?", [new Alcoler.Contracts.Events.InteractionOption("Done")]),
                    ]),
                };
                viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
                viewModel.SelectInteractionAnswer("Target", "main");
                if (dismiss)
                    await viewModel.DismissInteractionAsync();
                else
                    await viewModel.SubmitInteractionAnswersAsync();

                var command = Assert.IsType<ResolveInteractionCommand>(Assert.Single(runtime.Submitted));
                Assert.Equal(state.PendingInteraction.InteractionId, command.InteractionId);
                if (dismiss)
                    Assert.Empty(command.InteractionAnswer!.Answers);
                else
                    Assert.Equal("main", Assert.Single(command.InteractionAnswer!.Answers).Value);
            }
        }
    }

    [Fact]
    public async Task SubmitInteractionAnswers_RejectionKeepsDraftsAndAllowsRetry()
    {
        var (viewModel, runtime, store) = Create();
        using (store)
        using (viewModel)
        {
            var state = TestSnapshots.State(TurnPhase.AwaitingInteraction) with
            {
                PendingInteraction = new Alcoler.Contracts.Events.InteractionRequest(StrongId.NewInteractionId(), "Choose",
                    [new Alcoler.Contracts.Events.InteractionQuestion("Target", "Where?", [new Alcoler.Contracts.Events.InteractionOption("main")])]),
            };
            viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
            viewModel.PendingInteraction!.CurrentQuestion!.CustomAnswer = " custom ";
            runtime.RejectNextCommand = true;

            await viewModel.SubmitInteractionAnswersAsync();

            Assert.Single(runtime.Submitted);
            Assert.False(viewModel.IsInteractionSubmitting);
            Assert.Equal("rejected for test", viewModel.Banner);
            Assert.Equal(" custom ", viewModel.PendingInteraction.CurrentQuestion.CustomAnswer);
            Assert.Equal("custom", viewModel.PendingInteraction.SelectedAnswers["Target"]);
            runtime.RejectNextCommand = false;
            viewModel.ClearBanner();
            await viewModel.SubmitInteractionAnswersAsync();
            Assert.Equal(2, runtime.Submitted.Count);
            Assert.True(viewModel.IsInteractionSubmitting);
            Assert.Null(viewModel.Banner);
        }
    }

    [Fact]
    public async Task SubmitInteractionAnswers_AcceptanceDisablesDuplicateSubmissionUntilProjectionResolves()
    {
        var (viewModel, runtime, store) = Create();
        using (store)
        using (viewModel)
        {
            var state = TestSnapshots.State(TurnPhase.AwaitingInteraction) with
            {
                PendingInteraction = new Alcoler.Contracts.Events.InteractionRequest(StrongId.NewInteractionId(), "Choose", []),
            };
            viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], false));
            await viewModel.SubmitInteractionAnswersAsync();
            await viewModel.SubmitInteractionAnswersAsync();
            await viewModel.DismissInteractionAsync();
            Assert.Single(runtime.Submitted);
            Assert.True(viewModel.IsInteractionSubmitting);
            viewModel.ApplyUpdate(new ProjectionUpdate(2, state, [new StatePatched(state)], false));
            Assert.True(viewModel.IsInteractionSubmitting);
            var idle = TestSnapshots.State();
            viewModel.ApplyUpdate(new ProjectionUpdate(3, idle, [new StatePatched(idle)], false));
            Assert.False(viewModel.IsInteractionSubmitting);
            Assert.Null(viewModel.PendingInteraction);
        }
    }

    [Fact]
    public void InteractionDrafts_NavigationAndStatePatchPreserveCustomAndMultiSelection()
    {
        var (viewModel, _, store) = Create();
        using (store)
        using (viewModel)
        {
            var single = new Alcoler.Contracts.Events.InteractionQuestion("Target", "Where?",
                [new Alcoler.Contracts.Events.InteractionOption("main"), new("branch")]);
            var multi = new Alcoler.Contracts.Events.InteractionQuestion("Features", "Which features?",
                [new Alcoler.Contracts.Events.InteractionOption("Search"), new("History")], true);
            var request = new Alcoler.Contracts.Events.InteractionRequest(StrongId.NewInteractionId(), "Choose", [single, multi]);
            var state = TestSnapshots.State(TurnPhase.AwaitingInteraction) with { PendingInteraction = request };
            viewModel.ApplyUpdate(new ProjectionUpdate(1, state, [new StatePatched(state)], Reset: false));
            var pending = viewModel.PendingInteraction!;
            Assert.Empty(pending.SelectedAnswers);
            Assert.Equal(-1, pending.ActiveOptionIndex);
            pending.SelectAnswer("Target", "main");
            pending.CurrentQuestion!.CustomAnswer = "  release branch  ";
            Assert.Equal("release branch", pending.SelectedAnswers["Target"]);
            Assert.DoesNotContain(pending.CurrentQuestion.Options, option => option.IsSelected);
            Assert.False(pending.ContinueQuestion());
            pending.SetOptionSelected(multi, "History", true);
            pending.SetOptionSelected(multi, "Search", true);
            pending.CurrentQuestion!.CustomAnswer = "  Offline  ";
            pending.SetOptionSelected(multi, "History", false);

            var patchedState = state with { PendingInteraction = request with { Prompt = "Refreshed prompt" } };
            viewModel.ApplyUpdate(new ProjectionUpdate(2, patchedState, [new StatePatched(patchedState)], Reset: false));

            Assert.Same(pending, viewModel.PendingInteraction);
            Assert.Equal(1, pending.CurrentQuestionIndex);
            Assert.Equal("Search, Offline", pending.SelectedAnswers["Features"]);
            Assert.Equal("  Offline  ", pending.CurrentQuestion.CustomAnswer);
            pending.MovePrevious();
            Assert.Equal("  release branch  ", pending.CurrentQuestion!.CustomAnswer);
            pending.MoveNext();
            Assert.True(pending.ContinueQuestion());
            Assert.Equal(2, pending.SelectedAnswers.Count);
        }
    }

    [Fact]
    public void InteractionDrafts_OnlyExplicitSingleKeyboardFocusIsAutoSelectedOnContinue()
    {
        var single = new Alcoler.Contracts.Events.InteractionQuestion("Target", "Where?",
            [new Alcoler.Contracts.Events.InteractionOption("main"), new("branch")]);
        var multi = new Alcoler.Contracts.Events.InteractionQuestion("Features", "Which features?",
            [new Alcoler.Contracts.Events.InteractionOption("Search")], true);
        var pending = PendingInteractionViewModel.FromInteraction(new Alcoler.Contracts.Events.InteractionRequest(
            StrongId.NewInteractionId(), "Choose", [single, multi]));

        Assert.False(pending.ContinueQuestion());
        Assert.Empty(pending.SelectedAnswers);
        pending.MovePrevious();
        pending.MoveSelection(1);
        pending.MoveSelection(1);
        Assert.Empty(pending.SelectedAnswers);
        Assert.False(pending.ContinueQuestion());
        Assert.Equal("branch", pending.SelectedAnswers["Target"]);
        pending.MoveSelection(1);
        Assert.True(pending.ContinueQuestion());
        Assert.False(pending.SelectedAnswers.ContainsKey("Features"));
    }

    [Fact]
    public void InteractionDrafts_WhitespaceCustomIsOmittedAndPresetClearsCustom()
    {
        var question = new Alcoler.Contracts.Events.InteractionQuestion("Target", "Where?",
            [new Alcoler.Contracts.Events.InteractionOption("main")]);
        var pending = PendingInteractionViewModel.FromInteraction(new Alcoler.Contracts.Events.InteractionRequest(
            StrongId.NewInteractionId(), "Choose", [question]));
        pending.CurrentQuestion!.CustomAnswer = " \n  ";
        Assert.Empty(pending.SelectedAnswers);
        pending.CurrentQuestion.CustomAnswer = " custom ";
        Assert.Equal("custom", pending.SelectedAnswers["Target"]);
        pending.SelectAnswer("Target", "main");
        Assert.Equal(string.Empty, pending.CurrentQuestion.CustomAnswer);
        Assert.Equal("main", pending.SelectedAnswers["Target"]);
        pending.SelectAnswer("Target", "invalid");
        Assert.Equal("main", pending.SelectedAnswers["Target"]);
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
