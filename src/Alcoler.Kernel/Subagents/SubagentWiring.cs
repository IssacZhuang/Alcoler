using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Prompts;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Subagents;

/// <summary>
/// Bridges SubagentService to real runtimes: each request gets a child AgentRuntime sharing
/// the parent's store/workspace/providers with the subagent dispatch tools removed and the
/// tool set filtered to the agent type's scope. The child's system prompt is composed from
/// SubagentPrompts and installed via the per-session CustomSystemPrompt channel. Child
/// sessions run in Yolo mode because no UI answers a child broker; the parent already
/// approved the Agent call and the tool scope constrains the child. Completion mirroring
/// rides on the child's TurnDrained signal, which fires exactly once per submitted prompt,
/// so both foreground and background runs update the parent timeline.
/// </summary>
public static class SubagentWiring
{
    private static readonly string[] ChildHiddenTools = ["Agent", "Task", "SendMessage", "ExitPlanMode"];

    /// <param name="runtime">Lazy because the tool is registered on the runtime it resolves.</param>
    /// <param name="messenger">When set, background children register here so SendMessage can reach them.</param>
    public static SubagentTool CreateTool(Func<AgentRuntime> runtime, SubagentMessenger? messenger = null) =>
        new(
            new SubagentService((request, ct) => CreateChildAsync(runtime(), request, ct, messenger)),
            NotifyParentOfBackgroundCompletionAsync(runtime));

    // Background completions ride the parent's task-notification channel (never a user
    // steer): a local_agent-shaped notification per runtime-task/notification.ts, minus the
    // output-file field.
    private static Func<SubagentRequest, SubagentResult, ValueTask> NotifyParentOfBackgroundCompletionAsync(
        Func<AgentRuntime> runtime) =>
        (request, result) =>
        {
            var parent = runtime().GetSession(request.ParentSessionId);
            if (parent is null)
            {
                return ValueTask.CompletedTask;
            }

            var status = SubagentTool.BackgroundStatus(result.State.Status);
            var durationMs = Math.Max(
                0,
                (int)Math.Round((result.State.EndedAt.GetValueOrDefault() - result.State.StartedAt).TotalMilliseconds));
            var output = result.Output.Trim();
            var body = BackgroundJobNotifications.FormatLocalAgentCompleted(
                result.State.Id.Value,
                status,
                request.AgentType ?? SubagentPrompts.GeneralPurposeAgentType,
                result.State.Description,
                result: result.State.Status == SubagentStatus.Completed && output.Length > 0 ? output : null,
                error: result.State.Status == SubagentStatus.Failed && output.Length > 0 ? output : null,
                toolUses: result.ToolUseCount,
                durationMs: durationMs);
            return parent.EnqueueTaskNotificationAsync(body);
        };

    /// <summary>
    /// ZCode removes ExitPlanMode from every child (no plan-approval surface below the parent);
    /// Agent/Task recursion and parent-side SendMessage are removed on top of the type scope.
    /// </summary>
    public static IReadOnlyList<ITool> SelectChildTools(IReadOnlyList<ITool> tools, SubagentRequest request)
    {
        var hidden = new HashSet<string>(ChildHiddenTools, StringComparer.OrdinalIgnoreCase);
        IEnumerable<ITool> scope = tools.Where(tool => !hidden.Contains(tool.Contract.Name));
        if (string.Equals(request.AgentType, SubagentPrompts.ExploreAgentType, StringComparison.Ordinal))
            scope = scope.Where(tool => SubagentPrompts.ExploreToolScope.Contains(tool.Contract.Name, StringComparer.OrdinalIgnoreCase));
        if (request.AllowedTools is { Count: > 0 })
            scope = scope.Where(tool => request.AllowedTools.Contains(tool.Contract.Name, StringComparer.OrdinalIgnoreCase));
        return scope.ToList();
    }

    private static async ValueTask<SubagentChildSession> CreateChildAsync(
        AgentRuntime runtime,
        SubagentRequest request,
        CancellationToken ct,
        SubagentMessenger? messenger = null)
    {
        var options = runtime.Options;
        var parentRuntime = runtime.GetSession(request.ParentSessionId);
        var root = request.WorkspaceRoot
            ?? parentRuntime?.WorkspaceRoot
            ?? throw new InvalidOperationException(
                $"parent session {request.ParentSessionId} is not resident; subagents need a WorkspaceRoot");

        var selection = request.ModelSelection ?? options.DefaultModel;
        var childSystemPrompt = SubagentPrompts.BuildChildSystemPrompt(
            request.AgentType,
            new SubagentEnvironmentFacts(
                root,
                RuntimeEnvironmentFacts.IsGitRepository(root),
                RuntimeEnvironmentFacts.Platform,
                options.ShellName ?? RuntimeEnvironmentFacts.Shell,
                RuntimeEnvironmentFacts.OsVersion,
                selection?.ProviderId,
                selection?.ModelId));

        var childRuntime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = options.Store,
            Artifacts = options.Artifacts,
            Providers = options.Providers,
            Tools = SelectChildTools(options.Tools, request),
            WorkspaceFactory = options.WorkspaceFactory,
            PermissionRules = options.PermissionRules,
            Clock = options.Clock,
            DefaultModel = selection,
            AgentInstructions = options.AgentInstructions,
            CustomSystemPrompt = childSystemPrompt,
            ShellName = options.ShellName,
            GitSnapshotProvider = options.GitSnapshotProvider,
        });

        var handle = await childRuntime.CreateSessionAsync(
            new SessionOptions(root, request.Description, PermissionMode.Yolo, request.ModelSelection),
            ct);

        runtime.RegisterSubagentRuntime(request.ParentSessionId, handle.Id, childRuntime);
        var child = childRuntime.GetSession(handle.Id)!;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.TurnDrained += _ =>
        {
            drained.TrySetResult();
            return ValueTask.CompletedTask;
        };

        var description = string.IsNullOrWhiteSpace(request.Description) ? request.Prompt : request.Description;
        var state = new SubagentState(
            StrongId.NewSubagentId(),
            handle.Id,
            description,
            SubagentStatus.Running,
            null,
            options.Clock.GetUtcNow(),
            null);
        if (request.Background && messenger is not null)
        {
            messenger.Register(request.ParentSessionId, new SubagentChannel(state.Id, handle.Id, description, childRuntime));
        }
        if (parentRuntime is not null)
        {
            await parentRuntime.EmitSubagentStateAsync(state, started: true);
        }

        var completion = MirrorOnDrainAsync(runtime, childRuntime, parentRuntime, state, drained.Task);
        return new SubagentChildSession(
            handle.Id,
            (prompt, token) => SubmitPromptAsync(childRuntime, handle.Id, prompt, token),
            completion,
            () => ReadFinalOutputAsync(childRuntime, handle.Id),
            () => Task.FromResult(ReadToolUseCount(childRuntime, handle.Id)),
            async token => await childRuntime.SubmitAsync(
                new StopCommand(StrongId.NewCommandId(), handle.Id, 0), token));
    }

    private static async Task MirrorOnDrainAsync(
        AgentRuntime runtime,
        AgentRuntime childRuntime,
        SessionRuntime? parentRuntime,
        SubagentState state,
        Task drained)
    {
        await drained.ConfigureAwait(false);
        if (parentRuntime is null)
        {
            return;
        }

        try
        {
            var phase = childRuntime.TryObserve(state.ChildSessionId)?.Snapshot().State.Phase;
            var status = phase == TurnPhase.Error ? SubagentStatus.Failed : SubagentStatus.Completed;
            await parentRuntime.EmitSubagentStateAsync(
                state with { Status = status, EndedAt = runtime.Options.Clock.GetUtcNow() },
                started: false);
        }
        catch
        {
            // Mirroring is best-effort; the child run itself already finished.
        }
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

    private static async Task<string?> ReadFinalOutputAsync(AgentRuntime runtime, SessionId sessionId)
    {
        using var projection = runtime.Subscribe(sessionId);
        var snapshot = projection.Snapshot();
        return snapshot.Rows.OfType<AssistantMessageRow>().LastOrDefault() is { } last
            ? string.Join("\n\n", last.Parts.OfType<Contracts.Conversation.TextPart>().Select(p => p.Text))
            : null;
    }

    private static int ReadToolUseCount(AgentRuntime runtime, SessionId sessionId)
    {
        using var projection = runtime.Subscribe(sessionId);
        return projection.Snapshot().Rows.OfType<ToolCallRow>().Count();
    }
}
