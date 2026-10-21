using System.Runtime.CompilerServices;
using Alcoler.Contracts.Models;

namespace Alcoler.Kernel.Models;

public sealed record ModelRunnerOptions
{
    public int MaxAttempts { get; init; } = 11;
    public bool CommitOnFirstEvent { get; init; }
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);
    // ZCode jitters the capped delay uniformly into [50%, 100%]; the factor is the
    // shrink span so 0.5 reproduces that distribution exactly and 0 disables it.
    public double JitterFactor { get; init; } = 0.5;
    // Mirrors ZCode DEFAULT_MODEL_STREAM_IDLE_TIMEOUT_MS; grows by IdleTimeoutGrowthPerRetry per retry.
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(600);
    public TimeSpan IdleTimeoutGrowthPerRetry { get; init; } = TimeSpan.FromSeconds(30);
    public Func<int, TimeSpan, ValueTask>? RetryDelayObserver { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Retry/boundary wrapper around any <see cref="IChatModel"/>. Events of an attempt are
/// buffered until a committed boundary (first visible content or finish); a failure before
/// the boundary silently discards the prelude and retries, a failure after the boundary is
/// surfaced immediately after the already-emitted events and is never replayed.
/// </summary>
public static class ModelStreamRunner
{
    private static readonly TimeSpan s_infinite = Timeout.InfiniteTimeSpan;

    public static async IAsyncEnumerable<ModelStreamEvent> RunAsync(
        IChatModel model,
        ModelRequest request,
        ModelRunnerOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var timeProvider = options.TimeProvider;
        model = model.CreateRequestScope();

        for (var attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffered = new List<ModelStreamEvent>();
            var committed = false;
            var sawFinish = false;

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var idle = options.IdleTimeout == s_infinite
                ? s_infinite
                : options.IdleTimeout + options.IdleTimeoutGrowthPerRetry * (attempt - 1);
            using var idleTimer = timeProvider.CreateTimer(
                CancelAttempt, attemptCts, idle, s_infinite);

            await using var enumerator = model
                .StreamAsync(request, attemptCts.Token)
                .GetAsyncEnumerator(cancellationToken);

            ModelFailure? failure = null;
            while (true)
            {
                var hasEvent = false;
                ModelStreamEvent? next = null;
                try
                {
                    hasEvent = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasEvent)
                        next = enumerator.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (Exception ex)
                {
                    failure = ModelFailureClassifier.FromException(ex, cancellationToken);
                    break;
                }

                try
                {
                    idleTimer.Change(idle, s_infinite);
                }
                catch (ObjectDisposedException)
                {
                }

                if (!hasEvent)
                    break;
                if (next is ModelStreamFailed failed)
                {
                    committed |= options.CommitOnFirstEvent;
                    failure = failed.Failure;
                    break;
                }

                if (committed)
                {
                    yield return next!;
                    if (next is ModelStreamFinished)
                        sawFinish = true;
                    continue;
                }

                buffered.Add(next!);
                if (next is ModelStreamFinished)
                    sawFinish = true;
                if (options.CommitOnFirstEvent || IsBoundary(next!))
                {
                    committed = true;
                    foreach (var bufferedEvent in buffered)
                        yield return bufferedEvent;
                    buffered.Clear();
                }
            }

            if (failure is null)
            {
                if (sawFinish)
                    yield break;
                failure = new ModelFailure(
                    ModelFailureKind.StreamProtocolError, "model stream ended without a terminal event");
            }

            if (committed || !failure.IsRetryable)
            {
                if (!committed)
                    foreach (var bufferedEvent in buffered)
                        yield return bufferedEvent;
                yield return new ModelStreamFailed(failure);
                yield break;
            }

            if (attempt == options.MaxAttempts)
            {
                foreach (var bufferedEvent in buffered)
                    yield return bufferedEvent;
                yield return new ModelStreamFailed(failure);
                yield break;
            }

            var delay = ComputeDelay(options, attempt, failure);
            if (options.RetryDelayObserver is { } observer)
                await observer(attempt + 1, delay).ConfigureAwait(false);
            try
            {
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    internal static bool IsBoundary(ModelStreamEvent streamEvent) => streamEvent switch
    {
        ModelTextDelta text => text.Delta.Length > 0,
        ModelReasoningDelta reasoning => reasoning.Delta.Length > 0,
        ModelToolCallReady => true,
        ModelStreamFinished => true,
        _ => false,
    };

    private static void CancelAttempt(object? state)
    {
        try
        {
            ((CancellationTokenSource)state!).Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Mirrors ZCode calculateRetryDelay: a reasonable retry-after replaces the backoff
    // entirely (reasonableness is judged against the uncapped exponential delay), and
    // jitter only ever shrinks the capped delay.
    private static readonly TimeSpan s_maxReasonableRetryAfter = TimeSpan.FromMinutes(5);

    private static TimeSpan ComputeDelay(ModelRunnerOptions options, int attempt, ModelFailure failure)
    {
        var uncapped = TimeSpan.FromMilliseconds(options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var capped = uncapped > options.MaxDelay ? options.MaxDelay : uncapped;
        if (failure.RetryAfter is { } retryAfter &&
            retryAfter >= TimeSpan.Zero &&
            (retryAfter <= s_maxReasonableRetryAfter || retryAfter < uncapped))
            return retryAfter;
        if (options.JitterFactor <= 0 || capped <= TimeSpan.Zero)
            return capped;
        return capped * (1 - options.JitterFactor * Random.Shared.NextDouble());
    }
}
