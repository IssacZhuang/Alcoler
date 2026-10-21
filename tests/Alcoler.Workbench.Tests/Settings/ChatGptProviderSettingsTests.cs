using Alcoler.Workbench;
using Alcoler.Workbench.ViewModel;
using Xunit;

namespace Alcoler.Workbench.Tests.Settings;

public class ChatGptProviderSettingsTests
{
    [Fact]
    public void ChatGptTemplate_UsesAccountAccess()
    {
        var template = Assert.Single(WorkbenchModelCatalog.Templates, template => template.Id == "chatgpt");
        Assert.Equal("openai-responses", template.Kind);
        Assert.Equal("https://api.openai.com/v1", template.BaseUrl);
        Assert.Equal(ProviderTemplate.ChatGptAccount, template.AccessType);
        Assert.Null(template.ApiKeyManagementUrl);
    }

    [Fact]
    public void ChatGptProvider_MarksAccountAccessWithoutApiKey()
    {
        var settings = new SettingsViewModel(new WorkbenchConfig([]));
        var template = Assert.Single(WorkbenchModelCatalog.Templates, template => template.Id == "chatgpt");
        settings.AddProviderFromTemplate(template);
        var provider = Assert.Single(settings.Providers);
        Assert.True(provider.IsChatGptAccount);
        var config = provider.ToConfig();
        Assert.Equal(ProviderTemplate.ChatGptAccount, config.AccessType);
        Assert.Null(config.ApiKey);
        Assert.Equal("openai-responses", config.Kind);
    }

    [Fact]
    public void ApiKeyProviders_AreNotAccountProviders()
    {
        var settings = new SettingsViewModel(new WorkbenchConfig([]));
        settings.AddProviderCommand.Execute(null);
        var provider = Assert.Single(settings.Providers);
        provider.Kind = "openai-responses";
        provider.ApiKey = "sk-key";
        Assert.False(provider.IsChatGptAccount);
        Assert.Null(provider.ToConfig().AccessType);
    }

    [Fact]
    public void ApplyChatGptModels_ReplacesThePlanCatalog()
    {
        var settings = new SettingsViewModel(new WorkbenchConfig([]));
        var template = Assert.Single(WorkbenchModelCatalog.Templates, template => template.Id == "chatgpt");
        settings.AddProviderFromTemplate(template);
        var provider = Assert.Single(settings.Providers);
        settings.ApplyChatGptModels(["gpt-plan-a", "gpt-plan-b"]);
        Assert.Equal(["gpt-plan-a", "gpt-plan-b"], provider.Models.Select(model => model.ModelId));
        settings.ApplyChatGptModels(["gpt-plan-c"]);
        Assert.Equal(["gpt-plan-c"], provider.Models.Select(model => model.ModelId));
    }

    [Fact]
    public void ConfigRoundTrip_PreservesAccountAccess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alcoler-settings-{Guid.NewGuid():N}.json");
        try
        {
            new WorkbenchConfig([new WorkbenchProviderConfig("openai-responses", "https://api.openai.com/v1", null, [],
                "chatgpt-provider", "ChatGPT", AccessType: ProviderTemplate.ChatGptAccount)]).Save(path);
            var provider = Assert.Single(WorkbenchConfig.LoadOrDefault(path).Providers);
            Assert.Equal(ProviderTemplate.ChatGptAccount, provider.AccessType);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }
}
