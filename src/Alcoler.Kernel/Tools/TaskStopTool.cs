using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Stops a running background job started by the session (e.g. background Bash). The success
/// result is the reference JSON shape (message, task_id, task_type, optional command).
/// </summary>
public sealed class TaskStopTool : ITool
{
    internal const string TaskType = "local_bash";

    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.BackgroundJobs is not { } jobs)
            return ValueTask.FromResult(ToolExecutionResult.Text(
                "Background job service is not configured for TaskStop",
                isError: true));

        var taskId = ReadId(context.Input, "task_id") ?? ReadId(context.Input, "shell_id");
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

        var payload = new JsonObject
        {
            ["message"] = $"Successfully stopped task: {job.Id.Value} ({job.Description})",
            ["task_id"] = job.Id.Value,
            ["task_type"] = TaskType,
        };
        if (!string.IsNullOrEmpty(job.Description))
            payload["command"] = job.Description;
        return ValueTask.FromResult(ToolExecutionResult.Text(payload.ToJsonString(ResultJsonOptions)));
    }

    private static string? ReadId(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
