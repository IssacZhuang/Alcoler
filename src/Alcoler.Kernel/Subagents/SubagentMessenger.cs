using System.Collections.Concurrent;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Kernel.Runtime;

namespace Alcoler.Kernel.Subagents;

public sealed class SubagentChannel(
    SubagentId Id,
    SessionId ChildSessionId,
    string Description,
    AgentRuntime Runtime,
    Func<ValueTask>? BeforeResumeAsync = null,
    Func<string, string?, ToolCallId?, CancellationToken, ValueTask>? ResumeAsync = null,
    Func<SubagentStatus>? ReadStatus = null,
    string? OutputFile = null)
{
    private AgentRuntime? _runtime = Runtime;
    private SubagentStatus _status = SubagentStatus.Completed;

    public SubagentId Id { get; } = Id;
    public SessionId ChildSessionId { get; } = ChildSessionId;
    public string Description { get; } = Description;
    public string? OutputFile { get; } = OutputFile;
    public AgentRuntime Runtime => _runtime ?? throw new InvalidOperationException("Subagent runtime is not resident.");
    public Func<ValueTask>? BeforeResumeAsync { get; internal set; } = BeforeResumeAsync;
    public Func<string, string?, ToolCallId?, CancellationToken, ValueTask>? ResumeAsync { get; internal set; } = ResumeAsync;
    public Func<SubagentStatus>? ReadStatus { get; internal set; } = ReadStatus;

    internal SemaphoreSlim DeliveryLock { get; } = new(1, 1);
    internal AgentRuntime? ResidentRuntime => _runtime;
    internal SubagentMessenger? Messenger { get; set; }
    internal Func<SubagentChannel, CancellationToken, ValueTask>? ColdLoadAsync { get; init; }

    public bool IsBusy => Status == SubagentStatus.Running || _runtime?.GetSession(ChildSessionId)?.IsTurnActive == true;
    public SubagentStatus Status => ReadStatus?.Invoke() ?? _status;

    internal async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_runtime is not null)
            return;
        if (ColdLoadAsync is null)
            throw new InvalidOperationException("Subagent cannot be resumed after unloading.");
        await ColdLoadAsync(this, cancellationToken).ConfigureAwait(false);
    }

    internal void BindRuntime(
        AgentRuntime runtime,
        Func<string, string?, ToolCallId?, CancellationToken, ValueTask> resumeAsync,
        Func<SubagentStatus> readStatus)
    {
        _runtime = runtime;
        ResumeAsync = resumeAsync;
        ReadStatus = readStatus;
    }

    internal AgentRuntime? DetachRuntime()
    {
        var runtime = _runtime;
        _status = Status;
        _runtime = null;
        BeforeResumeAsync = null;
        ResumeAsync = null;
        ReadStatus = null;
        return runtime;
    }
}

public sealed class SubagentMessenger
{
    private const int DefaultCompletedChannelCapacity = 16;

    private readonly int _completedCapacity;
    private readonly ConcurrentDictionary<SessionId, ChannelBook> _byParent = new();

    public SubagentMessenger(int completedChannelCapacity = DefaultCompletedChannelCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedChannelCapacity);
        _completedCapacity = completedChannelCapacity;
    }

    public void Register(SessionId parent, SubagentChannel channel)
    {
        var book = _byParent.GetOrAdd(parent, static _ => new ChannelBook());
        lock (book)
        {
            if (book.Suspended)
                throw new InvalidOperationException("Coordinator session is unloading.");
            channel.Messenger = this;
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
            return book.ByKey.TryGetValue(to.Trim(), out var channel) ? channel : null;
    }

    internal bool CanDeliver(SessionId parent, SubagentChannel channel)
    {
        if (!_byParent.TryGetValue(parent, out var book))
            return false;
        lock (book)
            return !book.Suspended && book.ByKey.TryGetValue(channel.Id.Value, out var current)
                && ReferenceEquals(current, channel);
    }

    internal void OnTerminal(SessionId parent, SubagentChannel channel)
    {
        if (!_byParent.TryGetValue(parent, out var book))
            return;
        lock (book)
        {
            if (!book.ByKey.TryGetValue(channel.Id.Value, out var current) || !ReferenceEquals(current, channel))
                return;
            book.Ordered.Remove(channel);
            book.Ordered.Add(channel);
            TrimCompleted(book);
        }
    }

    internal void Maintain(SessionId parent)
    {
        if (!_byParent.TryGetValue(parent, out var book))
            return;
        lock (book)
            TrimCompleted(book);
    }

    internal IEnumerable<SessionId> ParentIds => _byParent.Keys;

    internal void ActivateParent(SessionId parent)
    {
        if (!_byParent.TryGetValue(parent, out var book))
            return;
        lock (book)
            book.Suspended = false;
    }

    internal async ValueTask<ParentLease?> SuspendParentAsync(SessionId parent, CancellationToken cancellationToken)
    {
        if (!_byParent.TryGetValue(parent, out var book))
            return null;
        await book.LifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        SubagentChannel[] channels;
        bool wasSuspended;
        lock (book)
        {
            wasSuspended = book.Suspended;
            book.Suspended = true;
            channels = [.. book.Ordered];
        }
        var acquired = 0;
        try
        {
            foreach (var channel in channels)
            {
                await channel.DeliveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired++;
            }
            return new ParentLease(this, parent, book, channels, wasSuspended);
        }
        catch
        {
            for (var index = 0; index < acquired; index++)
                channels[index].DeliveryLock.Release();
            lock (book)
                book.Suspended = wasSuspended;
            book.LifecycleLock.Release();
            throw;
        }
    }

    private void TrimCompleted(ChannelBook book, bool ownsDeliveryLocks = false)
    {
        if (book.Suspended && !ownsDeliveryLocks)
            return;
        var completed = book.Ordered.Count(channel => channel.Status != SubagentStatus.Running);
        for (var index = 0; index < book.Ordered.Count && completed > _completedCapacity; index++)
        {
            var channel = book.Ordered[index];
            // Delivery takes the book lock after this lock; maintenance must never wait for delivery.
            if (!ownsDeliveryLocks && !channel.DeliveryLock.Wait(0))
                continue;
            try
            {
                if (channel.IsBusy)
                    continue;
                book.Ordered.RemoveAt(index);
                index--;
                completed--;
                book.ByKey.Remove(channel.Id.Value);
                book.ByKey.Remove(channel.ChildSessionId.Value);
            }
            finally
            {
                if (!ownsDeliveryLocks)
                    channel.DeliveryLock.Release();
            }
        }
    }

    internal sealed class ParentLease(
        SubagentMessenger messenger,
        SessionId parent,
        ChannelBook book,
        SubagentChannel[] channels,
        bool wasSuspended) : IDisposable
    {
        private bool _committed;

        public IReadOnlyList<AgentRuntime> DetachRuntimes(bool forget)
        {
            var runtimes = new List<AgentRuntime>();
            foreach (var channel in channels)
            {
                if (channel.DetachRuntime() is { } runtime)
                    runtimes.Add(runtime);
            }
            lock (book)
                messenger.TrimCompleted(book, ownsDeliveryLocks: true);
            if (forget)
                messenger._byParent.TryRemove(new KeyValuePair<SessionId, ChannelBook>(parent, book));
            _committed = true;
            return runtimes;
        }

        public void Dispose()
        {
            for (var index = channels.Length - 1; index >= 0; index--)
                channels[index].DeliveryLock.Release();
            lock (book)
            {
                if (!_committed)
                    book.Suspended = wasSuspended;
            }
            book.LifecycleLock.Release();
            if (!_committed && !wasSuspended)
                messenger.Maintain(parent);
        }
    }

    internal sealed class ChannelBook
    {
        public SemaphoreSlim LifecycleLock { get; } = new(1, 1);
        public bool Suspended { get; set; }
        public Dictionary<string, SubagentChannel> ByKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SubagentChannel> Ordered { get; } = [];
    }
}
