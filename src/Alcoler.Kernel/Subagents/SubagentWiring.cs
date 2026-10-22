using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
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

/// <summary>
/// Bridges SubagentService to real runtimes: each request gets a child AgentRuntime sharing
/// the parent's store/workspace/providers with the subagent dispatch tools removed and the
/// tool set filtered to the agent type's scope. The child's system prompt is composed from
/// SubagentPrompts and installed via the per-session CustomSystemPrompt channel. Children
/// inherit plan mode when the parent is planning; otherwise they run in Yolo because no UI
/// answers a child broker and the parent already approved the Agent call. Interactive
/// tools are removed from plan-mode children so they cannot stall on a missing broker.
/// Completion mirroring rides on the child's terminal turn signal for both foreground and
/// background runs.
/// </summary>
public static class SubagentWiring
{
    private static readonly string[] ChildHiddenTools = ["Agent", "Task", "SendMessage", "RespondToCoordinator", "ExitPlanMode"];

    /// <param name="runtime">Lazy because the tool is registered on the runtime it resolves.</param>
    /// <param name="messenger">When set, children register here so SendMessage can reach them.</param>
    public static SubagentTool CreateTool(Func<AgentRuntime> runtime, SubagentMessenger? messenger = null)
    {
        var tasks = new SubagentTaskRegistry();
        return new(
            new SubagentService((id, request, ct) => CreateChildAsync(runtime(), id, request, ct, messenger, tasks)),
            NotifyParentOfBackgroundCompletionAsync(runtime, tasks),
            () => runtime().Options.Tools.Any(tool => tool.Contract.Name is "Read" or "Bash"),
            tasks);
    }

    private static Func<SubagentRequest, SubagentResult, ValueTask> NotifyParentOfBackgroundCompletionAsync(
        Func<AgentRuntime> runtime,
        SubagentTaskRegistry? tasks = null) =>
        async (request, result) =>
        {
            if (tasks is not null && tasks.Find(result.State.Id.Value)?.Notified == true)
            {
                return;
            }

            var parent = runtime().GetSession(request.ParentSessionId);
            if (parent is null)
            {
                return;
            }

            var status = SubagentTool.BackgroundStatus(result.State.Status);
            var durationMs = Math.Max(
                0,
                (int)Math.Round((result.State.EndedAt.GetValueOrDefault() - result.State.StartedAt).TotalMilliseconds));
            var output = result.Output.Trim();
            var outputFile = new SubagentArtifacts(ResolveArtifactsRoot(runtime()))
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
        };

    private static string ResolveArtifactsRoot(AgentRuntime runtime) =>
        runtime.Options.SubagentOutputRoot ?? Path.Combine(Path.GetTempPath(), "alcoler-agents");

    public static PermissionMode ResolveChildMode(PermissionMode parentMode) =>
        parentMode == PermissionMode.Plan ? PermissionMode.Plan : PermissionMode.Yolo;

    /// <summary>
    /// ExitPlanMode is removed from every child because plan approval exists only at the parent;
    /// Agent/Task recursion and parent-side SendMessage are removed on top of the type scope.
    /// </summary>
    public static IReadOnlyList<ITool> SelectChildTools(
        IReadOnlyList<ITool> tools,
        SubagentRequest request,
        RespondToCoordinatorTool? coordinatorResponse = null,
        PermissionMode childMode = PermissionMode.Yolo)
    {
        var hidden = new HashSet<string>(ChildHiddenTools, StringComparer.OrdinalIgnoreCase);
        IEnumerable<ITool> scope = tools.Where(tool => !hidden.Contains(tool.Contract.Name));
        if (childMode == PermissionMode.Plan)
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
        SubagentMessenger? messenger = null,
        SubagentTaskRegistry? tasks = null)
    {
        var options = runtime.Options;
        var artifacts = new SubagentArtifacts(ResolveArtifactsRoot(runtime));
        var logger = options.Logging?.CreateLogger(typeof(SubagentWiring).FullName!);
        var parentRuntime = runtime.GetSession(request.ParentSessionId);
        var parentRecord = parentRuntime is null
            ? await options.Store.GetSessionAsync(request.ParentSessionId, ct).ConfigureAwait(false)
            : null;
        var root = request.WorkspaceRoot
            ?? parentRuntime?.WorkspaceRoot
            ?? parentRecord?.WorkspaceRoot
            ?? throw new InvalidOperationException(
                $"parent session {request.ParentSessionId} was not found; subagents need a WorkspaceRoot");
        var inheritedShell = parentRuntime?.Shell ?? parentRecord?.Shell;
        var parentMode = parentRuntime?.Mode ?? parentRecord?.PermissionMode ?? PermissionMode.Yolo;
        var childMode = ResolveChildMode(parentMode);

        var selection = request.ModelSelection ?? runtime.CurrentDefaultModel;
        string BuildChildPrompt(ShellEnvironment? shell) => SubagentPrompts.BuildChildSystemPrompt(
            request.AgentType,
            new SubagentEnvironmentFacts(
                root,
                RuntimeEnvironmentFacts.IsGitRepository(root),
                RuntimeEnvironmentFacts.Platform,
                shell?.Name ?? options.ShellName ?? RuntimeEnvironmentFacts.Shell,
                RuntimeEnvironmentFacts.OsVersion,
                selection?.ProviderId,
                selection?.ModelId));

        var responseTool = new RespondToCoordinatorTool(
            id,
            request.AgentType ?? SubagentPrompts.GeneralPurposeAgentType,
            (body, token) =>
            {
                token.ThrowIfCancellationRequested();
                var parent = runtime.GetSession(request.ParentSessionId)
                    ?? throw new InvalidOperationException("Coordinator session is not resident.");
                return parent.EnqueueTaskNotificationAsync(body);
            });
        var childRuntime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = options.Store,
            Artifacts = options.Artifacts,
            Providers = runtime.CurrentProviders,
            Tools = SelectChildTools(parentRuntime?.ToolRegistry.Tools ?? options.Tools, request, responseTool, childMode),
            WorkspaceFactory = options.WorkspaceFactory,
            PermissionRules = options.PermissionRules,
            Clock = options.Clock,
            DefaultModel = selection,
            AgentInstructions = options.AgentInstructions,
            CustomSystemPromptFactory = BuildChildPrompt,
            ShellName = options.ShellName,
            ToolOutputRoot = options.ToolOutputRoot,
            ResolveShell = options.ResolveShell,
            GitSnapshotProvider = options.GitSnapshotProvider,
            UserSkillsBase = options.UserSkillsBase,
            DisabledSkillPathsProvider = options.DisabledSkillPathsProvider,
            PlanFileStore = options.PlanFileStore,
        });

        var handle = await childRuntime.CreateSessionAsync(
            new SessionOptions(root, request.Description, childMode, request.ModelSelection,
                ParentSessionId: request.ParentSessionId, Shell: inheritedShell),
            ct);

        runtime.RegisterSubagentRuntime(request.ParentSessionId, handle.Id, childRuntime);
        var child = childRuntime.GetSession(handle.Id)!;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedRuns = 0;
        var runRequest = request;
        var previousAssistantMessages = 0;
        var previousToolUses = 0;
        string? finalOutput = null;
        var description = string.IsNullOrWhiteSpace(request.Description) ? request.Prompt : request.Description;
        var agentType = request.AgentType ?? SubagentPrompts.GeneralPurposeAgentType;
        var outputFile = artifacts.GetOutputFile(request.ParentSessionId, id);
        var startedAt = options.Clock.GetUtcNow();
        var state = new SubagentState(
            id,
            handle.Id,
            description,
            SubagentStatus.Running,
            null,
            startedAt,
            null,
            OutputFile: outputFile,
            ParentToolCallId: request.ParentToolCallId,
            AgentType: agentType);

        // The task registry outlives the agent; a strong stop closure would root the child
        // runtime graph for the parent runtime's whole lifetime.
        var childRuntimeRef = new WeakReference<AgentRuntime>(childRuntime);
        async ValueTask StopChildAsync(CancellationToken token)
        {
            if (!childRuntimeRef.TryGetTarget(out var runtimeToStop))
                return;
            await runtimeToStop.SubmitAsync(new StopCommand(StrongId.NewCommandId(), handle.Id, 0), token)
                .ConfigureAwait(false);
        }

        tasks?.Register(
            new SubagentTaskInfo(
                id,
                handle.Id,
                agentType,
                description,
                request.Prompt,
                outputFile,
                SubagentStatus.Running,
                Result: null,
                Error: null,
                ToolUses: null,
                startedAt,
                EndedAt: null),
            StopChildAsync);

        void WriteTerminalArtifacts(SubagentState terminalState, string output, int toolUses)
        {
            var endedAt = terminalState.EndedAt ?? options.Clock.GetUtcNow();
            switch (terminalState.Status)
            {
                case SubagentStatus.Completed:
                    artifacts.WriteCompleted(
                        id, handle.Id, request.ParentSessionId, request.ParentToolCallId, agentType,
                        description, request.Prompt, root, output,
                        Math.Max(0, (int)Math.Round((endedAt - terminalState.StartedAt).TotalMilliseconds)),
                        toolUses,
                        terminalState.StartedAt, endedAt);
                    break;
                case SubagentStatus.Failed:
                    artifacts.WriteFailed(
                        id, handle.Id, request.ParentSessionId, request.ParentToolCallId, agentType,
                        description, request.Prompt, root, output, terminalState.StartedAt, endedAt);
                    break;
                default:
                    artifacts.WriteStopped(
                        id, handle.Id, request.ParentSessionId, request.ParentToolCallId, agentType,
                        description, request.Prompt, root, terminalState.StartedAt, endedAt);
                    break;
            }
        }

        if (request.Background)
        {
            try
            {
                artifacts.WriteRunning(
                    id, handle.Id, request.ParentSessionId, request.ParentToolCallId, agentType,
                    description, request.Prompt, root, startedAt, startedAt);
            }
            catch
            {
                await childRuntime.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        child.TurnFinished += async (_, outcome) =>
        {
            var initialRun = Interlocked.Increment(ref completedRuns) == 1;
            var terminal = state with
            {
                Status = outcome switch
                {
                    TurnOutcome.Completed => SubagentStatus.Completed,
                    TurnOutcome.Failed => SubagentStatus.Failed,
                    _ => SubagentStatus.Cancelled,
                },
                EndedAt = options.Clock.GetUtcNow(),
            };
            try
            {
                var rows = ReadRows(childRuntime, handle.Id);
                var output = ReadFinalOutput(rows, previousAssistantMessages) ?? string.Empty;
                var toolUses = CountToolUses(rows);
                if (terminal.Status == SubagentStatus.Failed && child.LastTurnError is { Length: > 0 } error)
                    output = string.IsNullOrWhiteSpace(output) ? error : $"{output}\n\n{error}";
                finalOutput = output;
                terminal = terminal with { ResultSummary = string.IsNullOrWhiteSpace(output) ? null : output[..Math.Min(output.Length, 200)] };
                state = terminal;
                tasks?.Update(id, info => info with
                {
                    Status = terminal.Status,
                    Result = terminal.Status == SubagentStatus.Completed ? output : null,
                    Error = terminal.Status == SubagentStatus.Failed ? output : null,
                    ToolUses = toolUses,
                    EndedAt = terminal.EndedAt,
                });
                try
                {
                    WriteTerminalArtifacts(terminal, output, toolUses);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Writing subagent artifacts for {SubagentId} failed", id.Value);
                }
                if (parentRuntime is not null)
                    await parentRuntime.EmitSubagentStateAsync(terminal, started: false).ConfigureAwait(false);
                if (!initialRun)
                {
                    await NotifyParentOfBackgroundCompletionAsync(() => runtime, tasks)(runRequest,
                        new SubagentResult(terminal, output,
                            toolUses - previousToolUses)).ConfigureAwait(false);
                }
            }
            catch
            {
                // Delivery is advisory; completion must still release the foreground caller.
            }
            finally
            {
                if (initialRun)
                {
                    if (terminal.Status == SubagentStatus.Failed)
                        finished.TrySetException(new InvalidOperationException(child.LastTurnError ?? "Subagent turn failed."));
                    else if (terminal.Status == SubagentStatus.Cancelled)
                        finished.TrySetCanceled();
                    else
                        finished.TrySetResult();
                }
            }
        };
        if (messenger is not null)
        {
            messenger.Register(request.ParentSessionId, new SubagentChannel(state.Id, handle.Id, description, childRuntime,
                ResumeAsync: async (prompt, summary, toolCallId, token) =>
                {
                    var previousState = state;
                    var previousRequest = runRequest;
                    var rows = ReadRows(childRuntime, handle.Id);
                    previousAssistantMessages = CountAssistantMessages(rows);
                    previousToolUses = CountToolUses(rows);
                    runRequest = request with
                    {
                        Prompt = prompt,
                        Description = string.IsNullOrWhiteSpace(summary) ? state.Description : summary,
                        Background = true,
                        ParentToolCallId = toolCallId,
                    };
                    state = state with
                    {
                        Description = runRequest.Description!,
                        Status = SubagentStatus.Running,
                        ResultSummary = null,
                        StartedAt = options.Clock.GetUtcNow(),
                        EndedAt = null,
                    };
                    var previousTask = tasks?.Find(id.Value);
                    tasks?.Update(id, info => info with
                    {
                        Description = state.Description,
                        Status = SubagentStatus.Running,
                        Result = null,
                        Error = null,
                        ToolUses = null,
                        StartedAt = state.StartedAt,
                        EndedAt = null,
                    }, resetNotified: true);
                    try
                    {
                        artifacts.WriteRunning(
                            id, handle.Id, request.ParentSessionId, request.ParentToolCallId, agentType,
                            description, request.Prompt, root, state.StartedAt, state.StartedAt,
                            resumedAt: state.StartedAt, resumedFromToolCallId: toolCallId);
                        if (parentRuntime is not null)
                            await parentRuntime.EmitSubagentStateAsync(state, started: false).ConfigureAwait(false);
                        await SubmitPromptAsync(childRuntime, handle.Id, prompt, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        state = previousState;
                        runRequest = previousRequest;
                        if (previousTask is not null)
                            tasks?.Restore(id, previousTask);
                        if (parentRuntime is not null)
                            await parentRuntime.EmitSubagentStateAsync(state, started: false).ConfigureAwait(false);
                        throw;
                    }
                },
                ReadStatus: () => state.Status,
                OutputFile: outputFile));
        }
        if (parentRuntime is not null)
            await parentRuntime.EmitSubagentStateAsync(state, started: true).ConfigureAwait(false);

        return new SubagentChildSession(
            handle.Id,
            (prompt, token) => SubmitPromptAsync(childRuntime, handle.Id, prompt, token),
            finished.Task,
            () => Task.FromResult(finalOutput),
            () => Task.FromResult(CountToolUses(ReadRows(childRuntime, handle.Id))),
            StopChildAsync,
            OutputFile: outputFile);
    }

    private static async ValueTask SubmitPromptAsync(
        AgentRuntime runtime,
        SessionId sessionId,
        string prompt,
        CancellationToken ct)
    {
        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), sessionId, 0, prompt), ct);
        if (!ack.Accepted)
        {
            throw new InvalidOperationException($"subagent prompt rejected: {ack.RejectionReason}");
        }
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
