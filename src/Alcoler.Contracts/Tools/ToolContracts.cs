using System.Runtime.InteropServices;
using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;

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

public static class ProviderNativeWebSearch
{
    public const string ToolType = "web_search_20260209";
    public const string ToolName = "web_search";
    public const string BetaHeader = "code-execution-web-tools-2026-02-09";
}

public sealed record ProviderNativeToolSpec(
    string Type,
    string Name,
    IReadOnlyDictionary<string, object?> Args);

public sealed record ToolContract(
    string Name,
    string Description,
    JsonElement InputSchema,
    ToolSource Source = ToolSource.System,
    ProviderNativeToolSpec? ProviderNative = null);

/// <summary>
/// Hides a tool from models that cannot run any of its backends.
/// </summary>
public interface IModelGatedTool
{
    bool IsAvailable(ModelProperties properties);
}

/// <summary>
/// Hides a tool on platforms that cannot run any of its backends.
/// </summary>
public interface IPlatformGatedTool
{
    bool IsAvailable(OSPlatform platform);
}

public sealed record ToolInputIssue(string Code, string Message);
