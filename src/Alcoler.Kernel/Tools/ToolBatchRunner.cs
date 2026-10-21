using Alcoler.Contracts.Identifiers;
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
        CancellationToken cancellationToken,
        IReadOnlyList<ToolBatchOutcome>? completedOutcomes = null)
    {
        var outcomes = new ToolBatchOutcome?[calls.Count];
        var indexByCall = new Dictionary<ScheduledToolCall, int>(ReferenceEqualityComparer.Instance);
        var completedById = completedOutcomes?.ToDictionary(outcome => outcome.Call.Id)
            ?? new Dictionary<ToolCallId, ToolBatchOutcome>();
        for (var i = 0; i < calls.Count; i++)
        {
            indexByCall[calls[i]] = i;
            if (completedById.TryGetValue(calls[i].Call.Id, out var completed))
                outcomes[i] = completed with { Call = calls[i].Call };
        }

        var stopped = false;
        foreach (var group in _scheduler.Schedule(calls))
        {
            if (stopped || cancellationToken.IsCancellationRequested)
                break;

            var pending = new ToolSchedulerGroup(group.Parallel,
                group.Calls.Where(call => outcomes[indexByCall[call]] is null).ToArray());
            var groupOutcomes = pending.Parallel && pending.Calls.Count > 1
                ? await ExecuteGroupParallelAsync(pending, contextFactory, sink, cancellationToken).ConfigureAwait(false)
                : await ExecuteGroupSequentiallyAsync(pending, contextFactory, sink, cancellationToken).ConfigureAwait(false);

            foreach (var (call, outcome) in groupOutcomes)
            {
                outcomes[indexByCall[call]] = new ToolBatchOutcome(
                    call.Call,
                    outcome.Result,
                    outcome.Duration,
                    Ran: true,
                    CancelledByStop: false);
            }

            foreach (var call in group.Calls)
            {
                var index = indexByCall[call];
                var outcome = outcomes[index]!;
                if (call.StopTurnAfterResult && !outcome.Result.IsError)
                    outcomes[index] = outcome with { Result = outcome.Result with { StopTurnAfterResult = true } };
                if (outcomes[index]!.Result.StopTurnAfterResult)
                    stopped = true;
            }
        }

        for (var i = 0; i < outcomes.Length; i++)
        {
            if (outcomes[i] is not null)
                continue;
            var skippedByStop = stopped && !cancellationToken.IsCancellationRequested;
            var result = ToolExecutionResult.Text(skippedByStop ? SkippedAfterStopTurnText : CancelledText, isError: true);
            outcomes[i] = new ToolBatchOutcome(
                calls[i].Call,
                result,
                TimeSpan.Zero,
                Ran: false,
                CancelledByStop: skippedByStop);
            await sink(new ToolExecutionResultNotification(calls[i].Call, result, TimeSpan.Zero)).ConfigureAwait(false);
        }

        return new ToolBatchResult(outcomes!, StoppedAfterResult: stopped
            || outcomes.Any(outcome => outcome!.Result.StopTurnAfterResult));
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
