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
using Alcoler.Workbench.Stores;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.Workbench.ViewModel;

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
    private readonly Dictionary<string, TimelineItemViewModel> _itemsByRowId = [];
    private readonly Dictionary<string, SystemNoticeRow> _agentRows = [];
    private readonly List<string> _rowOrder = [];
    private readonly HashSet<CommandId> _queueOperations = [];
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
        Composer.ModeSelected += OnModeSelected;
    }

    public SessionId SessionId { get; }

    public ComposerViewModel Composer { get; }

    public ConversationStatusViewModel Status { get; }

    public ModelSelection? CurrentSelection => _currentSelection;

    public event Action<PermissionMode>? ModeSubmitted;

    public event Action<ModelSelection>? ModelSelectionChanged;

    public event Action<ModelSelection, long>? ModelSelectionSubmitted;

    internal Func<long>? NextSubmissionSequence { get; set; }

    private bool _restoredMode;
    private bool _restoredComposer;
    private long _restoredRevision;
    private ModelChoice? _configuredModel;

    public ObservableCollection<TimelineItemViewModel> Items { get; } = [];

    /// <summary>Raised after rows were appended so the view can keep the timeline pinned to the bottom.</summary>
    public event Action? TimelineAppended;

    [ObservableProperty]
    private string _title = string.Empty;

    public string HeaderTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled session" : Title;

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
    private PermissionMode _mode = PermissionMode.Build;

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
        foreach (var op in update.Ops)
        {
            appendedDelta |= ApplyOp(op);
        }
        if (update.Ops.Any(op => op switch
            {
                RowAppended { Row: SystemNoticeRow { Subagent: not null } } => true,
                RowUpserted { Row: SystemNoticeRow { Subagent: not null } } => true,
                RowAppended { Row: ToolCallRow tool } => AgentCallItemViewModel.IsAgentTool(tool.ToolName),
                RowUpserted { Row: ToolCallRow tool } => AgentCallItemViewModel.IsAgentTool(tool.ToolName),
                RowRemoved => _agentRows.Count > 0 || Items.OfType<AgentCallItemViewModel>().Any(),
                _ => false,
            }))
            ReconcileAgents();
        if (update.Ops.OfType<StatePatched>().Any())
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
        foreach (var row in rows)
        {
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
        var dispatches = _itemsByRowId.Values.OfType<AgentCallItemViewModel>().Where(item => item.IsDispatch).ToArray();
        var byToolId = dispatches.ToDictionary(item => item.Id!.Value);
        var legacyIds = dispatches.Where(item => !string.IsNullOrEmpty(item.LegacyAgentId))
            .GroupBy(item => item.LegacyAgentId!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var paired = new HashSet<string>();
        var states = new Dictionary<AgentCallItemViewModel, Alcoler.Contracts.Subagents.SubagentState>();
        foreach (var row in _agentRows.Values)
        {
            var agent = row.Subagent!;
            AgentCallItemViewModel? dispatch = null;
            if (agent.ParentToolCallId is { } toolId)
                byToolId.TryGetValue(toolId, out dispatch);
            else
                legacyIds.TryGetValue(agent.Id.Value, out dispatch);
            if (dispatch is null)
                continue;
            states[dispatch] = agent;
            paired.Add(row.RowId);
        }
        foreach (var dispatch in dispatches)
            dispatch.SetSubagent(states.GetValueOrDefault(dispatch));
        for (var index = Items.Count - 1; index >= 0; index--)
            if (paired.Contains(Items[index].RowId))
                Items.RemoveAt(index);
        foreach (var row in _agentRows.Values.Where(row => !paired.Contains(row.RowId)))
        {
            var item = _itemsByRowId[row.RowId];
            if (Items.Contains(item))
                continue;
            var rowIndex = _rowOrder.IndexOf(row.RowId);
            var index = Items.TakeWhile(visible => _rowOrder.IndexOf(visible.RowId) < rowIndex).Count();
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
                _rowOrder.Add(row.RowId);
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
                _rowOrder.Remove(removed.RowId);
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
        if (_restoredComposer && _store.Revision > _restoredRevision)
            _restoredComposer = false;
        if (!_hasLocalSelection)
        {
            SetModelSelection(state.ModelSelection, local: false);
        }
        Title = state.Title;
        if (!_restoredMode && !_restoredComposer && Composer.Mode == Mode)
        {
            Composer.RestoreMode(state.Mode);
        }
        Mode = state.Mode;
        Composer.SessionMode = state.Mode;
        ModelText = _currentSelection is { } selection
            ? $"{selection.ProviderId}/{selection.ModelId}"
            : "default model";
        PhaseText = state.Phase.ToString();
        ActivityText = state.Phase switch
        {
            TurnPhase.ProcessingInput => "Preparing request",
            TurnPhase.AwaitingModel => "Waiting for model",
            TurnPhase.Streaming => "Generating response",
            TurnPhase.ExecutingTools => "Running tools",
            TurnPhase.AwaitingPermission => "Waiting for permission",
            TurnPhase.AwaitingInteraction => "Waiting for your answer",
            TurnPhase.Completing => "Finishing up",
            _ => string.Empty,
        };
        IsWorking = state.Phase is TurnPhase.ProcessingInput or TurnPhase.AwaitingModel
            or TurnPhase.Streaming or TurnPhase.ExecutingTools or TurnPhase.Completing;
        Composer.IsTurnActive = BusyPhases.Contains(state.Phase);
        Queue = state.Queue;
        OnPropertyChanged(nameof(HasQueuedInput));

        var context = state.ContextUsage ?? (state.ContextUsageTokens is { } used
            ? new ContextUsageState(used, state.ContextWindowTokens, state.TokenSource, []) : null);
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
        var text = Composer.Draft;
        var selection = CurrentSelection;
        var submissionSequence = NextSubmissionSequence?.Invoke() ?? 0;
        var mode = Composer.IsTurnActive ? (PermissionMode?)null : Composer.Mode;
        Composer.IsSending = true;
        try
        {
            var images = Composer.SnapshotImages();
            // Queued input must not mutate the active turn's permission policy.
            _restoredComposer = false;
            var ack = await SubmitAsync(new SendTextCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, text, ModelSelection: selection, Mode: mode, Images: images));
            if (ack.Accepted)
            {
                if (mode is { } submittedMode)
                {
                    AcknowledgeMode(submittedMode);
                    ModeSubmitted?.Invoke(submittedMode);
                }
                if (selection is not null)
                    ModelSelectionSubmitted?.Invoke(selection, submissionSequence);
                Composer.RecordSent(text);
                Composer.ClearIfSendable(text, images);
            }
            else
            {
                Banner = ack.RejectionReason ?? "Input was rejected";
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
                Banner = "That queued message is no longer available.";
                return;
            }
            if (!string.IsNullOrWhiteSpace(Composer.Draft) || Composer.HasAttachments)
            {
                Banner = "Send or clear the current draft before withdrawing a queued message.";
                return;
            }
            var ack = await SubmitAsync(new RemoveQueuedInputCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, queued.CommandId));
            if (!ack.Accepted)
            {
                Banner = ack.RejectionReason ?? "The queued message could not be withdrawn.";
                return;
            }
            Composer.Draft = queued.Text;
            if (queued.Mode is { } mode)
            {
                Composer.RestoreMode(mode);
            }
            if (queued.ModelSelection is { } selection)
            {
                RestoreModelSelection(selection);
            }
            await RestoreImagesAsync(queued.Images, "queued message", "withdrawn");
        }
        catch (Exception exception)
        {
            Banner = $"The queued message could not be withdrawn. {exception.Message}";
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
            Banner = "Send or clear the current draft before rewinding a message.";
            return;
        }
        try
        {
            var ack = await SubmitAsync(new RewindConversationCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, item.Id));
            if (!ack.Accepted)
            {
                Banner = ack.RejectionReason ?? "The message could not be rewound.";
                return;
            }
            Banner = null;
            Composer.Draft = item.Text;
            await RestoreImagesAsync(item.Images, "message", "rewound");
        }
        catch (Exception exception)
        {
            Banner = $"The message could not be rewound. {exception.Message}";
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
                Banner = ack.RejectionReason ?? "The queued message could not be deleted.";
            }
        }
        catch (Exception exception)
        {
            Banner = $"The queued message could not be deleted. {exception.Message}";
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
                    Banner = $"The {subject} was {action}, but some of its images are no longer available.";
                    continue;
                }
                restored.Add(ComposerImageAttachment.FromBytes(
                    data.Value.ToArray(), image.MimeType, image.FileName ?? "Image"));
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

    internal void AcknowledgeMode(PermissionMode mode)
    {
        if (Composer.Mode == mode)
            _restoredMode = false;
    }

    private void OnModeSelected(PermissionMode mode) => _restoredMode = true;

    internal void RestorePendingMode(PermissionMode mode)
    {
        _restoredMode = true;
        Composer.RestoreMode(mode);
    }

    public void RestoreComposerState(string draft, PermissionMode mode, ModelSelection? selection)
    {
        _restoredComposer = true;
        _restoredRevision = _store.Revision;
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
        ModelText = selection is null ? "default model" : $"{selection.ProviderId}/{selection.ModelId}";
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
        if (PendingInteraction is not { IsPermission: true } pending)
        {
            return;
        }
        await SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), SessionId, _store.Revision, pending.InteractionId, PermissionDecision: decision));
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
                Banner = ack.RejectionReason ?? "Answers were not accepted";
            else
                awaitingProjection = ReferenceEquals(PendingInteraction, pending);
        }
        catch (Exception exception)
        {
            Banner = $"Could not submit answers. {exception.Message}";
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
        Composer.ModeSelected -= OnModeSelected;
        Status.Dispose();
    }
}
