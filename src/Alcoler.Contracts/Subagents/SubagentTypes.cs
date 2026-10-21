using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;

namespace Alcoler.Contracts.Subagents;

public sealed record SubagentRequest(
    SessionId ParentSessionId,
    string Prompt,
    string? AgentType = null,
    string? Description = null,
    IReadOnlyList<string>? AllowedTools = null,
    ModelSelection? ModelSelection = null,
    bool Background = false,
    string? WorkspaceRoot = null,
    ToolCallId? ParentToolCallId = null);

public sealed record SubagentHandle(
    SubagentId Id,
    SessionId ChildSessionId,
    string Description,
    bool Background,
    string? OutputFile = null);

public enum SubagentStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record SubagentState(
    SubagentId Id,
    SessionId ChildSessionId,
    string Description,
    SubagentStatus Status,
    string? ResultSummary,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? OutputFile = null);

public sealed record SubagentResult(SubagentState State, string Output, int? ToolUseCount = null);

/// <summary>
/// Same-process logical subagent runner. Foreground runs block the calling tool until the
/// child turn completes; background runs report completion through the sink supplied by
/// the kernel so the parent session can emit its own events.
/// </summary>
public interface ISubagentPort
{
    ValueTask<SubagentResult> RunAsync(SubagentRequest request, CancellationToken cancellationToken);

    ValueTask<SubagentHandle> StartBackgroundAsync(
        SubagentRequest request,
        Func<SubagentResult, ValueTask> onCompleted,
        CancellationToken cancellationToken);
}
