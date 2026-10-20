using System.Globalization;
using System.Text.Json;

namespace Alcoler.Adapters.ProviderUsage;

internal static class ProviderUsageAdapters
{
    public static Uri GetEndpoint(ProviderUsageAdapterId adapterId, string baseUrl)
    {
        var host = new Uri(baseUrl.Trim()).IdnHost.ToLowerInvariant();
        return new Uri(adapterId switch
        {
            ProviderUsageAdapterId.Zhipu => host is "open.bigmodel.cn" or "bigmodel.cn"
                ? "https://open.bigmodel.cn/api/monitor/usage/quota/limit"
                : "https://api.z.ai/api/monitor/usage/quota/limit",
            ProviderUsageAdapterId.Kimi => "https://api.kimi.com/coding/v1/usages",
            ProviderUsageAdapterId.MiniMax => host == "api.minimax.io"
                ? "https://api.minimax.io/v1/api/openplatform/coding_plan/remains"
                : "https://api.minimaxi.com/v1/api/openplatform/coding_plan/remains",
            ProviderUsageAdapterId.DeepSeek => "https://api.deepseek.com/user/balance",
            ProviderUsageAdapterId.SiliconFlow => host == "api.siliconflow.com"
                ? "https://api.siliconflow.com/v1/user/info"
                : "https://api.siliconflow.cn/v1/user/info",
            ProviderUsageAdapterId.OpenRouter => "https://openrouter.ai/api/v1/credits",
            ProviderUsageAdapterId.StepFun => "https://api.stepfun.com/v1/accounts",
            ProviderUsageAdapterId.Novita => "https://api.novita.ai/v3/user/balance",
            _ => throw new ArgumentOutOfRangeException(nameof(adapterId)),
        });
    }

    public static IReadOnlyList<ProviderUsageTier> Parse(
        ProviderUsageAdapterId adapterId, Uri endpoint, JsonElement body)
    {
        Require(body.ValueKind == JsonValueKind.Object);
        return adapterId switch
        {
            ProviderUsageAdapterId.Zhipu => ParseZhipu(body),
            ProviderUsageAdapterId.Kimi => ParseKimi(body),
            ProviderUsageAdapterId.MiniMax => ParseMiniMax(body),
            ProviderUsageAdapterId.DeepSeek => ParseDeepSeek(body),
            ProviderUsageAdapterId.SiliconFlow => ParseSiliconFlow(body, endpoint.Host == "api.siliconflow.cn"),
            ProviderUsageAdapterId.OpenRouter => ParseOpenRouter(body),
            ProviderUsageAdapterId.StepFun => [Balance(RequiredNumber(body, "balance"), "CNY")],
            ProviderUsageAdapterId.Novita => [Balance(RequiredNumber(body, "availableBalance") / 10_000, "USD")],
            _ => throw new ArgumentOutOfRangeException(nameof(adapterId)),
        };
    }

    private static IReadOnlyList<ProviderUsageTier> ParseZhipu(JsonElement body)
    {
        Require(Field(body, "success").ValueKind != JsonValueKind.False);
        var data = Field(body, "data");
        var limits = Field(data, "limits");
        Require(limits.ValueKind == JsonValueKind.Array);
        var tiers = new Dictionary<ProviderUsageTierId, ProviderUsageTier>();
        var unclassified = new List<ProviderUsageTier>();
        foreach (var item in limits.EnumerateArray())
        {
            var type = Text(item, "type");
            if (!string.Equals(type, "tokens_limit", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "credit_limit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var tier = new ProviderUsageTier(
                ProviderUsageTierId.FiveHour,
                UsedPercentage: Number(item, "percentage") ?? 0,
                ResetsAt: Reset(Field(item, "nextResetTime")));
            var id = Number(item, "unit") switch
            {
                3 => ProviderUsageTierId.FiveHour,
                6 => ProviderUsageTierId.Weekly,
                5 => ProviderUsageTierId.Monthly,
                _ => (ProviderUsageTierId?)null,
            };
            if (id is { } known && !tiers.ContainsKey(known))
            {
                tiers.Add(known, tier with { Id = known });
            }
            else
            {
                unclassified.Add(tier);
            }
        }
        // Explicit window identities outrank reset ordering, including a weekly reset before the five-hour reset.
        foreach (var tier in unclassified.OrderBy(tier => tier.ResetsAt ?? DateTimeOffset.MinValue))
        {
            var id = !tiers.ContainsKey(ProviderUsageTierId.FiveHour)
                ? ProviderUsageTierId.FiveHour
                : ProviderUsageTierId.Weekly;
            tiers.TryAdd(id, tier with { Id = id });
        }
        return tiers.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
    }

    private static IReadOnlyList<ProviderUsageTier> ParseKimi(JsonElement body)
    {
        var limits = Field(body, "limits");
        var usage = Field(body, "usage");
        Require(limits.ValueKind == JsonValueKind.Array || usage.ValueKind == JsonValueKind.Object);
        var tiers = new List<ProviderUsageTier>();
        if (limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                if (Field(item, "detail") is { ValueKind: JsonValueKind.Object } detail)
                {
                    tiers.Add(KimiTier(detail, ProviderUsageTierId.FiveHour));
                }
            }
        }
        if (usage.ValueKind == JsonValueKind.Object)
        {
            tiers.Add(KimiTier(usage, ProviderUsageTierId.Weekly));
        }
        return tiers;
    }

    private static ProviderUsageTier KimiTier(JsonElement detail, ProviderUsageTierId id)
    {
        var total = RequiredNumber(detail, "limit");
        var remaining = RequiredNumber(detail, "remaining");
        var used = Math.Max(0, total - remaining);
        return new ProviderUsageTier(id,
            UsedPercentage: total > 0 ? used / total * 100 : null,
            Remaining: remaining, Total: total, Used: used,
            ResetsAt: Reset(Field(detail, "resetTime")));
    }

    private static IReadOnlyList<ProviderUsageTier> ParseMiniMax(JsonElement body)
    {
        var status = Number(Field(body, "base_resp"), "status_code");
        Require(status is null or 0);
        var remains = Field(body, "model_remains");
        Require(remains.ValueKind == JsonValueKind.Array);
        foreach (var item in remains.EnumerateArray())
        {
            if (Text(item, "model_name") != "general")
            {
                continue;
            }
            var tiers = new List<ProviderUsageTier>();
            if (Number(item, "current_interval_remaining_percent") is { } remaining)
            {
                tiers.Add(new ProviderUsageTier(ProviderUsageTierId.FiveHour,
                    UsedPercentage: 100 - remaining, ResetsAt: Reset(Field(item, "end_time"))));
            }
            if (Number(item, "current_weekly_status") == 1
                && Number(item, "current_weekly_remaining_percent") is { } weekly)
            {
                tiers.Add(new ProviderUsageTier(ProviderUsageTierId.Weekly,
                    UsedPercentage: 100 - weekly, ResetsAt: Reset(Field(item, "weekly_end_time"))));
            }
            return tiers;
        }
        return [];
    }

    private static IReadOnlyList<ProviderUsageTier> ParseDeepSeek(JsonElement body)
    {
        var infos = Field(body, "balance_infos");
        Require(infos.ValueKind == JsonValueKind.Array);
        foreach (var info in infos.EnumerateArray())
        {
            if (info.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var unit = Text(info, "currency");
            return [Balance(RequiredNumber(info, "total_balance"), Currency(unit, "CNY"))];
        }
        throw new JsonException();
    }

    private static IReadOnlyList<ProviderUsageTier> ParseSiliconFlow(JsonElement body, bool isCn) =>
        [Balance(RequiredNumber(Field(body, "data"), "totalBalance"), isCn ? "CNY" : "USD")];

    private static IReadOnlyList<ProviderUsageTier> ParseOpenRouter(JsonElement body)
    {
        var data = Field(body, "data");
        if (data.ValueKind != JsonValueKind.Object)
        {
            data = body;
        }
        var total = RequiredNumber(data, "total_credits");
        var used = RequiredNumber(data, "total_usage");
        return [new ProviderUsageTier(ProviderUsageTierId.Balance,
            Remaining: total - used, Total: total, Used: used, Unit: "USD")];
    }

    private static ProviderUsageTier Balance(double remaining, string unit) =>
        new(ProviderUsageTierId.Balance, Remaining: remaining, Unit: unit);

    private static JsonElement Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var field) ? field : default;

    private static string? Text(JsonElement element, string name) =>
        Field(element, name) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;

    private static double? Number(JsonElement element, string name) => Numeric(Field(element, name));

    private static double? Numeric(JsonElement field)
    {
        if (field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var numeric) && double.IsFinite(numeric))
        {
            return numeric;
        }
        return field.ValueKind == JsonValueKind.String
            && double.TryParse(field.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) ? parsed : null;
    }

    private static double RequiredNumber(JsonElement element, string name) =>
        Number(element, name) ?? throw new JsonException();

    private static DateTimeOffset? Reset(JsonElement field)
    {
        if (Numeric(field) is { } epoch && epoch > 0)
        {
            var milliseconds = epoch < 1_000_000_000_000 ? epoch * 1_000 : epoch;
            if (milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
            }
        }
        return field.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(field.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    private static string Currency(string? value, string fallback) =>
        value is { Length: 3 } && value.All(character => character is >= 'A' and <= 'Z') ? value : fallback;

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new JsonException();
        }
    }
}
