using System.Runtime.CompilerServices;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Xunit;

using Alcoler.Kernel.Models;

namespace Alcoler.Kernel.Tests.Models;

public class ModelStreamRunnerTests
{
    private static readonly ModelRequest s_request = new(
        [new ModelInputMessage(MessageRole.User, [new InputTextBlock("go")])],
        [],
        new ModelCallOptions());

    private static ModelFailure NetworkFailure() => new(ModelFailureKind.NetworkError, "connection reset");

    private static async Task<List<ModelStreamEvent>> CollectAsync(
        IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in stream)
            events.Add(streamEvent);
        return events;
    }

    [Fact]
    public async Task RetryBeforeBoundary_SucceedsSilently()
    {
        var model = new ScriptedChatModel(
        [
            new ScriptStep.FailOnce(NetworkFailure()),
            new ScriptStep.Events(
                new ModelStreamStarted(),
                new ModelTextDelta("ok"),
                new ModelStreamFinished(ModelFinishReason.Stop)),
        ]);
        var options = new ModelRunnerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(1),
            IdleTimeout = Timeout.InfiniteTimeSpan,
        };

        var events = await CollectAsync(ModelStreamRunner.RunAsync(model, s_request, options));

        Assert.Equal(
        [
            typeof(ModelStreamStarted),
            typeof(ModelTextDelta),
            typeof(ModelStreamFinished),
        ], events.Select(e => e.GetType()).ToArray());
        Assert.Equal(2, model.Attempts);
    }

    [Fact]
    public async Task FailureAfterBoundary_NeverReplays()
    {
        var model = new ScriptedChatModel(
        [
            new ScriptStep.FailOnce(NetworkFailure(), new ModelTextDelta("partial")),
            new ScriptStep.Events(new ModelStreamFinished(ModelFinishReason.Stop)),
        ]);
        var options = new ModelRunnerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(1),
            IdleTimeout = Timeout.InfiniteTimeSpan,
        };

        var events = await CollectAsync(ModelStreamRunner.RunAsync(model, s_request, options));

        Assert.Equal(2, events.Count);
        Assert.IsType<ModelTextDelta>(events[0]);
        Assert.Equal("partial", ((ModelTextDelta)events[0]).Delta);
        var failed = Assert.IsType<ModelStreamFailed>(events[1]);
        Assert.Equal(ModelFailureKind.NetworkError, failed.Failure.Kind);
        Assert.Equal(1, model.Attempts);
    }

    [Fact]
    public async Task RetryAfter_IsRespectedWhenLargerThanBackoff()
    {
        var model = new ScriptedChatModel(
        [
            new ScriptStep.FailOnce(new ModelFailure(
                ModelFailureKind.RateLimited, "slow down", RetryAfter: TimeSpan.FromSeconds(30))),
            new ScriptStep.Events(new ModelTextDelta("done"), new ModelStreamFinished(ModelFinishReason.Stop)),
        ]);
        var delays = new List<(int Attempt, TimeSpan Delay)>();
        var options = new ModelRunnerOptions
        {
            JitterFactor = 0,
            IdleTimeout = Timeout.InfiniteTimeSpan,
            TimeProvider = new ManualTimeProvider { AutoFireTimers = true },
            RetryDelayObserver = (attempt, delay) =>
            {
                delays.Add((attempt, delay));
                return ValueTask.CompletedTask;
            },
        };

        var events = await CollectAsync(ModelStreamRunner.RunAsync(model, s_request, options));

        Assert.Equal(2, model.Attempts);
        var retry = Assert.Single(delays);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(30), retry.Delay);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task UnreasonableRetryAfter_FallsBackToBackoff()
    {
        var model = new ScriptedChatModel(
        [
            new ScriptStep.FailOnce(new ModelFailure(
                ModelFailureKind.RateLimited, "slow down", RetryAfter: TimeSpan.FromMinutes(10))),
            new ScriptStep.Events(new ModelTextDelta("done"), new ModelStreamFinished(ModelFinishReason.Stop)),
        ]);
        var delays = new List<(int Attempt, TimeSpan Delay)>();
        var options = new ModelRunnerOptions
        {
            JitterFactor = 0,
            IdleTimeout = Timeout.InfiniteTimeSpan,
            TimeProvider = new ManualTimeProvider { AutoFireTimers = true },
            RetryDelayObserver = (attempt, delay) =>
            {
                delays.Add((attempt, delay));
                return ValueTask.CompletedTask;
            },
        };

        await CollectAsync(ModelStreamRunner.RunAsync(model, s_request, options));

        var retry = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(2), retry.Delay);
    }

    [Fact]
    public async Task LongRetryAfter_IsHonoredWhenBackoffWaitsLonger()
    {
        var model = new ScriptedChatModel(
        [
            new ScriptStep.FailOnce(new ModelFailure(
                ModelFailureKind.RateLimited, "slow down", RetryAfter: TimeSpan.FromMinutes(6))),
            new ScriptStep.Events(new ModelTextDelta("done"), new ModelStreamFinished(ModelFinishReason.Stop)),
        ]);
        var delays = new List<(int Attempt, TimeSpan Delay)>();
        var options = new ModelRunnerOptions
        {
            BaseDelay = TimeSpan.FromMinutes(7),
            MaxDelay = TimeSpan.FromMinutes(10),
            JitterFactor = 0,
            IdleTimeout = Timeout.InfiniteTimeSpan,
            TimeProvider = new ManualTimeProvider { AutoFireTimers = true },
            RetryDelayObserver = (attempt, delay) =>
            {
                delays.Add((attempt, delay));
                return ValueTask.CompletedTask;
            },
        };

        await CollectAsync(ModelStreamRunner.RunAsync(model, s_request, options));

        var retry = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromMinutes(6), retry.Delay);
    }

    [Fact]
    public async Task ToolInputDelta_IsNotABoundaryAndRetriesSilently()
    {
        var model = new ScriptedChatModel(
        [
            new ScriptStep.FailOnce(
                NetworkFailure(),
                new ModelToolInputStarted(new ToolCallId("call-1"), "Read"),
                new ModelToolInputDelta(new ToolCallId("call-1"), """{"file_path":"a.txt"}""")),
            new ScriptStep.Events(
                new ModelToolCallReady(new ToolCall(
                    new ToolCallId("call-1"), "Read",
                    System.Text.Json.JsonSerializer.SerializeToElement(new { file_path = "a.txt" }))),
                new ModelStreamFinished(ModelFinishReason.ToolCalls)),
        ]);
        var options = new ModelRunnerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(1),
            IdleTimeout = Timeout.InfiniteTimeSpan,
        };

        var events = await CollectAsync(ModelStreamRunner.RunAsync(model, s_request, options));

        Assert.Equal(2, model.Attempts);
        Assert.Equal(
        [
            typeof(ModelToolInputStarted),
            typeof(ModelToolInputDelta),
            typeof(ModelToolCallReady),
            typeof(ModelStreamFinished),
        ], events.Select(e => e.GetType()).ToArray());
        Assert.DoesNotContain(events, e => e is ModelStreamFailed);
    }

    [Fact]
    public async Task NonRetryableFailure_SurfacesImmediately()
    {
        var model = new ScriptedChatModel(
        [
            new ScriptStep.Fail(new ModelFailure(ModelFailureKind.AuthenticationFailed, "bad key")),
            new ScriptStep.Events(new ModelStreamFinished(ModelFinishReason.Stop)),
        ]);
        var delays = new List<(int Attempt, TimeSpan Delay)>();
        var options = new ModelRunnerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(1),
            IdleTimeout = Timeout.InfiniteTimeSpan,
            RetryDelayObserver = (attempt, delay) =>
            {
                delays.Add((attempt, delay));
                return ValueTask.CompletedTask;
            },
        };

        var events = await CollectAsync(ModelStreamRunner.RunAsync(model, s_request, options));

        var failed = Assert.IsType<ModelStreamFailed>(Assert.Single(events));
        Assert.Equal(ModelFailureKind.AuthenticationFailed, failed.Failure.Kind);
        Assert.Equal(1, model.Attempts);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task ExhaustedAttempts_YieldsFailureAfterLastAttempt()
    {
        var model = new ScriptedChatModel([new ScriptStep.Fail(NetworkFailure())]);
        var delays = new List<(int Attempt, TimeSpan Delay)>();
        var options = new ModelRunnerOptions
        {
            MaxAttempts = 3,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            IdleTimeout = Timeout.InfiniteTimeSpan,
            RetryDelayObserver = (attempt, delay) =>
            {
                delays.Add((attempt, delay));
                return ValueTask.CompletedTask;
            },
        };

        var events = await CollectAsync(ModelStreamRunner.RunAsync(model, s_request, options));

        var failed = Assert.IsType<ModelStreamFailed>(Assert.Single(events));
        Assert.Equal(ModelFailureKind.NetworkError, failed.Failure.Kind);
        Assert.Equal(3, model.Attempts);
        Assert.Equal([2, 3], delays.Select(d => d.Attempt).ToArray());
        Assert.All(delays.Select(d => d.Delay).ToArray(), d => Assert.True(d <= TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public void Defaults_IdleTimeoutIsTenMinutesWithGrowthPerRetry()
    {
        var options = new ModelRunnerOptions();

        Assert.Equal(TimeSpan.FromSeconds(600), options.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.IdleTimeoutGrowthPerRetry);
    }

    [Fact]
    public async Task IdleTimeout_DiscardsHangingAttemptAndRetries()
    {
        var model = new HangingThenGoodModel();
        var timeProvider = new ManualTimeProvider();
        var options = new ModelRunnerOptions
        {
            BaseDelay = TimeSpan.Zero,
            JitterFactor = 0,
            IdleTimeout = TimeSpan.FromSeconds(1),
            IdleTimeoutGrowthPerRetry = TimeSpan.Zero,
            TimeProvider = timeProvider,
        };

        var consumer = Task.Run(() => CollectAsync(ModelStreamRunner.RunAsync(
            model, s_request, options, CancellationToken.None)));
        await model.FirstAttemptStarted.Task;
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        var events = await consumer;

        Assert.Equal(
        [
            typeof(ModelStreamStarted),
            typeof(ModelTextDelta),
            typeof(ModelStreamFinished),
        ], events.Select(e => e.GetType()).ToArray());
        Assert.Equal(2, model.Attempts);
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceledException()
    {
        var model = new HangingThenGoodModel();
        using var cts = new CancellationTokenSource();
        var options = new ModelRunnerOptions
        {
            BaseDelay = TimeSpan.Zero,
            IdleTimeout = Timeout.InfiniteTimeSpan,
            TimeProvider = new ManualTimeProvider(),
        };

        var consumer = Task.Run(() => CollectAsync(ModelStreamRunner.RunAsync(
            model, s_request, options, cts.Token)));
        await model.FirstAttemptStarted.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        Assert.Equal(1, model.Attempts);
    }

    [Fact]
    public async Task StreamEndingWithoutTerminal_FailsWithProtocolError()
    {
        var model = new ScriptedChatModel(
        [
            new ScriptStep.Events(new ModelStreamStarted()),
        ]);
        var options = new ModelRunnerOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(1),
            IdleTimeout = Timeout.InfiniteTimeSpan,
        };

        var events = await CollectAsync(ModelStreamRunner.RunAsync(model, s_request, options));

        Assert.Equal(2, events.Count);
        Assert.IsType<ModelStreamStarted>(events[0]);
        var failed = Assert.IsType<ModelStreamFailed>(events[1]);
        Assert.Equal(ModelFailureKind.StreamProtocolError, failed.Failure.Kind);
        Assert.Equal(1, model.Attempts);
    }

    private sealed class HangingThenGoodModel : IChatModel
    {
        private int _attempts;

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts => Volatile.Read(ref _attempts);

        public ModelDescriptor Descriptor { get; } = new(
            "scripted", "hanging", "Hanging", new ModelProperties(ContextWindowTokens: 200_000));

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            yield return new ModelStreamStarted();
            if (attempt == 1)
            {
                FirstAttemptStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            yield return new ModelTextDelta("recovered");
            yield return new ModelStreamFinished(ModelFinishReason.Stop);
        }
    }
}
