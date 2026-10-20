using System.Collections;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

public delegate ValueTask<ToolExecutionContext> ToolExecutionContextFactory(
    ToolCall call,
    CancellationToken cancellationToken);

public sealed record ToolBatchOutcome(
    ToolCall Call,
    ToolExecutionResult Result,
    TimeSpan Duration,
    bool Ran,
    bool CancelledByStop);

public sealed record ToolBatchResult(IReadOnlyList<ToolBatchOutcome> Outcomes, bool StoppedAfterResult = false);

public sealed class ToolBatchRunner
{
    // ZCode call-runner abort text; the skip-after-stop-turn path carries its own message.
    private const string CancelledText = "Tool execution cancelled";
    private const string SkippedAfterStopTurnText = "Tool cancelled because a previous tool result requested a turn stop.";

    private readonly ToolScheduler _scheduler;
    private readonly ToolCallExecutor _executor;

    public ToolBatchRunner(ToolScheduler scheduler, ToolCallExecutor executor)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<ToolBatchResult> ExecuteAsync(
        IReadOnlyList<ScheduledToolCall> calls,
        ToolExecutionContextFactory contextFactory,
        Func<ToolExecutionNotification, ValueTask> sink,
        CancellationToken cancellationToken)
    {
        var outcomes = new ToolBatchOutcome?[calls.Count];
        var indexByCall = new Dictionary<ScheduledToolCall, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < calls.Count; i++)
            indexByCall[calls[i]] = i;

        var stopped = false;
        foreach (var group in _scheduler.Schedule(calls))
        {
            if (stopped || cancellationToken.IsCancellationRequested)
                break;

            var groupOutcomes = group.Parallel && group.Calls.Count > 1
                ? await ExecuteGroupParallelAsync(group, contextFactory, sink, cancellationToken).ConfigureAwait(false)
                : await ExecuteGroupSequentiallyAsync(group, contextFactory, sink, cancellationToken).ConfigureAwait(false);

            foreach (var (call, outcome) in groupOutcomes)
            {
                outcomes[indexByCall[call]] = new ToolBatchOutcome(
                    call.Call,
                    outcome.Result,
                    outcome.Duration,
                    Ran: true,
                    CancelledByStop: false);
                if (call.StopTurnAfterResult && !outcome.Result.IsError)
                    stopped = true;
            }
        }

        for (var i = 0; i < outcomes.Length; i++)
        {
            if (outcomes[i] is not null)
                continue;
            var skippedByStop = stopped && !cancellationToken.IsCancellationRequested;
            outcomes[i] = new ToolBatchOutcome(
                calls[i].Call,
                ToolExecutionResult.Text(skippedByStop ? SkippedAfterStopTurnText : CancelledText),
                TimeSpan.Zero,
                Ran: false,
                CancelledByStop: skippedByStop);
        }

        return new ToolBatchResult(outcomes!, StoppedAfterResult: stopped);
    }

    private async Task<List<(ScheduledToolCall Call, ToolExecutionOutcome Outcome)>> ExecuteGroupParallelAsync(
        ToolSchedulerGroup group,
        ToolExecutionContextFactory contextFactory,
        Func<ToolExecutionNotification, ValueTask> sink,
        CancellationToken cancellationToken)
    {
        var tasks = group.Calls
            .Select(call => _executor.ExecuteAsync(call.Call, contextFactory, sink, cancellationToken).AsTask())
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return group.Calls.Zip(results, (call, outcome) => (call, outcome)).ToList();
    }

    private async Task<List<(ScheduledToolCall Call, ToolExecutionOutcome Outcome)>> ExecuteGroupSequentiallyAsync(
        ToolSchedulerGroup group,
        ToolExecutionContextFactory contextFactory,
        Func<ToolExecutionNotification, ValueTask> sink,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<(ScheduledToolCall Call, ToolExecutionOutcome Outcome)>();
        foreach (var call in group.Calls)
        {
            var outcome = await _executor.ExecuteAsync(call.Call, contextFactory, sink, cancellationToken).ConfigureAwait(false);
            outcomes.Add((call, outcome));
        }
        return outcomes;
    }
}
