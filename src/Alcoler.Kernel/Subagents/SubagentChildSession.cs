using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;

namespace Alcoler.Kernel.Subagents;

/// <summary>
/// Handle over one child session run, produced outside this subsystem. Completion running to
/// completion makes the final assistant text available through ReadFinalOutputAsync; a faulted
/// Completion means the child run failed; a canceled Completion means the child run was
/// cancelled. StopAsync must terminate the child run even when passed CancellationToken.None.
/// </summary>
public sealed record SubagentChildSession(
    SessionId ChildSessionId,
    Func<string, CancellationToken, ValueTask> SubmitAsync,
    Task Completion,
    Func<Task<string?>> ReadFinalOutputAsync,
    Func<CancellationToken, ValueTask> StopAsync);

/// <summary>
/// Creates the child session runtime for one subagent run; SubagentService never constructs
/// runtimes itself. The returned session must not have its prompt submitted yet.
/// </summary>
public delegate ValueTask<SubagentChildSession> SubagentChildSessionFactory(
    SubagentRequest request,
    CancellationToken cancellationToken);
