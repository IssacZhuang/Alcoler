using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Runtime;

public sealed class StreamingToolCoordinator : IAsyncDisposable
{
    private const string NotExecutedText =
        "Tool execution was interrupted during streaming recovery before this tool was executed. Treat this tool call as failed and do not retry blindly.";
    private const string UnknownExecutionStateText =
        "Tool execution was interrupted during streaming recovery before a result was committed. Side effects may be unknown; inspect current state before retrying.";
    private static readonly TimeSpan CancelDrainTimeout = TimeSpan.FromMilliseconds(250);

    private readonly ToolRegistry _registry;
    private readonly ToolCallExecutor _executor;
    private readonly ToolBatchRunner _runner;
    private readonly ToolExecutionContextFactory _contextFactory;
    private readonly Func<ToolExecutionNotification, ValueTask> _sink;
    private readonly CancellationToken _turnCancellation;
    private readonly CancellationTokenSource _streamCancellation;
    private readonly SemaphoreSlim _executionSlots = new(ToolScheduler.MaxConcurrency);
    private readonly SemaphoreSlim _notificationGate = new(1);
    private readonly Dictionary<ToolCallId, AcceptedCall> _accepted = [];
    private readonly List<AcceptedCall> _acceptedCalls = [];
    private readonly List<ToolCall> _acceptedCallList = [];
    private Task<ToolBatchResult>? _completion;
    private bool _closed;
    private bool _disposed;

    public StreamingToolCoordinator(
        ToolRegistry registry,
        ToolCallExecutor executor,
        ToolBatchRunner runner,
        ToolExecutionContextFactory contextFactory,
        Func<ToolExecutionNotification, ValueTask> sink,
        CancellationToken cancellationToken)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _turnCancellation = cancellationToken;
        _streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public bool HasAcceptedCalls => _accepted.Count > 0;

    public IReadOnlyList<ToolCall> AcceptedCalls => _acceptedCallList;

    // Admission and terminal operations belong to the model-stream consumer, not tool continuations.
    public ValueTask AcceptAsync(ToolCall call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closed)
            throw new InvalidOperationException("The model stream has already been closed.");
        if (_accepted.ContainsKey(call.Id))
            return ValueTask.CompletedTask;

        var entry = new AcceptedCall(call with { Input = call.Input.Clone() });
        _accepted.Add(call.Id, entry);
        _acceptedCalls.Add(entry);
        _acceptedCallList.Add(entry.Call);
        if (!_turnCancellation.IsCancellationRequested
            && StreamingToolPolicy.CanExecuteDuringStream(_registry.TryFind(call.Name)))
        {
            entry.Execution = Task.Run(() => ExecuteDuringStreamAsync(entry), CancellationToken.None);
        }
        return ValueTask.CompletedTask;
    }

    public Task<ToolBatchResult> CompleteAsync(IReadOnlyList<ToolCall> calls) =>
        Finish(calls, recover: false);

    public Task<ToolBatchResult> RecoverAsync(IReadOnlyList<ToolCall> calls) =>
        Finish(calls, recover: true);

    public async Task AbandonAsync()
    {
        if (_closed)
        {
            if (_completion is not null)
                await _completion.ConfigureAwait(false);
            return;
        }
        _closed = true;
        foreach (var entry in _accepted.Values)
            entry.Finalized = true;
        await _streamCancellation.CancelAsync().ConfigureAwait(false);
        await DrainCancelledAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        try
        {
            await AbandonAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            // An abandoned handler may still hold its cancellation token or notification gate.
            _ = DisposeAfterExecutionsAsync(Task.WhenAll(AcceptedExecutions()));
        }
    }

    private Task<ToolBatchResult> Finish(IReadOnlyList<ToolCall> calls, bool recover)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completion is not null)
            return _completion;
        if (_closed)
            throw new InvalidOperationException("The model stream was abandoned.");
        _closed = true;
        var ordered = MergeCalls(calls);
        var executions = AcceptedExecutions();
        return _completion = recover ? RecoverCoreAsync(ordered, executions) : CompleteCoreAsync(ordered, executions);
    }

    private IReadOnlyList<ToolCall> MergeCalls(IReadOnlyList<ToolCall> calls)
    {
        var seen = new HashSet<ToolCallId>();
        var ordered = new List<ToolCall>(calls.Count + _acceptedCalls.Count);
        foreach (var call in calls)
        {
            if (seen.Add(call.Id))
                ordered.Add(_accepted.TryGetValue(call.Id, out var entry) ? entry.Call : call);
        }
        foreach (var call in _acceptedCallList)
        {
            if (seen.Add(call.Id))
                ordered.Add(call);
        }
        return ordered;
    }

    private async Task<ToolBatchResult> CompleteCoreAsync(
        IReadOnlyList<ToolCall> calls, Task<ToolBatchOutcome>[] executions)
    {
        var completed = await Task.WhenAll(executions).ConfigureAwait(false);
        var scheduled = new ScheduledToolCall[calls.Count];
        for (var index = 0; index < calls.Count; index++)
            scheduled[index] = new ScheduledToolCall(calls[index]);
        return await _runner.ExecuteAsync(
            scheduled,
            _contextFactory,
            NotifyAsync,
            _turnCancellation,
            completed).ConfigureAwait(false);
    }

    private async Task<ToolBatchResult> RecoverCoreAsync(
        IReadOnlyList<ToolCall> calls, Task<ToolBatchOutcome>[] executions)
    {
        await _streamCancellation.CancelAsync().ConfigureAwait(false);
        await DrainCancelledAsync(executions).ConfigureAwait(false);
        var outcomes = new List<ToolBatchOutcome>(calls.Count);
        var stopAfterResult = false;
        foreach (var call in calls)
        {
            _accepted.TryGetValue(call.Id, out var entry);
            if (entry is not null)
                entry.Finalized = true;
            var outcome = entry?.Completed;
            if (outcome is null)
            {
                var result = ToolExecutionResult.Text(entry?.Started == true ? UnknownExecutionStateText : NotExecutedText,
                    isError: true);
                outcome = new ToolBatchOutcome(call, result, TimeSpan.Zero, Ran: entry?.Started == true, CancelledByStop: false);
                await NotifyAsync(new ToolExecutionResultNotification(call, result, TimeSpan.Zero)).ConfigureAwait(false);
            }
            if (outcome.Result.StopTurnAfterResult)
                stopAfterResult = true;
            outcomes.Add(outcome);
        }
        return new ToolBatchResult(outcomes, stopAfterResult);
    }

    private Task<ToolBatchOutcome>[] AcceptedExecutions()
    {
        var count = 0;
        foreach (var entry in _acceptedCalls)
        {
            if (entry.Execution is not null)
                count++;
        }
        var executions = new Task<ToolBatchOutcome>[count];
        var index = 0;
        foreach (var entry in _acceptedCalls)
        {
            if (entry.Execution is not null)
                executions[index++] = entry.Execution;
        }
        return executions;
    }

    private async Task<ToolBatchOutcome> ExecuteDuringStreamAsync(AcceptedCall entry)
    {
        var entered = false;
        try
        {
            await _executionSlots.WaitAsync(_streamCancellation.Token).ConfigureAwait(false);
            entered = true;
            _streamCancellation.Token.ThrowIfCancellationRequested();
            entry.Started = true;
            var outcome = await _executor.ExecuteAsync(entry.Call, _contextFactory, async notification =>
            {
                await _notificationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (entry.Finalized)
                        return;
                    if (notification is ToolExecutionResultNotification result)
                    {
                        entry.Completed = new ToolBatchOutcome(entry.Call, result.Result, result.Duration,
                            Ran: true, CancelledByStop: false);
                    }
                    await _sink(notification).ConfigureAwait(false);
                }
                finally
                {
                    _notificationGate.Release();
                }
            }, _streamCancellation.Token).ConfigureAwait(false);
            return entry.Completed ?? new ToolBatchOutcome(entry.Call, outcome.Result, outcome.Duration,
                Ran: true, CancelledByStop: false);
        }
        catch (Exception)
        {
            if (entry.Completed is { } completed)
                return completed;
            var result = ToolExecutionResult.Text(entry.Started ? UnknownExecutionStateText : NotExecutedText, isError: true);
            var outcome = new ToolBatchOutcome(entry.Call, result, TimeSpan.Zero, entry.Started, CancelledByStop: false);
            entry.Completed = outcome;
            return outcome;
        }
        finally
        {
            if (entered)
                _executionSlots.Release();
        }
    }

    private async ValueTask NotifyAsync(ToolExecutionNotification notification)
    {
        await _notificationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _sink(notification).ConfigureAwait(false);
        }
        finally
        {
            _notificationGate.Release();
        }
    }

    private async Task DrainCancelledAsync(Task<ToolBatchOutcome>[]? executions = null)
    {
        executions ??= AcceptedExecutions();
        try
        {
            await Task.WhenAll(executions).WaitAsync(CancelDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private async Task DisposeAfterExecutionsAsync(Task executions)
    {
        try
        {
            await executions.ConfigureAwait(false);
        }
        finally
        {
            _streamCancellation.Dispose();
            _executionSlots.Dispose();
            _notificationGate.Dispose();
        }
    }

    private sealed class AcceptedCall(ToolCall call)
    {
        public ToolCall Call { get; } = call;
        public Task<ToolBatchOutcome>? Execution { get; set; }
        public volatile ToolBatchOutcome? Completed;
        public volatile bool Started;
        public volatile bool Finalized;
    }
}
