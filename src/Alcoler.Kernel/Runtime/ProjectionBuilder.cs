using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;

namespace Alcoler.Kernel.Runtime;

public sealed class ProjectionBuilder
{
    private readonly object _sync = new();
    private readonly List<ConversationRow> _rows = [];
    private readonly Dictionary<string, int> _rowIndex = [];
    private readonly Dictionary<string, string> _streamingText = [];
    private ConversationState _state = ConversationState.Initial(string.Empty, PermissionMode.Build, null, 200_000);
    private readonly List<QueuedInput> _queue = [];
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public ConversationState State { get { lock (_sync) return _state; } }

    public (IReadOnlyList<ConversationRow> Rows, ConversationState State) SnapshotState()
    {
        lock (_sync)
        {
            return ([.. _rows], _state);
        }
    }

    public IReadOnlyList<ConversationRow> Rows()
    {
        lock (_sync)
        {
            return [.. _rows];
        }
    }

    public IReadOnlyList<RowOp> Apply(SequencedEvent sequenced)
    {
        lock (_sync)
        {
            var ops = new List<RowOp>();
            var stateChanged = false;
            switch (sequenced.Event)
            {
                case SessionStartedEvent started:
                    _state = _state with { Title = started.Title, Mode = started.Mode, ModelSelection = started.ModelSelection };
                    stateChanged = true;
                    break;

                case SessionTitleChangedEvent title:
                    _state = _state with { Title = title.Title };
                    stateChanged = true;
                    break;

                case SessionModeChangedEvent mode:
                    _state = _state with { Mode = mode.Mode };
                    stateChanged = true;
                    break;

                case SessionModelChangedEvent model:
                    _state = _state with
                    {
                        ModelSelection = model.Selection,
                        ContextWindowTokens = model.ContextWindowTokens,
                        ContextUsageTokens = null,
                        ContextUsage = null,
                        TokenSource = "estimate",
                    };
                    stateChanged = true;
                    break;

                case TurnStartedEvent turnStarted:
                    _state = _state with { Phase = TurnPhase.ProcessingInput, ActiveTurn = turnStarted.TurnId };
                    stateChanged = true;
                    break;

                case TurnCompletedEvent turnCompleted:
                    _state = _state with
                    {
                        Phase = TurnPhase.Idle,
                        ActiveTurn = null,
                        PendingPermission = null,
                        PendingInteraction = null,
                        ContextUsageTokens = _state.ContextUsageTokens ?? turnCompleted.FinalUsage?.ContextUsageTokens ?? turnCompleted.FinalUsage?.InputTokens,
                    };
                    _streamingText.Clear();
                    stateChanged = true;
                    break;

                case TurnFailedEvent turnFailed:
                    _state = _state with
                    {
                        Phase = TurnPhase.Error,
                        ActiveTurn = null,
                        PendingPermission = null,
                        PendingInteraction = null,
                    };
                    _streamingText.Clear();
                    Append(new SystemNoticeRow(
                        $"error:{sequenced.Sequence}",
                        "turn_error",
                        turnFailed.Error), ops);
                    stateChanged = true;
                    break;

                case UserMessageCommittedEvent userMessage:
                    ApplyUserMessage(userMessage.Message, ops);
                    break;

                case AssistantMessageCommittedEvent assistantMessage:
                    ApplyAssistantMessage(assistantMessage.Message, ops);
                    stateChanged = false;
                    break;

                case ToolCallStartedEvent toolStarted:
                    Upsert(new ToolCallRow(
                        toolStarted.ToolCallId.Value,
                        toolStarted.ToolCallId,
                        toolStarted.ToolName,
                        toolStarted.Input,
                        ToolCallStatus.Running,
                        Output: null,
                        IsError: false,
                        Duration: null,
                        StartedAt: sequenced.OccurredAt), ops);
                    _state = _state with { Phase = TurnPhase.ExecutingTools };
                    stateChanged = true;
                    break;

                case ToolCallResultEvent toolResult:
                    {
                        var existing = _rowIndex.TryGetValue(toolResult.ToolCallId.Value, out var index)
                            ? _rows[index] as ToolCallRow
                            : null;
                        Upsert(new ToolCallRow(
                            toolResult.ToolCallId.Value,
                            toolResult.ToolCallId,
                            toolResult.ToolName,
                            existing?.Input ?? EmptyObject,
                            toolResult.Result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded,
                            toolResult.Result.Content,
                            toolResult.Result.IsError,
                            toolResult.Duration,
                            existing?.StartedAt ?? sequenced.OccurredAt), ops);
                        break;
                    }

                case PermissionRequestedEvent permission:
                    _state = _state with { Phase = TurnPhase.AwaitingPermission, PendingPermission = permission.Request };
                    stateChanged = true;
                    break;

                case PermissionResolvedEvent:
                    _state = _state with { Phase = TurnPhase.ExecutingTools, PendingPermission = null };
                    stateChanged = true;
                    break;

                case InteractionRequestedEvent interaction:
                    _state = _state with { Phase = TurnPhase.AwaitingInteraction, PendingInteraction = interaction.Request };
                    stateChanged = true;
                    break;

                case InteractionResolvedEvent:
                    _state = _state with { Phase = TurnPhase.ExecutingTools, PendingInteraction = null };
                    stateChanged = true;
                    break;

                case CompactBoundaryAppliedEvent boundary:
                    _state = _state with
                    {
                        ContextUsageTokens = boundary.Boundary.PostCompactTokens,
                        TokenSource = "estimate",
                        ContextUsage = null,
                    };
                    stateChanged = true;
                    Append(new SystemNoticeRow(
                        $"compact:{boundary.Boundary.BoundaryId}",
                        "compact",
                        $"Conversation compacted ({boundary.Boundary.Trigger}): {boundary.Boundary.PreCompactTokens} -> {boundary.Boundary.PostCompactTokens} tokens."), ops);
                    break;

                case SubagentStartedEvent subagentStarted:
                    Append(new SystemNoticeRow(
                        $"subagent:{subagentStarted.State.Id.Value}",
                        "subagent",
                        $"Subagent started: {subagentStarted.State.Description}",
                        subagentStarted.State), ops);
                    break;

                case SubagentStatusChangedEvent subagentChanged:
                    Upsert(new SystemNoticeRow(
                        $"subagent:{subagentChanged.State.Id.Value}",
                        "subagent",
                        $"Subagent {subagentChanged.State.Description}: {subagentChanged.State.Status}" +
                        (subagentChanged.State.ResultSummary is { } summary ? $" - {summary}" : string.Empty),
                        subagentChanged.State), ops);
                    break;

                case InputQueuedEvent queued:
                    _queue.Add(queued.Input);
                    _state = _state with { Queue = [.. _queue] };
                    stateChanged = true;
                    break;

                case InputDequeuedEvent dequeued:
                    _queue.RemoveAll(input => input.CommandId == dequeued.CommandId);
                    _state = _state with { Queue = [.. _queue] };
                    stateChanged = true;
                    break;

                case InputDiscardedEvent discarded:
                    _queue.RemoveAll(input => input.CommandId == discarded.CommandId);
                    _state = _state with { Queue = [.. _queue] };
                    stateChanged = true;
                    break;

                case ContextUsageUpdatedEvent context:
                    _state = _state with
                    {
                        ContextUsage = context.Usage,
                        ContextUsageTokens = context.Usage.UsedTokens,
                        ContextWindowTokens = context.Usage.MaxTokens,
                        TokenSource = context.Usage.TokenSource,
                    };
                    stateChanged = true;
                    break;

                case ModelRequestStartedEvent:
                    _state = _state with { Phase = TurnPhase.AwaitingModel };
                    stateChanged = true;
                    break;

                case ModelRequestCompletedEvent completed:
                    _state = _state with
                    {
                        Phase = TurnPhase.Streaming,
                        ContextUsageTokens = completed.Usage.ContextUsageTokens ?? completed.Usage.InputTokens,
                        TokenSource = "provider_usage",
                    };
                    stateChanged = true;
                    break;

                case ModelStreamingTextEvent streaming:
                    {
                        _state = _state with { Phase = TurnPhase.Streaming };
                        var rowId = $"stream:{streaming.TurnId.Value}";
                        if (!_streamingText.TryGetValue(rowId, out var accumulated))
                        {
                            accumulated = string.Empty;
                        }
                        accumulated += streaming.Delta;
                        _streamingText[rowId] = accumulated;
                        Upsert(new AssistantMessageRow(
                            rowId,
                            Id: default,
                            [new TextPart(default, accumulated)],
                            IsStreaming: true,
                            At: sequenced.OccurredAt), ops);
                        stateChanged = true;
                        break;
                    }

                case ModelRetryScheduledEvent:
                case ModelRequestFailedEvent:
                case ModelStreamingReasoningEvent:
                case ModelStreamingToolInputEvent:
                case StreamRecoveryEvent:
                case MicrocompactAppliedEvent:
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled session event {sequenced.Event.GetType().Name}.");
            }

            if (stateChanged)
            {
                ops.Add(new StatePatched(_state));
            }

            return ops;
        }
    }

    private void ApplyUserMessage(ConversationMessage message, List<RowOp> ops)
    {
        var text = string.Join("\n", message.Parts.OfType<TextPart>().Select(p => p.Text));
        var images = message.Parts.OfType<FilePart>()
            .Where(part => part.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (text.Length > 0 || images.Length > 0)
        {
            Append(new UserMessageRow(message.Id.Value, text, message.CreatedAt, images.Length > 0 ? images : null), ops);
        }

        foreach (var toolResult in message.Parts.OfType<ToolResultPart>())
        {
            var existing = _rowIndex.TryGetValue(toolResult.ToolCallId.Value, out var index)
                ? _rows[index] as ToolCallRow
                : null;
            Upsert(new ToolCallRow(
                toolResult.ToolCallId.Value,
                toolResult.ToolCallId,
                existing?.ToolName ?? "tool",
                existing?.Input ?? EmptyObject,
                toolResult.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded,
                toolResult.Content,
                toolResult.IsError,
                existing?.Duration,
                existing?.StartedAt ?? message.CreatedAt), ops);
        }
    }

    private void ApplyAssistantMessage(ConversationMessage message, List<RowOp> ops)
    {
        var streamingRowId = _state.ActiveTurn is { } turn ? $"stream:{turn.Value}" : null;
        if (streamingRowId is not null && _rowIndex.ContainsKey(streamingRowId))
        {
            Remove(streamingRowId, ops);
            _streamingText.Remove(streamingRowId);
        }

        Append(new AssistantMessageRow(
            message.Id.Value,
            message.Id,
            message.Parts,
            IsStreaming: false,
            message.CreatedAt), ops);

        foreach (var toolCall in message.Parts.OfType<ToolCallPart>())
        {
            if (_rowIndex.TryGetValue(toolCall.ToolCallId.Value, out var index)
                && _rows[index] is ToolCallRow existing)
            {
                Upsert(existing with { ToolName = toolCall.ToolName, Input = toolCall.Input }, ops);
                continue;
            }
            Upsert(new ToolCallRow(
                toolCall.ToolCallId.Value,
                toolCall.ToolCallId,
                toolCall.ToolName,
                toolCall.Input,
                ToolCallStatus.Pending,
                Output: null,
                IsError: false,
                Duration: null,
                StartedAt: message.CreatedAt), ops);
        }
    }

    private void Append(ConversationRow row, List<RowOp> ops)
    {
        if (_rowIndex.TryGetValue(row.RowId, out var existing))
        {
            _rows[existing] = row;
        }
        else
        {
            _rowIndex[row.RowId] = _rows.Count;
            _rows.Add(row);
        }
        ops.Add(new RowAppended(row));
    }

    private void Upsert(ConversationRow row, List<RowOp> ops)
    {
        if (_rowIndex.TryGetValue(row.RowId, out var existing))
        {
            _rows[existing] = row;
            ops.Add(new RowUpserted(row));
            return;
        }
        Append(row, ops);
    }

    private void Remove(string rowId, List<RowOp> ops)
    {
        if (!_rowIndex.TryGetValue(rowId, out var index))
        {
            return;
        }
        _rows.RemoveAt(index);
        _rowIndex.Remove(rowId);
        for (var i = index; i < _rows.Count; i++)
        {
            _rowIndex[_rows[i].RowId] = i;
        }
        ops.Add(new RowRemoved(rowId));
    }
}
