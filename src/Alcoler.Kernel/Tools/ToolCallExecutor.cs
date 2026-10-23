using System.Diagnostics;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Json.Schema;

namespace Alcoler.Kernel.Tools;

public sealed record ToolExecutionOutcome(ToolExecutionResult Result, TimeSpan Duration);

public sealed class ToolCallExecutor
{
    private const string ErrorPrefix = "<tool_use_error>";
    private const string ErrorSuffix = "</tool_use_error>";

    private readonly ToolRegistry _registry;
    private readonly IPermissionGate _permissionGate;
    private readonly Func<ToolExecutionContext, PermissionMode> _modeResolver;
    private readonly Dictionary<string, JsonSchema> _schemas;

    public ToolCallExecutor(
        ToolRegistry registry,
        IPermissionGate permissionGate,
        Func<ToolExecutionContext, PermissionMode>? modeResolver = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _permissionGate = permissionGate ?? throw new ArgumentNullException(nameof(permissionGate));
        _modeResolver = modeResolver ?? (static _ => PermissionMode.Build);

        _schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var tool in _registry.Tools)
        {
            var schema = tool.RuntimeInputSchema.Deserialize<JsonSchema>()
                ?? throw new InvalidOperationException($"Tool '{tool.Contract.Name}' declared an unreadable input schema.");
            _schemas.Add(tool.Contract.Name, schema);
        }
    }

    public async ValueTask<ToolExecutionOutcome> ExecuteAsync(
        ToolCall call,
        ToolExecutionContextFactory contextFactory,
        Func<ToolExecutionNotification, ValueTask> notificationSink,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await notificationSink(new ToolExecutionStarted(call)).ConfigureAwait(false);
        var result = await ExecutePipelineAsync(call, contextFactory, notificationSink, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        await notificationSink(new ToolExecutionResultNotification(call, result, stopwatch.Elapsed)).ConfigureAwait(false);
        return new ToolExecutionOutcome(result, stopwatch.Elapsed);
    }

    private async ValueTask<ToolExecutionResult> ExecutePipelineAsync(
        ToolCall call,
        ToolExecutionContextFactory contextFactory,
        Func<ToolExecutionNotification, ValueTask> notificationSink,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return CancelledResult();

        var tool = _registry.TryFind(call.Name);
        if (tool is null)
            return ToolUseError($"Unknown tool '{call.Name}'.");

        var schemaError = ValidateSchema(tool, call.Input);
        if (schemaError is not null)
            return ToolUseError(schemaError);

        try
        {
            var issue = await tool.ValidateInputAsync(call.Input, cancellationToken).ConfigureAwait(false);
            if (issue is not null)
                return ToolUseError($"Tool '{tool.Contract.Name}' rejected the input: {issue.Message}");

            var input = await tool.ResolveInputAsync(call.Input, cancellationToken).ConfigureAwait(false);
            var effectiveCall = call with { Input = input };
            var context = await contextFactory(effectiveCall, cancellationToken).ConfigureAwait(false);

            var request = new PermissionRequest(
                StrongId.NewInteractionId(),
                context.SessionId,
                tool.Contract.Name,
                input,
                tool.Metadata.RiskLevel,
                $"Tool '{tool.Contract.Name}' (risk level: {tool.Metadata.RiskLevel})",
                ToolCallId: call.Id);
            await notificationSink(new ToolExecutionPermissionAsked(effectiveCall, request)).ConfigureAwait(false);

            var decision = await _permissionGate.CheckAsync(
                new PermissionCheckRequest(
                    context.SessionId,
                    tool.Contract.Name,
                    input,
                    tool.Metadata,
                    _modeResolver(context),
                    TreatAsReadOnly: tool is IToolInputPolicy policy && policy.IsReadOnlyInput(input),
                    ToolCallId: call.Id),
                cancellationToken).ConfigureAwait(false);

            if (decision.Outcome == PermissionOutcome.Denied)
                return DeniedResult(tool, context, decision);

            if (decision.Outcome == PermissionOutcome.Modified && decision.ModifiedInput is { } modified)
            {
                var modifiedSchemaError = ValidateSchema(tool, modified);
                if (modifiedSchemaError is not null)
                    return ToolUseError(modifiedSchemaError);
                input = modified;
                effectiveCall = effectiveCall with { Input = input };
                context = await contextFactory(effectiveCall, cancellationToken).ConfigureAwait(false);
            }

            // Mirrors the reference guard: ExitPlanMode only executes while the session is in plan mode.
            if (string.Equals(tool.Contract.Name, ExitPlanModeTool.ToolName, StringComparison.Ordinal)
                && _modeResolver(context) != PermissionMode.Plan)
                return ToolExecutionResult.Text(ExitPlanModeTool.NotInPlanModeMessage, isError: true);

            var executionContext = context with { Input = input, ToolCallId = call.Id };
            return await ExecuteHandlerAsync(tool, executionContext, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CancelledResult();
        }
        catch (Exception ex)
        {
            return ToolUseError(ex.Message);
        }
    }

    private static async Task<ToolExecutionResult> ExecuteHandlerAsync(
        ITool tool,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return CancelledResult();

        var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutMs = tool.Metadata.DefaultTimeoutMs;
        if (timeoutMs is > 0)
            executionCts.CancelAfter(timeoutMs.Value);
        var execution = Task.Run(async () =>
        {
            executionCts.Token.ThrowIfCancellationRequested();
            return await tool.ExecuteAsync(context, executionCts.Token).ConfigureAwait(false);
        }, CancellationToken.None);
        try
        {
            var result = await execution.WaitAsync(executionCts.Token).ConfigureAwait(false);
            return WithEmptyOutputPlaceholder(result, tool.Contract.Name);
        }
        catch (OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested && executionCts.IsCancellationRequested && timeoutMs is > 0
                ? ToolExecutionResult.Text($"Tool execution timed out after {timeoutMs.Value}ms", isError: true)
                : CancelledResult();
        }
        catch (Exception ex)
        {
            return ToolUseError(ex.Message);
        }
        finally
        {
            if (execution.IsCompleted)
                executionCts.Dispose();
            else
                _ = ObserveAbandonedExecutionAsync(execution, executionCts);
        }
    }

    private static async Task ObserveAbandonedExecutionAsync(Task execution, CancellationTokenSource executionCts)
    {
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            executionCts.Dispose();
        }
    }

    private static ToolExecutionResult CancelledResult() => ToolExecutionResult.Text("Tool execution cancelled", isError: true);

    private ToolExecutionResult DeniedResult(ITool tool, ToolExecutionContext context, PermissionDecision decision)
    {
        if (string.Equals(tool.Contract.Name, ExitPlanModeTool.ToolName, StringComparison.Ordinal))
        {
            if (_modeResolver(context) != PermissionMode.Plan)
                return ToolExecutionResult.Text(ExitPlanModeTool.NotInPlanModeMessage, isError: true);
            if (!string.IsNullOrWhiteSpace(decision.Feedback))
            {
                return ToolExecutionResult.Text(
                    ExitPlanModeTool.PlanDeniedMessage,
                    isError: true,
                    followUpUserInput: decision.Feedback.Trim());
            }
            return ToolExecutionResult.Text(ExitPlanModeTool.PlanDeniedMessage, isError: true, stopTurnAfterResult: true);
        }
        return ToolExecutionResult.Text($"Permission denied for {tool.Contract.Name}", isError: true);
    }

    private static ToolExecutionResult WithEmptyOutputPlaceholder(ToolExecutionResult result, string toolName)
    {
        if (result.IsError || !HasOnlyBlankText(result))
            return result;
        return result with { Content = [new ToolResultText($"({toolName} completed with no output)")] };
    }

    private static bool HasOnlyBlankText(ToolExecutionResult result)
    {
        if (result.Content.Count == 0)
            return true;
        foreach (var block in result.Content)
            if (block is not ToolResultText text || !string.IsNullOrWhiteSpace(text.Text))
                return false;
        return true;
    }

    private static ToolExecutionResult ToolUseError(string message) =>
        ToolExecutionResult.Text($"{ErrorPrefix}{message}{ErrorSuffix}", isError: true);

    private string? ValidateSchema(ITool tool, JsonElement input)
    {
        if (!_schemas.TryGetValue(tool.Contract.Name, out var schema))
            return null;
        var results = schema.Evaluate(input);
        if (results.IsValid)
            return null;
        return $"Tool '{tool.Contract.Name}' input failed schema validation: {DescribeFailure(results)}";
    }

    private static string DescribeFailure(EvaluationResults results)
    {
        if (results.Details is not null)
        {
            foreach (var child in results.Details)
            {
                if (!child.IsValid)
                    return DescribeFailure(child);
            }
        }
        if (results.Errors is { Count: > 0 })
            return results.Errors.Values.First();
        return "the input does not match the tool's declared schema";
    }
}
