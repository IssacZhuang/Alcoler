using System.Collections.ObjectModel;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Subagents;
using Alcoler.Kernel.Workflows;
using Alcoler.Localization;
using Alcoler.Stores;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.ViewModel;

public sealed partial class ConversationViewModel : ObservableObject, IComposerHost, IDisposable
{
    private static readonly TurnPhase[] BusyPhases =
    [
        TurnPhase.ProcessingInput,
        TurnPhase.AwaitingModel,
        TurnPhase.Streaming,
        TurnPhase.ExecutingTools,
        TurnPhase.AwaitingPermission,
        TurnPhase.AwaitingInteraction,
        TurnPhase.Completing,
    ];

    private readonly IAgentRuntime _runtime;
    private readonly ProjectionStore _store;
    // Workflows requested by a parsed "/orchestrator <prompt>" command, consumed by the send below.
    private IReadOnlyList<string>? _pendingWorkflows;
    private readonly Dictionary<string, TimelineItemViewModel> _itemsByRowId = [];
    private readonly Dictionary<string, SystemNoticeRow> _agentRows = [];
    private readonly List<string> _rowOrder = [];
    private readonly Dictionary<string, int> _rowOrderIndex = new(StringComparer.Ordinal);
    private readonly List<AgentCallItemViewModel> _dispatches = [];
    private readonly Dictionary<ToolCallId, AgentCallItemViewModel> _dispatchByToolId = [];
    private readonly Dictionary<string, AgentCallItemViewModel> _dispatchByLegacyId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _legacyIdCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pairedAgentRows = new(StringComparer.Ordinal);
    private readonly Dictionary<AgentCallItemViewModel, SubagentState> _agentStates = [];
    private readonly HashSet<CommandId> _queueOperations = [];
    private readonly object _applyGate = new();
    private readonly IArtifactStore _artifacts;
    private IReadOnlyList<ModelChoice>? _modelChoices;
    private ModelSelection? _defaultSelection;
    private bool _hasLocalSelection;

    public ConversationViewModel(IAgentRuntime runtime, ProjectionStore store, ComposerViewModel? composer = null,
        IReadOnlyList<ModelChoice>? modelChoices = null, ModelSelection? defaultSelection = null,
        IArtifactStore? artifacts = null)
    {
        _runtime = runtime;
        _store = store;
        _artifacts = artifacts ?? new FileArtifactStore(WorkbenchDataPaths.ArtifactsPath);
        _modelChoices = modelChoices;
        _defaultSelection = defaultSelection;
        Composer = composer ?? new ComposerViewModel();
        SessionId = store.SessionId;
        Status = new ConversationStatusViewModel(SessionId, runtime);
        _store.Updated += ApplyUpdate;
        ApplyUpdate(new ProjectionUpdate(0, store.State, [], Reset: true, ResetRows: store.Rows));
    }

    public SessionId SessionId { get; }

    public ComposerViewModel Composer { get; }

    public ConversationStatusViewModel Status { get; }

    public ModelSelection? CurrentSelection => _currentSelection;

    public event Action<PermissionMode, bool>? ModeSubmitted;

    public event Action<ModelSelection>? ModelSelectionChanged;

    public event Action<ModelSelection, long>? ModelSelectionSubmitted;

    internal Func<long>? NextSubmissionSequence { get; set; }

    public Func<MessageId, Task>? ForkRequested { get; set; }

    private readonly HashSet<MessageId> _forkOperations = [];

    private ModelChoice? _configuredModel;

    public ObservableCollection<TimelineItemViewModel> Items { get; } = [];

    /// <summary>Raised after rows were appended so the view can keep the timeline pinned to the bottom.</summary>
    public event Action? TimelineAppended;

    [ObservableProperty]
    private string _title = string.Empty;

    public string HeaderTitle => string.IsNullOrWhiteSpace(Title) ? Loc.Get("sidebar_untitled") : Title;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(HeaderTitle));

    [ObservableProperty]
    private string _workspaceRoot = string.Empty;

    [ObservableProperty]
    private string _phaseText = TurnPhase.Idle.ToString();

    [ObservableProperty]
    private string _activityText = string.Empty;

    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private PermissionMode _mode = PermissionMode.Yolo;

    [ObservableProperty]
    private bool _planEnabled;

    [ObservableProperty]
    private string _modelText = string.Empty;

    [ObservableProperty]
    private string _thoughtLevel = "off";

    private ModelSelection? _currentSelection;

    private bool _applyingState;

    [ObservableProperty]
    private IReadOnlyList<string> _thoughtLevels = [];

    [ObservableProperty]
    private ContextUsageViewModel? _contextUsage;

    [ObservableProperty]
    private IReadOnlyList<QueuedInput> _queue = [];

    public bool HasQueuedInput => Queue.Count > 0;

    [ObservableProperty]
    private PendingInteractionViewModel? _pendingInteraction;

    public bool HasPendingInteraction => PendingInteraction is not null;

    public bool HasPendingQuestion => PendingInteraction is { IsPermission: false };

    [ObservableProperty]
    private bool _isInteractionSubmitting;

    [ObservableProperty]
    private string? _banner;

    internal void ApplyUpdate(ProjectionUpdate update)
    {
        // Updates enter both from the projection pump and from synchronous construction
        // paths; schedulers that run posted callbacks on the pump thread would otherwise
        // mutate the status row collections concurrently.
        lock (_applyGate)
        {
            ApplyUpdateCore(update);
        }
    }

    private void ApplyUpdateCore(ProjectionUpdate update)
    {
        Status.ApplyUpdate(update);
        if (update.Reset)
        {
            ReconcileRows(update.ResetRows ?? []);
            ApplyState(update.State);
            if (Items.Count > 0)
            {
                TimelineAppended?.Invoke();
            }
            return;
        }

        var appendedDelta = false;
        var reconcileAgents = false;
        var rowsRemoved = false;
        var statePatched = false;
        foreach (var op in update.Ops)
        {
            appendedDelta |= ApplyOp(op);
            switch (op)
            {
                case RowAppended { Row: SystemNoticeRow { Subagent: not null } }:
                case RowUpserted { Row: SystemNoticeRow { Subagent: not null } }:
                    reconcileAgents = true;
                    break;
                case RowAppended { Row: ToolCallRow appendedTool } when AgentCallItemViewModel.IsAgentTool(appendedTool.ToolName):
                case RowUpserted { Row: ToolCallRow upsertedTool } when AgentCallItemViewModel.IsAgentTool(upsertedTool.ToolName):
                    reconcileAgents = true;
                    break;
                case RowRemoved:
                    rowsRemoved = true;
                    break;
                case StatePatched:
                    statePatched = true;
                    break;
            }
        }
        if (rowsRemoved && (_agentRows.Count > 0 || Items.OfType<AgentCallItemViewModel>().Any()))
            reconcileAgents = true;
        if (reconcileAgents)
            ReconcileAgents();
        if (statePatched)
        {
            ApplyState(update.State);
        }
        if (appendedDelta)
        {
            TimelineAppended?.Invoke();
        }
    }

    private void ReconcileRows(IReadOnlyList<ConversationRow> rows)
    {
        List<TimelineItemViewModel> items = [];
        _agentRows.Clear();
        _rowOrder.Clear();
        _rowOrderIndex.Clear();
        foreach (var row in rows)
        {
            _rowOrderIndex[row.RowId] = _rowOrder.Count;
            _rowOrder.Add(row.RowId);
            if (row is SystemNoticeRow { Subagent: not null } agent)
                _agentRows[row.RowId] = agent;
            var existing = _itemsByRowId.GetValueOrDefault(row.RowId);
            var item = existing is not null && TryUpdateItem(existing, row)
                ? existing
                : TimelineItemFactory.Create(row);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        _itemsByRowId.Clear();
        foreach (var item in items)
        {
            _itemsByRowId[item.RowId] = item;
        }
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            var item = Items[index];
            if (!ReferenceEquals(_itemsByRowId.GetValueOrDefault(item.RowId), item))
            {
                Items.RemoveAt(index);
            }
        }
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (index < Items.Count && ReferenceEquals(Items[index], item))
            {
                continue;
            }
            var oldIndex = Items.IndexOf(item);
            if (oldIndex >= 0)
            {
                Items.Move(oldIndex, index);
            }
            else
            {
                Items.Insert(index, item);
            }
        }
        ReconcileAgents();
    }

    private void ReconcileAgents()
    {
        _dispatches.Clear();
        _dispatchByToolId.Clear();
        _dispatchByLegacyId.Clear();
        _legacyIdCounts.Clear();
        foreach (var item in _itemsByRowId.Values)
        {
            if (item is not AgentCallItemViewModel { IsDispatch: true } dispatch)
                continue;
            _dispatches.Add(dispatch);
            _dispatchByToolId[dispatch.Id!.Value] = dispatch;
            if (dispatch.LegacyAgentId is not { Length: > 0 } legacyId)
                continue;
            _legacyIdCounts[legacyId] = _legacyIdCounts.GetValueOrDefault(legacyId) + 1;
            _dispatchByLegacyId[legacyId] = dispatch;
        }

        _pairedAgentRows.Clear();
        _agentStates.Clear();
        foreach (var row in _agentRows.Values)
        {
            var agent = row.Subagent!;
            AgentCallItemViewModel? dispatch = null;
            if (agent.ParentToolCallId is { } toolId)
                _dispatchByToolId.TryGetValue(toolId, out dispatch);
            else if (_legacyIdCounts.GetValueOrDefault(agent.Id.Value) == 1)
                _dispatchByLegacyId.TryGetValue(agent.Id.Value, out dispatch);
            if (dispatch is null)
                continue;
            _agentStates[dispatch] = agent;
            _pairedAgentRows.Add(row.RowId);
        }
        foreach (var dispatch in _dispatches)
            dispatch.SetSubagent(_agentStates.GetValueOrDefault(dispatch));

        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (_pairedAgentRows.Contains(Items[index].RowId))
                Items.RemoveAt(index);
        }
        foreach (var row in _agentRows.Values)
        {
            if (_pairedAgentRows.Contains(row.RowId))
                continue;
            var item = _itemsByRowId[row.RowId];
            if (Items.Contains(item))
                continue;
            var rowIndex = _rowOrderIndex.GetValueOrDefault(row.RowId, -1);
            var index = 0;
            while (index < Items.Count
                && _rowOrderIndex.GetValueOrDefault(Items[index].RowId, -1) < rowIndex)
            {
                index++;
            }
            Items.Insert(index, item);
        }
    }

    private static bool TryUpdateItem(TimelineItemViewModel item, ConversationRow row) => (item, row) switch
    {
        (AgentCallItemViewModel agent, ToolCallRow tool) when agent.Id == tool.Id && AgentCallItemViewModel.IsAgentTool(tool.ToolName) =>
            UpdateAgent(agent, tool),
        (AgentCallItemViewModel { IsDispatch: false } agent, SystemNoticeRow { Subagent: not null } notice) =>
            UpdateAgent(agent, notice),
        (AssistantMessageItemViewModel assistant, AssistantMessageRow assistantRow) when assistant.Id == assistantRow.Id =>
            TimelineItemFactory.Update(item, row),
        (ToolCallItemViewModel tool, ToolCallRow toolRow) when tool.Id == toolRow.Id && tool.ToolName == toolRow.ToolName =>
            TimelineItemFactory.Update(item, row),
        (FileChangeSummaryItemViewModel, FileChangeSummaryRow) => TimelineItemFactory.Update(item, row),
        (UserMessageItemViewModel user, UserMessageRow userRow) =>
            user.Text == userRow.Text && user.CanRewind == userRow.CanRewind,
        (SystemNoticeItemViewModel notice, SystemNoticeRow noticeRow) => notice.Kind == noticeRow.Kind && notice.Text == noticeRow.Text,
        _ => false,
    };

    private static bool UpdateAgent(AgentCallItemViewModel item, ToolCallRow row)
    {
        item.UpdateFrom(row);
        return true;
    }

    private static bool UpdateAgent(AgentCallItemViewModel item, SystemNoticeRow row)
    {
        item.UpdateFrom(row);
        return true;
    }

    private bool ApplyOp(RowOp op)
    {
        var row = op switch { RowAppended appended => appended.Row, RowUpserted upserted => upserted.Row, _ => null };
        if (row is not null)
        {
            if (!_itemsByRowId.ContainsKey(row.RowId))
            {
                _rowOrderIndex[row.RowId] = _rowOrder.Count;
                _rowOrder.Add(row.RowId);
            }
            if (row is SystemNoticeRow { Subagent: not null } agent)
                _agentRows[row.RowId] = agent;
            else
                _agentRows.Remove(row.RowId);
        }
        switch (op)
        {
            case RowAppended appended when TimelineItemFactory.Create(appended.Row) is { } item:
                _itemsByRowId[item.RowId] = item;
                Items.Add(item);
                return true;

            case RowUpserted upserted:
                var existing = _itemsByRowId.GetValueOrDefault(upserted.Row.RowId);
                if (existing is not null && TryUpdateItem(existing, upserted.Row))
                {
                    return false;
                }
                if (TimelineItemFactory.Create(upserted.Row) is { } replacement)
                {
                    _itemsByRowId[replacement.RowId] = replacement;
                    var index = Items.IndexOf(existing!);
                    if (index >= 0)
                    {
                        Items[index] = replacement;
                        return false;
                    }
                    Items.Add(replacement);
                    return true;
                }
                return false;

            case RowRemoved removed:
                _agentRows.Remove(removed.RowId);
                if (_rowOrderIndex.Remove(removed.RowId, out var removedOrder))
                {
                    _rowOrder.RemoveAt(removedOrder);
                    for (var index = removedOrder; index < _rowOrder.Count; index++)
                        _rowOrderIndex[_rowOrder[index]] = index;
                }
                if (_itemsByRowId.Remove(removed.RowId, out var removedItem))
                {
                    Items.Remove(removedItem);
                }
                return false;

            default:
                return false;
        }
    }

    private void ApplyState(ConversationState state)
    {
        var modeChangedOnServer = state.Mode != Composer.SessionMode;
        var planChangedOnServer = state.PlanEnabled != Composer.SessionPlanEnabled;
        if (!_hasLocalSelection)
        {
            SetModelSelection(state.ModelSelection, local: false);
        }
        Title = state.Title;
        if (modeChangedOnServer || Composer.Mode == Mode)
        {
            Composer.RestoreModeOnly(state.Mode);
        }
        if (planChangedOnServer || Composer.PlanEnabled == PlanEnabled)
        {
            Composer.RestorePlanEnabled(state.PlanEnabled);
        }
        Mode = state.Mode;
        PlanEnabled = state.PlanEnabled;
        Composer.SessionMode = state.Mode;
        Composer.SessionPlanEnabled = state.PlanEnabled;
        Composer.SessionWorkflows = state.ActiveWorkflows ?? [];
        PhaseText = state.Phase.ToString();
        ActivityText = state.Phase switch
        {
            TurnPhase.ProcessingInput => Loc.Get("status_phase_preparing"),
            TurnPhase.AwaitingModel => Loc.Get("status_phase_awaiting_model"),
            TurnPhase.Streaming => Loc.Get("status_phase_streaming"),
            TurnPhase.ExecutingTools => Loc.Get("status_phase_running_tools"),
            TurnPhase.AwaitingPermission => Loc.Get("status_phase_awaiting_permission"),
            TurnPhase.AwaitingInteraction => Loc.Get("status_phase_awaiting_interaction"),
            TurnPhase.Completing => Loc.Get("status_phase_completing"),
            _ => string.Empty,
        };
        IsWorking = state.Phase is TurnPhase.ProcessingInput or TurnPhase.AwaitingModel
            or TurnPhase.Streaming or TurnPhase.ExecutingTools or TurnPhase.Completing;
        Composer.IsTurnActive = BusyPhases.Contains(state.Phase);
        Queue = state.Queue;
        OnPropertyChanged(nameof(HasQueuedInput));

        var context = state.ContextUsage;
        if (context is null && state.ContextUsageTokens is { } used)
        {
            var existing = ContextUsage?.Usage;
            context = existing is { Breakdown.Count: 0 }
                && existing.UsedTokens == used
                && existing.MaxTokens == state.ContextWindowTokens
                && string.Equals(existing.TokenSource, state.TokenSource, StringComparison.Ordinal)
                ? existing
                : new ContextUsageState(used, state.ContextWindowTokens, state.TokenSource, []);
        }
        if (ContextUsage?.Usage != context)
            ContextUsage = context is { UsedTokens: > 0, MaxTokens: > 0 } ? new ContextUsageViewModel(context) : null;

        var interactionId = state.PendingPermission?.InteractionId ?? state.PendingInteraction?.InteractionId;
        if (PendingInteraction?.InteractionId != interactionId)
        {
            PendingInteraction = state.PendingPermission is { } permission
                ? PendingInteractionViewModel.FromPermission(permission)
                : state.PendingInteraction is { } interaction
                    ? PendingInteractionViewModel.FromInteraction(interaction)
                    : null;
            IsInteractionSubmitting = false;
            OnPropertyChanged(nameof(HasPendingInteraction));
            OnPropertyChanged(nameof(HasPendingQuestion));
        }
    }

    public async Task SendAsync()
    {
        if (!Composer.CanSend)
        {
            return;
        }
        if (ComposerCommands.TryParse(Composer.Draft, out var command))
        {
            if (Composer.IsTurnActive)
            {
                Composer.InputError = Loc.Get("composer_stop_first_banner");
                return;
            }
            IReadOnlyList<string>? workflows = null;
            switch (command.Kind)
            {
                case ComposerCommandKind.Plan:
                    Composer.PlanEnabled = true;
                    break;
                case ComposerCommandKind.Orchestrator:
                    if (command.Prompt.Length == 0)
                    {
                        Composer.InputError = null;
                        Composer.Draft = string.Empty;
                        var toggled = ComposerViewModel.ToggleWorkflow(Composer.SessionWorkflows, OrchestratorWorkflow.Id);
                        var toggleAck = await SubmitAsync(new SetWorkflowsCommand(
                            StrongId.NewCommandId(), SessionId, _store.Revision, toggled));
                        if (toggleAck.Accepted)
                        {
                            // Optimistic; the authoritative state round-trip overwrites this.
                            Composer.SessionWorkflows = toggled;
                        }
                        else
                        {
                            Banner = toggleAck.RejectionReason ?? Loc.Get("conversation_input_rejected");
                        }
                        return;
                    }
                    if (!ComposerViewModel.ContainsWorkflow(Composer.SessionWorkflows, OrchestratorWorkflow.Id))
                    {
                        workflows = [.. Composer.SessionWorkflows, OrchestratorWorkflow.Id];
                    }
                    break;
            }
            if (command.Prompt.Length == 0)
            {
                Composer.InputError = null;
                Composer.Draft = string.Empty;
                return;
            }
            Composer.InputError = null;
            Composer.Draft = command.Prompt;
            _pendingWorkflows = workflows;
        }
        var text = Composer.Draft;
        var selection = CurrentSelection;
        var submissionSequence = NextSubmissionSequence?.Invoke() ?? 0;
        var mode = Composer.IsTurnActive ? (PermissionMode?)null : Composer.Mode;
        var planEnabled = Composer.IsTurnActive ? (bool?)null : Composer.PlanEnabled;
        var commandWorkflows = _pendingWorkflows;
        _pendingWorkflows = null;
        Composer.IsSending = true;
        try
        {
            var images = Composer.SnapshotImages();
            var ack = await SubmitAsync(new SendTextCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, text, ModelSelection: selection, Mode: mode,
                Images: images, Workflows: commandWorkflows, PlanEnabled: planEnabled));
            if (ack.Accepted)
            {
                if (mode is { } submittedMode)
                {
                    var submittedPlanEnabled = planEnabled ?? Composer.PlanEnabled;
                    ModeSubmitted?.Invoke(submittedMode, submittedPlanEnabled);
                }
                if (commandWorkflows is { } submittedWorkflows)
                {
                    // Optimistic; the authoritative state round-trip overwrites this.
                    Composer.SessionWorkflows = submittedWorkflows;
                }
                if (selection is not null)
                    ModelSelectionSubmitted?.Invoke(selection, submissionSequence);
                Composer.RecordSent(text);
                Composer.ClearIfSendable(text, images);
            }
            else
            {
                Banner = ack.RejectionReason ?? Loc.Get("conversation_input_rejected");
            }
        }
        catch (Exception exception)
        {
            Banner = $"Delivery could not be confirmed. Your message has been kept; check this conversation before sending again. {exception.Message}";
        }
        finally
        {
            Composer.IsSending = false;
        }
    }

    public async Task WithdrawQueuedInputAsync(QueuedInput input)
    {
        if (!_queueOperations.Add(input.CommandId))
        {
            return;
        }
        try
        {
            if (Queue.FirstOrDefault(item => item.CommandId == input.CommandId) is not { } queued)
            {
                Banner = Loc.Get("conversation_queue_unavailable");
                return;
            }
            if (!string.IsNullOrWhiteSpace(Composer.Draft) || Composer.HasAttachments)
            {
                Banner = Loc.Get("conversation_queue_withdraw_draft_first");
                return;
            }
            var ack = await SubmitAsync(new RemoveQueuedInputCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, queued.CommandId));
            if (!ack.Accepted)
            {
                Banner = ack.RejectionReason ?? Loc.Get("conversation_queue_withdraw_failed");
                return;
            }
            Composer.Draft = queued.Text;
            if (queued.Mode is { } mode)
            {
                Composer.RestoreExecutionState(mode, queued.PlanEnabled ?? false);
            }
            if (queued.ModelSelection is { } selection)
            {
                RestoreModelSelection(selection);
            }
            await RestoreImagesAsync(queued.Images,
                Loc.Get("conversation_subject_queued_message"), Loc.Get("conversation_action_withdrawn"));
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("conversation_queue_withdraw_failed_detail", exception.Message);
        }
        finally
        {
            _queueOperations.Remove(input.CommandId);
        }
    }

    public async Task RewindToMessageAsync(UserMessageItemViewModel item)
    {
        if (!item.CanRewind)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(Composer.Draft) || Composer.HasAttachments)
        {
            Banner = Loc.Get("conversation_rewind_draft_first");
            return;
        }
        try
        {
            var ack = await SubmitAsync(new RewindConversationCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, item.Id));
            if (!ack.Accepted)
            {
                Banner = ack.RejectionReason ?? Loc.Get("conversation_rewind_failed");
                return;
            }
            Banner = null;
            Composer.Draft = item.Text;
            await RestoreImagesAsync(item.Images,
                Loc.Get("conversation_subject_message"), Loc.Get("conversation_action_rewound"));
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("conversation_rewind_failed_detail", exception.Message);
        }
    }

    public async Task ForkFromMessageAsync(AssistantMessageItemViewModel item)
    {
        if (!item.CanFork || ForkRequested is null || !_forkOperations.Add(item.Id))
        {
            return;
        }
        try
        {
            await ForkRequested(item.Id);
        }
        finally
        {
            _forkOperations.Remove(item.Id);
        }
    }

    public async Task DeleteQueuedInputAsync(QueuedInput input)
    {
        if (!_queueOperations.Add(input.CommandId))
        {
            return;
        }
        try
        {
            if (Queue.All(item => item.CommandId != input.CommandId))
            {
                return;
            }
            var ack = await SubmitAsync(new RemoveQueuedInputCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, input.CommandId));
            if (!ack.Accepted)
            {
                Banner = ack.RejectionReason ?? Loc.Get("conversation_queue_delete_failed");
            }
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("conversation_queue_delete_failed_detail", exception.Message);
        }
        finally
        {
            _queueOperations.Remove(input.CommandId);
        }
    }

    private async Task RestoreImagesAsync(
        IReadOnlyList<FilePart>? images, string subject, string action)
    {
        if (images is not { Count: > 0 })
        {
            return;
        }
        var restored = new List<ComposerImageAttachment>(images.Count);
        try
        {
            foreach (var image in images)
            {
                var data = await _artifacts.TryLoadAsync(image.ArtifactUri, CancellationToken.None);
                if (data is null)
                {
                    Banner = Loc.Get("conversation_images_lost", subject, action);
                    continue;
                }
                restored.Add(ComposerImageAttachment.FromBytes(
                    data.Value.ToArray(), image.MimeType, image.FileName ?? Loc.Get("conversation_image_fallback")));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            Banner = $"The {subject} was {action}, but its images could not be restored. {exception.Message}";
        }
        if (restored.Count > 0)
        {
            Composer.AddAttachments(restored);
        }
    }

    public async Task StopAsync() =>
        await SubmitAsync(new StopCommand(StrongId.NewCommandId(), SessionId, _store.Revision));

    public async Task CompactAsync() =>
        await SubmitAsync(new CompactCommand(StrongId.NewCommandId(), SessionId, _store.Revision));

    internal void RestorePendingMode(PermissionMode mode) => Composer.RestoreModeOnly(mode);

    public void RestoreComposerState(string draft, PermissionMode mode, ModelSelection? selection)
    {
        Composer.Draft = draft;
        RestorePendingMode(mode);
        RestoreModelSelection(selection);
    }

    internal void RestoreModelSelection(ModelSelection? selection) => SetModelSelection(selection, local: true);

    internal void UpdateModelChoices(IReadOnlyList<ModelChoice> modelChoices, ModelSelection? defaultSelection)
    {
        _modelChoices = modelChoices;
        _defaultSelection = defaultSelection;
        if (_currentSelection is not { } selection)
        {
            return;
        }
        var choice = modelChoices.FirstOrDefault(model =>
            model.ProviderId == selection.ProviderId && model.ModelId == selection.ModelId);
        if (choice is null)
        {
            SetModelSelection(null, local: false);
            if (_hasLocalSelection && _currentSelection is { } fallback)
            {
                ModelSelectionChanged?.Invoke(fallback);
            }
            return;
        }
        ConfigureModel(choice);
    }

    private void SetModelSelection(ModelSelection? selection, bool local)
    {
        var previous = _currentSelection;
        _hasLocalSelection |= local && selection is not null;
        if (_modelChoices is { Count: > 0 })
        {
            var choice = _modelChoices.FirstOrDefault(model => model.ProviderId == selection?.ProviderId && model.ModelId == selection.ModelId);
            if (choice is null)
            {
                selection = _defaultSelection ?? _modelChoices[0].Selection;
                choice = _modelChoices.First(model => model.ProviderId == selection.ProviderId && model.ModelId == selection.ModelId);
            }
            _configuredModel = choice;
            selection = choice.NormalizeSelection(selection!);
        }
        else if (_configuredModel is { } configured && configured.ProviderId == selection?.ProviderId && configured.ModelId == selection.ModelId)
        {
            selection = configured.NormalizeSelection(selection);
        }
        _applyingState = true;
        try
        {
            _currentSelection = selection;
            if (_configuredModel is { } model && model.ProviderId == selection?.ProviderId && model.ModelId == selection.ModelId)
                ThoughtLevels = model.Properties?.ReasoningLevels ?? [];
            ThoughtLevel = selection?.Options?.ReasoningLevel ?? "off";
        }
        finally
        {
            _applyingState = false;
        }
        var selectionChanged = !Equals(previous, selection);
        if (selectionChanged || string.IsNullOrEmpty(ModelText))
            ModelText = selection is null ? "default model" : $"{selection.ProviderId}/{selection.ModelId}";
        if (selectionChanged)
            OnPropertyChanged(nameof(CurrentSelection));
    }

    public void ConfigureModel(ModelChoice choice, bool useDefaultReasoning = false)
    {
        _configuredModel = choice;
        _applyingState = true;
        try
        {
            ThoughtLevels = choice.Properties?.ReasoningLevels ?? [];
            if (useDefaultReasoning || !ThoughtLevels.Contains(ThoughtLevel))
                ThoughtLevel = (useDefaultReasoning ? ThoughtLevels.LastOrDefault() : ThoughtLevels.FirstOrDefault()) ?? "off";
        }
        finally
        {
            _applyingState = false;
        }
        if (_currentSelection is { } selection && selection.ProviderId == choice.ProviderId && selection.ModelId == choice.ModelId)
        {
            var normalized = ApplyThoughtLevel(choice.NormalizeSelection(selection));
            if (normalized != selection)
            {
                SetModelSelection(normalized, local: useDefaultReasoning);
                if (_hasLocalSelection)
                    ModelSelectionChanged?.Invoke(normalized);
            }
        }
    }

    public ModelSelection ApplyThoughtLevel(ModelSelection selection) => selection with
    {
        Options = (selection.Options ?? new ModelCallOptions()) with
        {
            ReasoningLevel = ThoughtLevel == "" || ThoughtLevel == "off" && !ThoughtLevels.Contains("off") ? null : ThoughtLevel,
        },
    };

    partial void OnThoughtLevelChanged(string value)
    {
        if (_applyingState || _currentSelection is not { } selection)
            return;
        if (_configuredModel is not null && !ThoughtLevels.Contains(value))
        {
            _applyingState = true;
            ThoughtLevel = selection.Options?.ReasoningLevel ?? "off";
            _applyingState = false;
            return;
        }
        _ = SwitchModelAsync(ApplyThoughtLevel(selection));
    }

    public Task SwitchModelAsync(ModelSelection selection)
    {
        RestoreModelSelection(selection);
        if (_currentSelection is not null)
            ModelSelectionChanged?.Invoke(_currentSelection);
        return Task.CompletedTask;
    }

    public async Task ResolvePermissionAsync(PermissionDecision decision)
    {
        if (IsInteractionSubmitting || PendingInteraction is not { IsPermission: true } pending)
        {
            return;
        }
        IsInteractionSubmitting = true;
        var awaitingProjection = false;
        try
        {
            var ack = await SubmitAsync(new ResolveInteractionCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, pending.InteractionId, PermissionDecision: decision));
            if (!ack.Accepted)
                Banner = ack.RejectionReason ?? Loc.Get("conversation_permission_rejected");
            else
                awaitingProjection = ReferenceEquals(PendingInteraction, pending);
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("conversation_permission_failed_detail", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(PendingInteraction, pending))
                IsInteractionSubmitting = awaitingProjection;
        }
    }

    public Task SubmitInteractionAnswersAsync() => ResolveQuestionAsync(dismiss: false);

    public Task DismissInteractionAsync() => ResolveQuestionAsync(dismiss: true);

    private async Task ResolveQuestionAsync(bool dismiss)
    {
        if (IsInteractionSubmitting || PendingInteraction is not { IsPermission: false } pending)
        {
            return;
        }
        IsInteractionSubmitting = true;
        var awaitingProjection = false;
        try
        {
            var answers = dismiss ? new Dictionary<string, string>() : pending.SelectedAnswers;
            var ack = await SubmitAsync(new ResolveInteractionCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, pending.InteractionId,
                InteractionAnswer: new InteractionAnswer(answers, dismiss ? null : pending.SelectedAnnotations)));
            if (!ack.Accepted)
                Banner = ack.RejectionReason ?? Loc.Get("conversation_answers_rejected");
            else
                awaitingProjection = ReferenceEquals(PendingInteraction, pending);
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("conversation_answers_failed_detail", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(PendingInteraction, pending))
                IsInteractionSubmitting = awaitingProjection;
        }
    }

    public void SelectInteractionAnswer(string header, string value) =>
        PendingInteraction?.SelectAnswer(header, value);

    public void ClearBanner() => Banner = null;

    private async Task<CommandAck> SubmitAsync(ConversationCommand command)
    {
        var ack = await _runtime.SubmitAsync(command, CancellationToken.None);
        _store.SetRevision(ack.Revision);
        return ack;
    }

    public void Dispose()
    {
        _store.Updated -= ApplyUpdate;
        Status.Dispose();
    }
}
