using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Stops a running background job started by the session (e.g. background Bash).
/// </summary>
public sealed class TaskStopTool : ITool
{
    public ToolContract Contract { get; } = new(
        "TaskStop",
        """
        - Stops a running background task by its ID
        - Takes a task_id parameter identifying the task to stop
        - Returns a success or failure status
        - Use this tool when you need to terminate a long-running task
        """,
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "task_id": { "type": "string", "description": "The task ID to stop" }
          },
          "required": ["task_id"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Session,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true,
        DefaultTimeoutMs: 10_000);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.BackgroundJobs is not { } jobs)
            return ValueTask.FromResult(ToolExecutionResult.Text(
                "Background job service is not configured for TaskStop",
                isError: true));

        var taskId = context.Input.TryGetProperty("task_id", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        if (string.IsNullOrEmpty(taskId))
            return ValueTask.FromResult(ToolExecutionResult.Text(
                "Missing required parameter: task_id",
                isError: true));

        var job = jobs.Find(new BackgroundJobId(taskId));
        if (job is null)
            return ValueTask.FromResult(ToolExecutionResult.Text(
                $"No task found with ID: {taskId}",
                isError: true));
        if (job.Status != BackgroundJobStatus.Running || !jobs.Stop(job.Id))
            return ValueTask.FromResult(ToolExecutionResult.Text(
                $"Task {taskId} is not running (status: {StatusText(job.Status)})",
                isError: true));

        return ValueTask.FromResult(ToolExecutionResult.Text(
            $"Successfully stopped task: {job.Id.Value} ({job.Description})"));
    }

    private static string StatusText(BackgroundJobStatus status) => status switch
    {
        BackgroundJobStatus.Running => "running",
        BackgroundJobStatus.Completed => "completed",
        BackgroundJobStatus.Failed => "failed",
        BackgroundJobStatus.Stopped => "stopped",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
