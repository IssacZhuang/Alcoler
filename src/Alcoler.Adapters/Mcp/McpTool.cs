using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Mcp;

public sealed class McpTool : ITool
{
    internal static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(120);

    private static readonly JsonElement PermissiveSchema =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    private readonly McpServerConnection _connection;
    private readonly string _remoteName;
    private readonly TimeSpan _callTimeout;

    internal McpTool(McpServerConnection connection, McpToolDefinition definition, TimeSpan? callTimeout = null)
    {
        _connection = connection;
        _remoteName = definition.Name!;
        _callTimeout = callTimeout ?? DefaultCallTimeout;
        // Tool names are prefixed "{serverName}_{toolName}" because model providers restrict tool names to letters, digits, '_' and '-'.
        Contract = new ToolContract(
            $"{connection.ServerName}_{_remoteName}",
            definition.Description ?? string.Empty,
            definition.InputSchema is { ValueKind: JsonValueKind.Object } schema ? schema : PermissiveSchema,
            ToolSource.Mcp);
        Metadata = BuildMetadata(definition.Annotations, (int)_callTimeout.TotalMilliseconds);
    }

    public ToolContract Contract { get; }

    public ToolMetadata Metadata { get; }

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return ToolExecutionResult.Text($"MCP tool '{Contract.Name}' arguments must be a JSON object.", isError: true);
        if (context.Input.ValueKind is not JsonValueKind.Object)
            return ToolExecutionResult.Text($"MCP tool '{Contract.Name}' arguments must be a JSON object.", isError: true);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_callTimeout);
        McpToolCallOutcome outcome;
        try
        {
            outcome = await _connection.CallToolAsync(_remoteName, context.Input, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolExecutionResult.Text(
                $"MCP tool '{Contract.Name}' timed out after {_callTimeout.TotalSeconds:0.#}s.",
                isError: true);
        }
        catch (McpProtocolException ex)
        {
            return ToolExecutionResult.Text(ex.Message, isError: true);
        }
        catch (ObjectDisposedException)
        {
            return ToolExecutionResult.Text(
                $"MCP server '{_connection.ServerName}' is no longer connected; tool '{Contract.Name}' cannot run.",
                isError: true);
        }
        List<ToolResultBlock> blocks;
        if (outcome.TextParts.Count == 0)
        {
            blocks = [new ToolResultText("")];
        }
        else
        {
            blocks = new List<ToolResultBlock>(outcome.TextParts.Count);
            foreach (var text in outcome.TextParts)
                blocks.Add(new ToolResultText(text));
        }
        return new ToolExecutionResult(blocks, outcome.IsError);
    }

    internal static ToolMetadata BuildMetadata(McpToolAnnotations? annotations, int defaultTimeoutMs)
    {
        if (annotations?.ReadOnlyHint == true)
        {
            return new ToolMetadata(
                ReadOnly: true,
                Destructive: false,
                ConcurrentSafe: true,
                RequiresUserInteraction: false,
                SideEffectScope: SideEffectScope.None,
                RiskLevel: RiskLevel.Low,
                NeedsApproval: false,
                AllowedInPlanMode: true,
                DefaultTimeoutMs: defaultTimeoutMs);
        }
        // Unannotated remote tools cannot be proven safe, so approval stays required.
        var destructive = annotations?.DestructiveHint == true;
        return new ToolMetadata(
            ReadOnly: false,
            Destructive: destructive,
            ConcurrentSafe: annotations?.IdempotentHint == true,
            RequiresUserInteraction: false,
            SideEffectScope: SideEffectScope.System,
            RiskLevel: destructive ? RiskLevel.High : RiskLevel.Medium,
            NeedsApproval: true,
            AllowedInPlanMode: !destructive,
            DefaultTimeoutMs: defaultTimeoutMs);
    }
}
