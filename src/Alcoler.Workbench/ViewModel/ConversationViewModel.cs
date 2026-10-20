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

public sealed partial class ConversationViewModel : ObservableObject, IDisposable
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

    public ConversationViewModel(IAgentRuntime runtime, ProjectionStore store)
    {
        _runtime = runtime;
        _store = store;
        SessionId = store.SessionId;
        _store.Updated += ApplyUpdate;
    }

    public SessionId SessionId { get; }

    public ComposerViewModel Composer { get; } = new();

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
    private PermissionMode _mode = PermissionMode.Build;

    [ObservableProperty]
    private string _modelText = string.Empty;

    [ObservableProperty]
    private string _thoughtLevel = "off";

    private ModelSelection? _currentSelection;

    private bool _applyingState;

    public IReadOnlyList<string> ThoughtLevels { get; } = ["off", "low", "medium", "high"];

    [ObservableProperty]
    private int _contextUsagePercent;

    [ObservableProperty]
    private string _contextUsageText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<QueuedInput> _queue = [];

    public bool HasQueuedInput => Queue.Count > 0;

    [ObservableProperty]
    private PendingInteractionViewModel? _pendingInteraction;

    public bool HasPendingInteraction => PendingInteraction is not null;

    [ObservableProperty]
    private string? _banner;

    internal void ApplyUpdate(ProjectionUpdate update)
    {
        if (update.Reset)
        {
            Items.Clear();
            _itemsByRowId.Clear();
            var appended = false;
            foreach (var row in update.ResetRows ?? [])
            {
                if (TimelineItemFactory.Create(row) is { } item)
                {
                    _itemsByRowId[item.RowId] = item;
                    Items.Add(item);
                    appended = true;
                }
            }
            ApplyState(update.State);
            if (appended)
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
                if (existing is not null && TimelineItemFactory.Update(existing, upserted.Row))
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
        _applyingState = true;
        try
        {
            _currentSelection = state.ModelSelection;
            ThoughtLevel = state.ModelSelection?.Options?.ReasoningLevel ?? "off";
        }
        finally
        {
            _applyingState = false;
        }
        Title = state.Title;
        if (Composer.Mode == Mode)
        {
            Composer.Mode = state.Mode;
        }
        Mode = state.Mode;
        Composer.SessionMode = state.Mode;
        ModelText = state.ModelSelection is { } selection
            ? $"{selection.ProviderId}/{selection.ModelId}"
            : "default model";
        PhaseText = state.Phase.ToString();
        Composer.IsTurnActive = BusyPhases.Contains(state.Phase);
        Queue = state.Queue;
        OnPropertyChanged(nameof(HasQueuedInput));

        if (state.ContextUsageTokens is { } used && state.ContextWindowTokens > 0)
        {
            ContextUsagePercent = Math.Clamp((int)(100L * used / state.ContextWindowTokens), 0, 100);
            var windowK = state.ContextWindowTokens / 1000;
            var usedK = used / 1000;
            ContextUsageText = $"{usedK}/{windowK}k ({state.TokenSource})";
        }
        else
        {
            ContextUsagePercent = 0;
            ContextUsageText = string.Empty;
        }

        var pending = state.PendingPermission is { } permission
            ? PendingInteractionViewModel.FromPermission(permission)
            : state.PendingInteraction is { } interaction
                ? PendingInteractionViewModel.FromInteraction(interaction)
                : null;
        if (PendingInteraction?.InteractionId != pending?.InteractionId)
        {
            PendingInteraction = pending;
            OnPropertyChanged(nameof(HasPendingInteraction));
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
            var ack = await SubmitAsync(new SendTextCommand(
                StrongId.NewCommandId(), SessionId, _store.Revision, text, Mode: mode));
            if (ack.Accepted)
            {
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

    public ModelSelection ApplyThoughtLevel(ModelSelection selection) =>
        ThoughtLevel is "off" or ""
            ? selection
            : selection with { Options = new ModelCallOptions(ReasoningLevel: ThoughtLevel) };

    partial void OnThoughtLevelChanged(string value)
    {
        if (_applyingState || _currentSelection is not { } selection)
        {
            return;
        }
        _ = SwitchModelAsync(ApplyThoughtLevel(selection with { Options = null }));
    }

    public async Task SwitchModelAsync(ModelSelection selection) =>
        await SubmitAsync(new SwitchModelCommand(StrongId.NewCommandId(), SessionId, _store.Revision, selection));

    public async Task ResolvePermissionAsync(PermissionDecision decision)
    {
        if (PendingInteraction is not { IsPermission: true } pending)
        {
            return;
        }
        await SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), SessionId, _store.Revision, pending.InteractionId, PermissionDecision: decision));
    }

    public async Task SubmitInteractionAnswersAsync()
    {
        if (PendingInteraction is not { IsPermission: false, CanSubmit: true } pending)
        {
            return;
        }
        await SubmitAsync(new ResolveInteractionCommand(
            StrongId.NewCommandId(), SessionId, _store.Revision, pending.InteractionId,
            InteractionAnswer: new InteractionAnswer(pending.SelectedAnswers)));
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
    }
}
