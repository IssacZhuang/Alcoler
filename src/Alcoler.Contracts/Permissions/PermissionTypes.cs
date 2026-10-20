using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Contracts.Permissions;

public enum PermissionMode
{
    Build,
    Edit,
    Plan,
    Yolo,
}

public enum PermissionVerdict
{
    Allow,
    Deny,
    Ask,
}

public sealed record PermissionRule(string ToolName, string? Content, PermissionVerdict Verdict);

public sealed record PermissionRuleset(
    IReadOnlyList<PermissionRule> Allow,
    IReadOnlyList<PermissionRule> Ask,
    IReadOnlyList<PermissionRule> Deny)
{
    public static PermissionRuleset Empty { get; } = new([], [], []);
}

public sealed record PermissionRequest(
    InteractionId InteractionId,
    SessionId SessionId,
    string ToolName,
    JsonElement Input,
    RiskLevel RiskLevel,
    string Reason);

public enum PermissionOutcome
{
    Allowed,
    Denied,
    Modified,
}

public sealed record PermissionDecision(
    PermissionOutcome Outcome,
    JsonElement? ModifiedInput = null)
{
    public static PermissionDecision Allow { get; } = new(PermissionOutcome.Allowed);
    public static PermissionDecision Deny { get; } = new(PermissionOutcome.Denied);
}

public interface IPermissionBroker
{
    ValueTask<PermissionDecision> DecideAsync(PermissionRequest request, CancellationToken cancellationToken);
}

public sealed record PermissionCheckRequest(
    SessionId SessionId,
    string ToolName,
    JsonElement Input,
    ToolMetadata Tool,
    PermissionMode Mode,
    bool TreatAsReadOnly = false);

public interface IPermissionGate
{
    ValueTask<PermissionDecision> CheckAsync(PermissionCheckRequest request, CancellationToken cancellationToken);
}
