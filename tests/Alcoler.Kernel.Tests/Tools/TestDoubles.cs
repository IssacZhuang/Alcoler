using System.Collections.Concurrent;
using System.Text.Json;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Tests.Tools;

internal static class TestSchemas
{
    public const string Permissive = """{"type":"object"}""";

    public const string RequiresValue =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["value"],
          "properties": { "value": { "type": "string" } },
          "additionalProperties": false
        }
        """;
}

internal static class FakeToolMetadata
{
    public static ToolMetadata ParallelSafe() => new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.None,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true);

    public static ToolMetadata Serial() => new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: false,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Workspace,
        RiskLevel: RiskLevel.Medium,
        NeedsApproval: false,
        AllowedInPlanMode: false);

    public static ToolMetadata Destructive() => new(
        ReadOnly: false,
        Destructive: true,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.Workspace,
        RiskLevel: RiskLevel.High,
        NeedsApproval: true,
        AllowedInPlanMode: false);

    public static ToolMetadata SystemSideEffect() => new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: true,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.System,
        RiskLevel: RiskLevel.Medium,
        NeedsApproval: false,
        AllowedInPlanMode: false);
}

internal sealed class FakeTool : ITool
{
    private readonly Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecutionResult>>? _execute;

    public FakeTool(
        string name,
        ToolMetadata? metadata = null,
        string schemaJson = TestSchemas.Permissive,
        Func<ToolExecutionContext, CancellationToken, ValueTask<ToolExecutionResult>>? execute = null,
        ToolInputIssue? validationIssue = null)
    {
        Contract = new ToolContract(name, $"fake {name}", JsonDocument.Parse(schemaJson).RootElement.Clone());
        Metadata = metadata ?? FakeToolMetadata.ParallelSafe();
        _execute = execute;
        ValidationIssue = validationIssue;
    }

    public ToolContract Contract { get; }

    public ToolMetadata Metadata { get; }

    public ToolInputIssue? ValidationIssue { get; }

    public List<ToolExecutionContext> ExecutedContexts { get; } = [];

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ValidationIssue);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        ExecutedContexts.Add(context);
        if (_execute is null)
            return ToolExecutionResult.Text("ok");
        return await _execute(context, cancellationToken);
    }
}

internal sealed class ScriptedPermissionGate : IPermissionGate
{
    private readonly Queue<PermissionDecision> _decisions;

    public ScriptedPermissionGate(params PermissionDecision[] decisions)
    {
        _decisions = new Queue<PermissionDecision>(decisions);
    }

    public List<PermissionCheckRequest> Requests { get; } = [];

    public ValueTask<PermissionDecision> CheckAsync(PermissionCheckRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return ValueTask.FromResult(_decisions.Count > 0 ? _decisions.Dequeue() : PermissionDecision.Allow);
    }
}

internal sealed class NotificationCollector
{
    private readonly ConcurrentQueue<ToolExecutionNotification> _notifications = [];

    public ValueTask Collect(ToolExecutionNotification notification)
    {
        _notifications.Enqueue(notification);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<ToolExecutionNotification> Notifications => _notifications.ToArray();

    public IReadOnlyList<T> Of<T>() where T : ToolExecutionNotification =>
        _notifications.OfType<T>().ToArray();
}

internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "alcoler-tool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Workspace = new LocalToolWorkspace(Root);
    }

    public string Root { get; }

    public LocalToolWorkspace Workspace { get; }

    public ToolExecutionContextFactory ContextFactory() =>
        (call, _) => ValueTask.FromResult(new ToolExecutionContext(
            new SessionId("sess_test"),
            Root,
            call.Input,
            Workspace,
            TimeProvider.System));

    public ToolExecutionContext ContextFor(string json) =>
        new(
            new SessionId("sess_test"),
            Root,
            JsonDocument.Parse(json).RootElement.Clone(),
            Workspace,
            TimeProvider.System);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class ToolCallTestExtensions
{
    public static ToolCall Call(this FakeTool tool, string json = "{}") =>
        new(StrongId.NewToolCallId(), tool.Contract.Name, JsonDocument.Parse(json).RootElement.Clone());

    public static string SingleText(this ToolExecutionResult result) =>
        result.Content.OfType<ToolResultText>().SingleOrDefault()?.Text
        ?? throw new InvalidOperationException("the result contains no text block");
}
