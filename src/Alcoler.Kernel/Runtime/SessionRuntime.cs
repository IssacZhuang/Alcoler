using System.Buffers.Text;
using System.Text;
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
using Alcoler.Kernel.Skills;
using Alcoler.Kernel.Tools;
using Microsoft.Extensions.Logging;

namespace Alcoler.Kernel.Runtime;

public sealed class SessionRuntimeOptions
{
    public string SystemPromptStable { get; init; } = PromptTexts.StableIdentity;

    public string? CustomSystemPrompt { get; init; }

    public string? AgentInstructions { get; init; }

    public string? SkillsListing { get; init; }

    public string? UserSkillsBase { get; init; }

    public string? ShellName { get; init; }

    public Func<ShellEnvironment?, ShellEnvironment>? ResolveShell { get; init; }

    internal Func<ShellEnvironment?, string>? CustomSystemPromptFactory { get; init; }

    public GitSnapshot? GitSnapshot { get; init; }

    public ModelSelection? DefaultModel { get; init; }

    public bool SessionTitleGenerationEnabled { get; init; }

    public TimeSpan SessionTitleGenerationTimeout { get; init; } = TimeSpan.FromSeconds(60);
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
    private const int MaxInputImages = 8;
    private const int MaxInputImageBytes = 20 * 1024 * 1024;
    private const int MaxInputImageBase64Characters = (MaxInputImageBytes + 2) / 3 * 4;
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
    private readonly ToolCallExecutor _toolExecutor;
    private readonly RuntimeSessionCommands _sessionCommands;
    private readonly FileReadState _fileReads = new();
    private readonly ShellSessionState _shell;
    private readonly BackgroundJobService _backgroundJobs;
    private readonly ISubagentTasks? _subagentTasks;
    private readonly SessionAuxModelClient _auxModel;
    private readonly SkillContentProvider _skills;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _recordLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCts = new();

    private SessionRecord _record;
    private long _revision;
    private Task _turnTask = Task.CompletedTask;
    private Task _titleTask = Task.CompletedTask;
    private bool _titleAttempted;
    private bool _shellInitialized;
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
    private int? _overflowTokenGap;
    private ModelSelection? _consumedGuideSelection;
    private bool _planModeExitReminderPending;
    private int _streamRecoveriesThisTurn;
    private ToolCallAnomalyTracker _toolAnomalyThisTurn = new();
    private readonly TurnRequestEntries _turnLocalRequestMessages = new();
    // -1 means no turn has finished yet in this session runtime.
    private int _lastTurnOutcome = -1;
    private bool _disposed;

    private SessionRuntime(SessionRecord record, SessionRuntimeDeps deps)
    {
        _deps = deps;
        _record = record;
        _events = new SessionEventStore(record.Id, deps.Clock);
        _broker = new SessionInteractionBroker(evt => EmitAsync(evt, persist: true), deps.Clock);
        _permissions = new PermissionService(_broker, deps.PermissionRules, record.PermissionMode);
        var scheduler = new ToolScheduler(deps.Tools);
        _toolExecutor = new ToolCallExecutor(deps.Tools, _permissions, _ => _permissions.Mode);
        _toolRunner = new ToolBatchRunner(scheduler, _toolExecutor);
        _sessionCommands = new RuntimeSessionCommands(this);
        _shell = new ShellSessionState(record.WorkspaceRoot, record.Shell);
        _backgroundJobs = new BackgroundJobService(deps.Clock, NotifyBackgroundJobCompletedAsync);
        _subagentTasks = deps.Tools.Tools.OfType<ISubagentTasks>().FirstOrDefault();
        _auxModel = new SessionAuxModelClient(() => _record.ModelSelection ?? deps.Options.DefaultModel, deps.ResolveModel);
        _skills = new SkillContentProvider(record.WorkspaceRoot, deps.Options.UserSkillsBase);
        _lastActivityUtc = deps.Clock.GetUtcNow();
        _isGitRepository = RuntimeEnvironmentFacts.IsGitRepository(record.WorkspaceRoot);
        _gitSnapshotSection = deps.Options.GitSnapshot is { } snapshot && _isGitRepository
            ? GitSnapshotSection.Render(snapshot, RuntimeEnvironmentFacts.Platform)
            : null;
    }

    public event Func<SessionRuntime, ValueTask>? TurnDrained;

    public event Action<SessionRuntime>? TurnStarted;

    public event Func<SessionRuntime, TurnOutcome, ValueTask>? TurnFinished;

    public event Action<SessionRuntime, string>? TitleChanged;

    internal ValueTask EmitSubagentStateAsync(SubagentState state, bool started) =>
        EmitAsync(
            started
                ? new SubagentStartedEvent(state)
                : new SubagentStatusChangedEvent(state),
            persist: true);

    public SessionId SessionId => _record.Id;

    public string WorkspaceRoot => _record.WorkspaceRoot;

    public string Title => _record.Title;

    public long Revision => Volatile.Read(ref _revision);

    public bool IsTurnActive => _turnMachine.IsBusy;

    internal ShellEnvironment? Shell => _shell.Environment ?? _record.Shell;

    internal string? LastTurnError { get; private set; }

    internal TurnPhase Phase => _turnMachine.Phase;

    internal SessionActivitySnapshot ActivitySnapshot()
    {
        var outcome = Volatile.Read(ref _lastTurnOutcome);
        return new SessionActivitySnapshot(Phase, outcome < 0 ? null : (TurnOutcome)outcome);
    }

    public static async Task<SessionRuntime> CreateAsync(SessionRecord record, SessionRuntimeDeps deps, CancellationToken ct, bool persistStartEvents = true)
    {
        var runtime = new SessionRuntime(record, deps);
        await runtime.ReplayHistoryAsync(persistStartEvents).ConfigureAwait(false);
        return runtime;
    }

    public IConversationProjection CreateProjection() => new LiveConversationProjection(_events, 0, () => Revision);

    internal ToolRegistry ToolRegistry => _deps.Tools;

    internal IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs() =>
        Volatile.Read(ref _disposed) ? [] : _backgroundJobs.List();

    internal bool StopBackgroundJob(BackgroundJobId jobId) =>
        !Volatile.Read(ref _disposed) && _backgroundJobs.Stop(jobId);

    internal ValueTask<bool> TryStopSubagentAsync(string taskId)
    {
        if (Volatile.Read(ref _disposed) || _subagentTasks is null)
            return ValueTask.FromResult(false);
        return _subagentTasks.TryStopAsync(taskId, CancellationToken.None);
    }

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
                    return await HandleSendTextAsync(sendText, ct).ConfigureAwait(false);

                case StopCommand stop:
                    _turnCts?.Cancel();
                    BumpRevision();
                    return CommandAck.AcceptedAt(stop.CommandId, Revision);

                case ResolveInteractionCommand resolve:
                    return HandleResolveInteraction(resolve);

                case CompactCommand compact:
                    return await HandleCompactAsync(compact, ct).ConfigureAwait(false);

                case SetModeCommand setMode:
                    SetPermissionMode(setMode.Mode);
                    await PersistRecordAsync(ct,
                        record => record with { PermissionMode = setMode.Mode, UpdatedAt = _deps.Clock.GetUtcNow() }).ConfigureAwait(false);
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
                    await PersistRecordAsync(ct,
                        record => record with { ModelSelection = switchModel.Selection, UpdatedAt = _deps.Clock.GetUtcNow() }).ConfigureAwait(false);
                    await EmitModelChangedAsync(switchModel.Selection).ConfigureAwait(false);
                    BumpRevision();
                    return CommandAck.AcceptedAt(switchModel.CommandId, Revision);

                case RenameSessionCommand rename:
                    await SetTitleAsync(rename.Title, SessionTitleSource.Custom, ct).ConfigureAwait(false);
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

    private async Task<CommandAck> HandleSendTextAsync(SendTextCommand sendText, CancellationToken ct)
    {
        sendText = sendText with { Text = sendText.Text ?? string.Empty };
        var images = sendText.Images?.ToArray();
        var rejection = Steering.ValidateInput(sendText.Text, images is { Length: > 0 }) ?? ValidateImages(images);
        if (rejection is not null)
        {
            return CommandAck.Rejected(sendText.CommandId, Revision, rejection);
        }

        var files = await StoreInputImagesAsync(images, ct).ConfigureAwait(false);
        var input = new PendingTurnInput(sendText with { Images = null }, null, files);
        if (_turnMachine.IsBusy)
        {
            await EnqueueInputAsync(input).ConfigureAwait(false);
            BumpRevision();
            return CommandAck.AcceptedAt(sendText.CommandId, Revision);
        }

        if (_pendingInputs.Count > 0)
        {
            // Inputs held by a cancelled/failed turn keep FIFO: the newcomer queues behind and
            // the oldest pending input drives the next turn.
            await EnqueueInputAsync(input).ConfigureAwait(false);
            StartTurnFor(DequeueOldestInput()!);
        }
        else
        {
            StartTurnFor(input);
        }

        BumpRevision();
        return CommandAck.AcceptedAt(sendText.CommandId, Revision);
    }

    private static string? ValidateImages(IReadOnlyList<InputImageBlock>? images)
    {
        if (images is null)
            return null;
        if (images.Count > MaxInputImages)
            return "too_many_images";
        foreach (var image in images)
        {
            if (image is null || ImageExtension(image.MimeType) is null)
                return "unsupported_image_mime_type";
            if (image.Base64Data is null || image.Base64Data.Length == 0)
                return "invalid_image_base64";
            if (image.Base64Data.Length > MaxInputImageBase64Characters)
                return "image_too_large";
            if (!Base64.IsValid(image.Base64Data, out var decodedLength) || decodedLength == 0)
                return "invalid_image_base64";
            if (decodedLength > MaxInputImageBytes)
                return "image_too_large";
        }
        return null;
    }

    private static string? ImageExtension(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => null,
    };

    private async Task<IReadOnlyList<FilePart>?> StoreInputImagesAsync(
        IReadOnlyList<InputImageBlock>? images, CancellationToken ct)
    {
        if (images is not { Count: > 0 })
            return null;
        var files = new List<FilePart>(images.Count);
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var artifact = await _deps.Artifacts.SaveAsync(_record.Id,
                $"image-{index + 1}.{ImageExtension(image.MimeType)}", image.MimeType.ToLowerInvariant(),
                Convert.FromBase64String(image.Base64Data), ct).ConfigureAwait(false);
            files.Add(new FilePart(StrongId.NewPartId(), artifact.Uri, artifact.MimeType, artifact.FileName));
        }
        return files;
    }

    private async Task EnqueueInputAsync(PendingTurnInput input)
    {
        if (input.Command is { } command)
        {
            await EmitAsync(new InputQueuedEvent(new QueuedInput(command.CommandId, command.Text,
                _deps.Clock.GetUtcNow(), input.Images, command.Mode, command.ModelSelection, command.Delivery)),
                persist: true).ConfigureAwait(false);
            BumpRevision();
        }
        _pendingInputs.Enqueue(input);
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

    // Task notifications cannot resume inputs held after cancellation or failure; only
    // explicit user input may restart that queue.
    private async ValueTask SubmitTaskNotificationAsync(string prefixedBody)
    {
        await _commandLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_turnMachine.IsBusy || _pendingInputs.Count > 0)
            {
                _pendingInputs.Enqueue(new PendingTurnInput(null, prefixedBody));
                return;
            }

            StartTurnFor(new PendingTurnInput(null, prefixedBody));
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
        TurnStarted?.Invoke(this);
        _turnTask = RunTurnAsync(input, turnCts.Token);
    }

    private async Task RunTurnAsync(PendingTurnInput input, CancellationToken turnCt)
    {
        var turnId = StrongId.NewTurnId();
        var holdQueue = false;
        var outcome = TurnOutcome.Completed;
        LastTurnError = null;
        try
        {
            var command = input.Command;
            if (command is not null)
                await ApplyInputIntentAsync(command).ConfigureAwait(false);
            var selection = command?.ModelSelection ?? _record.ModelSelection ?? _deps.Options.DefaultModel
                ?? throw new InvalidOperationException("No model configured for session.");
            var model = _deps.ResolveModel(selection);
            var options = selection.Options ?? new ModelCallOptions();
            model.ValidateOptions(options);
            await EnsureShellAsync(turnCt).ConfigureAwait(false);
            await EmitAsync(new TurnStartedEvent(turnId, command?.CommandId ?? StrongId.NewCommandId()), persist: true).ConfigureAwait(false);

            var openingMessage = input.NotificationBody is { } notificationBody
                ? BuildNotificationMessage(notificationBody)
                : BuildUserMessage(input);
            await CommitMessageAsync(openingMessage).ConfigureAwait(false);
            AppendSessionReferenceReminder(input);
            AppendDateReminder();

            var reactiveCompactAttempted = false;
            _outputContinuationsThisTurn = 0;
            _overflowTokenGap = null;
            _consumedGuideSelection = null;
            _streamRecoveriesThisTurn = 0;
            _toolAnomalyThisTurn = new ToolCallAnomalyTracker();
            _turnLocalRequestMessages.Clear();
            while (true)
            {
                turnCt.ThrowIfCancellationRequested();

                if (_consumedGuideSelection is { } guideSelection)
                {
                    model = _deps.ResolveModel(guideSelection);
                    options = guideSelection.Options ?? new ModelCallOptions();
                    _consumedGuideSelection = null;
                }
                var step = await RunModelStepAsync(turnId, model, options, turnCt).ConfigureAwait(false);
                turnCt.ThrowIfCancellationRequested();
                if (step == ModelStepOutcome.Stopped)
                    break;
                if (step == ModelStepOutcome.Finished)
                {
                    // A text-only step is a continuable boundary: one queued user input
                    // extends the same turn instead of waiting for a new one.
                    var (steered, _) = await DrainBoundaryAsync(drainNotifications: false, turnCt).ConfigureAwait(false);
                    if (steered)
                    {
                        _toolAnomalyThisTurn.ResetStreak();
                        reactiveCompactAttempted = false;
                        continue;
                    }
                    break;
                }

                if (step == ModelStepOutcome.ContextOverflow)
                {
                    if (reactiveCompactAttempted)
                    {
                        outcome = TurnOutcome.Failed;
                        break;
                    }
                    reactiveCompactAttempted = true;
                    var rapidRefill = NextRapidRefillCount();
                    if (rapidRefill >= CompactPolicy.MaxConsecutiveRapidRefills)
                    {
                        // The throw surfaces as a durable TurnFailedEvent carrying the exact
                        // breaker message, mirroring ZCode's structured compaction error.
                        throw new InvalidOperationException(CompactPolicy.RapidRefillBreakerMessage(
                            CompactPolicy.RapidRefillToolTurnThreshold,
                            CompactPolicy.MaxConsecutiveRapidRefills));
                    }
                    var compacted = await ExecuteCompactionAsync(CompactTrigger.Reactive, null, turnCt, model, options, _overflowTokenGap).ConfigureAwait(false);
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

                reactiveCompactAttempted = false;
                if (step == ModelStepOutcome.ContinuedOutput)
                {
                    continue;
                }

                // The tool-result boundary: queued notifications and one queued user input
                // join the running turn before the next model request.
                var (boundarySteered, boundaryNotified) = await DrainBoundaryAsync(drainNotifications: true, turnCt).ConfigureAwait(false);
                if (boundaryNotified || boundarySteered)
                {
                    _toolAnomalyThisTurn.ResetStreak();
                }
            }

            if (outcome != TurnOutcome.Completed)
            {
                holdQueue = true;
            }
            if (outcome == TurnOutcome.Failed)
                await EmitAsync(new TurnFailedEvent(turnId, LastTurnError ?? "Model request failed."), persist: true).ConfigureAwait(false);
            await EmitAsync(new TurnCompletedEvent(turnId, outcome, _lastProviderUsage), persist: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            holdQueue = true;
            outcome = TurnOutcome.Cancelled;
            await EmitAsync(new TurnCompletedEvent(turnId, outcome, _lastProviderUsage), persist: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            holdQueue = true;
            outcome = TurnOutcome.Failed;
            LastTurnError = ex.Message;
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
            Volatile.Write(ref _lastTurnOutcome, (int)outcome);
            if (TurnFinished is { } handlers)
            {
                foreach (Func<SessionRuntime, TurnOutcome, ValueTask> handler in handlers.GetInvocationList())
                {
                    await handler(this, outcome).ConfigureAwait(false);
                }
            }
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
    private async Task<(bool Steered, bool Notified)> DrainBoundaryAsync(bool drainNotifications, CancellationToken turnCt)
    {
        PendingTurnInput? steer = null;
        List<string>? notificationBodies = null;
        await _commandLock.WaitAsync(turnCt).ConfigureAwait(false);
        try
        {
            turnCt.ThrowIfCancellationRequested();
            List<PendingTurnInput>? held = null;
            while (_pendingInputs.Count > 0)
            {
                var next = _pendingInputs.Dequeue();
                if (next.Command is { } command)
                {
                    if (steer is null && command.Delivery == SendTextDelivery.Guide)
                    {
                        steer = next;
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
            await ApplyInputIntentAsync(steer.Command!).ConfigureAwait(false);
            _consumedGuideSelection = steer.Command!.ModelSelection;
            await CommitMessageAsync(BuildUserMessage(steer, MessagePresentation.UserSteer)).ConfigureAwait(false);
        }

        return (steer is not null, notificationBodies is { Count: > 0 });
    }

    private void SetPermissionMode(PermissionMode mode)
    {
        if (_permissions.Mode == PermissionMode.Plan && mode != PermissionMode.Plan)
            _planModeExitReminderPending = true;
        else if (mode == PermissionMode.Plan)
            _planModeExitReminderPending = false;
        _permissions.SetMode(mode);
    }

    private async Task ApplyInputIntentAsync(SendTextCommand command)
    {
        if (command.Mode is { } mode)
        {
            SetPermissionMode(mode);
            await PersistRecordAsync(_sessionCts.Token, record => record with { PermissionMode = mode }).ConfigureAwait(false);
            await EmitAsync(new SessionModeChangedEvent(mode), persist: true).ConfigureAwait(false);
        }
        if (command.ModelSelection is { } selection)
        {
            _deps.ResolveModel(selection).ValidateOptions(selection.Options ?? new ModelCallOptions());
            await PersistRecordAsync(_sessionCts.Token, record => record with { ModelSelection = selection }).ConfigureAwait(false);
            await EmitModelChangedAsync(selection).ConfigureAwait(false);
        }
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

    private ConversationMessage BuildUserMessage(PendingTurnInput input, MessagePresentation? presentation = null)
    {
        var parts = new List<MessagePart>();
        if (!string.IsNullOrWhiteSpace(input.Command!.Text))
            parts.Add(new TextPart(StrongId.NewPartId(), input.Command.Text));
        else if (presentation == MessagePresentation.UserSteer)
            parts.Add(new TextPart(StrongId.NewPartId(), "[Attached images]"));
        if (input.Images is { } images)
            parts.AddRange(images);
        return new ConversationMessage(StrongId.NewMessageId(), MessageRole.User, parts,
            _deps.Clock.GetUtcNow(), Source: MessageSource.RealUser, Presentation: presentation);
    }

    private ConversationMessage BuildNotificationMessage(string prefixedBody) =>
        new(
            StrongId.NewMessageId(),
            MessageRole.User,
            [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, SystemReminder.Wrap(prefixedBody), ProviderVisible: true)],
            _deps.Clock.GetUtcNow(), Source: MessageSource.TaskNotification);

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

        if (_outputContinuationsThisTurn == 0)
        {
            await CommitRuntimeRemindersAsync(history).ConfigureAwait(false);
            history = _history.Snapshot();
        }

        _turnMachine.TryTransition(TurnPhase.AwaitingModel);
        var request = await AssembleContextAsync(_turnLocalRequestMessages.Merge(history), initialRequest.Request.Options, model, turnCt).ConfigureAwait(false);
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
        await PublishContextUsageAsync(request, requestTokens, contextWindow, source).ConfigureAwait(false);
        var requestAnchorIndex = history.Count;

        await using var streamingTools = new StreamingToolCoordinator(
            _deps.Tools, _toolExecutor, _toolRunner,
            (call, _) => ValueTask.FromResult(CreateToolExecutionContext(call)),
            notification => HandleToolNotificationAsync(turnId, notification), turnCt);
        var accumulator = await StreamModelStepAsync(turnId, model, request, streamingTools, turnCt).ConfigureAwait(false);

        _lastActivityUtc = _deps.Clock.GetUtcNow();

        if (accumulator.Failure is { } failure)
        {
            if (streamingTools.HasAcceptedCalls && !turnCt.IsCancellationRequested
                && _streamRecoveriesThisTurn < StreamRecoveryMaxRetries)
            {
                _streamRecoveriesThisTurn++;
                await EmitAsync(new StreamRecoveryEvent(StreamRecoveryPhase.AnchorSelected, _streamRecoveriesThisTurn), persist: false).ConfigureAwait(false);
                var recoveredBatch = await streamingTools.RecoverAsync(accumulator.ToolCalls).ConfigureAwait(false);
                var recoveredAssistant = new ConversationMessage(
                    StrongId.NewMessageId(), MessageRole.Assistant,
                    [.. streamingTools.AcceptedCalls.Select(call => new ToolCallPart(
                        StrongId.NewPartId(), call.Id, call.Name, call.Input))],
                    _deps.Clock.GetUtcNow(), model.Descriptor.ProviderId, model.Descriptor.ModelId);
                await CommitMessageAsync(recoveredAssistant).ConfigureAwait(false);
                await CommitToolBatchAsync(recoveredBatch).ConfigureAwait(false);
                _toolRoundsSinceCompact++;
                _outputContinuationsThisTurn = 0;
                await EmitAsync(new StreamRecoveryEvent(StreamRecoveryPhase.TailDiscarded, _streamRecoveriesThisTurn), persist: false).ConfigureAwait(false);
                await EmitAsync(new StreamRecoveryEvent(StreamRecoveryPhase.RetryStarted, _streamRecoveriesThisTurn), persist: false).ConfigureAwait(false);
                return recoveredBatch.StoppedAfterResult ? ModelStepOutcome.Stopped : ModelStepOutcome.ContinueWithTools;
            }
            LastTurnError = failure.Message;
            await EmitAsync(new ModelRequestFailedEvent(turnId, failure, 1), persist: false).ConfigureAwait(false);
            if (failure.Kind == ModelFailureKind.ContextOverflow)
            {
                _overflowTokenGap = failure.DeclaredTokenGap;
                return ModelStepOutcome.ContextOverflow;
            }
            if (accumulator.HasBoundaryContent)
            {
                await CommitPartialAssistantAsync(accumulator, model.Descriptor).ConfigureAwait(false);
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

        var assistant = accumulator.BuildAssistantMessage(_deps.Clock.GetUtcNow()) with
        {
            ProviderId = model.Descriptor.ProviderId,
            ModelId = model.Descriptor.ModelId,
        };

        // Output-limit truncation classifies first: even an empty truncated body continues,
        // and the empty body itself is never committed (ZCode includeEmptyAssistant: false).
        if (accumulator.FinishReason == ModelFinishReason.Length
            && accumulator.ToolCalls.Count == 0)
        {
            if (_outputContinuationsThisTurn >= OutputContinuation.MaxContinuationsPerTurn)
            {
                LastTurnError = OutputContinuation.LimitExceededMessage;
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
            _turnLocalRequestMessages.AppendAfter(_history.Snapshot()[^1].Id, new ConversationMessage(
                StrongId.NewMessageId(), MessageRole.User,
                [new TextPart(StrongId.NewPartId(), OutputContinuation.ContinuePromptText)],
                _deps.Clock.GetUtcNow()));
            _outputContinuationsThisTurn++;
            return ModelStepOutcome.ContinuedOutput;
        }

        _outputContinuationsThisTurn = 0;

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
            LastTurnError = "Model returned no text, no tool calls, and no usage before completing the turn.";
            await EmitAsync(
                new ModelRequestFailedEvent(
                    turnId,
                    new ModelFailure(ModelFailureKind.ProviderError, LastTurnError),
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
                AppendInMemoryReminder(warning, MessageSource.ModelAnomaly);
            }
        }

        _toolRoundsSinceCompact++;
        _turnMachine.TryTransition(TurnPhase.ExecutingTools);
        var toolBatch = await streamingTools.CompleteAsync(accumulator.ToolCalls).ConfigureAwait(false);
        await CommitToolBatchAsync(toolBatch).ConfigureAwait(false);

        if (turnCt.IsCancellationRequested)
        {
            // The cancelled batch's results are committed, but the turn must not drain any
            // queued input at its boundary: cancellation holds the queue.
            return ModelStepOutcome.Cancelled;
        }

        if (toolBatch.StoppedAfterResult)
        {
            // Stop requests apply to error results too; guides remain queued for a new turn.
            _toolRoundsSinceCompact = 0;
            _turnMachine.TryTransition(TurnPhase.Completing);
            return ModelStepOutcome.Stopped;
        }

        return ModelStepOutcome.ContinueWithTools;
    }

    private async Task<ModelStepAccumulator> StreamModelStepAsync(
        TurnId turnId, IChatModel model, AssembledContext request, StreamingToolCoordinator streamingTools, CancellationToken turnCt)
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
                    if (streamEvent is ModelToolCallReady ready)
                        await streamingTools.AcceptAsync(ready.ToolCall with { Name = ready.ToolCall.Name.Trim() }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (turnCt.IsCancellationRequested)
            {
                // A cancelled stream still owns its partial text: commit it before the
                // cancellation surfaces as TurnCompleted(Cancelled).
                await streamingTools.AbandonAsync().ConfigureAwait(false);
                await CommitPartialAssistantAsync(accumulator, model.Descriptor).ConfigureAwait(false);
                throw;
            }

            // A transient failure after boundary content is recovered in-turn by discarding
            // the partial accumulator and re-running the request fresh, up to the budget;
            // each recovery grows the idle timeout for the next attempt.
            if (accumulator.Failure is { IsRetryable: true }
                && accumulator.HasBoundaryContent
                && !streamingTools.HasAcceptedCalls
                && _streamRecoveriesThisTurn < StreamRecoveryMaxRetries)
            {
                _streamRecoveriesThisTurn++;
                await EmitAsync(new StreamRecoveryEvent(StreamRecoveryPhase.AnchorSelected, _streamRecoveriesThisTurn), persist: false).ConfigureAwait(false);
                await EmitAsync(new StreamRecoveryEvent(StreamRecoveryPhase.TailDiscarded, _streamRecoveriesThisTurn), persist: false).ConfigureAwait(false);
                await EmitAsync(new StreamRecoveryEvent(StreamRecoveryPhase.RetryStarted, _streamRecoveriesThisTurn), persist: false).ConfigureAwait(false);
                continue;
            }

            if (accumulator.Failure is not null
                || accumulator.HasBoundaryContent
                || accumulator.ToolCalls.Count > 0
                || accumulator.FinishReason == ModelFinishReason.Length
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

    // Mode reminders live in canonical memory; only todo reminders are durable.
    private async Task CommitRuntimeRemindersAsync(IReadOnlyList<ConversationMessage> history)
    {
        if (_planModeExitReminderPending)
        {
            _planModeExitReminderPending = false;
            AppendInMemoryReminder(RuntimeReminders.PlanModeExitReminder);
        }

        if (_permissions.Mode == PermissionMode.Plan)
        {
            var (foundReminder, humanTurnsSince) = ScanPlanReminderCadence(history);
            if (!foundReminder || humanTurnsSince >= RuntimeReminders.TurnsBetweenAttachments)
            {
                var attachmentIndex = CountPlanReminders(history) + 1;
                AppendInMemoryReminder(RuntimeReminders.BuildModeReminder(attachmentIndex));
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
            _deps.Clock.GetUtcNow(), Source: MessageSource.TodoReminder)).ConfigureAwait(false);
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

    private static bool IsRealUserTurn(ConversationMessage message) =>
        message.Role == MessageRole.User
        && (message.Source == MessageSource.RealUser
            || message.Source is null
            && !message.Parts.Any(p => p is ToolResultPart or AttachmentPart)
            && message.Parts.OfType<TextPart>().Any());

    private ToolExecutionContext CreateToolExecutionContext(ToolCall call) =>
        new(_record.Id, _record.WorkspaceRoot, call.Input, _deps.Workspace, _deps.Clock,
            _sessionCommands, _fileReads, _shell, _backgroundJobs, _auxModel, _skills, _broker,
            call.Id, Subagents: _subagentTasks);

    private async ValueTask HandleToolNotificationAsync(TurnId turnId, ToolExecutionNotification notification)
    {
        switch (notification)
        {
            case ToolExecutionStarted started:
                await EmitAsync(new ToolCallStartedEvent(turnId, started.Call.Id, started.Call.Name, started.Call.Input), persist: true).ConfigureAwait(false);
                break;
            case ToolExecutionResultNotification result:
                await EmitAsync(new ToolCallResultEvent(turnId, result.Call.Id, result.Call.Name, result.Result, result.Duration), persist: true).ConfigureAwait(false);
                if (!result.Result.IsError
                    && string.Equals(result.Call.Name, "Read", StringComparison.OrdinalIgnoreCase)
                    && result.Result.Content.OfType<ToolResultText>().FirstOrDefault() is { } readText
                    && result.Call.Input.TryGetProperty("file_path", out var pathElement)
                    && pathElement.ValueKind == JsonValueKind.String
                    && pathElement.GetString() is { } path)
                {
                    var offset = result.Call.Input.TryGetProperty("offset", out var offsetElement)
                        && offsetElement.ValueKind == JsonValueKind.Number && offsetElement.TryGetInt32(out var offsetValue)
                        ? offsetValue : (int?)null;
                    var limit = result.Call.Input.TryGetProperty("limit", out var limitElement)
                        && limitElement.ValueKind == JsonValueKind.Number && limitElement.TryGetInt32(out var limitValue)
                        ? limitValue : (int?)null;
                    _history.RecordFileRead(path, readText.Text, _deps.Clock.GetUtcNow(), offset, limit);
                }
                break;
        }
    }

    private async Task CommitToolBatchAsync(ToolBatchResult batch)
    {
        if (batch.Outcomes.Count == 0)
            return;
        var results = batch.Outcomes.Select(outcome => new ToolResultPart(
            StrongId.NewPartId(), outcome.Call.Id, outcome.Result.Content, outcome.Result.IsError, outcome.Result.StructuredOutput));
        await CommitMessageAsync(new ConversationMessage(
            StrongId.NewMessageId(), MessageRole.User, [.. results], _deps.Clock.GetUtcNow())).ConfigureAwait(false);
    }

    private async Task<bool> ExecuteCompactionAsync(CompactTrigger trigger, string? instructions, CancellationToken ct,
        IChatModel? frozenModel = null, ModelCallOptions? frozenOptions = null, int? declaredTokenGap = null)
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
            ManualInstructions: instructions,
            DeclaredTokenGap: declaredTokenGap));
        if (!CompactionSelector.HasEnoughMessagesToCompact(plan.Selection.ToSummarize))
            return false;

        await EnsureShellAsync(ct).ConfigureAwait(false);
        var maxAttempts = trigger == CompactTrigger.Auto ? CompactPolicy.AutoCompactMaxAttempts : 1;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var persisted = false;
            try
            {
                var failure = await ApplyCompactionAttemptAsync(plan, history, instructions, model, options, contextWindow,
                    () => persisted = true, ct).ConfigureAwait(false);
                if (failure is null)
                    return true;
                LastTurnError = failure.Message;
                if (!IsCompactRetryableFailure(failure))
                    break;
            }
            catch (Exception exception)
            {
                var failure = ModelFailureClassifier.FromException(exception, ct);
                LastTurnError = failure.Message;
                if (persisted)
                    return true;
                if (!IsCompactRetryableFailure(failure))
                    break;
            }
        }

        _consecutiveCompactFailures++;
        _deps.Logger.LogWarning("Compaction ({Trigger}) failed after {Failures} consecutive attempts.", trigger, _consecutiveCompactFailures);
        return false;
    }

    private static bool IsCompactRetryableFailure(ModelFailure failure) =>
        !CompactSummaryModelRunner.IsMediaSizeFailure(failure)
        && (failure.IsRetryable || failure.Kind == ModelFailureKind.StreamProtocolError);

    private async Task<ModelFailure?> ApplyCompactionAttemptAsync(CompactionPlan plan, IReadOnlyList<ConversationMessage> history,
        string? instructions, IChatModel model, ModelCallOptions options, int contextWindow, Action markPersisted, CancellationToken ct)
    {
        var compactionSelection = plan.Selection;
        var trigger = plan.Trigger;
        var summaryText = string.Empty;
        ModelFailure? failure = null;
        var promptTooLongRetries = 0;
        var stripMediaForSummary = false;
        while (true)
        {
            var rawRequest = CompactionPlanner.BuildSummaryRequest(compactionSelection, instructions,
                plan.ContextPrefixMessages, plan.SummaryModelRequest.Tools);
            var summaryHistory = new List<ConversationMessage>(plan.ContextPrefixMessages);
            if (compactionSelection.TruncationMarker is { } marker)
            {
                summaryHistory.Add(new ConversationMessage(StrongId.NewMessageId(), MessageRole.User,
                    [new TextPart(StrongId.NewPartId(), marker)], _deps.Clock.GetUtcNow()));
            }
            summaryHistory.AddRange(compactionSelection.ToSummarize);
            var summaryLimit = Math.Min(CompactPolicy.SummaryMaxOutputTokens,
                model.Descriptor.Properties.DefaultMaxOutputTokens ?? DefaultRequestMaxOutputTokens);
            var summaryOptions = options with { MaxOutputTokens = summaryLimit };
            var summaryContext = await AssembleContextAsync(summaryHistory, summaryOptions, model, ct).ConfigureAwait(false);
            var summaryRequest = rawRequest with
            {
                Messages = [.. summaryContext.Request.Messages, rawRequest.Messages[^1]],
                Options = summaryOptions,
            };
            if (stripMediaForSummary)
                summaryRequest = CompactSummaryModelRunner.ProjectMediaForRetry(summaryRequest);
            var result = await CompactSummaryModelRunner.RunAsync(model, summaryRequest, ct).ConfigureAwait(false);
            summaryText = result.Text;
            failure = result.Failure;
            if (failure is not null && failure.Kind != ModelFailureKind.Cancelled && !ct.IsCancellationRequested
                && !stripMediaForSummary && CompactSummaryModelRunner.IsMediaSizeFailure(failure))
            {
                stripMediaForSummary = true;
                continue;
            }
            if (failure is { Kind: ModelFailureKind.ContextOverflow } overflow)
            {
                var reselected = trigger == CompactTrigger.Manual
                    ? CompactionSelector.TruncateForPromptTooLong(compactionSelection, overflow.DeclaredTokenGap, promptTooLongRetries)
                    : CompactionSelector.ReselectForPromptTooLong(history, compactionSelection, overflow.DeclaredTokenGap, trigger);
                if (!ReferenceEquals(reselected, compactionSelection))
                {
                    compactionSelection = reselected;
                    promptTooLongRetries++;
                    continue;
                }
                failure = overflow with
                {
                    Message = "Conversation too long to compact automatically. Try /compact again after narrowing the active context.",
                };
            }
            break;
        }

        var formattedSummary = SummarizationPrompt.FormatSummary(summaryText);
        if (failure is not null)
            return failure;
        if (formattedSummary.Length == 0)
            return new ModelFailure(ModelFailureKind.ProviderError, "Failed to generate compact summary");

        plan = plan with { Selection = compactionSelection };

        var summaryMessage = CompactionPlanner.CreateCompactSummaryMessage(formattedSummary);
        var recentlyRead = _history.RecentReads()
            .OrderByDescending(read => read.ReadAt)
            .ToList();
        var attachments = CompactionPlanner.BuildRecentlyReadFilesAttachments(recentlyRead, plan.Selection.PreservedTail);
        var postCompact = CompactionPlanner.BuildPostCompactHistory(
            plan,
            formattedSummary,
            summaryMessage,
            plan.Selection.PreservedTail,
            attachments);
        await _deps.Store.ReplaceMessagesAsync(_record.Id, postCompact.Messages, ct).ConfigureAwait(false);
        markPersisted();
        _history.ReplaceAll(postCompact.Messages);
        _history.ClearRecentReads();
        _consecutiveCompactFailures = 0;
        _consecutiveRapidRefills = 0;
        _toolRoundsSinceCompact = 0;
        _lastProviderUsage = null;
        _usageAnchorIndex = -1;
        _contextUsage = null;
        await EmitAsync(new CompactBoundaryAppliedEvent(postCompact.Boundary), persist: true).ConfigureAwait(false);
        var compactedContext = await AssembleContextAsync(postCompact.Messages, options, model, ct).ConfigureAwait(false);
        await PublishContextUsageAsync(compactedContext, TokenEstimator.EstimateRequestTokens(compactedContext.Request),
            contextWindow, TokenEstimator.EstimateSource).ConfigureAwait(false);
        return null;
    }

    private async Task CommitMessageAsync(ConversationMessage message)
    {
        _history.Append(message);
        var sequence = await _deps.Store.GetNextMessageSequenceAsync(_record.Id, _sessionCts.Token).ConfigureAwait(false);
        await _deps.Store.AppendMessagesAsync(_record.Id, [message], sequence, _sessionCts.Token).ConfigureAwait(false);
        await EmitAsync(MessageCommitted(message), persist: true).ConfigureAwait(false);
        await PersistRecordAsync(_sessionCts.Token,
            record => record with { UpdatedAt = _deps.Clock.GetUtcNow() }).ConfigureAwait(false);
        if (!_titleAttempted && IsRealUserTurn(message))
            await InitializeTitleAsync(message).ConfigureAwait(false);
    }

    private async Task InitializeTitleAsync(ConversationMessage message)
    {
        _titleAttempted = true;
        if (_record.ParentSessionId is not null || _record.TitleSource != SessionTitleSource.Default)
            return;

        var input = string.Join(" ", message.Parts.OfType<TextPart>().Select(part => part.Text));
        await SetTitleAsync(SessionTitleGenerator.FirstInputTitle(input), SessionTitleSource.FirstInput, _sessionCts.Token)
            .ConfigureAwait(false);
        var normalized = SessionTitleGenerator.NormalizeInput(input);
        if (_deps.Options.SessionTitleGenerationEnabled && _record.TitleSource == SessionTitleSource.FirstInput
            && normalized.EnumerateRunes().Count() >= 10)
            _titleTask = Task.Run(() => GenerateTitleAsync(normalized));
    }

    private async Task GenerateTitleAsync(string input)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        timeout.CancelAfter(_deps.Options.SessionTitleGenerationTimeout);
        try
        {
            var title = await SessionTitleGenerator.GenerateAsync(_auxModel, input, timeout.Token).ConfigureAwait(false);
            if (title is not null)
                await SetTitleAsync(title, SessionTitleSource.Generated, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_sessionCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _deps.Logger.LogWarning(exception, "Session title generation failed for {SessionId}.", SessionId);
        }
    }

    private async Task SetTitleAsync(string title, SessionTitleSource source, CancellationToken ct)
    {
        await _recordLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (source != SessionTitleSource.Custom && (_disposed || _record.TitleSource == SessionTitleSource.Custom))
                return;
            if (!await _deps.Store.TryUpdateSessionTitleAsync(SessionId, title, source, ct).ConfigureAwait(false))
                return;
            _record = _record with { Title = title, TitleSource = source };
            await EmitAsync(new SessionTitleChangedEvent(title), persist: true).ConfigureAwait(false);
            BumpRevision();
            TitleChanged?.Invoke(this, title);
        }
        finally
        {
            _recordLock.Release();
        }
    }

    private async Task CommitPartialAssistantAsync(ModelStepAccumulator accumulator, ModelDescriptor descriptor)
    {
        var partial = accumulator.BuildPartialAssistantMessage(_deps.Clock.GetUtcNow()) with
        {
            ProviderId = descriptor.ProviderId,
            ModelId = descriptor.ModelId,
        };
        if (partial.Parts.Count > 0)
        {
            await CommitMessageAsync(partial).ConfigureAwait(false);
        }
    }

    private async Task<AssembledContext> AssembleContextAsync(
        IReadOnlyList<ConversationMessage> history, ModelCallOptions options, IChatModel? model = null,
        CancellationToken cancellationToken = default)
    {
        var custom = _deps.Options.CustomSystemPromptFactory?.Invoke(Shell) ?? _deps.Options.CustomSystemPrompt;
        var currentDate = EnvironmentSections.FormatLocalIsoDate(_deps.Clock.GetLocalNow().LocalDateTime);
        var media = await LoadSessionMediaAsync(history, cancellationToken).ConfigureAwait(false);
        var skillsListing = _deps.Tools.VisibleContracts.Any(tool => tool.Name == "Skill")
            ? _deps.Options.SkillsListing ?? SkillsListing.Render(new SkillsCatalog(_deps.Options.UserSkillsBase).Load(_record.WorkspaceRoot))
            : null;
        return ContextBuilder.Assemble(new ContextAssemblyInput(
            CliPrefix: PromptTexts.CliPrefix,
            SystemStable: custom is { Length: > 0 } body ? "\n" + body.Trim() : _deps.Options.SystemPromptStable,
            SystemDynamic: custom is { Length: > 0 } ? null : BuildDynamicSystemSections(model, skillsListing),
            AgentInstructions: _deps.Options.AgentInstructions,
            SkillsListing: skillsListing,
            History: history,
            Tools: _deps.Tools.VisibleContracts,
            Options: options,
            CurrentDate: currentDate,
            Media: media,
            InlineToolResultImages: model?.InlineToolResultImages ?? false,
            UseMidConversationSystem: model?.Descriptor.Properties.SupportsMidConversationSystem ?? false));
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
    private string? BuildDynamicSystemSections(IChatModel? model, string? skillsListing)
    {
        var sections = new List<string>
        {
            PromptTexts.DynamicBehavior,
        };
        if (PromptTexts.SessionSpecificGuidance(
                skillToolAvailable: _deps.Tools.VisibleContracts.Any(tool => tool.Name == "Skill"),
                hasSkills: !string.IsNullOrEmpty(skillsListing)) is { } guidance)
        {
            sections.Add(guidance);
        }
        sections.Add(EnvironmentSections.EnvironmentSection(new EnvFacts(
            _record.WorkspaceRoot,
            _isGitRepository,
            RuntimeEnvironmentFacts.Platform,
            Shell?.Name ?? _deps.Options.ShellName ?? RuntimeEnvironmentFacts.Shell,
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

    private void AppendDateReminder()
    {
        var today = EnvironmentSections.FormatLocalIsoDate(_deps.Clock.GetLocalNow().LocalDateTime);
        if (_lastSeenLocalDate is not null && _lastSeenLocalDate != today)
            AppendInMemoryReminder(EnvironmentSections.DateChangeReminder(today), MessageSource.DateChange);
        _lastSeenLocalDate = today;
    }

    // Real user turns only: notification turns and mid-turn Guide input must not inject it.
    private void AppendSessionReferenceReminder(PendingTurnInput input)
    {
        if (input.NotificationBody is not null || string.IsNullOrWhiteSpace(input.Command?.Text))
            return;
        if (SessionReferenceReminder.BuildBody(input.Command.Text) is { } body)
            AppendInMemoryReminder(body, MessageSource.SessionReference);
    }

    private void AppendInMemoryReminder(string body, MessageSource source = MessageSource.RuntimeMode) =>
        _history.Append(new ConversationMessage(
            StrongId.NewMessageId(), MessageRole.User,
            [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, SystemReminder.Wrap(body), ProviderVisible: true)],
            _deps.Clock.GetUtcNow(), Source: source));

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

    private async Task EnsureShellAsync(CancellationToken ct)
    {
        if (_shellInitialized)
            return;

        var environment = _deps.Options.ResolveShell?.Invoke(_record.Shell) ?? _record.Shell;
        if (environment is not null)
        {
            if (environment != _record.Shell)
            {
                await PersistRecordAsync(ct, record => record with { Shell = environment }).ConfigureAwait(false);
            }
            _shell.SetEnvironment(environment);
        }
        _shellInitialized = true;
    }

    private async Task PersistRecordAsync(CancellationToken ct, Func<SessionRecord, SessionRecord> update)
    {
        await _recordLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _record = update(_record);
            await _deps.Store.UpsertSessionAsync(_record, ct).ConfigureAwait(false);
        }
        finally
        {
            _recordLock.Release();
        }
    }

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
        foreach (var (sessionEvent, occurredAt) in ReconstructTimeline(messages, durable))
        {
            _events.Append(sessionEvent, occurredAt);
        }
        var queuedInputs = new Dictionary<CommandId, (long Sequence, QueuedInput Input)>();
        foreach (var entry in durable)
        {
            switch (entry.Event)
            {
                case InputQueuedEvent queued:
                    queuedInputs[queued.Input.CommandId] = (entry.Sequence, queued.Input);
                    break;
                case InputDequeuedEvent dequeued:
                    queuedInputs.Remove(dequeued.CommandId);
                    break;
                case InputDiscardedEvent discarded:
                    queuedInputs.Remove(discarded.CommandId);
                    break;
            }
        }
        foreach (var (_, queued) in queuedInputs.Values.OrderBy(entry => entry.Sequence))
        {
            var command = new SendTextCommand(queued.CommandId, _record.Id, 0, queued.Text,
                queued.Mode, queued.ModelSelection, queued.Delivery);
            _pendingInputs.Enqueue(new PendingTurnInput(command, null, queued.Images));
            EmitForReplay(new InputQueuedEvent(queued));
        }
        _history.AppendRange(messages.Select(m => m.Message).ToList());
        _titleAttempted = messages.Any(message => IsRealUserTurn(message.Message));
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

    private static IEnumerable<(SessionEvent Event, DateTimeOffset OccurredAt)> ReconstructTimeline(
        IReadOnlyList<StoredMessage> messages, IReadOnlyList<SequencedEvent> durable)
    {
        var canonical = messages.ToDictionary(stored => stored.Message.Id, stored => stored.Message);
        var toolCalls = messages.SelectMany(stored => stored.Message.Parts).OfType<ToolCallPart>()
            .Select(call => call.ToolCallId).ToHashSet();
        var timeline = new List<(SessionEvent Event, DateTimeOffset OccurredAt)>();
        var anchors = new Dictionary<MessageId, int>();
        foreach (var entry in durable.OrderBy(entry => entry.Sequence))
        {
            var committed = entry.Event switch
            {
                UserMessageCommittedEvent user => user.Message,
                AssistantMessageCommittedEvent assistant => assistant.Message,
                _ => null,
            };
            if (committed is not null)
            {
                // Compaction can replace or remove messages without rewriting old envelopes.
                if (canonical.TryGetValue(committed.Id, out var message) && anchors.TryAdd(message.Id, timeline.Count))
                    timeline.Add((MessageCommitted(message), entry.OccurredAt));
            }
            else if (entry.Event is SubagentStartedEvent or SubagentStatusChangedEvent or TurnFailedEvent or TurnCompletedEvent
                || entry.Event is ToolCallStartedEvent started && toolCalls.Contains(started.ToolCallId)
                || entry.Event is ToolCallResultEvent result && toolCalls.Contains(result.ToolCallId))
            {
                timeline.Add((entry.Event, entry.OccurredAt));
            }
        }

        var nextAnchors = new int[messages.Count];
        var nextAnchor = timeline.Count;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (anchors.TryGetValue(messages[index].Message.Id, out var anchor))
                nextAnchor = anchor;
            nextAnchors[index] = nextAnchor;
        }

        var eventIndex = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index].Message;
            if (anchors.TryGetValue(message.Id, out var anchor))
            {
                while (eventIndex <= anchor)
                    yield return timeline[eventIndex++];
            }
            else
            {
                // Legacy messages have no shared sequence; ties favor messages, bounded by exact anchors.
                while (eventIndex < nextAnchors[index] && timeline[eventIndex].OccurredAt < message.CreatedAt)
                    yield return timeline[eventIndex++];
                yield return (MessageCommitted(message), message.CreatedAt);
            }
        }
        while (eventIndex < timeline.Count)
            yield return timeline[eventIndex++];
    }

    private static SessionEvent MessageCommitted(ConversationMessage message) => message.Role == MessageRole.User
        ? new UserMessageCommittedEvent(message)
        : new AssistantMessageCommittedEvent(message);

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
        await EmitAsync(new UserMessageCommittedEvent(repairMessage), persist: true).ConfigureAwait(false);
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
            await _titleTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch
        {
        }
        _sessionCts.Dispose();
        _events.DropAll();
        _commandLock.Dispose();
        _recordLock.Dispose();
    }

    private enum ModelStepOutcome
    {
        ContinueWithTools,
        Finished,
        Stopped,
        Failed,
        Cancelled,
        ContextOverflow,
        ContinuedOutput,
    }

    // A pending turn input is either a real user command or a task notification body that
    // already carries the system-notification prefix.
    private sealed record PendingTurnInput(
        SendTextCommand? Command, string? NotificationBody, IReadOnlyList<FilePart>? Images = null);

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
