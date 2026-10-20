using System.Collections.Concurrent;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Kernel.Runtime;

namespace Alcoler.Kernel.Subagents;

public sealed record SubagentChannel(
    SubagentId Id,
    SessionId ChildSessionId,
    string Description,
    AgentRuntime Runtime)
{
    public bool IsBusy => Runtime.GetSession(ChildSessionId)?.IsTurnActive == true;
}

/// <summary>
/// Per-parent registry of background subagents so SendMessage can steer a running child
/// or resume a completed one; channels stay registered after completion because resuming
/// a finished agent is the primary use.
/// </summary>
public sealed class SubagentMessenger
{
    private readonly ConcurrentDictionary<SessionId, ConcurrentDictionary<string, SubagentChannel>> _byParent = new();

    public void Register(SessionId parent, SubagentChannel channel)
    {
        var channels = _byParent.GetOrAdd(parent, static _ => new ConcurrentDictionary<string, SubagentChannel>(StringComparer.OrdinalIgnoreCase));
        channels[channel.Id.Value] = channel;
        channels[channel.ChildSessionId.Value] = channel;
    }

    public SubagentChannel? Find(SessionId parent, string to)
    {
        return _byParent.TryGetValue(parent, out var channels)
            && channels.TryGetValue(to.Trim(), out var channel)
            ? channel
            : null;
    }
}
