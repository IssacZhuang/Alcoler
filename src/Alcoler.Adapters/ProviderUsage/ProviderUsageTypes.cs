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

        return uri.IdnHost.ToLowerInvariant() switch
        {
            "open.bigmodel.cn" or "bigmodel.cn" or "api.z.ai" => ProviderUsageAdapterId.Zhipu,
            "api.kimi.com" => ProviderUsageAdapterId.Kimi,
            "api.minimaxi.com" or "api.minimax.cn" or "api.minimax.io" => ProviderUsageAdapterId.MiniMax,
            "api.deepseek.com" => ProviderUsageAdapterId.DeepSeek,
            "api.siliconflow.cn" or "api.siliconflow.com" => ProviderUsageAdapterId.SiliconFlow,
            "openrouter.ai" => ProviderUsageAdapterId.OpenRouter,
            "api.stepfun.ai" or "api.stepfun.com" => ProviderUsageAdapterId.StepFun,
            "api.novita.ai" => ProviderUsageAdapterId.Novita,
            _ => null,
        };
    }

    public static ProviderUsageKind GetKind(ProviderUsageAdapterId adapterId) =>
        adapterId is ProviderUsageAdapterId.Zhipu or ProviderUsageAdapterId.Kimi or ProviderUsageAdapterId.MiniMax
            ? ProviderUsageKind.CodingPlan
            : ProviderUsageKind.Balance;
}
