using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Prompts;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tools;
using Microsoft.Extensions.Logging;

namespace Alcoler.Kernel.Subagents;

public static class SubagentWiring
{
    private static readonly string[] ChildHiddenTools = ["Agent", "Task", "SendMessage", "RespondToCoordinator", "ExitPlanMode"];

    public static SubagentTool CreateTool(Func<AgentRuntime> runtime, SubagentMessenger? messenger = null)
    {
        var tasks = new SubagentTaskRegistry();
        return new(
            new SubagentService((id, request, ct) => CreateChildAsync(runtime(), id, request, ct, messenger, tasks)),
            (request, result) => NotifyParentOfBackgroundCompletionAsync(runtime(), tasks, request, result),
            () => runtime().Options.Tools.Any(tool => tool.Contract.Name is "Read" or "Bash"),
            tasks);
    }

    private static async ValueTask NotifyParentOfBackgroundCompletionAsync(
        AgentRuntime runtime,
        SubagentTaskRegistry? tasks,
        SubagentRequest request,
        SubagentResult result)
    {
        if (tasks?.Find(result.State.Id.Value)?.Notified == true)
            return;
        var parent = runtime.GetSession(request.ParentSessionId);
        if (parent is null)
            return;

        var status = SubagentTool.BackgroundStatus(result.State.Status);
        var durationMs = Math.Max(
            0,
            (int)Math.Round((result.State.EndedAt.GetValueOrDefault() - result.State.StartedAt).TotalMilliseconds));
        var output = result.Output.Trim();
        var outputFile = new SubagentArtifacts(ResolveArtifactsRoot(runtime))
            .GetOutputFile(request.ParentSessionId, result.State.Id);
        var body = BackgroundJobNotifications.FormatLocalAgentCompleted(
            result.State.Id.Value,
            status,
            request.AgentType ?? SubagentPrompts.GeneralPurposeAgentType,
            result.State.Description,
            result: result.State.Status == SubagentStatus.Completed && output.Length > 0 ? output : null,
            error: result.State.Status == SubagentStatus.Failed && output.Length > 0 ? output : null,
            toolUses: result.ToolUseCount,
            durationMs: durationMs,
            toolUseId: request.ParentToolCallId?.Value,
            outputFile: outputFile);
        await parent.EnqueueTaskNotificationAsync(body).ConfigureAwait(false);
        tasks?.TryMarkNotified(result.State.Id.Value);
    }

    private static string ResolveArtifactsRoot(AgentRuntime runtime) =>
        runtime.Options.SubagentOutputRoot ?? Path.Combine(Path.GetTempPath(), "alcoler-agents");

    public static PermissionMode ResolveChildMode(PermissionMode parentMode, bool parentPlanEnabled = false) =>
        parentPlanEnabled ? parentMode : PermissionMode.Yolo;

    public static ModelSelection ResolveChildModelSelection(
        ModelSelection? requestSelection,
        ModelSelection? parentSelection,
        SessionId parentSessionId) =>
        requestSelection
        ?? parentSelection
        ?? throw new InvalidOperationException(
            $"No model is selected for parent session {parentSessionId.Value}; " +
            "subagents inherit the parent session model and do not fall back to a runtime default.");

    public static IReadOnlyList<ITool> SelectChildTools(
        IReadOnlyList<ITool> tools,
        SubagentRequest request,
        RespondToCoordinatorTool? coordinatorResponse = null,
        PermissionMode childMode = PermissionMode.Yolo,
        bool childPlanEnabled = false)
    {
        var hidden = new HashSet<string>(ChildHiddenTools, StringComparer.OrdinalIgnoreCase);
        IEnumerable<ITool> scope = tools.Where(tool => !hidden.Contains(tool.Contract.Name));
        if (childPlanEnabled)
        {
            // No UI answers a child broker; interactive tools would stall until the broker timeout.
            scope = scope.Where(tool => !tool.Metadata.RequiresUserInteraction);
        }
        if (string.Equals(request.AgentType, SubagentPrompts.ExploreAgentType, StringComparison.Ordinal))
            scope = scope.Where(tool => SubagentPrompts.ExploreToolScope.Contains(tool.Contract.Name, StringComparer.OrdinalIgnoreCase));
        if (request.AllowedTools is { Count: > 0 })
            scope = scope.Where(tool => request.AllowedTools.Contains(tool.Contract.Name, StringComparer.OrdinalIgnoreCase));
        var selected = scope.ToList();
        if (coordinatorResponse is not null)
            selected.Add(coordinatorResponse);
        return selected;
    }

    private static async ValueTask<SubagentChildSession> CreateChildAsync(
        AgentRuntime runtime,
        SubagentId id,
        SubagentRequest request,
        CancellationToken ct,
        SubagentMessenger? messenger,
        SubagentTaskRegistry? tasks)
    {
        using var parentLease = await runtime.LockSessionLifecycleAsync(request.ParentSessionId, ct).ConfigureAwait(false);
        var options = runtime.Options;
        var parent = runtime.GetSession(request.ParentSessionId);
        var parentRecord = parent is null
            ? await options.Store.GetSessionAsync(request.ParentSessionId, ct).ConfigureAwait(false)
            : null;
        var root = request.WorkspaceRoot ?? parent?.WorkspaceRoot ?? parentRecord?.WorkspaceRoot
            ?? throw new InvalidOperationException(
                $"parent session {request.ParentSessionId} was not found; subagents need a WorkspaceRoot");
        var shell = parent?.Shell ?? parentRecord?.Shell;
        var planEnabled = parent?.PlanEnabled ?? parentRecord?.PlanEnabled ?? false;
        var mode = ResolveChildMode(parent?.Mode ?? parentRecord?.PermissionMode ?? PermissionMode.Yolo, planEnabled);
        var selection = ResolveChildModelSelection(
            request.ModelSelection, parent?.ModelSelection ?? parentRecord?.ModelSelection, request.ParentSessionId);
        var metadata = new ChildMetadata(runtime, id,
            request with { WorkspaceRoot = root, ModelSelection = selection }, tasks, mode, planEnabled);
        var childRuntime = metadata.CreateRuntime(runtime);
        try
        {
            var handle = await childRuntime.CreateSessionAsync(
                new SessionOptions(root, request.Description, mode, selection,
                    ParentSessionId: request.ParentSessionId, Shell: shell, PlanEnabled: planEnabled), ct)
                .ConfigureAwait(false);
            metadata.Initialize(handle.Id, childRuntime);
            var channel = new SubagentChannel(id, handle.Id, metadata.State.Description, childRuntime,
                OutputFile: metadata.State.OutputFile)
            {
                ColdLoadAsync = metadata.ColdLoadAsync,
            };
            var run = new ChildRun(metadata, childRuntime, channel, initialRun: true);
            run.Bind();
            tasks?.Register(new SubagentTaskInfo(
                id, handle.Id, metadata.AgentType, metadata.State.Description, request.Prompt,
                metadata.State.OutputFile, SubagentStatus.Running, Result: null, Error: null,
                ToolUses: null, metadata.State.StartedAt, EndedAt: null)
            {
                ParentSessionId = request.ParentSessionId,
            }, metadata.Stop.StopAsync);
            if (request.Background)
                metadata.WriteRunning();
            if (messenger is not null)
            {
                runtime.RegisterSubagentMessenger(messenger);
                messenger.Register(request.ParentSessionId, channel);
            }
            runtime.RegisterSubagentRuntime(request.ParentSessionId, handle.Id, childRuntime);
            await metadata.EmitStateAsync(started: true).ConfigureAwait(false);
            return run.ChildSession();
        }
        catch (Exception ex)
        {
            metadata.State = metadata.State with { Status = SubagentStatus.Failed, EndedAt = options.Clock.GetUtcNow() };
            tasks?.Update(id, info => info with
            {
                Status = SubagentStatus.Failed,
                Error = ex.Message,
                EndedAt = metadata.State.EndedAt,
            });
            messenger?.Maintain(request.ParentSessionId);
            await childRuntime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class ChildStop(AgentRuntime runtime, SessionId sessionId)
    {
        private readonly WeakReference<AgentRuntime> _runtime = new(runtime);
        private readonly SessionId _sessionId = sessionId;

        public void Retarget(AgentRuntime runtime) => _runtime.SetTarget(runtime);

        public async ValueTask StopAsync(CancellationToken cancellationToken)
        {
            if (_runtime.TryGetTarget(out var runtime))
                await runtime.SubmitAsync(new StopCommand(StrongId.NewCommandId(), _sessionId, 0), cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    // Cold callbacks must not retain a resident parent, child, or warm execution callback.
    private sealed class ChildMetadata
    {
        private readonly WeakReference<AgentRuntime> _parent;
        private readonly SubagentArtifacts _artifacts;
        private readonly TimeProvider _clock;
        private ModelSelection _selection;
        private PermissionMode _mode;
        private bool _planEnabled;
        private SubagentState _state;
        private ChildStop? _stop;

        public ChildMetadata(AgentRuntime parent, SubagentId id, SubagentRequest request,
            SubagentTaskRegistry? tasks, PermissionMode mode, bool planEnabled)
        {
            _parent = new(parent);
            _artifacts = new SubagentArtifacts(ResolveArtifactsRoot(parent));
            _clock = parent.Options.Clock;
            _selection = request.ModelSelection!;
            _mode = mode;
            _planEnabled = planEnabled;
            Request = request;
            Tasks = tasks;
            AgentType = request.AgentType ?? SubagentPrompts.GeneralPurposeAgentType;
            var description = string.IsNullOrWhiteSpace(request.Description) ? request.Prompt : request.Description;
            _state = new SubagentState(id, default, description, SubagentStatus.Running, null,
                _clock.GetUtcNow(), null, _artifacts.GetOutputFile(request.ParentSessionId, id),
                request.ParentToolCallId, AgentType);
        }

        public SubagentRequest Request { get; }
        private string OriginalDescription => string.IsNullOrWhiteSpace(Request.Description) ? Request.Prompt : Request.Description;
        public SubagentTaskRegistry? Tasks { get; }
        public string AgentType { get; }
        public ChildStop Stop => _stop!;
        public TimeProvider Clock => _clock;
        public SubagentState State
        {
            get => Volatile.Read(ref _state);
            set => Volatile.Write(ref _state, value);
        }

        public void Initialize(SessionId sessionId, AgentRuntime runtime)
        {
            State = State with { ChildSessionId = sessionId };
            _stop = new ChildStop(runtime, sessionId);
        }

        public AgentRuntime CreateRuntime(AgentRuntime parent)
        {
            var options = parent.Options;
            var response = new RespondToCoordinatorTool(State.Id, AgentType, DeliverToParentAsync);
            return new AgentRuntime(new AgentRuntimeOptions
            {
                Store = options.Store,
                Artifacts = options.Artifacts,
                Providers = parent.CurrentProviders,
                Tools = SelectChildTools(parent.GetSession(Request.ParentSessionId)?.ToolRegistry.Tools ?? options.Tools,
                    Request, response, _mode, _planEnabled),
                WorkspaceFactory = options.WorkspaceFactory,
                PermissionRules = options.PermissionRules,
                Clock = _clock,
                DefaultModel = _selection,
                AgentInstructions = options.AgentInstructions,
                CustomSystemPromptFactory = BuildPrompt,
                ShellName = options.ShellName,
                ToolOutputRoot = options.ToolOutputRoot,
                ResolveShell = options.ResolveShell,
                GitSnapshotProvider = options.GitSnapshotProvider,
                UserSkillsBase = options.UserSkillsBase,
                DisabledSkillPathsProvider = options.DisabledSkillPathsProvider,
                PlanFileStore = options.PlanFileStore,
                Logging = options.Logging,
            });
        }

        public async ValueTask ColdLoadAsync(SubagentChannel channel, CancellationToken cancellationToken)
        {
            if (!_parent.TryGetTarget(out var parent) || parent.GetSession(Request.ParentSessionId) is null)
                throw new InvalidOperationException("Coordinator session is not resident.");
            var record = await parent.Options.Store.GetSessionAsync(State.ChildSessionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Session {State.ChildSessionId.Value} not found.");
            _selection = record.ModelSelection ?? _selection;
            _mode = record.PermissionMode;
            _planEnabled = record.PlanEnabled;
            var runtime = CreateRuntime(parent);
            try
            {
                await runtime.ResumeSessionAsync(State.ChildSessionId, cancellationToken).ConfigureAwait(false);
                var run = new ChildRun(this, runtime, channel, initialRun: false);
                run.Bind();
                Stop.Retarget(runtime);
                parent.RegisterSubagentRuntime(Request.ParentSessionId, State.ChildSessionId, runtime);
            }
            catch
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public ValueTask EmitStateAsync(bool started) =>
            _parent.TryGetTarget(out var parent) && parent.GetSession(Request.ParentSessionId) is { } session
                ? session.EmitSubagentStateAsync(State, started)
                : ValueTask.CompletedTask;

        public ValueTask NotifyCompletionAsync(SubagentRequest request, SubagentResult result) =>
            _parent.TryGetTarget(out var parent)
                ? NotifyParentOfBackgroundCompletionAsync(parent, Tasks, request, result)
                : ValueTask.CompletedTask;

        public ILogger? CreateLogger() => _parent.TryGetTarget(out var parent)
            ? parent.Options.Logging?.CreateLogger(typeof(SubagentWiring).FullName!) : null;

        public SubagentStatus ReadStatus() => State.Status;

        public void WriteRunning(bool resumed = false, ToolCallId? resumedFrom = null)
        {
            var state = State;
            _artifacts.WriteRunning(state.Id, state.ChildSessionId, Request.ParentSessionId,
                Request.ParentToolCallId, AgentType, OriginalDescription, Request.Prompt,
                Request.WorkspaceRoot!, state.StartedAt, state.StartedAt,
                resumedAt: resumed ? state.StartedAt : null, resumedFromToolCallId: resumedFrom);
        }

        public void WriteTerminal(string output, int toolUses)
        {
            var state = State;
            var endedAt = state.EndedAt ?? _clock.GetUtcNow();
            var description = OriginalDescription;
            switch (state.Status)
            {
                case SubagentStatus.Completed:
                    _artifacts.WriteCompleted(state.Id, state.ChildSessionId, Request.ParentSessionId,
                        Request.ParentToolCallId, AgentType, description, Request.Prompt, Request.WorkspaceRoot!,
                        output, Math.Max(0, (int)Math.Round((endedAt - state.StartedAt).TotalMilliseconds)),
                        toolUses, state.StartedAt, endedAt);
                    break;
                case SubagentStatus.Failed:
                    _artifacts.WriteFailed(state.Id, state.ChildSessionId, Request.ParentSessionId,
                        Request.ParentToolCallId, AgentType, description, Request.Prompt, Request.WorkspaceRoot!,
                        output, state.StartedAt, endedAt);
                    break;
                default:
                    _artifacts.WriteStopped(state.Id, state.ChildSessionId, Request.ParentSessionId,
                        Request.ParentToolCallId, AgentType, description, Request.Prompt, Request.WorkspaceRoot!,
                        state.StartedAt, endedAt);
                    break;
            }
        }

        private ValueTask DeliverToParentAsync(string body, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = _parent.TryGetTarget(out var parent) ? parent.GetSession(Request.ParentSessionId) : null;
            return (session ?? throw new InvalidOperationException("Coordinator session is not resident."))
                .EnqueueTaskNotificationAsync(body);
        }

        private string BuildPrompt(ShellEnvironment? shell) => SubagentPrompts.BuildChildSystemPrompt(
            Request.AgentType,
            new SubagentEnvironmentFacts(Request.WorkspaceRoot!,
                RuntimeEnvironmentFacts.IsGitRepository(Request.WorkspaceRoot!), RuntimeEnvironmentFacts.Platform,
                shell?.Name ?? (_parent.TryGetTarget(out var parent) ? parent.Options.ShellName : null)
                    ?? RuntimeEnvironmentFacts.Shell,
                RuntimeEnvironmentFacts.OsVersion, _selection.ProviderId, _selection.ModelId));
    }

    private sealed class ChildRun(ChildMetadata metadata, AgentRuntime runtime, SubagentChannel channel, bool initialRun)
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SubagentRequest _runRequest = metadata.Request;
        private bool _initialRun = initialRun;
        private int _previousAssistantMessages;
        private int _previousToolUses;
        private string? _finalOutput;

        public void Bind()
        {
            channel.BindRuntime(runtime, ResumeAsync, metadata.ReadStatus);
            runtime.GetSession(channel.ChildSessionId)!.TurnFinished += OnTurnFinishedAsync;
        }

        public SubagentChildSession ChildSession() => new(
            channel.ChildSessionId,
            (prompt, token) => SubmitPromptAsync(runtime, channel.ChildSessionId, prompt, token),
            _finished.Task,
            () => Task.FromResult(_finalOutput),
            () => Task.FromResult(CountToolUses(ReadRows(runtime, channel.ChildSessionId))),
            metadata.Stop.StopAsync,
            OutputFile: channel.OutputFile);

        private async ValueTask OnTurnFinishedAsync(SessionRuntime child, TurnOutcome outcome)
        {
            await channel.DeliveryLock.WaitAsync().ConfigureAwait(false);
            var firstRun = _initialRun;
            _initialRun = false;
            var terminal = metadata.State with
            {
                Status = outcome switch
                {
                    TurnOutcome.Completed => SubagentStatus.Completed,
                    TurnOutcome.Failed => SubagentStatus.Failed,
                    _ => SubagentStatus.Cancelled,
                },
                EndedAt = metadata.Clock.GetUtcNow(),
            };
            try
            {
                var rows = ReadRows(child);
                var output = ReadFinalOutput(rows, _previousAssistantMessages) ?? string.Empty;
                var toolUses = CountToolUses(rows);
                if (terminal.Status == SubagentStatus.Failed && child.LastTurnError is { Length: > 0 } error)
                    output = string.IsNullOrWhiteSpace(output) ? error : $"{output}\n\n{error}";
                _finalOutput = output;
                terminal = terminal with
                {
                    ResultSummary = string.IsNullOrWhiteSpace(output) ? null : output[..Math.Min(output.Length, 200)],
                };
                metadata.State = terminal;
                metadata.Tasks?.Update(terminal.Id, info => info with
                {
                    Status = terminal.Status,
                    Result = terminal.Status == SubagentStatus.Completed ? output : null,
                    Error = terminal.Status == SubagentStatus.Failed ? output : null,
                    ToolUses = toolUses,
                    EndedAt = terminal.EndedAt,
                });
                try
                {
                    metadata.WriteTerminal(output, toolUses);
                }
                catch (Exception ex)
                {
                    metadata.CreateLogger()?.LogWarning(ex, "Writing subagent artifacts for {SubagentId} failed", terminal.Id.Value);
                }
                await metadata.EmitStateAsync(started: false).ConfigureAwait(false);
                if (!firstRun)
                    await metadata.NotifyCompletionAsync(_runRequest,
                        new SubagentResult(terminal, output, toolUses - _previousToolUses)).ConfigureAwait(false);
            }
            catch
            {
                // Delivery is advisory; completion must still release the foreground caller.
            }
            finally
            {
                if (firstRun)
                {
                    if (terminal.Status == SubagentStatus.Failed)
                        _finished.TrySetException(new InvalidOperationException(child.LastTurnError ?? "Subagent turn failed."));
                    else if (terminal.Status == SubagentStatus.Cancelled)
                        _finished.TrySetCanceled();
                    else
                        _finished.TrySetResult();
                }
                channel.DeliveryLock.Release();
                channel.Messenger?.OnTerminal(metadata.Request.ParentSessionId, channel);
            }
        }

        private async ValueTask ResumeAsync(string prompt, string? summary, ToolCallId? toolCallId, CancellationToken token)
        {
            var previousState = metadata.State;
            var previousRequest = _runRequest;
            var rows = ReadRows(runtime, channel.ChildSessionId);
            _previousAssistantMessages = CountAssistantMessages(rows);
            _previousToolUses = CountToolUses(rows);
            _runRequest = metadata.Request with
            {
                Prompt = prompt,
                Description = string.IsNullOrWhiteSpace(summary) ? previousState.Description : summary,
                Background = true,
                ParentToolCallId = toolCallId,
            };
            metadata.State = previousState with
            {
                Description = _runRequest.Description!,
                Status = SubagentStatus.Running,
                ResultSummary = null,
                StartedAt = metadata.Clock.GetUtcNow(),
                EndedAt = null,
            };
            var previousTask = metadata.Tasks?.Find(previousState.Id.Value);
            metadata.Tasks?.Update(previousState.Id, info => info with
            {
                Description = metadata.State.Description,
                Status = SubagentStatus.Running,
                Result = null,
                Error = null,
                ToolUses = null,
                StartedAt = metadata.State.StartedAt,
                EndedAt = null,
            }, resetNotified: true);
            try
            {
                metadata.WriteRunning(resumed: true, resumedFrom: toolCallId);
                await metadata.EmitStateAsync(started: false).ConfigureAwait(false);
                await SubmitPromptAsync(runtime, channel.ChildSessionId, prompt, token).ConfigureAwait(false);
            }
            catch
            {
                metadata.State = previousState;
                _runRequest = previousRequest;
                if (previousTask is not null)
                    metadata.Tasks?.Restore(previousState.Id, previousTask);
                await metadata.EmitStateAsync(started: false).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async ValueTask SubmitPromptAsync(
        AgentRuntime runtime,
        SessionId sessionId,
        string prompt,
        CancellationToken ct)
    {
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), sessionId, 0, prompt), ct).ConfigureAwait(false);
        if (!ack.Accepted)
            throw new InvalidOperationException($"subagent prompt rejected: {ack.RejectionReason}");
    }

    private static IReadOnlyList<ConversationRow> ReadRows(SessionRuntime runtime)
    {
        using var projection = runtime.CreateProjection();
        return projection.Snapshot().Rows;
    }

    private static IReadOnlyList<ConversationRow> ReadRows(AgentRuntime runtime, SessionId sessionId)
    {
        using var projection = runtime.Subscribe(sessionId);
        return projection.Snapshot().Rows;
    }

    private static string? ReadFinalOutput(IReadOnlyList<ConversationRow> rows, int skipMessages)
    {
        AssistantMessageRow? last = null;
        var assistantIndex = 0;
        foreach (var row in rows)
        {
            if (row is AssistantMessageRow assistant && assistantIndex++ >= skipMessages)
                last = assistant;
        }
        if (last is null)
            return null;

        var builder = new System.Text.StringBuilder();
        var first = true;
        foreach (var part in last.Parts)
        {
            if (part is not Contracts.Conversation.TextPart text)
                continue;
            if (!first)
                builder.Append("\n\n");
            first = false;
            builder.Append(text.Text);
        }
        return builder.ToString();
    }

    private static int CountAssistantMessages(IReadOnlyList<ConversationRow> rows)
    {
        var count = 0;
        foreach (var row in rows)
        {
            if (row is AssistantMessageRow)
                count++;
        }
        return count;
    }

    private static int CountToolUses(IReadOnlyList<ConversationRow> rows)
    {
        var count = 0;
        foreach (var row in rows)
        {
            if (row is ToolCallRow)
                count++;
        }
        return count;
    }
}
