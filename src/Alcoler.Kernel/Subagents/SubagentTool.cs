using System.Text.Json;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Subagents;

public sealed class SubagentTool : ITool, ISubagentTasks
{
    private const string EmptyOutputText = "(Subagent completed but returned no output.)";

    private readonly ISubagentPort _port;
    private readonly Func<SubagentRequest, SubagentResult, ValueTask>? _onBackgroundCompleted;
    private readonly Func<bool>? _canReadOutputFile;

    public SubagentTool(
        ISubagentPort port,
        Func<SubagentRequest, SubagentResult, ValueTask>? onBackgroundCompleted = null,
        Func<bool>? canReadOutputFile = null,
        SubagentTaskRegistry? tasks = null)
    {
        _port = port;
        _onBackgroundCompleted = onBackgroundCompleted;
        _canReadOutputFile = canReadOutputFile;
        Tasks = tasks;
    }

    public ISubagentTasks? Tasks { get; }

    public SubagentTaskInfo? Find(string taskId) => Tasks?.Find(taskId);

    public bool TryMarkNotified(string taskId) => Tasks?.TryMarkNotified(taskId) ?? false;

    public ValueTask<bool> TryStopAsync(string taskId, CancellationToken cancellationToken) =>
        Tasks?.TryStopAsync(taskId, cancellationToken) ?? ValueTask.FromResult(false);

    public ToolContract Contract { get; } = new(
        "Agent",
        SubagentPrompts.BuildToolDescription(),
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "description": { "type": "string", "description": "A short (3-5 word) description of the task" },
            "prompt": { "type": "string", "description": "The task for the agent to perform" },
            "subagent_type": { "type": "string", "enum": ["general-purpose", "Explore"], "description": "The type of specialized agent to use for this task" },
            "run_in_background": { "type": "boolean", "description": "Set to true to run this agent in the background. You will be notified when it completes." }
          },
          "required": ["description", "prompt"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Session,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: false);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return ValueTask.FromResult<ToolInputIssue?>(new ToolInputIssue(
                "missing_prompt",
                "'prompt' must be a non-empty string"));

        foreach (var (name, code) in new[] { ("prompt", "missing_prompt"), ("description", "missing_description") })
        {
            if (!input.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
                return ValueTask.FromResult<ToolInputIssue?>(new ToolInputIssue(
                    code,
                    $"'{name}' must be a non-empty string"));
        }

        if (input.TryGetProperty("subagent_type", out var agentType))
        {
            if (agentType.ValueKind != JsonValueKind.String
                || !SubagentPrompts.IsKnownAgentType(agentType.GetString()))
                return ValueTask.FromResult<ToolInputIssue?>(new ToolInputIssue(
                    "unknown_subagent_type",
                    $"'subagent_type' must be one of: {string.Join(", ", SubagentPrompts.AgentTypes)}"));
        }

        if (input.TryGetProperty("run_in_background", out var background)
            && background.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return ValueTask.FromResult<ToolInputIssue?>(new ToolInputIssue(
                "invalid_run_in_background",
                "'run_in_background' must be a boolean"));

        return ValueTask.FromResult<ToolInputIssue?>(null);
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var prompt = context.Input.GetProperty("prompt").GetString()!;
        var description = context.Input.GetProperty("description").GetString()!;
        var background = BoolOrDefault(context.Input, "run_in_background");
        var request = new SubagentRequest(
            context.SessionId,
            prompt,
            AgentType: StringOrNull(context.Input, "subagent_type"),
            Description: description,
            ModelSelection: null,
            Background: background,
            ParentToolCallId: context.ToolCallId);

        if (!background)
        {
            var result = await _port.RunAsync(request, cancellationToken).ConfigureAwait(false);
            return ToolExecutionResult.Text(
                FormatForegroundResult(result),
                isError: result.State.Status == SubagentStatus.Failed,
                displaySummary: description);
        }

        var handle = await _port.StartBackgroundAsync(
            request,
            result => _onBackgroundCompleted?.Invoke(request, result) ?? ValueTask.CompletedTask,
            cancellationToken).ConfigureAwait(false);
        return ToolExecutionResult.Text(
            FormatBackgroundResult(handle, _canReadOutputFile?.Invoke() ?? true),
            displaySummary: description);
    }

    internal static string FormatBackgroundResult(SubagentHandle handle, bool canReadOutputFile)
    {
        var lines = new List<string>
        {
            "Async agent launched successfully.",
            $"agentId: {handle.Id.Value} (internal ID - do not mention to user. Use SendMessage with to: '{handle.Id.Value}' to continue this agent.)",
            "The agent is working in the background. You will be notified automatically when it completes.",
        };
        if (canReadOutputFile && handle.OutputFile is { } outputFile)
        {
            lines.Add("Do not duplicate this agent's work - avoid working with the same files or topics it is using. Work on non-overlapping tasks, or briefly tell the user what you launched and end your response.");
            lines.Add($"output_file: {outputFile}");
            lines.Add("Do NOT Read or tail this file via the shell tool. If the user asks for progress, say the agent is still running; you'll get a completion notification.");
        }
        else
        {
            lines.Add("Briefly tell the user what you launched and end your response. Do not generate any other text - agent results will arrive in a subsequent message.");
        }

        return string.Join("\n", lines);
    }

    internal static string FormatForegroundResult(SubagentResult result)
    {
        var childText = result.Output;
        var body = childText.Trim().Length > 0 ? childText : EmptyOutputText;
        var durationMs = Math.Max(
            0,
            (int)Math.Round((result.State.EndedAt.GetValueOrDefault() - result.State.StartedAt).TotalMilliseconds));
        var usageLines = new List<string>();
        if (result.ToolUseCount is int toolUses)
            usageLines.Add($"tool_uses: {toolUses}");
        usageLines.Add($"duration_ms: {durationMs}");

        return string.Join("\n",
            [
                body,
                $"agentId: {result.State.Id.Value} (use SendMessage with to: '{result.State.Id.Value}' to continue this agent)",
                $"<usage>{string.Join("\n", usageLines)}</usage>",
            ]);
    }

    internal static string BackgroundStatus(SubagentStatus status) => status switch
    {
        SubagentStatus.Completed => "completed",
        SubagentStatus.Failed => "failed",
        _ => "stopped",
    };

    private static string? StringOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool BoolOrDefault(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : false;

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
