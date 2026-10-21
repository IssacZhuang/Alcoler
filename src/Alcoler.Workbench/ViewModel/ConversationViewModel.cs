using System.Collections.ObjectModel;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
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

    public ConversationViewModel(IAgentRuntime runtime, ProjectionStore store, ComposerViewModel? composer = null)
    {
        _runtime = runtime;
        _store = store;
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
        foreach (var row in rows)
        {
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
    }

    private static bool TryUpdateItem(TimelineItemViewModel item, ConversationRow row) => (item, row) switch
    {
        (AssistantMessageItemViewModel assistant, AssistantMessageRow assistantRow) when assistant.Id == assistantRow.Id =>
            TimelineItemFactory.Update(item, row),
        (ToolCallItemViewModel tool, ToolCallRow toolRow) when tool.Id == toolRow.Id && tool.ToolName == toolRow.ToolName =>
            TimelineItemFactory.Update(item, row),
        (UserMessageItemViewModel user, UserMessageRow userRow) => user.Text == userRow.Text,
        (SystemNoticeItemViewModel notice, SystemNoticeRow noticeRow) => notice.Kind == noticeRow.Kind && notice.Text == noticeRow.Text,
        _ => false,
    };

    private bool ApplyOp(RowOp op)
    {
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
        _applyingState = true;
        try
        {
            if (!_restoredComposer)
            {
                _currentSelection = state.ModelSelection;
                ThoughtLevel = state.ModelSelection?.Options?.ReasoningLevel ?? "off";
            }
        }
        finally
        {
            _applyingState = false;
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
        var mode = Composer.IsTurnActive ? (PermissionMode?)null : Composer.Mode;
        Composer.IsSending = true;
        try
        {
            // Queued input must not mutate the active turn's permission policy.
            var restoredSelection = _restoredComposer ? _currentSelection : null;
            _restoredComposer = false;
            var ack = await SubmitAsync(new SendTextCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, text, ModelSelection: restoredSelection, Mode: mode));
            if (ack.Accepted)
            {
                if (mode is { } submittedMode)
                {
                    AcknowledgeMode(submittedMode);
                    ModeSubmitted?.Invoke(submittedMode);
                }
                Composer.ClearIfSendable(text);
            }
            else
            {
                Banner = ack.RejectionReason ?? "Input was rejected";
            }
        }
        finally
        {
            Composer.IsSending = false;
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
        _applyingState = true;
        try
        {
            _currentSelection = selection;
            ThoughtLevel = selection?.Options?.ReasoningLevel ?? "off";
            ModelText = selection is null ? "default model" : $"{selection.ProviderId}/{selection.ModelId}";
        }
        finally
        {
            _applyingState = false;
        }
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
        if (_applyingState || _currentSelection is not { } selection
            || (_configuredModel is not null && !ThoughtLevels.Contains(value)))
        {
            return;
        }
        _ = SwitchModelAsync(ApplyThoughtLevel(selection));
    }

    public async Task SwitchModelAsync(ModelSelection selection)
    {
        _restoredComposer = false;
        var ack = await SubmitAsync(new SwitchModelCommand(StrongId.NewCommandId(), SessionId, _store.Revision, selection));
        if (ack.Accepted)
        {
            _currentSelection = selection;
            ModelText = $"{selection.ProviderId}/{selection.ModelId}";
        }
        else
        {
            Banner = ack.RejectionReason ?? "Model selection was rejected";
        }
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
