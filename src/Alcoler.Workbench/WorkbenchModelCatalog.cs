using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Alcoler.Contracts.Models;

namespace Alcoler.Workbench;

public sealed record ProviderTemplate(string Id, string Name, string Kind, string BaseUrl, string? ApiKeyManagementUrl, IReadOnlyList<string> ModelIds, string? AccessType = null)
{
    public const string ChatGptAccount = "chatgpt-account";
}

public static class WorkbenchModelCatalog
{
    private static readonly JsonObject Data = LoadData();
    private static readonly Dictionary<(string Kind, string BaseUrl, string? TemplateId, string ModelId), ModelProperties> DefaultsCache = [];
    private static readonly object CacheLock = new();

    public static IReadOnlyList<ProviderTemplate> Templates { get; } = ReadTemplates();

    public static ModelProperties Defaults(WorkbenchProviderConfig provider, string modelId)
    {
        var key = (provider.Kind, provider.BaseUrl, provider.TemplateId, modelId);
        lock (CacheLock)
        {
            if (DefaultsCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var resolved = ResolveDefaults(provider, modelId);
            if (DefaultsCache.Count >= 256)
            {
                DefaultsCache.Clear();
            }
            DefaultsCache[key] = resolved;
            return resolved;
        }
    }

    private static ModelProperties ResolveDefaults(WorkbenchProviderConfig provider, string modelId)
    {
        var merged = new JsonObject();
        var rules = Data["rules"]!.AsObject();
        var apiType = provider.Kind.ToLowerInvariant() switch
        {
            "anthropic" => "anthropic-messages",
            "openai-compatible" => "openai-chat-completions",
            "openai-responses" => "openai-responses",
            _ => provider.Kind,
        };
        foreach (var bucket in new[] { "modelRules", "modelApiRules", "providerSiteRules", "templateModelRules" })
        {
            foreach (var entry in rules[bucket]!.AsArray())
            {
                var rule = entry!.AsObject();
                if (!Matches(rule["modelMatch"]?.GetValue<string>(), modelId)
                    || !Matches(rule["apiTypeMatch"]?.GetValue<string>(), apiType)
                    || !Matches(rule["baseUrlMatch"]?.GetValue<string>(), provider.BaseUrl)
                    || (rule["templateId"] is { } template && template.GetValue<string>() != provider.TemplateId)
                    || (rule["modelId"] is { } exact && !string.Equals(exact.GetValue<string>(), modelId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                Merge(merged, rule["config"]!.AsObject());
            }
        }
        var properties = merged["properties"]!.AsObject();
        var input = properties["inputFormat"]!.AsObject();
        var options = merged["optionSpecs"]!.AsObject();
        return new ModelProperties(
            properties["contextWindow"]!.GetValue<int>(),
            options["maxOutputTokens"]!["max"]!.GetValue<int>(),
            SupportsImage: Flag(input, "supportsImage"),
            SupportsVideo: Flag(input, "supportsVideo"),
            SupportsAudio: Flag(input, "supportsAudio"),
            SupportsPdf: Flag(input, "supportsPdf"),
            SupportsJsonSchemaOutput: Flag(properties, "supportsJsonSchemaOutput"),
            SupportsNativeWebSearch: Flag(properties, "supportsNativeWebSearch"),
            SupportsMidConversationSystem: Flag(properties, "supportsMidConversationSystem"),
            SupportsToolCall: Flag(properties, "supportsToolCall"),
            RequiresMfjsToolSchema: Flag(properties, "requiresMfjsToolSchema"),
            ReasoningLevels: options["reasoningLevel"]!["values"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
            ReasoningMapping: options["reasoningLevel"]!["map"]?.GetValue<string>(),
            MaxOutputTokensMapping: options["maxOutputTokens"]!["map"]?.GetValue<string>());
    }

    public static ModelProperties Resolve(WorkbenchProviderConfig provider, WorkbenchModelConfig model)
    {
        var defaults = Defaults(provider, model.ModelId);
        return defaults with
        {
            ContextWindowTokens = model.ContextWindowTokens ?? defaults.ContextWindowTokens,
            DefaultMaxOutputTokens = model.MaxOutputTokens ?? defaults.DefaultMaxOutputTokens,
            SupportsImage = model.SupportsImage ?? defaults.SupportsImage,
            SupportsVideo = model.SupportsVideo ?? defaults.SupportsVideo,
            SupportsAudio = model.SupportsAudio ?? defaults.SupportsAudio,
            SupportsPdf = model.SupportsPdf ?? defaults.SupportsPdf,
            SupportsJsonSchemaOutput = model.SupportsJsonSchemaOutput ?? defaults.SupportsJsonSchemaOutput,
            SupportsNativeWebSearch = model.SupportsNativeWebSearch ?? defaults.SupportsNativeWebSearch,
            SupportsMidConversationSystem = model.SupportsMidConversationSystem ?? defaults.SupportsMidConversationSystem,
            ReasoningLevels = model.ReasoningLevels ?? defaults.ReasoningLevels,
            ReasoningMapping = model.ReasoningMapping ?? defaults.ReasoningMapping,
            MaxOutputTokensMapping = model.MaxOutputTokensMapping ?? defaults.MaxOutputTokensMapping,
        };
    }

    public static ModelSelection? DefaultSelection(WorkbenchConfig config)
    {
        var candidates = config.Providers.Where(provider => provider.Enabled)
            .SelectMany(provider => provider.Models.Where(model => model.Enabled).Select(model => (Provider: provider, Model: model)))
            .ToArray();
        var selected = candidates.FirstOrDefault(candidate => candidate.Provider.EffectiveId == config.DefaultModel?.ProviderId
            && candidate.Model.ModelId == config.DefaultModel.ModelId);
        if (selected.Provider is null)
        {
            selected = candidates.FirstOrDefault();
        }
        if (selected.Provider is null)
        {
            return null;
        }
        var properties = Resolve(selected.Provider, selected.Model);
        return new ModelSelection(selected.Provider.EffectiveId, selected.Model.ModelId,
            new ModelCallOptions(ReasoningLevel: properties.ReasoningLevels?.LastOrDefault()));
    }

    private static JsonObject LoadData()
    {
        using var stream = typeof(WorkbenchModelCatalog).Assembly.GetManifestResourceStream("Alcoler.Workbench.Models.ModelDefaults.json")!;
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static IReadOnlyList<ProviderTemplate> ReadTemplates()
    {
        var result = new List<ProviderTemplate>();
        foreach (var entry in Data["templates"]!.AsArray())
        {
            var config = entry!["config"]!;
            var type = config["api"]?["type"]?.GetValue<string>();
            var kind = type switch
            {
                "anthropic-messages" => "anthropic",
                "openai-chat-completions" => "openai-compatible",
                "openai-responses" => "openai-responses",
                _ => null,
            };
            if (kind is null || config["access"]?["type"]?.GetValue<string>() is not ("api-key" or "zhipu-coding-plan-api-key" or ProviderTemplate.ChatGptAccount))
            {
                continue;
            }
            result.Add(new ProviderTemplate(
                entry!["templateId"]!.GetValue<string>(),
                entry["templateNameMap"]!["en-US"]!.GetValue<string>(),
                kind,
                config["api"]!["baseUrl"]!.GetValue<string>(),
                config["access"]?["apiKeyManagementUrl"]?.GetValue<string>(),
                config["builtinModelIds"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [],
                config["access"]?["type"]?.GetValue<string>() == ProviderTemplate.ChatGptAccount ? ProviderTemplate.ChatGptAccount : null));
        }
        return result;
    }

    private static bool Flag(JsonObject properties, string key) => properties[key]?.GetValue<bool>() == true;

    private static bool Matches(string? pattern, string value) => pattern is null
        || Regex.IsMatch(value, $"\\A(?:{pattern})\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (pair.Value is JsonObject nested && target[pair.Key] is JsonObject existing)
            {
                Merge(existing, nested);
            }
            else
            {
                target[pair.Key] = pair.Value?.DeepClone();
            }
        }
    }
}
