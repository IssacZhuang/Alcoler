using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Subagents;
using Alcoler.Kernel.Runtime;

namespace Alcoler.Kernel.Subagents;

/// <summary>
/// Bridges SubagentService to real runtimes: each request gets a child AgentRuntime sharing
/// the parent's store/providers/workspace with the Task tool removed and the tool set filtered
/// to AllowedTools. Child sessions run in Yolo mode because no UI answers a child broker; the
/// parent already approved the Task call and the model constrains the child via AllowedTools.
/// Completion mirroring rides on the child's TurnDrained signal, which fires exactly once per
/// submitted prompt, so both foreground and background runs update the parent timeline.
/// </summary>
public static class SubagentWiring
{
    /// <param name="runtime">Lazy because the tool is registered on the runtime it resolves.</param>
    public static SubagentTool CreateTool(Func<AgentRuntime> runtime) =>
        new(new SubagentService((request, ct) => CreateChildAsync(runtime(), request, ct)));

    private static async ValueTask<SubagentChildSession> CreateChildAsync(
        AgentRuntime runtime,
        SubagentRequest request,
        CancellationToken ct)
    {
        var options = runtime.Options;
        var parentRuntime = runtime.GetSession(request.ParentSessionId);
        var root = request.WorkspaceRoot
            ?? parentRuntime?.WorkspaceRoot
            ?? throw new InvalidOperationException(
                $"parent session {request.ParentSessionId} is not resident; subagents need a WorkspaceRoot");

        var childTools = options.Tools
            .Where(t => !string.Equals(t.Contract.Name, "Task", StringComparison.OrdinalIgnoreCase))
            .Where(t => request.AllowedTools is null
                || request.AllowedTools.Contains(t.Contract.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var childRuntime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = options.Store,
            Artifacts = options.Artifacts,
            Providers = options.Providers,
            Tools = childTools,
            WorkspaceFactory = options.WorkspaceFactory,
            PermissionRules = options.PermissionRules,
            Clock = options.Clock,
            DefaultModel = request.ModelSelection ?? options.DefaultModel,
        });

        var handle = await childRuntime.CreateSessionAsync(
            new SessionOptions(root, request.Description, PermissionMode.Yolo, request.ModelSelection),
            ct);

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
}
