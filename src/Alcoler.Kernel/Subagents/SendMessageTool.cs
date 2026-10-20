using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Subagents;

/// <summary>
/// Delivers a follow-up message to a background subagent of this session: a busy child is
/// steered (its queue drains into its running turn), an idle child is resumed in the
/// background. Scoped to the parent side of Alcoler's subagent model, where the Agent
/// tool already returns the child's final output directly.
/// </summary>
public sealed class SendMessageTool(SubagentMessenger messenger) : ITool
{
    private const string BusyWrapper =
        "The parent session sent a message while you were working:\n{0}\n\nAddress this before completing your current task.";

    public ToolContract Contract { get; } = new(
        "SendMessage",
        """
        Send a message to a background subagent started via the Agent tool in this session.

        ```json
        {"to": "sub_<id>", "summary": "narrow the search", "message": "focus only on the Kernel project"}
        ```

        Your plain text output is NOT visible to subagents — to communicate with one, you MUST call this tool. A running subagent is steered with your message inside its current turn; a completed subagent resumes in the background and you'll be notified when it finishes. Refer to subagents by the agentId returned when the Agent tool started them.
        """,
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "to": { "type": "string", "minLength": 1, "maxLength": 200, "description": "Recipient: the agentId (sub_...) or child session id (sess_...) returned when the agent was launched." },
            "summary": { "type": "string", "minLength": 1, "maxLength": 200, "description": "A 5-10 word summary shown as a preview in the UI." },
            "message": { "type": "string", "minLength": 1, "maxLength": 20000, "description": "Plain text message content." }
          },
          "required": ["to", "summary", "message"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: false,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Session,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: false);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var to = StringOrNull(context.Input, "to");
        var message = StringOrNull(context.Input, "message");
        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(message))
            return ToolExecutionResult.Text("to and message are required", isError: true);

        var channel = messenger.Find(context.SessionId, to);
        if (channel is null)
            return ToolExecutionResult.Text(
                $"No active local_agent task found for target {to}.",
                isError: true);

        var busy = channel.IsBusy;
        // ZCode resumes a finished agent with the raw message as its next prompt; only an
        // active turn gets wrapper framing so the model knows to address it mid-task.
        var body = busy ? string.Format(BusyWrapper, message) : message;
        var commandId = StrongId.NewCommandId();
        var ack = await channel.Runtime.SubmitAsync(
            new SendTextCommand(commandId, channel.ChildSessionId, 0, body),
            cancellationToken).ConfigureAwait(false);
        if (!ack.Accepted)
            return ToolExecutionResult.Text($"Message delivery failed: {ack.RejectionReason}", isError: true);

        var resultText = busy
            ? $"Message {commandId.Value} was sent to its active turn for local agent {channel.Id.Value}."
            : $"Agent \"{channel.Id.Value}\" was stopped; resumed it in the background with your message. You'll be notified when it finishes.";
        return ToolExecutionResult.Text(resultText, displaySummary: StringOrNull(context.Input, "summary"));
    }

    private static string? StringOrNull(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
