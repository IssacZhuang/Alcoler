using System.Text.Json;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Contracts.Tools;

public enum SideEffectScope
{
    None,
    Workspace,
    Git,
    Network,
    System,
    Session,
    UserInteraction,
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

public sealed record ToolMetadata(
    bool ReadOnly,
    bool Destructive,
    bool ConcurrentSafe,
    bool RequiresUserInteraction,
    SideEffectScope SideEffectScope,
    RiskLevel RiskLevel,
    bool NeedsApproval,
    bool AllowedInPlanMode,
    bool ProviderVisible = true,
    int? DefaultTimeoutMs = null,
    int? MaxTimeoutMs = null);

public enum ToolSource
{
    System,
    Mcp,
}

public sealed record ToolContract(string Name, string Description, JsonElement InputSchema, ToolSource Source = ToolSource.System);

public sealed record ToolInputIssue(string Code, string Message);
