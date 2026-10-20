using Alcoler.Adapters.Models;
using Alcoler.Contracts.Models;
using Xunit;

namespace Alcoler.Workbench.Tests.Settings;

public sealed class WorkbenchModelCatalogTests
{
    [Theory]
    [InlineData("anthropic", "max_tokens")]
    [InlineData("openai-compatible", "max_completion_tokens")]
    [InlineData("openai-responses", "max_output_tokens")]
    public void Defaults_UnknownModelInheritsProtocolAndCatchAll(string kind, string outputKey)
    {
        var provider = new WorkbenchProviderConfig(kind, "https://example.com/v1", null, []);
        var properties = WorkbenchModelCatalog.Defaults(provider, "unknown-model");

        Assert.Equal(200_000, properties.ContextWindowTokens);
        Assert.Equal(32_000, properties.DefaultMaxOutputTokens);
        Assert.Equal(["disabled", "enabled"], properties.ReasoningLevels);
        Assert.False(properties.SupportsImage);
        var output = ModelOptionMapping.Evaluate(properties.MaxOutputTokensMapping!, maxOutputTokens: 1024);
        Assert.Equal(1024, output[outputKey]!.GetValue<double>());
        ModelOptionMapping.Validate(properties.ReasoningMapping!);
    }

    [Fact]
    public void Resolve_KnownModelAppliesSparseExplicitOverrides()
    {
        var provider = new WorkbenchProviderConfig("openai-responses", "https://api.openai.com/v1", null, []);
        var defaults = WorkbenchModelCatalog.Defaults(provider, "GPT-6-ASTRA");
        var effective = WorkbenchModelCatalog.Resolve(provider, new WorkbenchModelConfig(
            "GPT-6-ASTRA", ContextWindowTokens: 256_000, SupportsPdf: false));

        Assert.Equal(1_050_000, defaults.ContextWindowTokens);
        Assert.Equal(128_000, defaults.DefaultMaxOutputTokens);
        Assert.True(defaults.SupportsImage);
        Assert.True(defaults.SupportsPdf);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], defaults.ReasoningLevels);
        Assert.Equal(256_000, effective.ContextWindowTokens);
        Assert.False(effective.SupportsPdf);
        Assert.True(effective.SupportsImage);
        Assert.Equal(defaults.ReasoningMapping, effective.ReasoningMapping);
    }

    [Fact]
    public void Defaults_SiteOverridesLegacyOutputAndReasoningMappings()
    {
        var provider = new WorkbenchProviderConfig("openai-compatible", "https://opencode.ai/zen/go/v1", null, []);
        var properties = WorkbenchModelCatalog.Defaults(provider, "glm-5.3");

        var output = ModelOptionMapping.Evaluate(properties.MaxOutputTokensMapping!, maxOutputTokens: 1234);
        Assert.Equal(1234, output["max_tokens"]!.GetValue<double>());
        var reasoning = ModelOptionMapping.Evaluate(properties.ReasoningMapping!, "high");
        Assert.Equal("high", reasoning["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void BuildProviders_PreservesSeparateIdentitiesAndFiltersDisabledModels()
    {
        var config = new WorkbenchConfig([
            new("openai-compatible", "https://one.example/v1", null,
                [new("first"), new("disabled", Enabled: false)], Id: "one"),
            new("openai-compatible", "https://two.example/v1", null, [new("second")], Id: "two"),
            new("anthropic", "https://api.anthropic.com", null, [], Id: "hidden", Enabled: false),
        ]);
        var providers = Composition.BuildProviders(config);

        Assert.Equal(["one", "two"], providers.Select(provider => provider.Id));
        Assert.All(providers, provider => Assert.Equal("openai-compatible", provider.Kind));
    }

    [Fact]
    public void Templates_ExposeApiKeyProvidersWithValidCatalogs()
    {
        Assert.NotEmpty(WorkbenchModelCatalog.Templates);
        Assert.Equal(WorkbenchModelCatalog.Templates.Count,
            WorkbenchModelCatalog.Templates.Select(template => template.Id).Distinct().Count());
        foreach (var template in WorkbenchModelCatalog.Templates)
        {
            Assert.True(Uri.TryCreate(template.BaseUrl, UriKind.Absolute, out _));
            var provider = new WorkbenchProviderConfig(template.Kind, template.BaseUrl, null, [], TemplateId: template.Id);
            foreach (var model in template.ModelIds)
            {
                var properties = WorkbenchModelCatalog.Defaults(provider, model);
                Assert.True(properties.ContextWindowTokens > 0);
                Assert.True(properties.DefaultMaxOutputTokens > 0);
                Assert.NotEmpty(properties.ReasoningLevels!);
                foreach (var level in properties.ReasoningLevels!)
                {
                    ModelOptionMapping.Evaluate(properties.ReasoningMapping!, level);
                }
            }
        }
    }
}
