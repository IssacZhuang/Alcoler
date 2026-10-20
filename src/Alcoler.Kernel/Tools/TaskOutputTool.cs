using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Reads or awaits a background job's status and output. The executor timeout is disabled
/// (DefaultTimeoutMs null) because waiting is governed by the tool's own timeout parameter.
/// </summary>
public sealed class TaskOutputTool : ITool
{
    internal const int DefaultTimeoutMs = 30_000;
    internal const int MaxTimeoutMs = 600_000;
    internal const int DefaultOutputLength = 32_000;
    private const int PollIntervalMs = 100;
    private const string TaskType = "local_bash";

    public ToolContract Contract { get; } = new(
        "TaskOutput",
        """
        DEPRECATED: Background tasks return their output file path in the tool result, and you receive a <task-notification> with the same path when the task completes.
        - For bash tasks: prefer using the Read tool on that output file path — it contains stdout/stderr.
        - For local_agent tasks: use the Agent tool result directly. Do NOT Read the .output file — it is a symlink to the full subagent conversation transcript (JSONL) and will overflow your context window.
        - For remote_agent tasks: prefer using the Read tool on the output file path — it contains the streamed remote session output (same as bash).

        - Retrieves output from a running or completed task (background shell, agent, or remote session)
        - Takes a task_id parameter identifying the task
        - Returns the task output along with status information
        - Use block=true (default) to wait for task completion
        - Use block=false for non-blocking check of current status
        - Task IDs can be found using the /tasks command
        - Works with all task types: background shells, async agents, and remote sessions
        """,
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "task_id": { "type": "string", "description": "The task ID to get output from" },
            "block": { "type": "boolean", "default": true, "description": "Whether to wait for completion" },
            "timeout": { "type": "number", "minimum": 0, "maximum": 600000, "default": 30000, "description": "Max wait time in ms" }
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
        if (context.BackgroundJobs is not { } jobs)
            return ToolExecutionResult.Text(
                "Background job service is not configured for TaskOutput",
                isError: true);

        var taskId = ReadString(context.Input, "task_id");
        if (string.IsNullOrEmpty(taskId))
            return ToolExecutionResult.Text("Task ID is required", isError: true);

        var job = jobs.Find(new BackgroundJobId(taskId));
        if (job is null)
            return ToolExecutionResult.Text($"No task found with ID: {taskId}", isError: true);

        var block = ReadBool(context.Input, "block", defaultValue: true);
        var timeoutMs = Math.Clamp(ReadInt(context.Input, "timeout") ?? DefaultTimeoutMs, 0, MaxTimeoutMs);

        if (block)
        {
            var deadline = context.Clock.GetUtcNow() + TimeSpan.FromMilliseconds(timeoutMs);
            while (job.Status == BackgroundJobStatus.Running)
            {
                if (context.Clock.GetUtcNow() >= deadline)
                    return ToolExecutionResult.Text(FormatResult("timeout", job));
                await Task.Delay(TimeSpan.FromMilliseconds(PollIntervalMs), context.Clock, cancellationToken)
                    .ConfigureAwait(false);
                job = jobs.Find(new BackgroundJobId(taskId));
                if (job is null)
                    return ToolExecutionResult.Text(FormatResult("timeout", null));
            }
        }

        if (job.Status == BackgroundJobStatus.Running)
            return ToolExecutionResult.Text(FormatResult("not_ready", job));
        return ToolExecutionResult.Text(FormatResult("success", job));
    }

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
            var output = File.ReadAllText(job.OutputPath, Encoding.UTF8);
            return TruncateTail(output, job.OutputPath);
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
