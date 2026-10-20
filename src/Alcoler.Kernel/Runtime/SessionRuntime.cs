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
    SessionRuntimeOptions Options);

public sealed class SessionRuntime : IAsyncDisposable
{
    private const int StreamRecoveryMaxRetries = 10;
    private const int ReactiveCompactMaxRetries = 3;

    private readonly SessionRuntimeDeps _deps;
    private readonly SessionEventStore _events;
    private readonly MessageHistory _history = new();
    private readonly TurnMachine _turnMachine = new();
    private readonly SessionInteractionBroker _broker;
    private readonly PermissionService _permissions;
    private readonly ToolBatchRunner _toolRunner;
    private readonly RuntimeSessionCommands _sessionCommands;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCts = new();

    private SessionRecord _record;
    private long _revision;
    private Task _turnTask = Task.CompletedTask;
    private CancellationTokenSource? _turnCts;
    private readonly Queue<SendTextCommand> _inputQueue = new();
    private TokenUsage? _lastProviderUsage;
    private int _usageAnchorIndex = -1;
    private DateTimeOffset _lastActivityUtc;
    private int _consecutiveCompactFailures;
    private int _consecutiveRapidRefills;
    private int _toolRoundsSinceCompact;
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
            await EmitAsync(new TurnStartedEvent(turnId, command.CommandId), persist: true).ConfigureAwait(false);

            var userMessage = new ConversationMessage(
                StrongId.NewMessageId(),
                MessageRole.User,
                [new TextPart(StrongId.NewPartId(), command.Text)],
                _deps.Clock.GetUtcNow());
            await CommitMessageAsync(userMessage).ConfigureAwait(false);

            var model = _deps.ResolveModel(command.ModelSelection ?? _record.ModelSelection ?? _deps.Options.DefaultModel
                ?? throw new InvalidOperationException("No model configured for session."));

            var outcome = TurnOutcome.Completed;
            var reactiveCompactRetries = 0;
            while (true)
            {
                turnCt.ThrowIfCancellationRequested();

                var step = await RunModelStepAsync(turnId, model, turnCt).ConfigureAwait(false);
                if (step == ModelStepOutcome.Finished)
                {
                    break;
                }

                if (step == ModelStepOutcome.ContextOverflow && reactiveCompactRetries < ReactiveCompactMaxRetries)
                {
                    reactiveCompactRetries++;
                    var compacted = await ExecuteCompactionAsync(CompactTrigger.Reactive, null, turnCt).ConfigureAwait(false);
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
                    _ = EmitAsync(new InputDequeuedEvent(next.CommandId), persist: true);
                    StartTurnInternal(next);
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

    private async Task<ModelStepOutcome> RunModelStepAsync(TurnId turnId, IChatModel model, CancellationToken turnCt)
    {
        var history = _history.Snapshot();
        var contextWindow = model.Descriptor.Properties.ContextWindowTokens ?? CompactPolicy.DefaultContextWindowTokens;
        var threshold = CompactPolicy.AutoCompactThreshold(contextWindow);
        var (effectiveTokens, tokenSource) = TokenEstimator.EstimateCurrentInputTokens(history, _usageAnchorIndex, _lastProviderUsage);

        var now = _deps.Clock.GetUtcNow();
        var micro = MicroCompactor.Apply(history, _lastActivityUtc, now, threshold);
        if (micro.ClearedParts.Count > 0)
        {
            _history.ReplaceAll(micro.Messages);
            history = micro.Messages;
            effectiveTokens = TokenEstimator.EstimateTokens(string.Concat(history.SelectMany(m => m.Parts.OfType<TextPart>().Select(p => p.Text))));
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
            await ExecuteCompactionAsync(CompactTrigger.Auto, null, turnCt).ConfigureAwait(false);
            history = _history.Snapshot();
        }

        _turnMachine.TryTransition(TurnPhase.AwaitingModel);
        var request = ContextBuilder.Assemble(new ContextAssemblyInput(
            CliPrefix: "Alcoler",
            SystemStable: _deps.Options.SystemPromptStable,
            SystemDynamic: BuildDynamicSystemPrompt(),
            AgentInstructions: _deps.Options.AgentInstructions,
            SkillsListing: _deps.Options.SkillsListing,
            History: history,
            Tools: _deps.Tools.VisibleContracts,
            Options: _record.ModelSelection?.Options ?? new ModelCallOptions()));

        await EmitAsync(new ModelRequestStartedEvent(turnId, 1), persist: false).ConfigureAwait(false);

        var accumulator = new ModelStepAccumulator(turnId);
        await foreach (var streamEvent in ModelStreamRunner.RunAsync(model, request.Request, new ModelRunnerOptions(), turnCt)
                           .WithCancellation(turnCt).ConfigureAwait(false))
        {
            await accumulator.ApplyAsync(streamEvent, evt => EmitAsync(evt, persist: false)).ConfigureAwait(false);
        }

        _lastActivityUtc = _deps.Clock.GetUtcNow();

        if (accumulator.Failure is { } failure)
        {
            await EmitAsync(new ModelRequestFailedEvent(turnId, failure, 1), persist: false).ConfigureAwait(false);
            if (failure.Kind == ModelFailureKind.ContextOverflow)
            {
                return ModelStepOutcome.ContextOverflow;
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
            _usageAnchorIndex = _history.Count;
            await EmitAsync(new ModelRequestCompletedEvent(turnId, usage), persist: false).ConfigureAwait(false);
        }

        var assistant = accumulator.BuildAssistantMessage(_deps.Clock.GetUtcNow());
        if (assistant.Parts.Count == 0)
        {
            return ModelStepOutcome.Finished;
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
                _sessionCommands)),
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

    private async Task<bool> ExecuteCompactionAsync(CompactTrigger trigger, string? instructions, CancellationToken ct)
    {
        var history = _history.Snapshot();
        var model = _deps.ResolveModel(_record.ModelSelection ?? _deps.Options.DefaultModel
            ?? throw new InvalidOperationException("No model configured."));
        var contextWindow = model.Descriptor.Properties.ContextWindowTokens ?? CompactPolicy.DefaultContextWindowTokens;
        var (effectiveTokens, _) = TokenEstimator.EstimateCurrentInputTokens(history, _usageAnchorIndex, _lastProviderUsage);

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

        var summaryText = new System.Text.StringBuilder();
        var failed = false;
        try
        {
            await foreach (var streamEvent in ModelStreamRunner.RunAsync(
                               model,
                               plan.SummaryModelRequest,
                               new ModelRunnerOptions { MaxAttempts = 3 },
                               ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (streamEvent is ModelTextDelta textDelta)
                {
                    summaryText.Append(textDelta.Delta);
                }
                else if (streamEvent is ModelStreamFailed)
                {
                    failed = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            failed = true;
        }

        if (failed || summaryText.Length == 0)
        {
            _consecutiveCompactFailures++;
            _deps.Logger.LogWarning("Compaction ({Trigger}) failed after {Failures} consecutive attempts.", trigger, _consecutiveCompactFailures);
            return false;
        }

        _consecutiveCompactFailures = 0;
        _consecutiveRapidRefills = 0;
        _toolRoundsSinceCompact = 0;

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
        await EmitAsync(new CompactBoundaryAppliedEvent(postCompact.Boundary), persist: true).ConfigureAwait(false);
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
        _usageAnchorIndex = _history.Count;
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
