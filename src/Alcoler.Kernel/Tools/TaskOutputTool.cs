using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Subagents;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Reads or awaits a background job's status and output. The executor timeout is disabled
/// (DefaultTimeoutMs null) because waiting is governed by the tool's own timeout parameter.
/// </summary>
public sealed class TaskOutputTool : ITool
{
    internal const int DefaultTimeoutMs = 600_000;
    internal const int MaxTimeoutMs = 600_000;
    internal const int DefaultOutputLength = 32_000;
    private const int PollIntervalMs = 100;
    private const int MaxOutputFileBytes = 8 * 1024 * 1024;
    private const string TaskType = "local_bash";
    private const string SubagentTaskType = "local_agent";
    private const string InterruptedHint =
        "\n\nA message, task notification, or user input arrived while you were waiting; it is queued and will start your next turn. End your turn now to process it instead of waiting again.";

    public ToolContract Contract { get; } = new(
        "TaskOutput",
        """
        Waits for a background task (shell or agent) to finish and returns its result, or checks its current status without waiting. When you are not blocked on a result, prefer ending your turn: completed tasks also deliver a <task-notification> on their own.

        - block=true (default): event-driven wait. It ends the moment the task finishes (returning the result inline) OR any message, task notification, or user input arrives (retrieval_status "interrupted" — end your turn so the queued input can be processed). One call with a long timeout replaces any polling loop; never re-call in a loop.
        - block=false: instant status check (running/completed/failed, exit code when finished).
        - Task IDs can be found using the /tasks command.
        - Do NOT Read local_agent .output files — they are full conversation transcripts.
        """,
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "task_id": { "type": "string", "description": "The task ID to get output from" },
            "block": { "type": "boolean", "default": true, "description": "Whether to wait for completion" },
            "timeout": { "type": "number", "minimum": 0, "maximum": 600000, "default": 600000, "description": "Max wait time in ms; the wait returns early on completion or incoming activity, so prefer the maximum." }
          },
          "required": ["task_id", "block", "timeout"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.None,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true,
        DefaultTimeoutMs: null);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.BackgroundJobs is null && context.Subagents is null)
            return ToolExecutionResult.Text(
                "Background job service is not configured for TaskOutput",
                isError: true);

        var taskId = ReadString(context.Input, "task_id");
        if (string.IsNullOrEmpty(taskId))
            return ToolExecutionResult.Text("Task ID is required", isError: true);

        var job = context.BackgroundJobs?.Find(new BackgroundJobId(taskId));
        if (job is null)
        {
            if (context.Subagents is { } subagents)
                return await ReadSubagentAsync(subagents, taskId, context, cancellationToken).ConfigureAwait(false);
            return ToolExecutionResult.Text($"No task found with ID: {taskId}", isError: true);
        }

        var block = ReadBool(context.Input, "block", defaultValue: true);
        var timeoutMs = Math.Clamp(ReadInt(context.Input, "timeout") ?? DefaultTimeoutMs, 0, MaxTimeoutMs);

        if (block)
        {
            var deadline = context.Clock.GetUtcNow() + TimeSpan.FromMilliseconds(timeoutMs);
            while (job.Status == BackgroundJobStatus.Running)
            {
                if (context.Clock.GetUtcNow() >= deadline)
                    return ToolExecutionResult.Text(FormatResult("timeout", job));
                var completion = (context.BackgroundJobs as BackgroundJobService)
                    ?.TryGetCompletion(new BackgroundJobId(taskId));
                switch (await AwaitWakeAsync(completion, context, deadline, cancellationToken).ConfigureAwait(false))
                {
                    case WakeReason.Deadline:
                        return ToolExecutionResult.Text(FormatResult("timeout", job));
                    case WakeReason.Interrupted:
                        job = context.BackgroundJobs?.Find(new BackgroundJobId(taskId));
                        if (job is null)
                            return ToolExecutionResult.Text(FormatResult("timeout", null));
                        if (job.Status == BackgroundJobStatus.Running)
                            return ToolExecutionResult.Text(FormatResult("interrupted", job) + InterruptedHint);
                        break;
                    case WakeReason.TargetCompleted:
                        job = context.BackgroundJobs?.Find(new BackgroundJobId(taskId));
                        if (job is null)
                            return ToolExecutionResult.Text(FormatResult("timeout", null));
                        break;
                }
            }
        }

        if (job.Status == BackgroundJobStatus.Running)
            return ToolExecutionResult.Text(FormatResult("not_ready", job));
        return ToolExecutionResult.Text(FormatResult("success", job));
    }

    private static async ValueTask<ToolExecutionResult> ReadSubagentAsync(
        ISubagentTasks subagents,
        string taskId,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var block = ReadBool(context.Input, "block", defaultValue: true);
        var timeoutMs = Math.Clamp(ReadInt(context.Input, "timeout") ?? DefaultTimeoutMs, 0, MaxTimeoutMs);
        var task = subagents.Find(taskId);
        if (task is null)
            return ToolExecutionResult.Text($"No task found with ID: {taskId}", isError: true);

        if (block)
        {
            var deadline = context.Clock.GetUtcNow() + TimeSpan.FromMilliseconds(timeoutMs);
            while (task.Status == SubagentStatus.Running)
            {
                if (context.Clock.GetUtcNow() >= deadline)
                    return ToolExecutionResult.Text(FormatSubagentResult("timeout", task));
                var completion = (subagents as SubagentTaskRegistry)?.TryGetCompletion(task.Id);
                switch (await AwaitWakeAsync(completion, context, deadline, cancellationToken).ConfigureAwait(false))
                {
                    case WakeReason.Deadline:
                        return ToolExecutionResult.Text(FormatSubagentResult("timeout", task));
                    case WakeReason.Interrupted:
                        task = subagents.Find(taskId);
                        if (task is null)
                            return ToolExecutionResult.Text(FormatSubagentResult("timeout", null));
                        if (task.Status == SubagentStatus.Running)
                            return ToolExecutionResult.Text(
                                FormatSubagentResult("interrupted", task) + InterruptedHint);
                        break;
                    case WakeReason.TargetCompleted:
                        task = subagents.Find(taskId);
                        if (task is null)
                            return ToolExecutionResult.Text(FormatSubagentResult("timeout", null));
                        break;
                }
            }
        }

        if (task.Status == SubagentStatus.Running)
            return ToolExecutionResult.Text(FormatSubagentResult("not_ready", task));
        var text = FormatSubagentResult("success", task);
        subagents.TryMarkNotified(taskId);
        return ToolExecutionResult.Text(text);
    }

    private enum WakeReason
    {
        TargetCompleted,
        Interrupted,
        Deadline,
    }

    // Wakes on target completion, queued turn input, or the deadline — whichever comes first.
    // Without a completion signal, a short poll substitutes so terminal transitions are still
    // observed; without a turn-input signal, only completion/deadline can end the wait.
    private static async Task<WakeReason> AwaitWakeAsync(
        Task? completion,
        ToolExecutionContext context,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var remaining = deadline - context.Clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return WakeReason.Deadline;

        // (wait, reason): a poll wake means "recheck status", so it shares the completion reason.
        var waits = new List<(Task Wait, WakeReason Reason)>
        {
            (Task.Delay(remaining, context.Clock, cancellationToken), WakeReason.Deadline),
        };
        if (completion is not null)
            waits.Add((completion, WakeReason.TargetCompleted));
        else
            waits.Add((Task.Delay(TimeSpan.FromMilliseconds(PollIntervalMs), context.Clock, cancellationToken), WakeReason.TargetCompleted));
        if (context.TurnInputSignal is { } signal)
            waits.Add((signal.WaitAsync(signal.Version, cancellationToken), WakeReason.Interrupted));

        var done = await Task.WhenAny(waits.Select(static wait => wait.Wait)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return waits.First(wait => wait.Wait == done).Reason;
    }

    internal static string FormatSubagentResult(string retrievalStatus, SubagentTaskInfo? task)
    {
        var blocks = new List<string> { $"<retrieval_status>{retrievalStatus}</retrieval_status>" };
        if (task is not null)
        {
            blocks.Add($"<task_id>{Escape(task.Id.Value)}</task_id>");
            blocks.Add($"<task_type>{SubagentTaskType}</task_type>");
            blocks.Add($"<status>{SubagentStatusText(task.Status)}</status>");
            var output = ReadSubagentOutput(task);
            if (output.Trim().Length > 0)
                blocks.Add($"<output>\n{output.TrimEnd()}\n</output>");
            if (!string.IsNullOrEmpty(task.Error))
                blocks.Add($"<error>{Escape(task.Error)}</error>");
        }

        return string.Join("\n\n", blocks);
    }

    private static string ReadSubagentOutput(SubagentTaskInfo task)
    {
        var output = task.Result;
        if (string.IsNullOrWhiteSpace(output) && task.OutputFile is { } file)
            output = ReadOutputTail(file);
        if (string.IsNullOrWhiteSpace(output))
            return string.Empty;
        return task.OutputFile is { } outputPath ? TruncateTail(output, outputPath) : output;
    }

    private static string ReadOutputTail(string outputPath)
    {
        try
        {
            using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0L, stream.Length - MaxOutputFileBytes);
            var buffer = new byte[stream.Length - start];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            return start > 0 ? $"[{start / 1024}KB of earlier output omitted]\n{text}" : text;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string SubagentStatusText(SubagentStatus status) => status switch
    {
        SubagentStatus.Running => "running",
        SubagentStatus.Completed => "completed",
        SubagentStatus.Failed => "failed",
        _ => "stopped",
    };

    internal static string FormatResult(string retrievalStatus, BackgroundJobInfo? job)
    {
        var blocks = new List<string> { $"<retrieval_status>{retrievalStatus}</retrieval_status>" };
        if (job is not null)
        {
            blocks.Add($"<task_id>{Escape(job.Id.Value)}</task_id>");
            blocks.Add($"<task_type>{TaskType}</task_type>");
            blocks.Add($"<status>{StatusText(job.Status)}</status>");
            if (job.ExitCode is { } exitCode)
                blocks.Add($"<exit_code>{exitCode}</exit_code>");
            var output = ReadOutput(job);
            if (output.Trim().Length > 0)
                blocks.Add($"<output>\n{output.TrimEnd()}\n</output>");
            if (job.Error is { } error && error.Length > 0)
                blocks.Add($"<error>{Escape(error)}</error>");
        }

        return string.Join("\n\n", blocks);
    }

    private static string ReadOutput(BackgroundJobInfo job)
    {
        if (job.OutputPath is null || !File.Exists(job.OutputPath))
            return string.Empty;
        try
        {
            // 3 bytes is the widest UTF-8 encoding of a single char, and the slack absorbs
            // the partial sequence the window start can cut; TruncateTail keeps only the
            // final DefaultOutputLength chars, so the head never reaches the result.
            using var stream = new FileStream(job.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[4];
            var headerLength = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var prefix = header[..headerLength];
            if (prefix.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])
                || prefix.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])
                || prefix.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
            {
                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return TruncateTail(reader.ReadToEnd(), job.OutputPath);
            }
            var start = Math.Max(0L, stream.Length - (DefaultOutputLength * 3L + 512));
            var buffer = new byte[stream.Length - start];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            if (start == 0 && text.StartsWith('\uFEFF'))
                text = text[1..];
            return TruncateTail(text, job.OutputPath);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string TruncateTail(string output, string outputPath)
    {
        if (output.Length <= DefaultOutputLength)
            return output;
        var prefix = $"[Truncated. Full output: {outputPath}]\n\n";
        var tailLength = DefaultOutputLength - prefix.Length;
        return prefix + output[^tailLength..];
    }

    private static string StatusText(BackgroundJobStatus status) => status switch
    {
        BackgroundJobStatus.Running => "running",
        BackgroundJobStatus.Completed => "completed",
        BackgroundJobStatus.Failed => "failed",
        BackgroundJobStatus.Stopped => "stopped",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string? ReadString(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement input, string name, bool defaultValue) =>
        input.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static int? ReadInt(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
