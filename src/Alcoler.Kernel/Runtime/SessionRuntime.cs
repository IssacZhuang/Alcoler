using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Workspace;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.History;
using Alcoler.Kernel.Models;
using Alcoler.Kernel.Permissions;
using Alcoler.Kernel.Prompts;
using Alcoler.Kernel.Tools;
using Microsoft.Extensions.Logging;

namespace Alcoler.Kernel.Runtime;

public sealed class SessionRuntimeOptions
{
    public string SystemPromptStable { get; init; } = PromptTexts.StableIdentity;

    public string? CustomSystemPrompt { get; init; }

    public string? AgentInstructions { get; init; }

    public string? SkillsListing { get; init; }

    public string? ShellName { get; init; }

    public GitSnapshot? GitSnapshot { get; init; }

    public ModelSelection? DefaultModel { get; init; }
}

public sealed record SessionRuntimeDeps(
    ISessionStore Store,
    Func<ModelSelection, IChatModel> ResolveModel,
    ToolRegistry Tools,
    IToolWorkspace Workspace,
    PermissionRuleset PermissionRules,
    TimeProvider Clock,
    ILogger<SessionRuntime> Logger,
    SessionRuntimeOptions Options,
    IArtifactStore Artifacts,
    ITodoStore? TodoStore = null);

public sealed class SessionRuntime : IAsyncDisposable
{
    private const int StreamRecoveryMaxRetries = 10;
    private const int ReactiveCompactMaxRetries = 3;
    // ZCode DEFAULT_NORMAL_REQUEST_MAX_OUTPUT_TOKENS; only used when the model declares none.
    private const int DefaultRequestMaxOutputTokens = 32_000;
    // ZCode session-history-hydrator INTERRUPTED_TOOL_RESULT.
    private const string InterruptedToolResultText = "[Tool execution was interrupted before resume]";

    private readonly SessionRuntimeDeps _deps;
    private readonly SessionEventStore _events;
    private readonly MessageHistory _history = new();
    private readonly TurnMachine _turnMachine = new();
    private readonly SessionInteractionBroker _broker;
    private readonly PermissionService _permissions;
    private readonly ToolBatchRunner _toolRunner;
    private readonly RuntimeSessionCommands _sessionCommands;
    private readonly FileReadState _fileReads = new();
    private readonly ShellSessionState _shell;
    private readonly BackgroundJobService _backgroundJobs;
    private readonly SessionAuxModelClient _auxModel;
    private readonly SkillContentProvider _skills;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCts = new();

    private SessionRecord _record;
    private long _revision;
    private Task _turnTask = Task.CompletedTask;
    private CancellationTokenSource? _turnCts;
    private readonly Queue<PendingTurnInput> _pendingInputs = new();
    private TokenUsage? _lastProviderUsage;
    private int _usageAnchorIndex = -1;
    private ContextUsageState? _contextUsage;
    private string? _contextModelId;
    private DateTimeOffset _lastActivityUtc;
    private string? _lastSeenLocalDate;
    private readonly bool _isGitRepository;
    private readonly string? _gitSnapshotSection;
    private int _consecutiveCompactFailures;
    private int _consecutiveRapidRefills;
    private int _toolRoundsSinceCompact;
    private int _outputContinuationsThisTurn;
    private bool _signatureRepairUsedThisTurn;
    private bool _planModeExitReminderPending;
    private int _streamRecoveriesThisTurn;
    private ToolCallAnomalyTracker _toolAnomalyThisTurn = new();
    private readonly List<ModelInputMessage> _turnLocalRequestMessages = [];
    private bool _disposed;

    private SessionRuntime(SessionRecord record, SessionRuntimeDeps deps)
    {
        _deps = deps;
        _record = record;
        _events = new SessionEventStore(record.Id, deps.Clock);
        _broker = new SessionInteractionBroker(evt => EmitAsync(evt, persist: true), deps.Clock);
        _permissions = new PermissionService(_broker, deps.PermissionRules, record.PermissionMode);
        var scheduler = new ToolScheduler(deps.Tools);
        var executor = new ToolCallExecutor(deps.Tools, _permissions, _ => _permissions.Mode);
        _toolRunner = new ToolBatchRunner(scheduler, executor);
        _sessionCommands = new RuntimeSessionCommands(this);
        _shell = new ShellSessionState(record.WorkspaceRoot);
        _backgroundJobs = new BackgroundJobService(deps.Clock, NotifyBackgroundJobCompletedAsync);
        _auxModel = new SessionAuxModelClient(() => _record.ModelSelection ?? deps.Options.DefaultModel, deps.ResolveModel);
        _skills = new SkillContentProvider(record.WorkspaceRoot);
        _lastActivityUtc = deps.Clock.GetUtcNow();
        _isGitRepository = RuntimeEnvironmentFacts.IsGitRepository(record.WorkspaceRoot);
        _gitSnapshotSection = deps.Options.GitSnapshot is { } snapshot && _isGitRepository
            ? GitSnapshotSection.Render(snapshot, RuntimeEnvironmentFacts.Platform)
            : null;
    }

    public event Func<SessionRuntime, ValueTask>? TurnDrained;

    internal ValueTask EmitSubagentStateAsync(SubagentState state, bool started) =>
        EmitAsync(
            started
                ? new SubagentStartedEvent(state)
                : new SubagentStatusChangedEvent(state),
            persist: true);

    public SessionId SessionId => _record.Id;

    public string WorkspaceRoot => _record.WorkspaceRoot;

    public long Revision => Volatile.Read(ref _revision);

    public bool IsTurnActive => _turnMachine.IsBusy;

    public static async Task<SessionRuntime> CreateAsync(SessionRecord record, SessionRuntimeDeps deps, CancellationToken ct, bool persistStartEvents = true)
    {
        var runtime = new SessionRuntime(record, deps);
        await runtime.ReplayHistoryAsync(persistStartEvents).ConfigureAwait(false);
        return runtime;
    }

    public IConversationProjection CreateProjection() => new LiveConversationProjection(_events, 0, () => Revision);

    internal IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs() =>
        Volatile.Read(ref _disposed) ? [] : _backgroundJobs.List();

    internal bool StopBackgroundJob(BackgroundJobId jobId) =>
        !Volatile.Read(ref _disposed) && _backgroundJobs.Stop(jobId);

    public async Task<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken ct)
    {
        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (command.BaseRevision > Revision)
            {
                return CommandAck.Rejected(command.CommandId, Revision, "base revision is in the future");
            }

            switch (command)
            {
                case SendTextCommand sendText:
                    return HandleSendText(sendText);

                case StopCommand stop:
                    _turnCts?.Cancel();
                    BumpRevision();
                    return CommandAck.AcceptedAt(stop.CommandId, Revision);

                case ResolveInteractionCommand resolve:
                    return HandleResolveInteraction(resolve);

                case CompactCommand compact:
                    return await HandleCompactAsync(compact, ct).ConfigureAwait(false);

                case SetModeCommand setMode:
                    _permissions.SetMode(setMode.Mode);
                    _record = _record with { PermissionMode = setMode.Mode, UpdatedAt = _deps.Clock.GetUtcNow() };
                    await PersistRecordAsync(ct).ConfigureAwait(false);
                    await EmitAsync(new SessionModeChangedEvent(setMode.Mode), persist: true).ConfigureAwait(false);
                    BumpRevision();
                    return CommandAck.AcceptedAt(setMode.CommandId, Revision);

                case SwitchModelCommand switchModel:
                    try
                    {
                        _deps.ResolveModel(switchModel.Selection).ValidateOptions(switchModel.Selection.Options ?? new ModelCallOptions());
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        return CommandAck.Rejected(switchModel.CommandId, Revision, ex.Message);
                    }
                    _record = _record with { ModelSelection = switchModel.Selection, UpdatedAt = _deps.Clock.GetUtcNow() };
                    await PersistRecordAsync(ct).ConfigureAwait(false);
                    await EmitModelChangedAsync(switchModel.Selection).ConfigureAwait(false);
                    BumpRevision();
                    return CommandAck.AcceptedAt(switchModel.CommandId, Revision);

                case RenameSessionCommand rename:
                    _record = _record with { Title = rename.Title, UpdatedAt = _deps.Clock.GetUtcNow() };
                    await PersistRecordAsync(ct).ConfigureAwait(false);
                    await EmitAsync(new SessionTitleChangedEvent(rename.Title), persist: true).ConfigureAwait(false);
                    BumpRevision();
                    return CommandAck.AcceptedAt(rename.CommandId, Revision);

                default:
                    return CommandAck.Rejected(command.CommandId, Revision, $"unsupported command {command.GetType().Name}");
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private CommandAck HandleSendText(SendTextCommand sendText)
    {
        if (Steering.ValidateInput(sendText.Text) is { } rejection)
        {
            return CommandAck.Rejected(sendText.CommandId, Revision, rejection);
        }

        if (sendText.Mode is { } mode)
        {
            _permissions.SetMode(mode);
            _record = _record with { PermissionMode = mode };
        }

        if (_turnMachine.IsBusy)
        {
            EnqueueInput(new PendingTurnInput(sendText, null));
            BumpRevision();
            return CommandAck.AcceptedAt(sendText.CommandId, Revision);
        }

        if (_pendingInputs.Count > 0)
        {
            // Inputs held by a cancelled/failed turn keep FIFO: the newcomer queues behind and
            // the oldest pending input drives the next turn.
            EnqueueInput(new PendingTurnInput(sendText, null));
            StartTurnFor(DequeueOldestInput()!);
        }
        else
        {
            StartTurnFor(new PendingTurnInput(sendText, null));
        }

        BumpRevision();
        return CommandAck.AcceptedAt(sendText.CommandId, Revision);
    }

    private void EnqueueInput(PendingTurnInput input)
    {
        _pendingInputs.Enqueue(input);
        if (input.Command is { } command)
        {
            _ = EmitAsync(new InputQueuedEvent(new QueuedInput(command.CommandId, command.Text, _deps.Clock.GetUtcNow())), persist: true);
            BumpRevision();
        }
    }

    private PendingTurnInput? DequeueOldestInput()
    {
        if (_pendingInputs.Count == 0)
            return null;
        var next = _pendingInputs.Dequeue();
        if (next.Command is { } command)
        {
            _ = EmitAsync(new InputDequeuedEvent(command.CommandId), persist: true);
            BumpRevision();
        }
        return next;
    }

    internal ValueTask EnqueueTaskNotificationAsync(string prefixedBody)
    {
        if (Volatile.Read(ref _disposed))
            return ValueTask.CompletedTask;
        try
        {
            return SubmitTaskNotificationAsync(prefixedBody);
        }
        catch (ObjectDisposedException)
        {
            return ValueTask.CompletedTask;
        }
    }

    // Task notifications never enter the user-input queue as text: mid-turn they ride a
    // dedicated channel drained at the model-step boundary; idle they start their own turn
    // whose opening message is the wrapped notification.
    private async ValueTask SubmitTaskNotificationAsync(string prefixedBody)
    {
        await _commandLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_turnMachine.IsBusy)
            {
                _pendingInputs.Enqueue(new PendingTurnInput(null, prefixedBody));
                return;
            }

            if (_pendingInputs.Count > 0)
            {
                _pendingInputs.Enqueue(new PendingTurnInput(null, prefixedBody));
                StartTurnFor(DequeueOldestInput()!);
            }
            else
            {
                StartTurnFor(new PendingTurnInput(null, prefixedBody));
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private CommandAck HandleResolveInteraction(ResolveInteractionCommand resolve)
    {
        var handled = false;
        if (resolve.PermissionDecision is { } decision)
        {
            handled = _broker.ResolvePermission(resolve.InteractionId, decision);
            if (handled)
            {
                _ = EmitAsync(new PermissionResolvedEvent(resolve.InteractionId, decision), persist: true);
            }
        }
        else if (resolve.InteractionAnswer is { } answer)
        {
            handled = _broker.ResolveInteraction(resolve.InteractionId, answer);
            if (handled)
            {
                _ = EmitAsync(new InteractionResolvedEvent(resolve.InteractionId, answer), persist: true);
            }
        }

        BumpRevision();
        return handled
            ? CommandAck.AcceptedAt(resolve.CommandId, Revision)
            : CommandAck.Rejected(resolve.CommandId, Revision, "no pending interaction with that id");
    }

    private async Task<CommandAck> HandleCompactAsync(CompactCommand compact, CancellationToken ct)
    {
        if (_turnMachine.IsBusy)
        {
            return CommandAck.Rejected(compact.CommandId, Revision, "a turn is active");
        }

        var succeeded = await ExecuteCompactionAsync(CompactTrigger.Manual, compact.Instructions, _sessionCts.Token).ConfigureAwait(false);
        BumpRevision();
        return succeeded
            ? CommandAck.AcceptedAt(compact.CommandId, Revision)
            : CommandAck.Rejected(compact.CommandId, Revision, "compaction failed; see session events");
    }

    private void StartTurnFor(PendingTurnInput input)
    {
        if (!_turnMachine.TryTransition(TurnPhase.ProcessingInput))
        {
            _pendingInputs.Enqueue(input);
            return;
        }

        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        var turnCts = _turnCts;
        _turnTask = RunTurnAsync(input, turnCts.Token);
    }

    private async Task RunTurnAsync(PendingTurnInput input, CancellationToken turnCt)
    {
        var turnId = StrongId.NewTurnId();
        var holdQueue = false;
        try
        {
            var command = input.Command;
            var selection = command?.ModelSelection ?? _record.ModelSelection ?? _deps.Options.DefaultModel
                ?? throw new InvalidOperationException("No model configured for session.");
            var model = _deps.ResolveModel(selection);
            var options = selection.Options ?? new ModelCallOptions();
            model.ValidateOptions(options);
            await EmitAsync(new TurnStartedEvent(turnId, command?.CommandId ?? StrongId.NewCommandId()), persist: true).ConfigureAwait(false);

            var openingMessage = input.NotificationBody is { } notificationBody
                ? BuildNotificationMessage(notificationBody)
                : new ConversationMessage(
                    StrongId.NewMessageId(),
                    MessageRole.User,
                    [new TextPart(StrongId.NewPartId(), command!.Text)],
                    _deps.Clock.GetUtcNow());
            await CommitMessageAsync(openingMessage).ConfigureAwait(false);

            var outcome = TurnOutcome.Completed;
            var reactiveCompactRetries = 0;
            _outputContinuationsThisTurn = 0;
            _signatureRepairUsedThisTurn = false;
            _streamRecoveriesThisTurn = 0;
            _toolAnomalyThisTurn = new ToolCallAnomalyTracker();
            _turnLocalRequestMessages.Clear();
            while (true)
            {
                turnCt.ThrowIfCancellationRequested();

                var step = await RunModelStepAsync(turnId, model, options, turnCt).ConfigureAwait(false);
                if (step == ModelStepOutcome.Finished)
                {
                    // A text-only step is a continuable boundary: one queued user input
                    // extends the same turn instead of waiting for a new one.
                    var (steered, _) = await DrainBoundaryAsync(drainNotifications: false).ConfigureAwait(false);
                    if (steered)
                    {
                        continue;
                    }
                    break;
                }

                if (step == ModelStepOutcome.ContextOverflow && reactiveCompactRetries < ReactiveCompactMaxRetries)
                {
                    reactiveCompactRetries++;
                    var rapidRefill = NextRapidRefillCount();
                    if (rapidRefill >= CompactPolicy.MaxConsecutiveRapidRefills)
                    {
                        // The throw surfaces as a durable TurnFailedEvent carrying the exact
                        // breaker message, mirroring ZCode's structured compaction error.
                        throw new InvalidOperationException(CompactPolicy.RapidRefillBreakerMessage(
                            CompactPolicy.RapidRefillToolTurnThreshold,
                            CompactPolicy.MaxConsecutiveRapidRefills));
                    }
                    var compacted = await ExecuteCompactionAsync(CompactTrigger.Reactive, null, turnCt, model, options).ConfigureAwait(false);
                    if (!compacted)
                    {
                        outcome = TurnOutcome.Failed;
                        break;
                    }
                    _consecutiveRapidRefills = rapidRefill;
                    continue;
                }

                if (step == ModelStepOutcome.Failed)
                {
                    outcome = TurnOutcome.Failed;
                    break;
                }

                if (step == ModelStepOutcome.Cancelled)
                {
                    outcome = TurnOutcome.Cancelled;
                    break;
                }

                // The tool-result boundary: queued notifications and one queued user input
                // join the running turn before the next model request.
                var (boundarySteered, boundaryNotified) = await DrainBoundaryAsync(drainNotifications: true).ConfigureAwait(false);
                if (boundaryNotified)
                {
                    _toolAnomalyThisTurn.ResetStreak();
                }
            }

            if (outcome != TurnOutcome.Completed)
            {
                holdQueue = true;
            }
            await EmitAsync(new TurnCompletedEvent(turnId, outcome, _lastProviderUsage), persist: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            holdQueue = true;
            await EmitAsync(new TurnCompletedEvent(turnId, TurnOutcome.Cancelled, _lastProviderUsage), persist: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            holdQueue = true;
            _deps.Logger.LogError(ex, "Turn {TurnId} failed unexpectedly.", turnId);
            await EmitAsync(new TurnFailedEvent(turnId, ex.Message), persist: true).ConfigureAwait(false);
        }
        finally
        {
            _turnLocalRequestMessages.Clear();
            _turnCts?.Dispose();
            _turnCts = null;
            // Only Completing and Error may reach Idle; a cancel mid-stream or mid-tool
            // leaves other phases, so route through Completing first or the Validate throw
            // would strand the machine in a busy phase.
            _turnMachine.TryTransition(TurnPhase.Completing);
            _turnMachine.Transition(TurnPhase.Idle);
            _events.EvictTransientOlderThan(
                _deps.Clock.GetUtcNow().AddSeconds(-SessionEventRetention.SealedTurnTransientGraceMs / 1000.0),
                _events.LastSequence);
            // Cancelled or failed turns hold pending inputs for an explicit user resume;
            // only a completed turn auto-drains the queue.
            if (!holdQueue)
            {
                await DrainQueueAsync().ConfigureAwait(false);
            }
        }
    }

    // Consumes at most one queued user input (one guide per model-step boundary) and, when
    // requested, every queued task notification (batched into a single message).
    private async Task<(bool Steered, bool Notified)> DrainBoundaryAsync(bool drainNotifications)
    {
        SendTextCommand? steer = null;
        List<string>? notificationBodies = null;
        await _commandLock.WaitAsync(_sessionCts.Token).ConfigureAwait(false);
        try
        {
            List<PendingTurnInput>? held = null;
            while (_pendingInputs.Count > 0)
            {
                var next = _pendingInputs.Dequeue();
                if (next.Command is { } command)
                {
                    if (steer is null)
                    {
                        steer = command;
                        _ = EmitAsync(new InputDequeuedEvent(command.CommandId), persist: true);
                    }
                    else
                    {
                        (held ??= []).Add(next);
                    }
                }
                else if (drainNotifications)
                {
                    (notificationBodies ??= []).Add(next.NotificationBody!);
                }
                else
                {
                    (held ??= []).Add(next);
                }
            }

            if (held is not null)
            {
                foreach (var pending in held)
                {
                    _pendingInputs.Enqueue(pending);
                }
            }
        }
        finally
        {
            _commandLock.Release();
        }

        if (notificationBodies is { Count: > 0 })
        {
            await CommitMessageAsync(BuildNotificationMessage(string.Join("\n\n", notificationBodies))).ConfigureAwait(false);
        }

        if (steer is not null)
        {
            await CommitMessageAsync(new ConversationMessage(
                StrongId.NewMessageId(),
                MessageRole.User,
                [new TextPart(StrongId.NewPartId(), Steering.FormatUserSteer(steer.Text))],
                _deps.Clock.GetUtcNow())).ConfigureAwait(false);
        }

        return (steer is not null, notificationBodies is { Count: > 0 });
    }

    private async Task DrainQueueAsync()
    {
        while (true)
        {
            PendingTurnInput? next = null;
            await _commandLock.WaitAsync(_sessionCts.Token).ConfigureAwait(false);
            try
            {
                if (_pendingInputs.Count > 0 && !_turnMachine.IsBusy)
                {
                    next = _pendingInputs.Dequeue();
                    if (next.Command is { } command)
                    {
                        _ = EmitAsync(new InputDequeuedEvent(command.CommandId), persist: true);
                    }
                    StartTurnFor(next);
                }
            }
            finally
            {
                _commandLock.Release();
            }

            if (next is null)
            {
                break;
            }
        }

        if (TurnDrained is { } handler)
        {
            await handler(this).ConfigureAwait(false);
        }
    }

    private ConversationMessage BuildNotificationMessage(string prefixedBody) =>
        new(
            StrongId.NewMessageId(),
            MessageRole.User,
            [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, SystemReminder.Wrap(prefixedBody), ProviderVisible: true)],
            _deps.Clock.GetUtcNow());

    // Arrives on a thread-pool continuation; the notification channel routes it into the
    // pending-input queue while a turn is active or starts a notification turn when idle.
    private ValueTask NotifyBackgroundJobCompletedAsync(BackgroundJobInfo job) =>
        EnqueueTaskNotificationAsync(BackgroundJobNotifications.FormatCompleted(job));

    // ZCode evaluateRapidRefill: a compact that fires before enough tool turns elapsed is a
    // rapid refill and extends the streak; otherwise the streak resets.
    private int NextRapidRefillCount() =>
        _toolRoundsSinceCompact < CompactPolicy.RapidRefillToolTurnThreshold
            ? _consecutiveRapidRefills + 1
            : 0;

    private async Task<ModelStepOutcome> RunModelStepAsync(TurnId turnId, IChatModel model, ModelCallOptions options, CancellationToken turnCt)
    {
        var history = _history.Snapshot();
        var contextWindow = model.Descriptor.Properties.ContextWindowTokens ?? CompactPolicy.DefaultContextWindowTokens;
        var threshold = CompactPolicy.AutoCompactThreshold(contextWindow);
        if (_contextModelId != model.Descriptor.FullId)
        {
            _lastProviderUsage = null;
            _usageAnchorIndex = -1;
            _contextModelId = model.Descriptor.FullId;
        }
        var initialRequest = await AssembleContextAsync(history, options, model, turnCt).ConfigureAwait(false);
        var (effectiveTokens, _) = EstimateContextTokens(initialRequest, history);

        var now = _deps.Clock.GetUtcNow();
        var micro = MicroCompactor.Apply(history, _lastActivityUtc, now, threshold);
        if (micro.ClearedParts.Count > 0)
        {
            _history.ReplaceAll(micro.Messages);
            history = micro.Messages;
            _lastProviderUsage = null;
            _usageAnchorIndex = -1;
            var postMicro = await AssembleContextAsync(history, initialRequest.Request.Options, model, turnCt).ConfigureAwait(false);
            effectiveTokens = TokenEstimator.EstimateRequestTokens(postMicro.Request);
            await EmitAsync(new MicrocompactAppliedEvent(micro.ClearedParts, micro.EstimatedTokensSaved), persist: true).ConfigureAwait(false);
        }

        var autoCompact = CompactPolicy.ShouldAutoCompact(new ShouldAutoCompactInput(
            EffectiveTokens: effectiveTokens,
            ContextWindowTokens: contextWindow,
            AssistantLedRoundCount: history.Count(m => m.Role == MessageRole.Assistant),
            ConsecutiveFailures: _consecutiveCompactFailures,
            ConsecutiveRapidRefills: _consecutiveRapidRefills,
            Disabled: false));
        if (autoCompact.Compact)
        {
            var rapidRefill = NextRapidRefillCount();
            if (rapidRefill >= CompactPolicy.MaxConsecutiveRapidRefills)
            {
                // Third consecutive rapid refill: a structured breaker failure, never a
                // silent reset-and-continue.
                throw new InvalidOperationException(CompactPolicy.RapidRefillBreakerMessage(
                    CompactPolicy.RapidRefillToolTurnThreshold,
                    CompactPolicy.MaxConsecutiveRapidRefills));
            }
            var compacted = await ExecuteCompactionAsync(CompactTrigger.Auto, null, turnCt, model, options).ConfigureAwait(false);
            if (compacted)
            {
                _consecutiveRapidRefills = rapidRefill;
            }
            history = _history.Snapshot();
        }

        await CommitRuntimeRemindersAsync(history).ConfigureAwait(false);
        history = _history.Snapshot();

        _turnMachine.TryTransition(TurnPhase.AwaitingModel);
        var request = await AssembleContextAsync(history, initialRequest.Request.Options, model, turnCt).ConfigureAwait(false);
        var (requestTokens, source) = EstimateContextTokens(request, history);
        if (model.Descriptor.Properties.ContextWindowTokens is { } configuredContext)
        {
            var requestedMax = options.MaxOutputTokens
                ?? model.Descriptor.Properties.DefaultMaxOutputTokens
                ?? DefaultRequestMaxOutputTokens;
            var estimatedAvailable = configuredContext - requestTokens - 1000;
            // No positive headroom is a local estimate, not a provider verdict: keep the
            // baseline unclamped and let the provider 400 drive reactive compaction.
            var maxOutput = estimatedAvailable <= 0 ? requestedMax : Math.Min(requestedMax, estimatedAvailable);
            request = request with { Request = request.Request with { Options = options with { MaxOutputTokens = maxOutput } } };
        }
        request = AppendDateReminder(AppendTurnLocalEntries(request));
        await PublishContextUsageAsync(request, requestTokens, contextWindow, source).ConfigureAwait(false);
        var requestAnchorIndex = history.Count - 1;

        var accumulator = await StreamModelStepAsync(turnId, model, request, turnCt).ConfigureAwait(false);

        _lastActivityUtc = _deps.Clock.GetUtcNow();

        if (accumulator.Failure is { } failure)
        {
            await EmitAsync(new ModelRequestFailedEvent(turnId, failure, 1), persist: false).ConfigureAwait(false);
            if (failure.Kind == ModelFailureKind.ContextOverflow)
            {
                return ModelStepOutcome.ContextOverflow;
            }
            // The request itself was rejected before any content; repairing history and
            // retrying once per turn never consumes the stream-recovery budget.
            if (!_signatureRepairUsedThisTurn
                && !accumulator.HasBoundaryContent
                && ThinkingSignatureRecovery.IsSignatureRejection(failure)
                && await StripReasoningHistoryAsync().ConfigureAwait(false))
            {
                _signatureRepairUsedThisTurn = true;
                return ModelStepOutcome.ContinueAfterHistoryRepair;
            }
            if (accumulator.HasBoundaryContent)
            {
                await CommitPartialAssistantAsync(accumulator).ConfigureAwait(false);
                return ModelStepOutcome.Failed;
            }
            return ModelStepOutcome.Failed;
        }

        var usage = accumulator.Usage;
        if (usage is not null)
        {
            _lastProviderUsage = usage;
            _usageAnchorIndex = requestAnchorIndex;
            await EmitAsync(new ModelRequestCompletedEvent(turnId, usage), persist: false).ConfigureAwait(false);
            await PublishContextUsageAsync(request, usage.ContextUsageTokens ?? usage.InputTokens ?? requestTokens,
                contextWindow, usage.ContextUsageTokens is not null || usage.InputTokens is not null
                    ? TokenEstimator.ProviderUsageSource : source, usage).ConfigureAwait(false);
        }

        var assistant = accumulator.BuildAssistantMessage(_deps.Clock.GetUtcNow());

        // Output-limit truncation classifies first: even an empty truncated body continues,
        // and the empty body itself is never committed (ZCode includeEmptyAssistant: false).
        if (accumulator.FinishReason == ModelFinishReason.Length
            && accumulator.ToolCalls.Count == 0)
        {
            if (_outputContinuationsThisTurn >= OutputContinuation.MaxContinuationsPerTurn)
            {
                await CommitMessageAsync(assistant).ConfigureAwait(false);
                await EmitAsync(new ModelRequestFailedEvent(
                    turnId,
                    new ModelFailure(
                        ModelFailureKind.ProviderError,
                        OutputContinuation.LimitExceededMessage,
                        ProviderCode: "model_output_limit_exceeded"),
                    1), persist: false).ConfigureAwait(false);
                return ModelStepOutcome.Failed;
            }
            if (assistant.Parts.Count > 0)
                await CommitMessageAsync(assistant).ConfigureAwait(false);
            // The Continue prompt is turn-request-local: later requests in this turn see
            // it, but it never lands in history or the store.
            _turnLocalRequestMessages.Add(new ModelInputMessage(
                MessageRole.User,
                [new InputTextBlock(OutputContinuation.ContinuePromptText)]));
            _outputContinuationsThisTurn++;
            return ModelStepOutcome.ContinuedOutput;
        }

        if (assistant.Parts.Count == 0)
        {
            if (!IsSuspiciousEmptyCompletion(accumulator))
            {
                // ZCode commits an empty assistant for a stop-finish empty completion
                // and completes the turn normally.
                await CommitMessageAsync(assistant).ConfigureAwait(false);
                _toolRoundsSinceCompact = 0;
                _turnMachine.TryTransition(TurnPhase.Completing);
                return ModelStepOutcome.Finished;
            }
            await EmitAsync(
                new ModelRequestFailedEvent(
                    turnId,
                    new ModelFailure(
                        ModelFailureKind.ProviderError,
                        "Model returned no text, no tool calls, and no usage before completing the turn."),
                    1),
                persist: false).ConfigureAwait(false);
            return ModelStepOutcome.Failed;
        }

        await CommitMessageAsync(assistant).ConfigureAwait(false);

        if (accumulator.ToolCalls.Count == 0)
        {
            _toolRoundsSinceCompact = 0;
            _turnMachine.TryTransition(TurnPhase.Completing);
            return ModelStepOutcome.Finished;
        }

        foreach (var call in accumulator.ToolCalls)
        {
            if (_toolAnomalyThisTurn.Track(call.Name, call.Input) is { } warning)
            {
                _turnLocalRequestMessages.Add(new ModelInputMessage(
                    MessageRole.User,
                    [new InputTextBlock(SystemReminder.Wrap(warning))]));
            }
        }

        _toolRoundsSinceCompact++;
        _turnMachine.TryTransition(TurnPhase.ExecutingTools);
        var toolBatch = await ExecuteToolCallsAsync(turnId, accumulator.ToolCalls, turnCt).ConfigureAwait(false);
        if (toolBatch.Parts.Count > 0)
        {
            var resultsMessage = new ConversationMessage(
                StrongId.NewMessageId(),
                MessageRole.User,
                [.. toolBatch.Parts],
                _deps.Clock.GetUtcNow());
            await CommitMessageAsync(resultsMessage).ConfigureAwait(false);
        }

        if (turnCt.IsCancellationRequested)
        {
            // The cancelled batch's results are committed, but the turn must not drain any
            // queued input at its boundary: cancellation holds the queue.
            return ModelStepOutcome.Cancelled;
        }

        if (toolBatch.StoppedAfterResult)
        {
            // A successful stop-turn tool result ends the turn after its batch: the
            // committed results are the turn's final model-visible output.
            _toolRoundsSinceCompact = 0;
            _turnMachine.TryTransition(TurnPhase.Completing);
            return ModelStepOutcome.Finished;
        }

        return ModelStepOutcome.ContinueWithTools;
    }

    private async Task<ModelStepAccumulator> StreamModelStepAsync(
        TurnId turnId, IChatModel model, AssembledContext request, CancellationToken turnCt)
    {
        var runnerBase = new ModelRunnerOptions();
        for (var attempt = 0; ; attempt++)
        {
            await EmitAsync(new ModelRequestStartedEvent(turnId, attempt + 1), persist: false).ConfigureAwait(false);
            var runnerOptions = new ModelRunnerOptions
            {
                IdleTimeout = runnerBase.IdleTimeout + runnerBase.IdleTimeoutGrowthPerRetry * _streamRecoveriesThisTurn,
            };
            var accumulator = new ModelStepAccumulator(turnId);
            try
            {
                await foreach (var streamEvent in ModelStreamRunner.RunAsync(model, request.Request, runnerOptions, turnCt)
                                   .WithCancellation(turnCt).ConfigureAwait(false))
                {
                    await accumulator.ApplyAsync(streamEvent, evt => EmitAsync(evt, persist: false)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
            {
                // A cancelled stream still owns its partial text: commit it before the
                // cancellation surfaces as TurnCompleted(Cancelled).
                await CommitPartialAssistantAsync(accumulator).ConfigureAwait(false);
                throw;
            }

            // A transient failure after boundary content is recovered in-turn by discarding
            // the partial accumulator and re-running the request fresh, up to the budget;
            // each recovery grows the idle timeout for the next attempt.
            if (accumulator.Failure is { IsRetryable: true }
                && accumulator.HasBoundaryContent
                && _streamRecoveriesThisTurn < StreamRecoveryMaxRetries)
            {
                _streamRecoveriesThisTurn++;
                continue;
            }

            if (accumulator.Failure is not null
                || accumulator.HasBoundaryContent
                || accumulator.ToolCalls.Count > 0
                || attempt >= EmptyCompletionRetry.MaxRetries
                || !IsSuspiciousEmptyCompletion(accumulator))
            {
                return accumulator;
            }
        }
    }

    // ZCode isSuspiciousEmptyModelResult: empty text and tool calls with a non-stop
    // finish and zero reported usage; a stop finish is a normal empty completion.
    private static bool IsSuspiciousEmptyCompletion(ModelStepAccumulator accumulator)
    {
        if (accumulator.FinishReason is ModelFinishReason.Stop or ModelFinishReason.ToolCalls)
            return false;
        return accumulator.Usage is null
            || (accumulator.Usage.InputTokens ?? 0) + (accumulator.Usage.OutputTokens ?? 0)
                + (accumulator.Usage.CacheReadTokens ?? 0) + (accumulator.Usage.CacheWriteTokens ?? 0) == 0;
    }

    // Runtime reminders commit as provider-visible synthetic user messages so they stay in
    // every later request of the turn (and across restarts) instead of being request-local.
    // Order follows the reference loop: plan-mode exit, plan-mode cadence, todo.
    private async Task CommitRuntimeRemindersAsync(IReadOnlyList<ConversationMessage> history)
    {
        if (_planModeExitReminderPending)
        {
            _planModeExitReminderPending = false;
            await CommitSyntheticReminderAsync(RuntimeReminders.PlanModeExitReminder).ConfigureAwait(false);
        }

        if (_permissions.Mode == PermissionMode.Plan)
        {
            var (foundReminder, humanTurnsSince) = ScanPlanReminderCadence(history);
            if (!foundReminder || humanTurnsSince >= RuntimeReminders.TurnsBetweenAttachments)
            {
                var attachmentIndex = CountPlanReminders(history) + 1;
                await CommitSyntheticReminderAsync(RuntimeReminders.BuildModeReminder(attachmentIndex)).ConfigureAwait(false);
            }
        }

        var (sinceTodoWrite, sinceTodoReminder) = TodoCountsFromHistory(history);
        if (TodoReminderPolicy.ShouldRemind(_deps.Tools, sinceTodoWrite, sinceTodoReminder))
        {
            await CommitSyntheticReminderAsync(TodoReminderPolicy.BuildReminderBody(_deps.TodoStore?.Get(_record.Id))).ConfigureAwait(false);
        }
    }

    // ZCode derives both counters from persisted history (committed assistant entries
    // after the last TodoWrite call / after the last committed reminder), so the cadence
    // survives restarts; the fallback is the total assistant count.
    private static (int SinceTodoWrite, int SinceTodoReminder) TodoCountsFromHistory(
        IReadOnlyList<ConversationMessage> history)
    {
        var assistants = 0;
        int? sinceWrite = null;
        int? sinceReminder = null;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];
            if (sinceReminder is null
                && message.Parts.OfType<AttachmentPart>().Any(a => TodoReminderPolicy.IsTodoReminder(a.Content)))
            {
                sinceReminder = assistants;
            }
            if (message.Role != MessageRole.Assistant)
                continue;
            if (sinceWrite is null
                && message.Parts.OfType<ToolCallPart>().Any(c =>
                    string.Equals(c.ToolName, TodoReminderPolicy.TodoWriteToolName, StringComparison.OrdinalIgnoreCase)))
            {
                sinceWrite = assistants;
            }
            assistants++;
        }
        return (sinceWrite ?? assistants, sinceReminder ?? assistants);
    }

    private async Task CommitSyntheticReminderAsync(string body)
    {
        await CommitMessageAsync(new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.User,
            [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, SystemReminder.Wrap(body), ProviderVisible: true)],
            _deps.Clock.GetUtcNow())).ConfigureAwait(false);
    }

    private static (bool Found, int HumanTurnsSince) ScanPlanReminderCadence(IReadOnlyList<ConversationMessage> history)
    {
        var humanTurnsSince = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];
            if (message.Parts.OfType<AttachmentPart>().Any(a => RuntimeReminders.IsPlanModeReminder(a.Content)))
                return (true, humanTurnsSince);
            if (IsRealUserTurn(message))
                humanTurnsSince++;
        }
        return (false, humanTurnsSince);
    }

    private static int CountPlanReminders(IReadOnlyList<ConversationMessage> history) =>
        history.Count(m => m.Parts.OfType<AttachmentPart>().Any(a => RuntimeReminders.IsPlanModeReminder(a.Content)));

    // Real user turns are plain text inputs; steers, tool results, notifications, and
    // synthetic reminders never count as human turns.
    private static bool IsRealUserTurn(ConversationMessage message) =>
        message.Role == MessageRole.User
        && !message.Parts.Any(p => p is ToolResultPart)
        && message.Parts.OfType<TextPart>().Any(t => !t.Text.StartsWith("<system-reminder>", StringComparison.Ordinal));

    private AssembledContext AppendTurnLocalEntries(AssembledContext request)
    {
        if (_turnLocalRequestMessages.Count == 0)
            return request;
        return request with
        {
            Request = request.Request with
            {
                Messages = [.. request.Request.Messages, .. _turnLocalRequestMessages],
            },
        };
    }

    private async Task<bool> StripReasoningHistoryAsync()
    {
        var history = _history.Snapshot();
        var stripped = ThinkingSignatureRecovery.StripReasoningParts(history);
        if (ReferenceEquals(stripped, history))
            return false;

        _history.ReplaceAll(stripped);
        _lastProviderUsage = null;
        _usageAnchorIndex = -1;
        await _deps.Store.ReplaceMessagesAsync(_record.Id, stripped, _sessionCts.Token).ConfigureAwait(false);
        return true;
    }

    private sealed record ToolBatchCommit(IReadOnlyList<ToolResultPart> Parts, bool StoppedAfterResult);

    private async Task<ToolBatchCommit> ExecuteToolCallsAsync(
        TurnId turnId,
        IReadOnlyList<ToolCall> calls,
        CancellationToken turnCt)
    {
        var results = new List<ToolResultPart>();

        var batch = await _toolRunner.ExecuteAsync(
            [.. calls.Select(c => new ScheduledToolCall(c))],
            (resolvedCall, ct) => ValueTask.FromResult(new ToolExecutionContext(
                _record.Id,
                _record.WorkspaceRoot,
                resolvedCall.Input,
                _deps.Workspace,
                _deps.Clock,
                _sessionCommands,
                _fileReads,
                _shell,
                _backgroundJobs,
                _auxModel,
                _skills,
                _broker)),
            async notification =>
            {
                switch (notification)
                {
                    case ToolExecutionStarted started:
                        await EmitAsync(new ToolCallStartedEvent(turnId, started.Call.Id, started.Call.Name, started.Call.Input), persist: false).ConfigureAwait(false);
                        break;
                    case ToolExecutionResultNotification result:
                        await EmitAsync(
                            new ToolCallResultEvent(turnId, result.Call.Id, result.Call.Name, result.Result, result.Duration),
                            persist: false).ConfigureAwait(false);
                        if (string.Equals(result.Call.Name, "Read", StringComparison.OrdinalIgnoreCase)
                            && result.Result.Content.OfType<ToolResultText>().FirstOrDefault() is { } readText)
                        {
                            var path = result.Call.Input.TryGetProperty("file_path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
                                ? pathElement.GetString()
                                : null;
                            if (path is not null)
                            {
                                _history.RecordFileRead(path, readText.Text, _deps.Clock.GetUtcNow());
                            }
                        }
                        if (string.Equals(result.Call.Name, ExitPlanModeTool.ToolName, StringComparison.Ordinal)
                            && !result.Result.IsError)
                        {
                            _planModeExitReminderPending = true;
                        }
                        break;
                }
            },
            turnCt).ConfigureAwait(false);

        foreach (var outcome in batch.Outcomes)
        {
            var budgeted = ToolResultBudget.ApplyBudget(outcome.Result, ToolResultLimits.DefaultMaxInlineChars);
            results.Add(new ToolResultPart(
                StrongId.NewPartId(),
                outcome.Call.Id,
                budgeted.Content,
                budgeted.IsError));
        }

        return new ToolBatchCommit(results, batch.StoppedAfterResult);
    }

    private async Task<bool> ExecuteCompactionAsync(CompactTrigger trigger, string? instructions, CancellationToken ct,
        IChatModel? frozenModel = null, ModelCallOptions? frozenOptions = null)
    {
        var history = _history.Snapshot();
        var selection = _record.ModelSelection ?? _deps.Options.DefaultModel;
        var model = frozenModel ?? _deps.ResolveModel(selection
            ?? throw new InvalidOperationException("No model configured."));
        var options = frozenOptions ?? selection?.Options ?? new ModelCallOptions();
        var contextWindow = model.Descriptor.Properties.ContextWindowTokens ?? CompactPolicy.DefaultContextWindowTokens;
        var (effectiveTokens, _) = EstimateContextTokens(
            await AssembleContextAsync(history, options, model, ct).ConfigureAwait(false), history);

        var plan = CompactionPlanner.PlanCompaction(new CompactionPlanInput(
            Messages: history,
            Trigger: trigger,
            EffectiveTokens: effectiveTokens,
            ContextWindowTokens: contextWindow,
            Model: model.Descriptor,
            Tools: _deps.Tools.VisibleContracts,
            LastProviderUsage: _lastProviderUsage,
            AnchorIndex: _usageAnchorIndex,
            ManualInstructions: instructions));
        var compactionSelection = plan.Selection;

        var summaryText = new System.Text.StringBuilder();
        ModelFailure? failure = null;
        var promptTooLongRetries = 0;
        while (true)
        {
            var rawRequest = CompactionPlanner.BuildSummaryRequest(compactionSelection, instructions);
            var summaryLimit = Math.Min(CompactPolicy.SummaryMaxOutputTokens,
                Math.Min(options.MaxOutputTokens ?? int.MaxValue, model.Descriptor.Properties.DefaultMaxOutputTokens ?? int.MaxValue));
            summaryLimit = Math.Min(summaryLimit, Math.Max(1, contextWindow - TokenEstimator.EstimateRequestTokens(rawRequest) - 1000));
            var summaryRequest = rawRequest with
            {
                Options = options with { MaxOutputTokens = summaryLimit },
            };

            summaryText.Clear();
            failure = null;
            try
            {
                await foreach (var streamEvent in ModelStreamRunner.RunAsync(
                                   model,
                                   summaryRequest,
                                   new ModelRunnerOptions(),
                                   ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    switch (streamEvent)
                    {
                        case ModelTextDelta textDelta:
                            summaryText.Append(textDelta.Delta);
                            break;
                        case ModelStreamFailed streamFailed:
                            failure = streamFailed.Failure;
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                failure = new ModelFailure(ModelFailureKind.Cancelled, "compaction cancelled");
            }

            // A summary request whose own prompt exceeds the window is retried over a
            // truncated selection; other failures keep the circuit-breaker path below.
            if (failure is { Kind: ModelFailureKind.ContextOverflow } overflow
                && promptTooLongRetries < CompactPolicy.PromptTooLongMaxRetries)
            {
                var reselected = CompactionSelector.ReselectForPromptTooLong(history, compactionSelection, overflow.DeclaredTokenGap);
                if (!ReferenceEquals(reselected, compactionSelection))
                {
                    compactionSelection = reselected;
                    promptTooLongRetries++;
                    continue;
                }
            }
            break;
        }

        if (failure is not null || summaryText.Length == 0)
        {
            _consecutiveCompactFailures++;
            _deps.Logger.LogWarning("Compaction ({Trigger}) failed after {Failures} consecutive attempts.", trigger, _consecutiveCompactFailures);
            return false;
        }

        _consecutiveCompactFailures = 0;
        _consecutiveRapidRefills = 0;
        _toolRoundsSinceCompact = 0;

        plan = plan with { Selection = compactionSelection };

        var summaryMessage = CompactionPlanner.CreateCompactSummaryMessage(summaryText.ToString());
        var recentlyRead = _history.RecentReads()
            .Select(r => (r.Path, r.Content))
            .ToList();
        var attachments = CompactionPlanner.BuildRecentlyReadFilesAttachments(recentlyRead);
        var postCompact = CompactionPlanner.BuildPostCompactHistory(
            plan,
            summaryText.ToString(),
            summaryMessage,
            plan.Selection.PreservedTail,
            attachments);
        _history.ReplaceAll(postCompact.Messages);
        _history.ClearRecentReads();
        await _deps.Store.ReplaceMessagesAsync(_record.Id, postCompact.Messages, ct).ConfigureAwait(false);
        _lastProviderUsage = null;
        _usageAnchorIndex = -1;
        _contextUsage = null;
        await EmitAsync(new CompactBoundaryAppliedEvent(postCompact.Boundary), persist: true).ConfigureAwait(false);
        var compactedContext = await AssembleContextAsync(postCompact.Messages, options, cancellationToken: ct).ConfigureAwait(false);
        await PublishContextUsageAsync(compactedContext, TokenEstimator.EstimateRequestTokens(compactedContext.Request),
            contextWindow, TokenEstimator.EstimateSource).ConfigureAwait(false);
        return true;
    }

    private async Task CommitMessageAsync(ConversationMessage message)
    {
        _history.Append(message);
        var sequence = await _deps.Store.GetNextMessageSequenceAsync(_record.Id, _sessionCts.Token).ConfigureAwait(false);
        await _deps.Store.AppendMessagesAsync(_record.Id, [message], sequence, _sessionCts.Token).ConfigureAwait(false);
        SessionEvent committed = message.Role == MessageRole.User
            ? new UserMessageCommittedEvent(message)
            : new AssistantMessageCommittedEvent(message);
        await EmitAsync(committed, persist: false).ConfigureAwait(false);
        _record = _record with { UpdatedAt = _deps.Clock.GetUtcNow() };
        await PersistRecordAsync(_sessionCts.Token).ConfigureAwait(false);
    }

    private async Task CommitPartialAssistantAsync(ModelStepAccumulator accumulator)
    {
        var partial = accumulator.BuildPartialAssistantMessage(_deps.Clock.GetUtcNow());
        if (partial.Parts.Count > 0)
        {
            await CommitMessageAsync(partial).ConfigureAwait(false);
        }
    }

    private async Task<AssembledContext> AssembleContextAsync(
        IReadOnlyList<ConversationMessage> history, ModelCallOptions options, IChatModel? model = null,
        CancellationToken cancellationToken = default)
    {
        var custom = _deps.Options.CustomSystemPrompt;
        var currentDate = EnvironmentSections.FormatLocalIsoDate(_deps.Clock.GetLocalNow().LocalDateTime);
        var media = await LoadSessionMediaAsync(history, cancellationToken).ConfigureAwait(false);
        return ContextBuilder.Assemble(new ContextAssemblyInput(
            CliPrefix: PromptTexts.CliPrefix,
            SystemStable: custom is { Length: > 0 } body ? "\n" + body.Trim() : _deps.Options.SystemPromptStable,
            SystemDynamic: custom is { Length: > 0 } ? null : BuildDynamicSystemSections(model),
            AgentInstructions: _deps.Options.AgentInstructions,
            SkillsListing: _deps.Options.SkillsListing,
            History: history,
            Tools: _deps.Tools.VisibleContracts,
            Options: options,
            CurrentDate: currentDate,
            Media: media));
    }

    // Stored history references media by artifact URI so sessions stay light in memory;
    // image payloads are hydrated per request, and models without image support get the
    // adapter-level downgrade instead of a hydration skip.
    private async Task<Dictionary<string, MediaPayload>?> LoadSessionMediaAsync(
        IReadOnlyList<ConversationMessage> history, CancellationToken cancellationToken)
    {
        Dictionary<string, MediaPayload>? media = null;
        foreach (var (uri, mimeType, fileName) in history.SelectMany(MediaReferences))
        {
            if (media?.ContainsKey(uri) == true
                || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var bytes = await _deps.Artifacts.TryLoadAsync(uri, cancellationToken).ConfigureAwait(false);
            if (bytes is not { } data)
            {
                continue;
            }
            media ??= new Dictionary<string, MediaPayload>(StringComparer.Ordinal);
            media[uri] = new MediaPayload(mimeType, Convert.ToBase64String(data.Span), fileName);
        }
        return media;
    }

    private static IEnumerable<(string Uri, string MimeType, string? FileName)> MediaReferences(
        ConversationMessage message)
    {
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case ToolResultPart result:
                    foreach (var block in result.Content.OfType<ToolResultMedia>())
                        yield return (block.ArtifactUri, block.MimeType, block.FileName);
                    break;
                case FilePart file:
                    yield return (file.ArtifactUri, file.MimeType, file.FileName);
                    break;
            }
        }
    }

    // Section order follows the reference builder: dynamic behavior, session guidance,
    // environment, output style (unused), context management, git snapshot.
    private string? BuildDynamicSystemSections(IChatModel? model)
    {
        var sections = new List<string>
        {
            PromptTexts.DynamicBehavior,
        };
        if (PromptTexts.SessionSpecificGuidance(
                skillToolAvailable: _deps.Tools.VisibleContracts.Any(tool => tool.Name == "Skill"),
                hasSkills: !string.IsNullOrEmpty(_deps.Options.SkillsListing)) is { } guidance)
        {
            sections.Add(guidance);
        }
        sections.Add(EnvironmentSections.EnvironmentSection(new EnvFacts(
            _record.WorkspaceRoot,
            _isGitRepository,
            RuntimeEnvironmentFacts.Platform,
            _deps.Options.ShellName ?? RuntimeEnvironmentFacts.Shell,
            RuntimeEnvironmentFacts.OsVersion,
            model?.Descriptor.ProviderId,
            model?.Descriptor.ModelId)));
        sections.Add(PromptTexts.ContextManagementAndAutonomy);
        if (_gitSnapshotSection is not null)
        {
            sections.Add(_gitSnapshotSection);
        }
        return string.Join("\n\n", sections);
    }

    private (int Tokens, string Source) EstimateContextTokens(AssembledContext context, IReadOnlyList<ConversationMessage> history) =>
        _lastProviderUsage?.ContextUsageTokens is not null || _lastProviderUsage?.InputTokens is not null
            ? TokenEstimator.EstimateCurrentInputTokens(history, _usageAnchorIndex, _lastProviderUsage)
            : (TokenEstimator.EstimateRequestTokens(context.Request), TokenEstimator.EstimateSource);

    private async ValueTask PublishContextUsageAsync(
        AssembledContext context, int used, int max, string source, TokenUsage? usage = null)
    {
        var cacheRead = _contextUsage?.CacheReadTokens ?? 0;
        var cacheInput = _contextUsage?.CacheInputTokens ?? 0;
        if ((usage?.ContextUsageTokens ?? usage?.InputTokens) is int inputTokens && inputTokens > 0)
        {
            cacheInput += inputTokens;
            cacheRead += Math.Clamp(usage?.CacheReadTokens ?? 0, 0, inputTokens);
        }
        _contextUsage = new ContextUsageState(Math.Max(0, used), max, source, context.Breakdown,
            cacheRead, cacheInput, usage ?? _contextUsage?.LastUsage);
        await EmitAsync(new ContextUsageUpdatedEvent(_contextUsage), persist: true).ConfigureAwait(false);
    }

    private AssembledContext AppendDateReminder(AssembledContext request)
    {
        var today = EnvironmentSections.FormatLocalIsoDate(_deps.Clock.GetLocalNow().LocalDateTime);
        if (_lastSeenLocalDate is null || _lastSeenLocalDate == today)
        {
            _lastSeenLocalDate = today;
            return request;
        }
        _lastSeenLocalDate = today;
        return request with
        {
            Request = request.Request with
            {
                Messages =
                [
                    .. request.Request.Messages,
                    new ModelInputMessage(MessageRole.User,
                        [new InputTextBlock(SystemReminder.Wrap(EnvironmentSections.DateChangeReminder(today)))]),
                ],
            },
        };
    }

    private async Task EmitModelChangedAsync(ModelSelection selection)
    {
        var descriptor = _deps.ResolveModel(selection).Descriptor;
        var window = descriptor.Properties.ContextWindowTokens ?? CompactPolicy.DefaultContextWindowTokens;
        await EmitAsync(new SessionModelChangedEvent(selection, window), persist: true).ConfigureAwait(false);
    }

    private async ValueTask EmitAsync(SessionEvent sessionEvent, bool persist)
    {
        var sequenced = _events.Append(sessionEvent);
        if (persist && !SessionEventRetention.IsTransient(sessionEvent))
        {
            await _deps.Store.AppendDurableEventsAsync([sequenced], _sessionCts.Token).ConfigureAwait(false);
        }
    }

    private async Task PersistRecordAsync(CancellationToken ct) =>
        await _deps.Store.UpsertSessionAsync(_record, ct).ConfigureAwait(false);

    private void BumpRevision() => Interlocked.Increment(ref _revision);

    private async Task ReplayHistoryAsync(bool persistStartEvents)
    {
        var durable = await _deps.Store.LoadDurableEventsAsync(_record.Id, CancellationToken.None).ConfigureAwait(false);
        _events.AdvanceSequenceTo(durable.Count > 0 ? durable.Max(evt => evt.Sequence) : 0);
        var started = new SessionStartedEvent(_record.WorkspaceRoot, _record.Title, _record.PermissionMode, _record.ModelSelection);
        if (persistStartEvents)
        {
            await EmitAsync(started, persist: true).ConfigureAwait(false);
        }
        else
        {
            EmitForReplay(started);
        }

        if (_record.ModelSelection is { } selection)
        {
            var descriptor = _deps.ResolveModel(selection).Descriptor;
            var modelChanged = new SessionModelChangedEvent(
                selection,
                descriptor.Properties.ContextWindowTokens ?? CompactPolicy.DefaultContextWindowTokens);
            if (persistStartEvents)
            {
                await EmitAsync(modelChanged, persist: true).ConfigureAwait(false);
            }
            else
            {
                EmitForReplay(modelChanged);
            }
        }

        var messages = await _deps.Store.LoadMessagesAsync(_record.Id, CancellationToken.None).ConfigureAwait(false);
        foreach (var stored in messages)
        {
            SessionEvent committed = stored.Message.Role == MessageRole.User
                ? new UserMessageCommittedEvent(stored.Message)
                : new AssistantMessageCommittedEvent(stored.Message);
            EmitForReplay(committed);
        }
        _history.AppendRange(messages.Select(m => m.Message).ToList());
        await RepairDanglingToolCallsAsync(messages).ConfigureAwait(false);
        _usageAnchorIndex = -1;
        if (messages.Count > 0)
        {
            _contextUsage = durable.Select(evt => evt.Event).OfType<ContextUsageUpdatedEvent>().LastOrDefault()?.Usage;
            var resumedSelection = _record.ModelSelection ?? _deps.Options.DefaultModel;
            if (resumedSelection is not null)
            {
                var resumedModel = _deps.ResolveModel(resumedSelection);
                var descriptor = resumedModel.Descriptor;
                _contextModelId = descriptor.FullId;
                var resumed = await AssembleContextAsync(
                    _history.Snapshot(), resumedSelection.Options ?? new ModelCallOptions(), resumedModel).ConfigureAwait(false);
                _contextUsage = new ContextUsageState(TokenEstimator.EstimateRequestTokens(resumed.Request),
                    descriptor.Properties.ContextWindowTokens ?? CompactPolicy.DefaultContextWindowTokens,
                    TokenEstimator.EstimateSource, resumed.Breakdown,
                    _contextUsage?.CacheReadTokens ?? 0, _contextUsage?.CacheInputTokens ?? 0);
                EmitForReplay(new ContextUsageUpdatedEvent(_contextUsage));
            }
        }
    }

    private void EmitForReplay(SessionEvent sessionEvent) => _events.Append(sessionEvent);

    // ZCode session-history-hydrator: a persisted tool call without a later tool result is
    // interrupted work; a synthesized result keeps provider replay free of dangling tool_use.
    private async Task RepairDanglingToolCallsAsync(IReadOnlyList<StoredMessage> messages)
    {
        List<ToolResultPart>? repairs = null;
        for (var i = 0; i < messages.Count; i++)
        {
            foreach (var call in messages[i].Message.Parts.OfType<ToolCallPart>())
            {
                if (!HasLaterToolResult(messages, i + 1, call.ToolCallId))
                {
                    (repairs ??= []).Add(new ToolResultPart(
                        StrongId.NewPartId(),
                        call.ToolCallId,
                        [new ToolResultText(InterruptedToolResultText)],
                        IsError: false));
                }
            }
        }

        if (repairs is null)
        {
            return;
        }

        var repairMessage = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.User,
            [.. repairs],
            _deps.Clock.GetUtcNow());
        _history.Append(repairMessage);
        var sequence = await _deps.Store.GetNextMessageSequenceAsync(_record.Id, CancellationToken.None).ConfigureAwait(false);
        await _deps.Store.AppendMessagesAsync(_record.Id, [repairMessage], sequence, CancellationToken.None).ConfigureAwait(false);
        EmitForReplay(new UserMessageCommittedEvent(repairMessage));
    }

    private static bool HasLaterToolResult(IReadOnlyList<StoredMessage> messages, int fromIndex, ToolCallId toolCallId)
    {
        for (var i = fromIndex; i < messages.Count; i++)
        {
            foreach (var part in messages[i].Message.Parts)
            {
                if (part is ToolResultPart result && result.ToolCallId == toolCallId)
                    return true;
            }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _sessionCts.Cancel();
        _backgroundJobs.StopAll();
        try
        {
            await _turnTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch
        {
        }
        _sessionCts.Dispose();
        _events.DropAll();
        _commandLock.Dispose();
    }

    private enum ModelStepOutcome
    {
        ContinueWithTools,
        Finished,
        Failed,
        Cancelled,
        ContextOverflow,
        ContinuedOutput,
        ContinueAfterHistoryRepair,
    }

    // A pending turn input is either a real user command or a task notification body that
    // already carries the system-notification prefix.
    private sealed record PendingTurnInput(SendTextCommand? Command, string? NotificationBody);

    // Runs on the turn task inside tool execution; the command lock is free at that point,
    // so a mode switch submits like any other client command. Base revision 0 always passes
    // because only future revisions are rejected.
    private sealed class RuntimeSessionCommands(SessionRuntime runtime) : ISessionCommands
    {
        public async ValueTask SetModeAsync(PermissionMode mode, CancellationToken cancellationToken)
        {
            await runtime.SubmitAsync(
                new SetModeCommand(StrongId.NewCommandId(), runtime.SessionId, 0, mode),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
