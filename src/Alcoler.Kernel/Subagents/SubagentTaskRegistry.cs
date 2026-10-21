using System.Collections.Concurrent;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Subagents;

public sealed class SubagentTaskRegistry : ISubagentTasks
{
    private readonly ConcurrentDictionary<SubagentId, Entry> _entries = new();

    public void Register(SubagentTaskInfo info, Func<CancellationToken, ValueTask> stopAsync) =>
        _entries[info.Id] = new Entry(info, stopAsync);

    public void Update(SubagentId id, Func<SubagentTaskInfo, SubagentTaskInfo> update) =>
        Update(id, update, resetNotified: false);

    internal void Update(SubagentId id, Func<SubagentTaskInfo, SubagentTaskInfo> update, bool resetNotified)
    {
        if (_entries.TryGetValue(id, out var entry))
            entry.Update(update, resetNotified);
    }

    public void Restore(SubagentId id, SubagentTaskInfo info)
    {
        if (_entries.TryGetValue(id, out var entry))
            entry.Restore(info);
    }

    public SubagentTaskInfo? Find(string taskId) =>
        _entries.TryGetValue(new SubagentId(taskId), out var entry) ? entry.Snapshot() : null;

    public bool TryMarkNotified(string taskId) =>
        _entries.TryGetValue(new SubagentId(taskId), out var entry) && entry.TryMarkNotified();

    public async ValueTask<bool> TryStopAsync(string taskId, CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(new SubagentId(taskId), out var entry)
            || entry.Snapshot().Status != SubagentStatus.Running)
            return false;

        try
        {
            await entry.StopAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class Entry(SubagentTaskInfo info, Func<CancellationToken, ValueTask> stopAsync)
    {
        private readonly object _gate = new();
        private SubagentTaskInfo _info = info;
        private bool _notified;

        public Func<CancellationToken, ValueTask> StopAsync { get; } = stopAsync;

        public SubagentTaskInfo Snapshot()
        {
            lock (_gate)
            {
                return _info with { Notified = _notified };
            }
        }

        public bool TryMarkNotified()
        {
            lock (_gate)
            {
                if (_notified)
                    return false;
                _notified = true;
                return true;
            }
        }

        public void Update(Func<SubagentTaskInfo, SubagentTaskInfo> update, bool resetNotified)
        {
            lock (_gate)
            {
                _info = update(_info);
                if (resetNotified)
                    _notified = false;
            }
        }

        public void Restore(SubagentTaskInfo info)
        {
            lock (_gate)
            {
                _info = info;
                _notified = info.Notified;
            }
        }
    }
}
