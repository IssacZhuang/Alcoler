using System.Collections.Concurrent;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Kernel.Runtime;

namespace Alcoler.Kernel.Subagents;

public sealed record SubagentChannel(
    SubagentId Id,
    SessionId ChildSessionId,
    string Description,
    AgentRuntime Runtime,
    Func<ValueTask>? BeforeResumeAsync = null,
    Func<string, string?, ToolCallId?, CancellationToken, ValueTask>? ResumeAsync = null,
    Func<SubagentStatus>? ReadStatus = null,
    string? OutputFile = null)
{
    internal SemaphoreSlim DeliveryLock { get; } = new(1, 1);

    public bool IsBusy => Runtime.GetSession(ChildSessionId)?.IsTurnActive == true;

    public SubagentStatus Status => ReadStatus?.Invoke() ?? SubagentStatus.Completed;
}

/// <summary>
/// Per-parent registry of subagents so SendMessage can steer a running child
/// or resume a completed one. Channels hold the child runtime strongly, so only
/// the most recently completed channels are kept; older ones are evicted to make
/// their runtimes collectible. Running channels are never evicted.
/// </summary>
public sealed class SubagentMessenger
{
    private const int DefaultCompletedChannelCapacity = 16;

    private readonly int _completedCapacity;
    private readonly ConcurrentDictionary<SessionId, ChannelBook> _byParent = new();

    public SubagentMessenger(int completedChannelCapacity = DefaultCompletedChannelCapacity) =>
        _completedCapacity = completedChannelCapacity;

    public void Register(SessionId parent, SubagentChannel channel)
    {
        var book = _byParent.GetOrAdd(parent, static _ => new ChannelBook());
        lock (book)
        {
            book.ByKey[channel.Id.Value] = channel;
            book.ByKey[channel.ChildSessionId.Value] = channel;
            if (!book.Ordered.Contains(channel))
                book.Ordered.Add(channel);
            TrimCompleted(book);
        }
    }

    public SubagentChannel? Find(SessionId parent, string to)
    {
        if (!_byParent.TryGetValue(parent, out var book))
            return null;
        lock (book)
        {
            return book.ByKey.TryGetValue(to.Trim(), out var channel) ? channel : null;
        }
    }

    private void TrimCompleted(ChannelBook book)
    {
        var completed = book.Ordered.Count(channel => channel.Status != SubagentStatus.Running);
        for (var index = 0; index < book.Ordered.Count && completed > _completedCapacity; index++)
        {
            var channel = book.Ordered[index];
            if (channel.Status == SubagentStatus.Running)
                continue;
            book.Ordered.RemoveAt(index);
            index--;
            completed--;
            book.ByKey.Remove(channel.Id.Value);
            book.ByKey.Remove(channel.ChildSessionId.Value);
        }
    }

    private sealed class ChannelBook
    {
        public Dictionary<string, SubagentChannel> ByKey { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<SubagentChannel> Ordered { get; } = [];
    }
}
