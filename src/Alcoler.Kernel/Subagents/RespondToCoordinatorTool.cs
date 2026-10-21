using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Subagents;

public sealed class RespondToCoordinatorTool(
    SubagentId agentId,
    string agentType,
    Func<string, CancellationToken, ValueTask> enqueue) : ITool
{
    private const string Continuation = "Continue the current task unless the coordinator explicitly changed or ended it.";

    public ToolContract Contract { get; } = new(
        "RespondToCoordinator",
        """
        Respond to the coordinator that owns this subagent.

        Usage:
        - When you receive "The coordinator sent a message while you were working:" (or the legacy "Message from coordinator:" prefix), use this tool to answer it.
        - Use this tool for a concise response or progress update to the coordinator.
        - When replying while work remains, do not use assistant text as the reply.
        - Place this call before or alongside the next work tool call when possible.
        - Continue the current task unless the coordinator explicitly changed or ended it.
        - Do not use this tool as a substitute for the final task result.
        """,
        JsonDocument.Parse("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "summary": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Short summary of the response." },
            "message": { "type": "string", "minLength": 1, "maxLength": 20000, "description": "Response or progress update for the coordinator." }
          },
          "required": ["summary", "message"],
          "additionalProperties": false
        }
        """).RootElement.Clone());

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
        var summary = context.Input.GetProperty("summary").GetString()!;
        var message = context.Input.GetProperty("message").GetString()!;
        var responseId = $"response_{Guid.NewGuid()}";
        var body = string.Join("\n",
            "<subagent-message>",
            $"<agent-id>{Escape(agentId.Value)}</agent-id>",
            $"<agent-type>{Escape(agentType)}</agent-type>",
            $"<summary>{Escape(summary)}</summary>",
            $"<message>{Escape(message)}</message>",
            "</subagent-message>");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await enqueue(body, cancellationToken).ConfigureAwait(false);
            return ToolExecutionResult.Text(
                $"Response {responseId} was queued for the coordinator. {Continuation}", displaySummary: summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Text(
                $"Response {responseId} failed to queue for the coordinator. {Continuation} Failure: {ex.Message}.",
                isError: true, displaySummary: summary);
        }
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
