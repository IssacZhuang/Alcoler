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

    public IReadOnlyList<SubagentTaskInfo> List() =>
        [.. _entries.Values.Select(static entry => entry.Snapshot())];

    public SubagentTaskInfo? Find(string taskId) =>
        _entries.TryGetValue(new SubagentId(taskId), out var entry) ? entry.Snapshot() : null;

    public bool TryMarkNotified(string taskId) =>
        _entries.TryGetValue(new SubagentId(taskId), out var entry) && entry.TryMarkNotified();

    // TaskOutputTool's blocking read awaits this instead of polling; the source resets when
    // a completed agent is resumed, so a second wait on the same id parks again.
    internal Task? TryGetCompletion(SubagentId id) =>
        _entries.TryGetValue(id, out var entry) ? entry.Completion : null;

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
        private TaskCompletionSource _completion = NewCompletion();

        public Func<CancellationToken, ValueTask> StopAsync { get; } = stopAsync;

        public Task Completion
        {
            get
            {
                lock (_gate)
                {
                    return _completion.Task;
                }
            }
        }

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
                var next = update(_info);
                OnStatusTransition(next.Status);
                _info = next;
                if (resetNotified)
                    _notified = false;
            }
        }

        public void Restore(SubagentTaskInfo info)
        {
            lock (_gate)
            {
                OnStatusTransition(info.Status);
                _info = info;
                _notified = info.Notified;
            }
        }

        private void OnStatusTransition(SubagentStatus next)
        {
            if (next == _info.Status)
                return;
            if (next == SubagentStatus.Running)
                _completion = NewCompletion();
            else
                _completion.TrySetResult();
        }

        private static TaskCompletionSource NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
