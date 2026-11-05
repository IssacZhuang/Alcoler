using System.Text.Json;
using System.Text.Json.Nodes;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Stops a running background job started by the session (e.g. background Bash). The success
/// result is the reference JSON shape (message, task_id, task_type, optional command).
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
        ToolJson.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "task_id": { "type": "string", "description": "The ID of the background task to stop" },
            "shell_id": { "type": "string", "description": "Deprecated: use task_id instead" }
          },
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

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.BackgroundJobs is null && context.Subagents is null)
            return ToolExecutionResult.Text(
                "Background job service is not configured for TaskStop",
                isError: true);

        var taskId = ToolJson.StringOrNull(context.Input, "task_id") ?? ToolJson.StringOrNull(context.Input, "shell_id");
        if (string.IsNullOrEmpty(taskId))
            return ToolExecutionResult.Text(
                "Missing required parameter: task_id",
                isError: true);

        var job = context.BackgroundJobs?.Find(new BackgroundJobId(taskId));
        if (job is not null)
        {
            if (job.Status != BackgroundJobStatus.Running || !context.BackgroundJobs!.Stop(job.Id))
                return ToolExecutionResult.Text(
                    $"Task {taskId} is not running (status: {TaskText.StatusText(job.Status)})",
                    isError: true);
            return ToolExecutionResult.Text(FormatSuccess(job.Id.Value, job.Description, TaskText.BashTaskType));
        }

        if (context.Subagents is { } subagents)
        {
            var task = subagents.Find(taskId);
            if (task is null)
                return ToolExecutionResult.Text($"No task found with ID: {taskId}", isError: true);
            if (task.Status != SubagentStatus.Running
                || !await subagents.TryStopAsync(taskId, cancellationToken).ConfigureAwait(false))
            {
                var current = subagents.Find(taskId);
                if (current is null)
                    return ToolExecutionResult.Text($"No task found with ID: {taskId}", isError: true);
                return ToolExecutionResult.Text(
                    $"Task {taskId} is not running (status: {TaskText.SubagentStatusText(current.Status)})",
                    isError: true);
            }

            return ToolExecutionResult.Text(FormatSuccess(task.Id.Value, task.Description, TaskText.SubagentTaskType));
        }

        return ToolExecutionResult.Text($"No task found with ID: {taskId}", isError: true);
    }

    private static string FormatSuccess(string taskId, string description, string taskType)
    {
        var payload = new JsonObject
        {
            ["message"] = $"Successfully stopped task: {taskId} ({description})",
            ["task_id"] = taskId,
            ["task_type"] = taskType,
        };
        if (!string.IsNullOrEmpty(description))
            payload["command"] = description;
        return payload.ToJsonString(ToolJson.ResultJsonOptions);
    }
}
