using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Kernel.Subagents;
using Microsoft.Extensions.Logging;

namespace Alcoler.Kernel.Tests.Subagents;

internal sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset start) => _now = start;

    public void AdvanceTo(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class RecordingLogger : ILogger
{
    public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, exception, formatter(state, exception)));
}

internal sealed class FakeChildSession
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeChildSession(string? finalOutput = "child output") => FinalOutput = finalOutput;

    public SessionId ChildSessionId { get; } = StrongId.NewSessionId();

    public string? FinalOutput { get; set; }

    public bool ReadOutputThrows { get; set; }

    public List<string> SubmittedPrompts { get; } = [];

    public int StopCalls { get; private set; }

    public SubagentChildSession ToChildSession() => new(
        ChildSessionId,
        SubmitAsync,
        _completion.Task,
        ReadFinalOutputAsync,
        StopAsync);

    public void Complete() => _completion.TrySetResult();

    public void Fail(Exception exception) => _completion.TrySetException(exception);

    public void Cancel() => _completion.TrySetCanceled();

    private ValueTask SubmitAsync(string prompt, CancellationToken cancellationToken)
    {
        SubmittedPrompts.Add(prompt);
        return ValueTask.CompletedTask;
    }

    private Task<string?> ReadFinalOutputAsync() =>
        ReadOutputThrows
            ? Task.FromException<string?>(new InvalidOperationException("final output unreadable"))
            : Task.FromResult(FinalOutput);

    private ValueTask StopAsync(CancellationToken cancellationToken)
    {
        StopCalls++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSubagentFactory
{
    private readonly Func<SubagentRequest, FakeChildSession>? _create;

    public FakeSubagentFactory(Func<SubagentRequest, FakeChildSession>? create = null) => _create = create;

    public List<FakeChildSession> Children { get; } = [];

    public List<SubagentRequest> Requests { get; } = [];

    public SubagentChildSessionFactory ToFactory() => CreateAsync;

    private ValueTask<SubagentChildSession> CreateAsync(SubagentRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var child = _create is null ? new FakeChildSession() : _create(request);
        Children.Add(child);
        return ValueTask.FromResult(child.ToChildSession());
    }
}

internal sealed class FakeSubagentPort : ISubagentPort
{
    public SubagentResult? RunResult { get; set; }

    public SubagentHandle BackgroundHandle { get; set; } =
        new(StrongId.NewSubagentId(), StrongId.NewSessionId(), "background task", Background: true);

    public List<SubagentRequest> RunRequests { get; } = [];

    public List<(SubagentRequest Request, Func<SubagentResult, ValueTask> Sink)> BackgroundRequests { get; } = [];

    public ValueTask<SubagentResult> RunAsync(SubagentRequest request, CancellationToken cancellationToken)
    {
        RunRequests.Add(request);
        return ValueTask.FromResult(RunResult ?? SubagentTestResults.Completed());
    }

    public ValueTask<SubagentHandle> StartBackgroundAsync(
        SubagentRequest request,
        Func<SubagentResult, ValueTask> onCompleted,
        CancellationToken cancellationToken)
    {
        BackgroundRequests.Add((request, onCompleted));
        return ValueTask.FromResult(BackgroundHandle);
    }
}

internal static class SubagentTestResults
{
    public static SubagentResult Completed(string output = "child output", SubagentStatus status = SubagentStatus.Completed) =>
        new(
            new SubagentState(
                StrongId.NewSubagentId(),
                StrongId.NewSessionId(),
                "task",
                status,
                ResultSummary: "summary",
                StartedAt: DateTimeOffset.UtcNow,
                EndedAt: DateTimeOffset.UtcNow),
            output);
}

internal static class AsyncTestWait
{
    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("condition was not met within 5 seconds");
            await Task.Delay(10);
        }
    }
}
