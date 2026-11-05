using Alcoler.Contracts.Projections;
using Alcoler.Localization;
using Alcoler.Stores;

namespace Alcoler.ViewModel;

public sealed partial class ConversationViewModel
{
    internal void ApplyUpdate(ProjectionUpdate update)
    {
        lock (_applyGate)
        {
            if (!_disposed)
                ApplyUpdateCore(update);
        }
    }

    private void ApplyUpdateCore(ProjectionUpdate update)
    {
        TimelineChanging?.Invoke();
        Status.ApplyUpdate(update);
        if (update.Reset)
        {
            ReconcileRows(update.ResetRows ?? []);
            // A reload re-derives turn boundaries from row order, so live timing
            // tracked before the reset no longer applies.
            _lastPhase = TurnPhase.Idle;
            _turnStartedAt = null;
            ApplyState(update.State);
            RebuildTurnLayout();
            _turnLayoutDirty = false;
            if (Items.Count > 0)
            {
                TimelineAppended?.Invoke();
            }
            return;
        }

        var appendedDelta = false;
        var rowUpserted = false;
        var reconcileAgents = false;
        var rowsRemoved = false;
        var statePatched = false;
        foreach (var op in update.Ops)
        {
            appendedDelta |= ApplyOp(op);
            rowUpserted |= op is RowUpserted or RowTextAppended;
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
        // A coalesced flush can deliver a turn start and completion as one terminal
        // state patch; row activity plus a terminal phase still marks the turn done.
        if (rowsRemoved || reconcileAgents ||
            (statePatched && (appendedDelta || rowUpserted) &&
             update.State.Phase is TurnPhase.Idle or TurnPhase.Error))
            _turnLayoutDirty = true;
        if (_turnLayoutDirty)
        {
            _turnLayoutDirty = false;
            RebuildTurnLayout();
            appendedDelta = true;
        }
        if (appendedDelta)
        {
            TimelineAppended?.Invoke();
        }
    }

    private TimelineItemViewModel? CreateTimelineItem(ConversationRow row)
    {
        var item = TimelineItemFactory.Create(row);
        if (!_restoredExpansion.Remove(row.RowId, out var expansion))
            return item;
        switch (item)
        {
            case ToolCallItemViewModel tool:
                tool.IsExpanded = expansion.HasFlag(TimelineExpansion.Tool);
                break;
            case AssistantMessageItemViewModel assistant:
                assistant.IsReasoningExpanded = expansion.HasFlag(TimelineExpansion.Reasoning);
                break;
            case FileChangeSummaryItemViewModel files:
                files.IsExpanded = expansion.HasFlag(TimelineExpansion.Files);
                break;
        }
        return item;
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
                : CreateTimelineItem(row);
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
            // Past the end every earlier position already matched, so the item is
            // new; the row-order map confirms it belongs exactly here.
            if (index == Items.Count && _rowOrderIndex.GetValueOrDefault(item.RowId, -1) == index)
            {
                Items.Add(item);
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
        _itemsInCollection.Clear();
        foreach (var item in items)
            _itemsInCollection.Add(item);
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
            if (!_pairedAgentRows.Contains(Items[index].RowId))
                continue;
            _itemsInCollection.Remove(Items[index]);
            Items.RemoveAt(index);
        }
        foreach (var row in _agentRows.Values)
        {
            if (_pairedAgentRows.Contains(row.RowId))
                continue;
            var item = _itemsByRowId[row.RowId];
            if (_itemsInCollection.Contains(item))
                continue;
            var rowIndex = _rowOrderIndex.GetValueOrDefault(row.RowId, -1);
            var index = 0;
            while (index < Items.Count
                && _rowOrderIndex.GetValueOrDefault(Items[index].RowId, -1) < rowIndex)
            {
                index++;
            }
            Items.Insert(index, item);
            _itemsInCollection.Add(item);
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
            case RowAppended appended when CreateTimelineItem(appended.Row) is { } item:
                _itemsByRowId[item.RowId] = item;
                Items.Add(item);
                _itemsInCollection.Add(item);
                return true;

            case RowTextAppended appended when _itemsByRowId.GetValueOrDefault(appended.RowId) is AssistantMessageItemViewModel textItem:
                textItem.AppendText(appended);
                return false;

            case RowUpserted upserted:
                var existing = _itemsByRowId.GetValueOrDefault(upserted.Row.RowId);
                if (existing is not null && TryUpdateItem(existing, upserted.Row))
                {
                    return false;
                }
                if (CreateTimelineItem(upserted.Row) is { } replacement)
                {
                    _itemsByRowId[replacement.RowId] = replacement;
                    var index = Items.IndexOf(existing!);
                    if (index >= 0)
                    {
                        Items[index] = replacement;
                        _itemsInCollection.Remove(existing!);
                        _itemsInCollection.Add(replacement);
                        return false;
                    }
                    Items.Add(replacement);
                    _itemsInCollection.Add(replacement);
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
                    _itemsInCollection.Remove(removedItem);
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
        ActivityText = SessionActivityText.Phase(state.Phase);
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

        var previousPhase = _lastPhase;
        _lastPhase = state.Phase;
        if (state.Phase is not (TurnPhase.Idle or TurnPhase.Error))
        {
            _turnStartedAt ??= DateTimeOffset.UtcNow;
        }
        else if (previousPhase is not (TurnPhase.Idle or TurnPhase.Error))
        {
            if (_turnStartedAt is { } startedAt && FindLastTurnAnchor() is { } anchor)
                _turnDurations[anchor.RowId] = DateTimeOffset.UtcNow - startedAt;
            _turnStartedAt = null;
            _turnLayoutDirty = true;
        }
    }

    private UserMessageItemViewModel? FindLastTurnAnchor()
    {
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (Items[index] is UserMessageItemViewModel anchor)
                return anchor;
        }
        return null;
    }

    private void RebuildTurnLayout()
    {
        List<(UserMessageItemViewModel Anchor, int Index)> anchors = [];
        for (var index = 0; index < Items.Count; index++)
        {
            var item = Items[index];
            item.IsFolded = false;
            switch (item)
            {
                case AssistantMessageItemViewModel assistant:
                    assistant.IsReasoningSuppressed = false;
                    break;
                case UserMessageItemViewModel anchor:
                    anchor.TurnSummaryText = string.Empty;
                    anchor.IsTurnExpanded = true;
                    anchors.Add((anchor, index));
                    break;
            }
        }

        var turnActive = _lastPhase is not (TurnPhase.Idle or TurnPhase.Error);
        for (var index = 0; index < anchors.Count; index++)
        {
            if (turnActive && index == anchors.Count - 1)
                continue;
            var (anchor, start) = anchors[index];
            var end = index + 1 < anchors.Count ? anchors[index + 1].Index : Items.Count;
            ApplyTurnFold(anchor, start, end);
        }
    }

    private void ApplyTurnFold(UserMessageItemViewModel anchor, int start, int end)
    {
        var finalIndex = FindFinalAssistantIndex(start, end);
        if (finalIndex <= start + 1)
            return;
        var duration = ResolveTurnDuration(anchor, start, end);
        anchor.TurnSummaryText = Loc.Get("timeline_turn_worked", SessionActivityText.Duration(duration));
        var expanded = _turnExpansion.GetValueOrDefault(anchor.RowId);
        anchor.IsTurnExpanded = expanded;
        if (expanded)
            return;
        for (var index = start + 1; index < finalIndex; index++)
            Items[index].IsFolded = true;
        if (Items[finalIndex] is AssistantMessageItemViewModel assistant)
            assistant.IsReasoningSuppressed = true;
    }

    private int FindFinalAssistantIndex(int start, int end)
    {
        for (var index = end - 1; index > start; index--)
        {
            if (Items[index] is AssistantMessageItemViewModel { MarkdownText.Length: > 0 })
                return index;
        }
        return -1;
    }

    private TimeSpan ResolveTurnDuration(UserMessageItemViewModel anchor, int start, int end)
    {
        if (_turnDurations.TryGetValue(anchor.RowId, out var measured))
            return measured;
        var finish = anchor.At;
        for (var index = start + 1; index < end; index++)
        {
            var candidate = Items[index] switch
            {
                AssistantMessageItemViewModel assistant => assistant.At,
                ToolCallItemViewModel tool => tool.StartedAt + (tool.Duration ?? TimeSpan.Zero),
                _ => finish,
            };
            if (candidate > finish)
                finish = candidate;
        }
        return finish > anchor.At ? finish - anchor.At : TimeSpan.Zero;
    }
}
