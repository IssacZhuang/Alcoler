using System.Text.Json;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Subagents;

public sealed class SubagentTool : ITool
{
    private readonly ISubagentPort _port;
    private readonly Func<SubagentResult, ValueTask> _onBackgroundCompleted;

    public SubagentTool(ISubagentPort port, Func<SubagentResult, ValueTask>? onBackgroundCompleted = null)
    {
        _port = port;
        _onBackgroundCompleted = onBackgroundCompleted ?? NoOpCompleted;
    }

    public ToolContract Contract { get; } = new(
        "Task",
        "Delegates a self-contained task to a subagent with its own session and tool scope; the subagent sees no parent conversation. Foreground subagents block until done and return their final result; background subagents start immediately, let the parent continue, and notify the parent session on completion.",
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "prompt": { "type": "string", "description": "Full, self-contained instructions for the subagent." },
            "description": { "type": "string", "description": "Short task label shown in the UI." },
            "role": { "type": "string", "description": "Optional role shaping the subagent's behavior." },
            "background": { "type": "boolean", "description": "Run without blocking the parent session; defaults to false." },
            "allowedTools": { "type": "array", "items": { "type": "string" }, "description": "Tool names the subagent may use; omit for the default set." }
          },
          "required": ["prompt"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: false,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Workspace,
        RiskLevel: RiskLevel.Medium,
        NeedsApproval: false,
        AllowedInPlanMode: false);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("prompt", out var prompt)
            || prompt.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(prompt.GetString()))
            return ValueTask.FromResult<ToolInputIssue?>(new ToolInputIssue(
                "missing_prompt",
                "'prompt' must be a non-empty string"));

        if (input.TryGetProperty("allowedTools", out var allowedTools) && !IsValidToolList(allowedTools))
            return ValueTask.FromResult<ToolInputIssue?>(new ToolInputIssue(
                "invalid_allowed_tools",
                "'allowedTools' must be an array of non-empty strings"));

        return ValueTask.FromResult<ToolInputIssue?>(null);
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var prompt = context.Input.GetProperty("prompt").GetString()!;
        var description = StringOrNull(context.Input, "description");
        var background = BoolOrDefault(context.Input, "background");
        var request = new SubagentRequest(
            context.SessionId,
            prompt,
            Role: StringOrNull(context.Input, "role"),
            Description: description,
            AllowedTools: StringArrayOrNull(context.Input, "allowedTools"),
            ModelSelection: null,
            Background: background);

        if (!background)
        {
            var result = await _port.RunAsync(request, cancellationToken).ConfigureAwait(false);
            return ToolExecutionResult.Text(
                result.Output,
                isError: result.State.Status == SubagentStatus.Failed,
                displaySummary: description);
        }

        var handle = await _port.StartBackgroundAsync(request, _onBackgroundCompleted, cancellationToken).ConfigureAwait(false);
        return ToolExecutionResult.Text($"Subagent started: {handle.Description}", displaySummary: description);
    }

    private static ValueTask NoOpCompleted(SubagentResult result) => ValueTask.CompletedTask;

    private static bool IsValidToolList(JsonElement allowedTools)
    {
        if (allowedTools.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in allowedTools.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                return false;
        }
        return true;
    }

    private static string? StringOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool BoolOrDefault(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : false;

    private static IReadOnlyList<string>? StringArrayOrNull(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;
        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
