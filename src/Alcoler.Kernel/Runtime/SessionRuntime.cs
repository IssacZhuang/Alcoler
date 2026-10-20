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
using Alcoler.Kernel.Tools;
using Microsoft.Extensions.Logging;

namespace Alcoler.Kernel.Runtime;

public sealed class SessionRuntimeOptions
{
    public string SystemPromptStable { get; init; } = DefaultPrompts.Stable;

    public string? AgentInstructions { get; init; }

    public string? SkillsListing { get; init; }

    public ModelSelection? DefaultModel { get; init; }
}

public static class DefaultPrompts
{
    public const string Stable =
        """
        You are Alcoler, an interactive coding agent. Help the user with software engineering tasks:
        reading and editing code, running commands, searching the workspace, and answering questions
        about the project. Prefer the smallest change that achieves the goal. Verify your work by
        running the project's build, lint, and test commands before calling a task complete. When you
        reference code, use path:line format. Ask before destructive or irreversible actions.
        """;
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
    ITodoStore? TodoStore = null);

public sealed class SessionRuntime : IAsyncDisposable
{
    private const int StreamRecoveryMaxRetries = 10;
    private const int ReactiveCompactMaxRetries = 3;
    // ZCode DEFAULT_NORMAL_REQUEST_MAX_OUTPUT_TOKENS; only used when the model declares none.
    private const int DefaultRequestMaxOutputTokens = 32_000;

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
    private readonly Queue<SendTextCommand> _inputQueue = new();
    private TokenUsage? _lastProviderUsage;
    private int _usageAnchorIndex = -1;
    private ContextUsageState? _contextUsage;
    private string? _contextModelId;
    private DateTimeOffset _lastActivityUtc;
    private int _consecutiveCompactFailures;
    private int _consecutiveRapidRefills;
    private int _toolRoundsSinceCompact;
    private int _stepsSinceTodoWrite;
    private int _stepsSinceTodoReminder;
    private int _outputContinuationsThisTurn;
    private bool _signatureRepairUsedThisTurn;
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
        if (sendText.Mode is { } mode)
        {
            _permissions.SetMode(mode);
            _record = _record with { PermissionMode = mode };
        }

        if (_turnMachine.IsBusy)
        {
            _inputQueue.Enqueue(sendText);
            _ = EmitAsync(new InputQueuedEvent(new QueuedInput(sendText.CommandId, sendText.Text, _deps.Clock.GetUtcNow())), persist: true);
            BumpRevision();
            return CommandAck.AcceptedAt(sendText.CommandId, Revision);
        }

        StartTurn(sendText);
        BumpRevision();
        return CommandAck.AcceptedAt(sendText.CommandId, Revision);
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

    private void StartTurn(SendTextCommand command)
    {
        if (!_turnMachine.TryTransition(TurnPhase.ProcessingInput))
        {
            _inputQueue.Enqueue(command);
            return;
        }

        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        var turnCts = _turnCts;
        _turnTask = RunTurnAsync(command, turnCts.Token);
    }

    private async Task RunTurnAsync(SendTextCommand command, CancellationToken turnCt)
    {
        var turnId = StrongId.NewTurnId();
        try
        {
            var selection = command.ModelSelection ?? _record.ModelSelection ?? _deps.Options.DefaultModel
                ?? throw new InvalidOperationException("No model configured for session.");
            var model = _deps.ResolveModel(selection);
            var options = selection.Options ?? new ModelCallOptions();
            model.ValidateOptions(options);
            await EmitAsync(new TurnStartedEvent(turnId, command.CommandId), persist: true).ConfigureAwait(false);

            var userMessage = new ConversationMessage(
                StrongId.NewMessageId(),
                MessageRole.User,
                [new TextPart(StrongId.NewPartId(), command.Text)],
                _deps.Clock.GetUtcNow());
            await CommitMessageAsync(userMessage).ConfigureAwait(false);

            var outcome = TurnOutcome.Completed;
            var reactiveCompactRetries = 0;
            _outputContinuationsThisTurn = 0;
            _signatureRepairUsedThisTurn = false;
            while (true)
            {
                turnCt.ThrowIfCancellationRequested();

                var step = await RunModelStepAsync(turnId, model, options, turnCt).ConfigureAwait(false);
                if (step == ModelStepOutcome.Finished)
                {
                    break;
                }

                if (step == ModelStepOutcome.ContextOverflow && reactiveCompactRetries < ReactiveCompactMaxRetries)
                {
                    reactiveCompactRetries++;
                    var compacted = await ExecuteCompactionAsync(CompactTrigger.Reactive, null, turnCt, model, options).ConfigureAwait(false);
                    if (!compacted)
                    {
                        outcome = TurnOutcome.Failed;
                        break;
                    }
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

                // Queued user input joins the running turn instead of waiting for it to end.
                await DrainSteeringAsync().ConfigureAwait(false);
            }

            await EmitAsync(new TurnCompletedEvent(turnId, outcome, _lastProviderUsage), persist: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await EmitAsync(new TurnCompletedEvent(turnId, TurnOutcome.Cancelled, _lastProviderUsage), persist: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _deps.Logger.LogError(ex, "Turn {TurnId} failed unexpectedly.", turnId);
            await EmitAsync(new TurnFailedEvent(turnId, ex.Message), persist: true).ConfigureAwait(false);
        }
        finally
        {
            _turnCts?.Dispose();
            _turnCts = null;
            _turnMachine.Transition(TurnPhase.Idle);
            _events.EvictTransientOlderThan(
                _deps.Clock.GetUtcNow().AddSeconds(-SessionEventRetention.SealedTurnTransientGraceMs / 1000.0),
                _events.LastSequence);
            await DrainQueueAsync().ConfigureAwait(false);
        }
    }

    // Takes ownership of queued inputs while the turn is still running so the model sees
    // them in the next step of this turn rather than after it completes.
    private async Task DrainSteeringAsync()
    {
        while (true)
        {
            SendTextCommand? next = null;
            await _commandLock.WaitAsync(_sessionCts.Token).ConfigureAwait(false);
            try
            {
                if (_inputQueue.Count > 0)
                {
                    next = _inputQueue.Dequeue();
                    _ = EmitAsync(new InputDequeuedEvent(next.CommandId), persist: true);
                }
            }
            finally
            {
                _commandLock.Release();
            }

            if (next is null)
            {
                return;
            }

            var steer = new ConversationMessage(
                StrongId.NewMessageId(),
                MessageRole.User,
                [new TextPart(StrongId.NewPartId(), Steering.FormatUserSteer(next.Text))],
                _deps.Clock.GetUtcNow());
            await CommitMessageAsync(steer).ConfigureAwait(false);
        }
    }

    private async Task DrainQueueAsync()
    {
        while (true)
        {
            SendTextCommand? next = null;
            await _commandLock.WaitAsync(_sessionCts.Token).ConfigureAwait(false);
            try
            {
                if (_inputQueue.Count > 0 && !_turnMachine.IsBusy)
                {
                    next = _inputQueue.Dequeue();
                    _ = EmitAsync(new InputDequeuedEvent(next.CommandId), persist: true);                    StartTurnInternal(next);
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

    private void StartTurnInternal(SendTextCommand command)
    {
        if (!_turnMachine.TryTransition(TurnPhase.ProcessingInput))
        {
            _inputQueue.Enqueue(command);
            return;
        }
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        _turnTask = RunTurnAsync(command, _turnCts.Token);
    }

    // Arrives on a thread-pool continuation; SubmitAsync routes it into the input queue
    // while a turn is active or starts a fresh turn when the session is idle, so a
    // finished background job re-invokes the model with its outcome.
    private async ValueTask NotifyBackgroundJobCompletedAsync(BackgroundJobInfo job)
    {
        if (_disposed)
            return;
        try
        {
            await SubmitAsync(
                new SendTextCommand(StrongId.NewCommandId(), _record.Id, 0, BackgroundJobNotifications.FormatCompleted(job)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

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
        var initialRequest = AssembleContext(history, options);
        var (effectiveTokens, _) = EstimateContextTokens(initialRequest, history);

        var now = _deps.Clock.GetUtcNow();
        var micro = MicroCompactor.Apply(history, _lastActivityUtc, now, threshold);
        if (micro.ClearedParts.Count > 0)
        {
            _history.ReplaceAll(micro.Messages);
            history = micro.Messages;
            _lastProviderUsage = null;
            _usageAnchorIndex = -1;
            effectiveTokens = TokenEstimator.EstimateRequestTokens(AssembleContext(history, initialRequest.Request.Options).Request);
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
            if (_toolRoundsSinceCompact >= CompactPolicy.RapidRefillToolTurnThreshold)
            {
                _consecutiveRapidRefills++;
                if (_consecutiveRapidRefills > CompactPolicy.MaxConsecutiveRapidRefills)
                {
                    _consecutiveRapidRefills = 0;
                }
            }
            await ExecuteCompactionAsync(CompactTrigger.Auto, null, turnCt, model, options).ConfigureAwait(false);
            history = _history.Snapshot();
        }

        _turnMachine.TryTransition(TurnPhase.AwaitingModel);
        var request = AssembleContext(history, initialRequest.Request.Options);
        var (requestTokens, source) = EstimateContextTokens(request, history);
        if (model.Descriptor.Properties.ContextWindowTokens is { } configuredContext)
        {
            var requestedMax = options.MaxOutputTokens
                ?? model.Descriptor.Properties.DefaultMaxOutputTokens
                ?? DefaultRequestMaxOutputTokens;
            var availableOutput = Math.Max(1, configuredContext - requestTokens - 1000);
            request = request with { Request = request.Request with { Options = options with { MaxOutputTokens = Math.Min(requestedMax, availableOutput) } } };
        }
        request = AppendTodoReminder(request);
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
        if (assistant.Parts.Count == 0)
        {
            // ZCode treats a post-retry empty completion as a structured model error surfaced
            // to the turn, never as a silently successful empty turn.
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

        if (accumulator.FinishReason == ModelFinishReason.Length
            && accumulator.ToolCalls.Count == 0
            && assistant.Parts.OfType<TextPart>().Any()
            && _outputContinuationsThisTurn < OutputContinuation.MaxContinuationsPerTurn)
        {
            await CommitMessageAsync(assistant).ConfigureAwait(false);
            await CommitMessageAsync(new ConversationMessage(
                StrongId.NewMessageId(),
                MessageRole.User,
                [new TextPart(StrongId.NewPartId(), OutputContinuation.ContinuePromptText)],
                _deps.Clock.GetUtcNow())).ConfigureAwait(false);
            _outputContinuationsThisTurn++;
            return ModelStepOutcome.ContinuedOutput;
        }

        await CommitMessageAsync(assistant).ConfigureAwait(false);

        if (accumulator.ToolCalls.Count == 0)
        {
            _toolRoundsSinceCompact = 0;
            _turnMachine.TryTransition(TurnPhase.Completing);
            return ModelStepOutcome.Finished;
        }

        _toolRoundsSinceCompact++;
        _turnMachine.TryTransition(TurnPhase.ExecutingTools);
        var toolResults = await ExecuteToolCallsAsync(turnId, accumulator.ToolCalls, turnCt).ConfigureAwait(false);
        if (toolResults.Count > 0)
        {
            var resultsMessage = new ConversationMessage(
                StrongId.NewMessageId(),
                MessageRole.User,
                [.. toolResults],
                _deps.Clock.GetUtcNow());
            await CommitMessageAsync(resultsMessage).ConfigureAwait(false);
        }

        return ModelStepOutcome.ContinueWithTools;
    }

    private async Task<ModelStepAccumulator> StreamModelStepAsync(
        TurnId turnId, IChatModel model, AssembledContext request, CancellationToken turnCt)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EmitAsync(new ModelRequestStartedEvent(turnId, attempt + 1), persist: false).ConfigureAwait(false);
            var accumulator = new ModelStepAccumulator(turnId);
            await foreach (var streamEvent in ModelStreamRunner.RunAsync(model, request.Request, new ModelRunnerOptions(), turnCt)
                               .WithCancellation(turnCt).ConfigureAwait(false))
            {
                await accumulator.ApplyAsync(streamEvent, evt => EmitAsync(evt, persist: false)).ConfigureAwait(false);
            }

            if (accumulator.Failure is not null
                || accumulator.HasBoundaryContent
                || accumulator.ToolCalls.Count > 0
                || attempt >= EmptyCompletionRetry.MaxRetries)
            {
                return accumulator;
            }
        }
    }

    // Counted per model step; the reminder itself is request-local and never persisted.
    private AssembledContext AppendTodoReminder(AssembledContext request)
    {
        _stepsSinceTodoWrite++;
        _stepsSinceTodoReminder++;
        if (!TodoReminderPolicy.ShouldRemind(_deps.Tools, _stepsSinceTodoWrite, _stepsSinceTodoReminder))
            return request;

        _stepsSinceTodoReminder = 0;
        return request with
        {
            Request = request.Request with
            {
                Messages = [.. request.Request.Messages, TodoReminderPolicy.BuildReminderMessage(_deps.TodoStore?.Get(_record.Id))],
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

    private async Task<List<ToolResultPart>> ExecuteToolCallsAsync(
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
                        if (string.Equals(result.Call.Name, "ReadFile", StringComparison.OrdinalIgnoreCase)
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
                        if (string.Equals(result.Call.Name, TodoReminderPolicy.TodoWriteToolName, StringComparison.OrdinalIgnoreCase))
                        {
                            _stepsSinceTodoWrite = 0;
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

        return results;
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
        var (effectiveTokens, _) = EstimateContextTokens(AssembleContext(history, options), history);

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
                                   new ModelRunnerOptions { MaxAttempts = 3 },
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
        var compactedContext = AssembleContext(postCompact.Messages, options);
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

    private AssembledContext AssembleContext(IReadOnlyList<ConversationMessage> history, ModelCallOptions options) =>
        ContextBuilder.Assemble(new ContextAssemblyInput(
            CliPrefix: "Alcoler",
            SystemStable: _deps.Options.SystemPromptStable,
            SystemDynamic: BuildDynamicSystemPrompt(),
            AgentInstructions: _deps.Options.AgentInstructions,
            SkillsListing: _deps.Options.SkillsListing,
            History: history,
            Tools: _deps.Tools.VisibleContracts,
            Options: options));

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

    private string BuildDynamicSystemPrompt() =>
        $"Platform: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}. " +
        $"Current date: {_deps.Clock.GetUtcNow():yyyy-MM-dd}. " +
        $"Workspace root: {_record.WorkspaceRoot}.";

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
        _usageAnchorIndex = -1;
        if (messages.Count > 0)
        {
            _contextUsage = durable.Select(evt => evt.Event).OfType<ContextUsageUpdatedEvent>().LastOrDefault()?.Usage;
            var resumedSelection = _record.ModelSelection ?? _deps.Options.DefaultModel;
            if (resumedSelection is not null)
            {
                var descriptor = _deps.ResolveModel(resumedSelection).Descriptor;
                _contextModelId = descriptor.FullId;
                var resumed = AssembleContext(_history.Snapshot(), resumedSelection.Options ?? new ModelCallOptions());
                _contextUsage = new ContextUsageState(TokenEstimator.EstimateRequestTokens(resumed.Request),
                    descriptor.Properties.ContextWindowTokens ?? CompactPolicy.DefaultContextWindowTokens,
                    TokenEstimator.EstimateSource, resumed.Breakdown,
                    _contextUsage?.CacheReadTokens ?? 0, _contextUsage?.CacheInputTokens ?? 0);
                EmitForReplay(new ContextUsageUpdatedEvent(_contextUsage));
            }
        }
    }

    private void EmitForReplay(SessionEvent sessionEvent) => _events.Append(sessionEvent);

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
