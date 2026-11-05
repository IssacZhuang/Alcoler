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
    // Mirrors the exact instance membership of Items so hot reconcile paths can
    // answer Contains without scanning the collection.
    private readonly HashSet<TimelineItemViewModel> _itemsInCollection = [];
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
    private readonly Dictionary<string, bool> _turnExpansion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _turnDurations = new(StringComparer.Ordinal);
    private TurnPhase _lastPhase = TurnPhase.Idle;
    private DateTimeOffset? _turnStartedAt;
    private bool _turnLayoutDirty;
    private long _storeSequence;
    private bool _hasStoreSnapshot;
    private bool _disposed;
    private TimelineReadingState _readingState;
    private readonly Dictionary<string, TimelineExpansion> _restoredExpansion;

    public ConversationViewModel(IAgentRuntime runtime, ProjectionStore store, ComposerViewModel? composer = null,
        IReadOnlyList<ModelChoice>? modelChoices = null, ModelSelection? defaultSelection = null,
        IArtifactStore? artifacts = null, TimelineReadingState? readingState = null)
    {
        _runtime = runtime;
        _store = store;
        _artifacts = artifacts ?? new FileArtifactStore(WorkbenchDataPaths.ArtifactsPath);
        _modelChoices = modelChoices;
        _defaultSelection = defaultSelection;
        _readingState = readingState ?? TimelineReadingState.Initial;
        _restoredExpansion = _readingState.ExpandedRows.TakeLast(TimelineReadingState.MaxExpandedRows)
            .ToDictionary(row => row.RowId, row => row.Expansion, StringComparer.Ordinal);
        foreach (var (rowId, expansion) in _restoredExpansion)
            if (expansion.HasFlag(TimelineExpansion.Turn))
                _turnExpansion[rowId] = true;
        Composer = composer ?? new ComposerViewModel();
        SessionId = store.SessionId;
        Status = new ConversationStatusViewModel(SessionId, runtime);
        lock (_applyGate)
        {
            var initial = store.Attach(OnStoreUpdated, out _hasStoreSnapshot);
            _storeSequence = initial.Sequence;
            ApplyUpdateCore(initial);
        }
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

    internal event Action? TimelineChanging;
    internal event Action? ReadingStateCapturing;

    internal TimelineReadingState ReadingState => _readingState;

    internal void SaveReadingPosition(TimelineScrollAnchor? anchor, bool followLatest) =>
        _readingState = _readingState with { Anchor = anchor, FollowLatest = followLatest };

    internal TimelineReadingState CaptureReadingState()
    {
        ReadingStateCapturing?.Invoke();
        List<TimelineExpandedRow> expanded = [];
        HashSet<string> captured = new(StringComparer.Ordinal);
        var center = _readingState.Anchor is { } anchor && _itemsByRowId.TryGetValue(anchor.RowId, out var anchorItem)
            ? Items.IndexOf(anchorItem) : Items.Count - 1;
        for (var index = center; index >= 0; index--)
            if (Items[index] is UserMessageItemViewModel turn)
            {
                Capture(turn);
                break;
            }
        for (var distance = 0; expanded.Count < TimelineReadingState.MaxExpandedRows &&
            (center - distance >= 0 || center + distance < Items.Count); distance++)
        {
            if (center - distance >= 0)
                Capture(Items[center - distance]);
            if (distance > 0 && center + distance < Items.Count)
                Capture(Items[center + distance]);
        }
        return _readingState = _readingState with { ExpandedRows = expanded.ToArray() };

        void Capture(TimelineItemViewModel item)
        {
            if (expanded.Count >= TimelineReadingState.MaxExpandedRows || captured.Contains(item.RowId))
                return;
            var expansion = item switch
            {
                UserMessageItemViewModel when _turnExpansion.GetValueOrDefault(item.RowId) => TimelineExpansion.Turn,
                ToolCallItemViewModel { IsExpanded: true } => TimelineExpansion.Tool,
                AssistantMessageItemViewModel { IsReasoningExpanded: true } => TimelineExpansion.Reasoning,
                FileChangeSummaryItemViewModel { IsExpanded: true } => TimelineExpansion.Files,
                _ => TimelineExpansion.None,
            };
            if (expansion != TimelineExpansion.None)
            {
                captured.Add(item.RowId);
                expanded.Add(new TimelineExpandedRow(item.RowId, expansion));
            }
        }
    }

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

    private void OnStoreUpdated(ProjectionUpdate update)
    {
        lock (_applyGate)
        {
            if (_disposed || (_hasStoreSnapshot && update.Sequence <= _storeSequence))
                return;
            _storeSequence = update.Sequence;
            _hasStoreSnapshot = true;
            ApplyUpdateCore(update);
        }
    }

    public async Task SendAsync()
    {
        if (!Composer.CanSend)
        {
            return;
        }
        await ComposerSubmission.SubmitAsync(new ComposerSubmission.Request<ConversationSendState>
        {
            Composer = Composer,
            ApplyCommandAsync = async command =>
            {
                if (Composer.IsTurnActive)
                {
                    Composer.InputError = Loc.Get("composer_stop_first_banner");
                    return true;
                }
                if (command.Kind == ComposerCommandKind.Orchestrator)
                {
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
                        return true;
                    }
                    if (!ComposerViewModel.ContainsWorkflow(Composer.SessionWorkflows, OrchestratorWorkflow.Id))
                    {
                        _pendingWorkflows = [.. Composer.SessionWorkflows, OrchestratorWorkflow.Id];
                    }
                }
                return false;
            },
            CaptureState = () =>
            {
                var workflows = _pendingWorkflows;
                _pendingWorkflows = null;
                return new ConversationSendState(CurrentSelection, NextSubmissionSequence?.Invoke() ?? 0,
                    Composer.IsTurnActive ? (PermissionMode?)null : Composer.Mode,
                    Composer.IsTurnActive ? (bool?)null : Composer.PlanEnabled, workflows);
            },
            BuildCommandAsync = (state, text, images) => Task.FromResult(new SendTextCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, text, ModelSelection: state.Selection,
                Mode: state.Mode, Images: images, Workflows: state.Workflows, PlanEnabled: state.PlanEnabled)),
            SubmitCommandAsync = SubmitAsync,
            OnAccepted = state =>
            {
                if (state.Mode is { } submittedMode)
                {
                    var submittedPlanEnabled = state.PlanEnabled ?? Composer.PlanEnabled;
                    ModeSubmitted?.Invoke(submittedMode, submittedPlanEnabled);
                }
                if (state.Workflows is { } submittedWorkflows)
                {
                    // Optimistic; the authoritative state round-trip overwrites this.
                    Composer.SessionWorkflows = submittedWorkflows;
                }
                if (state.Selection is not null)
                    ModelSelectionSubmitted?.Invoke(state.Selection, state.Sequence);
            },
            OnRejected = (_, ack) => Banner = ack.RejectionReason ?? Loc.Get("conversation_input_rejected"),
            DescribeFailure = exception => Loc.Get("workbench_delivery_unconfirmed", exception.Message),
            ReportFailure = (_, message) => Banner = message,
        });
    }

    public async Task SendQueuedNowAsync(QueuedInput input)
    {
        if (!_queueOperations.Add(input.CommandId))
        {
            return;
        }
        try
        {
            if (Queue.All(item => item.CommandId != input.CommandId))
            {
                Banner = Loc.Get("conversation_queue_unavailable");
                return;
            }
            var ack = await SubmitAsync(new SendQueuedNowCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, input.CommandId));
            if (!ack.Accepted)
            {
                Banner = ack.RejectionReason ?? Loc.Get("conversation_queue_send_now_failed");
            }
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("conversation_queue_send_now_failed_detail", exception.Message);
        }
        finally
        {
            _queueOperations.Remove(input.CommandId);
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

    public void ToggleTurnSummary(UserMessageItemViewModel anchor)
    {
        var start = Items.IndexOf(anchor);
        if (start < 0)
            return;
        var end = Items.Count;
        for (var index = start + 1; index < Items.Count; index++)
        {
            if (Items[index] is UserMessageItemViewModel)
            {
                end = index;
                break;
            }
        }
        var finalIndex = FindFinalAssistantIndex(start, end);
        if (finalIndex < 0)
            return;
        TimelineChanging?.Invoke();
        var expanded = !anchor.IsTurnExpanded;
        if (expanded)
            _turnExpansion[anchor.RowId] = true;
        else
            _turnExpansion.Remove(anchor.RowId);
        anchor.IsTurnExpanded = expanded;
        for (var index = start + 1; index < finalIndex; index++)
            Items[index].IsFolded = !expanded;
        if (Items[finalIndex] is AssistantMessageItemViewModel assistant)
            assistant.IsReasoningSuppressed = !expanded;
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
        await ResolveInteractionAsync(pending, interaction => new ResolveInteractionCommand(
            StrongId.NewCommandId(), SessionId, _store.Revision, interaction.InteractionId, PermissionDecision: decision),
            "conversation_permission_rejected", "conversation_permission_failed_detail");
    }

    public Task SubmitInteractionAnswersAsync() => ResolveQuestionAsync(dismiss: false);

    public Task DismissInteractionAsync() => ResolveQuestionAsync(dismiss: true);

    private async Task ResolveQuestionAsync(bool dismiss)
    {
        if (IsInteractionSubmitting || PendingInteraction is not { IsPermission: false } pending)
        {
            return;
        }
        await ResolveInteractionAsync(pending, interaction =>
        {
            var answers = dismiss ? new Dictionary<string, string>() : interaction.SelectedAnswers;
            return new ResolveInteractionCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, interaction.InteractionId,
                InteractionAnswer: new InteractionAnswer(answers, dismiss ? null : interaction.SelectedAnnotations));
        }, "conversation_answers_rejected", "conversation_answers_failed_detail");
    }

    // IsInteractionSubmitting hands off with PendingInteraction: a replacement interaction
    // resets it in ApplyState, so an accepted resolve stays raised until then.
    private async Task ResolveInteractionAsync(PendingInteractionViewModel pending,
        Func<PendingInteractionViewModel, ResolveInteractionCommand> buildCommand, string rejectionKey, string failureKey)
    {
        IsInteractionSubmitting = true;
        var awaitingProjection = false;
        try
        {
            var ack = await SubmitAsync(buildCommand(pending));
            if (!ack.Accepted)
                Banner = ack.RejectionReason ?? Loc.Get(rejectionKey);
            else
                awaitingProjection = ReferenceEquals(PendingInteraction, pending);
        }
        catch (Exception exception)
        {
            Banner = Loc.Get(failureKey, exception.Message);
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
        lock (_applyGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _store.Detach(OnStoreUpdated);
            Status.Dispose();
        }
    }

    private sealed record ConversationSendState(
        ModelSelection? Selection,
        long Sequence,
        PermissionMode? Mode,
        bool? PlanEnabled,
        IReadOnlyList<string>? Workflows);
}
