using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Runtime;

public sealed class ProjectionBuilder
{
    private readonly object _sync = new();
    private readonly List<ConversationRow> _rows = [];
    private readonly Dictionary<string, int> _rowIndex = [];
    private readonly Dictionary<string, (string RowId, StringBuilder Text)> _streamingText = [];
    private ConversationState _state = ConversationState.Initial(string.Empty, PermissionMode.Build, null, 200_000);
    private readonly List<QueuedInput> _queue = [];
    private IReadOnlyList<QueuedInput> _queueSnapshot = Array.Empty<QueuedInput>();
    private bool _queueDirty;
    private string? _lastAssistantRowId;
    private string _workspaceRoot = string.Empty;
    private readonly List<PendingFileChange> _turnFileChanges = [];
    private readonly Dictionary<string, PendingFileChange> _turnFileChangeIndex =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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
                    _workspaceRoot = started.WorkspaceRoot;
                    _state = _state with
                    {
                        Title = started.Title,
                        Mode = started.Mode,
                        PlanEnabled = started.PlanEnabled,
                        ModelSelection = started.ModelSelection,
                        ActiveWorkflows = started.ActiveWorkflows ?? [],
                    };
                    stateChanged = true;
                    break;

                case SessionTitleChangedEvent title:
                    _state = _state with { Title = title.Title };
                    stateChanged = true;
                    break;

                case SessionModeChangedEvent mode:
                    _state = _state with { Mode = mode.Mode, PlanEnabled = mode.PlanEnabled };
                    stateChanged = true;
                    break;

                case SessionWorkflowsChangedEvent workflows:
                    _state = _state with { ActiveWorkflows = workflows.Active };
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
                    _lastAssistantRowId = null;
                    ClearTurnFileChanges();
                    _state = _state with { Phase = TurnPhase.ProcessingInput, ActiveTurn = turnStarted.TurnId };
                    stateChanged = true;
                    break;

                case TurnCompletedEvent turnCompleted:
                    if (turnCompleted.Outcome == TurnOutcome.Completed && _lastAssistantRowId is not null
                        && _rowIndex.TryGetValue(_lastAssistantRowId, out var tailIndex)
                        && _rows[tailIndex] is AssistantMessageRow tail)
                    {
                        Upsert(tail with { CanFork = true }, ops);
                    }
                    AppendFileChangeSummary(turnCompleted.TurnId, ops);
                    _lastAssistantRowId = null;
                    _state = _state with
                    {
                        Phase = TurnPhase.Idle,
                        ActiveTurn = null,
                        PendingPermission = null,
                        PendingInteraction = null,
                        ContextUsageTokens = _state.ContextUsageTokens ?? turnCompleted.FinalUsage?.ContextUsageTokens ?? turnCompleted.FinalUsage?.InputTokens,
                    };
                    _streamingText.Clear();
                    CloseOpenToolRows(
                        turnCompleted.Outcome == TurnOutcome.Failed ? ToolCallStatus.Failed : ToolCallStatus.Cancelled,
                        ops);
                    stateChanged = true;
                    break;

                case TurnFailedEvent turnFailed:
                    if (_state.ActiveTurn is { } failedTurn)
                        AppendFileChangeSummary(failedTurn, ops);
                    else
                        ClearTurnFileChanges();
                    _lastAssistantRowId = null;
                    _state = _state with
                    {
                        Phase = TurnPhase.Error,
                        ActiveTurn = null,
                        PendingPermission = null,
                        PendingInteraction = null,
                    };
                    _streamingText.Clear();
                    CloseOpenToolRows(ToolCallStatus.Failed, ops);
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

                case ConversationRewoundEvent rewound:
                    _lastAssistantRowId = null;
                    ClearTurnFileChanges();
                    ApplyRewind(rewound.TargetMessageId.Value, ops);
                    _streamingText.Clear();
                    _queue.Clear();
                    _queueSnapshot = Array.Empty<QueuedInput>();
                    _queueDirty = false;
                    _state = _state with
                    {
                        Phase = TurnPhase.Idle,
                        ActiveTurn = null,
                        PendingPermission = null,
                        PendingInteraction = null,
                        Queue = _queueSnapshot,
                        ContextUsageTokens = null,
                        ContextUsage = null,
                        TokenSource = "estimate",
                    };
                    stateChanged = true;
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
                        if (!toolResult.Result.IsError)
                            TrackFileChange(toolResult.Result.FileChangePath, toolResult.Result.FileChange);
                        Upsert(new ToolCallRow(
                            toolResult.ToolCallId.Value,
                            toolResult.ToolCallId,
                            toolResult.ToolName,
                            existing?.Input ?? EmptyObject,
                            toolResult.Result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded,
                            toolResult.Result.Content,
                            toolResult.Result.IsError,
                            toolResult.Duration,
                            existing?.StartedAt ?? sequenced.OccurredAt,
                            toolResult.Result.FileChange), ops);
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
                    _queueDirty = true;
                    _state = _state with { Queue = QueueSnapshot() };
                    stateChanged = true;
                    break;

                case InputDequeuedEvent dequeued:
                    if (_queue.RemoveAll(input => input.CommandId == dequeued.CommandId) > 0)
                        _queueDirty = true;
                    _state = _state with { Queue = QueueSnapshot() };
                    stateChanged = true;
                    break;

                case InputDiscardedEvent discarded:
                    if (_queue.RemoveAll(input => input.CommandId == discarded.CommandId) > 0)
                        _queueDirty = true;
                    _state = _state with { Queue = QueueSnapshot() };
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
                        if (!_streamingText.TryGetValue(streaming.TurnId.Value, out var entry))
                        {
                            entry = ($"stream:{streaming.TurnId.Value}", new StringBuilder());
                            _streamingText[streaming.TurnId.Value] = entry;
                        }
                        var accumulated = entry.Text;
                        accumulated.Append(streaming.Delta);
                        Upsert(new AssistantMessageRow(
                            entry.RowId,
                            Id: default,
                            [new TextPart(default, accumulated.ToString())],
                            IsStreaming: true,
                            At: sequenced.OccurredAt), ops);
                        if (_state.Phase != TurnPhase.Streaming)
                        {
                            _state = _state with { Phase = TurnPhase.Streaming };
                            stateChanged = true;
                        }
                        else
                        {
                            ops.Add(new StatePatched(_state));
                        }
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
        if (SessionRuntime.IsRealUserTurn(message))
        {
            _lastAssistantRowId = null;
        }

        string? singleText = null;
        StringBuilder? textBuilder = null;
        var textCount = 0;
        FilePart[]? images = null;
        var imageCount = 0;
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart textPart:
                    if (textCount == 0)
                    {
                        singleText = textPart.Text;
                    }
                    else
                    {
                        textBuilder ??= new StringBuilder(singleText);
                        textBuilder.Append('\n').Append(textPart.Text);
                    }
                    textCount++;
                    break;
                case FilePart file
                    when file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                    if (images is null)
                    {
                        images = new FilePart[1];
                    }
                    else if (imageCount == images.Length)
                    {
                        Array.Resize(ref images, images.Length * 2);
                    }
                    images[imageCount++] = file;
                    break;
            }
        }

        if (images is not null && imageCount != images.Length)
            Array.Resize(ref images, imageCount);
        var text = textBuilder?.ToString() ?? singleText ?? string.Empty;
        if (text.Length > 0 || imageCount > 0)
        {
            Append(new UserMessageRow(
                message.Id.Value,
                text,
                message.CreatedAt,
                imageCount > 0 ? images : null,
                SessionRuntime.IsRealUserTurn(message)), ops);
        }

        foreach (var part in message.Parts)
        {
            if (part is not ToolResultPart toolResult)
                continue;
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
                existing?.StartedAt ?? message.CreatedAt,
                existing?.FileChange), ops);
        }
    }

    private IReadOnlyList<QueuedInput> QueueSnapshot()
    {
        if (!_queueDirty)
            return _queueSnapshot;
        _queueSnapshot = [.. _queue];
        _queueDirty = false;
        return _queueSnapshot;
    }

    private void ApplyRewind(string targetRowId, List<RowOp> ops)
    {
        if (!_rowIndex.TryGetValue(targetRowId, out var targetIndex))
        {
            return;
        }

        for (var index = _rows.Count - 1; index >= targetIndex; index--)
        {
            var rowId = _rows[index].RowId;
            _rows.RemoveAt(index);
            _rowIndex.Remove(rowId);
            ops.Add(new RowRemoved(rowId));
        }
    }

    private void ApplyAssistantMessage(ConversationMessage message, List<RowOp> ops)
    {
        if (_state.ActiveTurn is { } turn)
        {
            var streamingRowId = $"stream:{turn.Value}";
            if (_rowIndex.ContainsKey(streamingRowId))
            {
                Remove(streamingRowId, ops);
                _streamingText.Remove(turn.Value);
            }
        }

        Append(new AssistantMessageRow(
            message.Id.Value,
            message.Id,
            message.Parts,
            IsStreaming: false,
            message.CreatedAt), ops);
        _lastAssistantRowId = message.Id.Value;

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

    private void TrackFileChange(string? path, FileChangeStats? change)
    {
        if (change is null || string.IsNullOrWhiteSpace(path) || change.Added + change.Removed <= 0)
            return;

        var displayPath = ToDisplayPath(path);
        if (_turnFileChangeIndex.TryGetValue(displayPath, out var existing))
        {
            existing.Added += change.Added;
            existing.Removed += change.Removed;
            return;
        }

        var pending = new PendingFileChange(displayPath)
        {
            Added = change.Added,
            Removed = change.Removed,
        };
        _turnFileChangeIndex.Add(displayPath, pending);
        _turnFileChanges.Add(pending);
    }

    private string ToDisplayPath(string path)
    {
        try
        {
            var fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, _workspaceRoot);
            if (_workspaceRoot.Length > 0)
            {
                var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
                if (relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    return relative.Replace('\\', '/');
            }
            return fullPath.Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Replace('\\', '/');
        }
    }

    private void AppendFileChangeSummary(TurnId turnId, List<RowOp> ops)
    {
        if (_turnFileChanges.Count > 0)
        {
            var files = _turnFileChanges
                .Select(file => new FileChangeSummaryItem(file.Path, file.Added, file.Removed))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var added = 0;
            var removed = 0;
            foreach (var file in files)
            {
                added += file.Added;
                removed += file.Removed;
            }
            Append(new FileChangeSummaryRow($"file-changes:{turnId.Value}", files, added, removed), ops);
        }
        ClearTurnFileChanges();
    }

    private void ClearTurnFileChanges()
    {
        _turnFileChanges.Clear();
        _turnFileChangeIndex.Clear();
    }

    private void CloseOpenToolRows(ToolCallStatus status, List<RowOp> ops)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i] is not ToolCallRow row
                || row.Status is not (ToolCallStatus.Pending or ToolCallStatus.Running or ToolCallStatus.AwaitingPermission))
            {
                continue;
            }
            var closed = row with { Status = status, IsError = status == ToolCallStatus.Failed };
            _rows[i] = closed;
            ops.Add(new RowUpserted(closed));
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

    private sealed class PendingFileChange(string path)
    {
        public string Path { get; } = path;

        public int Added { get; set; }

        public int Removed { get; set; }
    }
}
