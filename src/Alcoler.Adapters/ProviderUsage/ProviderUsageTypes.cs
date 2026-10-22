namespace Alcoler.Adapters.ProviderUsage;

public enum ProviderUsageAdapterId
{
    Zhipu,
    Kimi,
    MiniMax,
    DeepSeek,
    SiliconFlow,
    OpenRouter,
    StepFun,
    Novita,
}

public enum ProviderUsageKind
{
    CodingPlan,
    Balance,
}

public enum ProviderUsageTierId
{
    FiveHour,
    Weekly,
    Monthly,
    Balance,
}

public enum ProviderUsageErrorCode
{
    Unsupported,
    MissingApiKey,
    AuthFailed,
    Network,
    BadResponse,
    Unknown,
}

public sealed record ProviderUsageTier(
    ProviderUsageTierId Id,
    double? UsedPercentage = null,
    double? Remaining = null,
    double? Total = null,
    double? Used = null,
    string? Unit = null,
    DateTimeOffset? ResetsAt = null);

public sealed record ProviderUsageError(ProviderUsageErrorCode Code, string Message);

public sealed record ProviderUsageSnapshot(
    string ProviderId,
    ProviderUsageAdapterId? AdapterId,
    ProviderUsageKind? Kind,
    bool CredentialValid,
    IReadOnlyList<ProviderUsageTier> Tiers,
    DateTimeOffset QueriedAt,
    ProviderUsageError? Error = null);

public interface IProviderUsageService
{
    Task<ProviderUsageSnapshot> GetUsageAsync(
        string providerId,
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default);
}

public static class ProviderUsageDetection
{
    public static ProviderUsageAdapterId? DetectAdapter(string? baseUrl)
    {
        if (baseUrl?.Contains('\\') == true
            || !Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length != 0)
        {
            return null;
        }

        var host = uri.IdnHost;
        if (HostIs(host, "open.bigmodel.cn") || HostIs(host, "bigmodel.cn") || HostIs(host, "api.z.ai"))
            return ProviderUsageAdapterId.Zhipu;
        if (HostIs(host, "api.kimi.com"))
            return ProviderUsageAdapterId.Kimi;
        if (HostIs(host, "api.minimaxi.com") || HostIs(host, "api.minimax.cn") || HostIs(host, "api.minimax.io"))
            return ProviderUsageAdapterId.MiniMax;
        if (HostIs(host, "api.deepseek.com"))
            return ProviderUsageAdapterId.DeepSeek;
        if (HostIs(host, "api.siliconflow.cn") || HostIs(host, "api.siliconflow.com"))
            return ProviderUsageAdapterId.SiliconFlow;
        if (HostIs(host, "openrouter.ai"))
            return ProviderUsageAdapterId.OpenRouter;
        if (HostIs(host, "api.stepfun.ai") || HostIs(host, "api.stepfun.com"))
            return ProviderUsageAdapterId.StepFun;
        if (HostIs(host, "api.novita.ai"))
            return ProviderUsageAdapterId.Novita;
        return null;
    }

    private static bool HostIs(string host, string expected) => host.Equals(expected, StringComparison.OrdinalIgnoreCase);

    public static ProviderUsageKind GetKind(ProviderUsageAdapterId adapterId) =>
        adapterId is ProviderUsageAdapterId.Zhipu or ProviderUsageAdapterId.Kimi or ProviderUsageAdapterId.MiniMax
            ? ProviderUsageKind.CodingPlan
            : ProviderUsageKind.Balance;
}
