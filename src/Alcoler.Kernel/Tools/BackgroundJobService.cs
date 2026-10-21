using System.Collections.Concurrent;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Jobs run on the thread pool and are tracked in a concurrent registry; each job's
/// CancellationTokenSource lets Stop abort a running job cooperatively.
/// </summary>
public sealed class BackgroundJobService : IBackgroundJobService
{
    private readonly TimeProvider _clock;
    private readonly Func<BackgroundJobInfo, ValueTask> _onCompleted;
    private readonly ConcurrentDictionary<BackgroundJobId, JobEntry> _jobs = new();

    public BackgroundJobService(TimeProvider clock, Func<BackgroundJobInfo, ValueTask> onCompleted)
    {
        _clock = clock;
        _onCompleted = onCompleted;
    }

    public BackgroundJobInfo Start(
        string description,
        string? outputPath,
        Func<CancellationToken, Task<BackgroundJobResult>> run,
        ToolCallId? toolUseId = null)
    {
        var id = StrongId.NewBackgroundJobId();
        var cts = new CancellationTokenSource();
        var info = new BackgroundJobInfo(id, description, _clock.GetUtcNow(), BackgroundJobStatus.Running,
            OutputPath: outputPath, ToolUseId: toolUseId);
        var entry = new JobEntry(cts, info);
        _jobs[id] = entry;

        _ = RunToCompletionAsync(entry, run);
        return info;
    }

    public IReadOnlyList<BackgroundJobInfo> List() =>
        [.. _jobs.Values.Select(static entry => entry.Latest).OrderBy(static job => job.StartedAt)];

    public BackgroundJobInfo? Find(BackgroundJobId id) =>
        _jobs.TryGetValue(id, out var entry) ? entry.Latest : null;

    public bool Stop(BackgroundJobId id)
    {
        if (!_jobs.TryGetValue(id, out var entry))
            return false;
        if (entry.Latest.Status != BackgroundJobStatus.Running)
            return false;

        entry.Cts.Cancel();
        return true;
    }

    public void StopAll()
    {
        foreach (var entry in _jobs.Values)
        {
            if (entry.Latest.Status == BackgroundJobStatus.Running)
                entry.Cts.Cancel();
        }
    }

    private async Task RunToCompletionAsync(JobEntry entry, Func<CancellationToken, Task<BackgroundJobResult>> run)
    {
        BackgroundJobResult result;
        var status = BackgroundJobStatus.Completed;
        try
        {
            using (entry.Cts)
            {
                result = await run(entry.Cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result = new BackgroundJobResult(null, "Job was stopped");
            status = BackgroundJobStatus.Stopped;
        }
        catch (Exception ex)
        {
            result = new BackgroundJobResult(null, ex.Message);
            status = BackgroundJobStatus.Failed;
        }

        entry.Latest = entry.Latest with
        {
            Status = status,
            ExitCode = result.ExitCode,
            Error = result.Error,
        };
        try
        {
            await _onCompleted(entry.Latest).ConfigureAwait(false);
        }
        catch
        {
            // Completion notification is best-effort; the job outcome is already recorded.
        }
    }

    private sealed class JobEntry(CancellationTokenSource cts, BackgroundJobInfo initial)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public BackgroundJobInfo Latest { get; set; } = initial;
    }
}
