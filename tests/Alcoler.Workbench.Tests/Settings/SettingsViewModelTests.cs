using Alcoler.Workbench.Themes;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace Alcoler.Workbench.Tests.Settings;

public class SettingsViewModelTests
{
    private static string TempConfigPath() =>
        Path.Combine(Path.GetTempPath(), "alcoler-settings-test-" + Guid.NewGuid().ToString("N") + ".json");

    private static WorkbenchConfig SampleConfig() => new(
    [
        new WorkbenchProviderConfig(
            "anthropic",
            "https://api.anthropic.com",
            "sk-test",
            [new WorkbenchModelConfig("claude-sonnet-4-5", 200_000, "Sonnet")]),
    ],
    new WorkbenchDefaultModelConfig("anthropic", "claude-sonnet-4-5"),
    [
        new WorkbenchMcpServerConfig("fetch", "uvx", ["mcp-server-fetch"], new Dictionary<string, string>
        {
            ["HTTP_TIMEOUT"] = "30",
        }),
    ]);

    [Fact]
    public async Task SaveAsync_RoundTripsEditsThroughLoadOrDefault()
    {
        var path = TempConfigPath();
        try
        {
            var viewModel = new SettingsViewModel(SampleConfig());

            var provider = Assert.Single(viewModel.Providers);
            provider.AddModelCommand.Execute(null);
            provider.Models[1].ModelId = "claude-haiku-4-5";
            provider.Models[1].ContextWindowTokens = "100000";

            viewModel.AddServerCommand.Execute(null);
            var server = Assert.Single(viewModel.McpServers, s => s.Name.Length == 0);
            server.Name = "filesystem";
            server.Command = "npx";
            server.ArgsLine = "-y @modelcontextprotocol/server-filesystem \"C:\\ws\"";
            server.EnvText = "MCP_MODE=strict";

            viewModel.Theme = "light";

            var saved = await viewModel.SaveAsync(path);
            Assert.NotNull(saved);
            Assert.Equal("light", saved!.Theme);
            Assert.Null(saved.Providers[0].Models[1].DisplayName);
            Assert.Equal("claude-sonnet-4-5", saved.DefaultModel!.ModelId);

            var loaded = WorkbenchConfig.LoadOrDefault(path);
            var loadedProvider = Assert.Single(loaded.Providers);
            Assert.Equal("anthropic", loadedProvider.Kind);
            Assert.Equal("https://api.anthropic.com", loadedProvider.BaseUrl);
            Assert.Equal("sk-test", loadedProvider.ApiKey);
            Assert.Equal(["claude-sonnet-4-5", "claude-haiku-4-5"], loadedProvider.Models.Select(m => m.ModelId));
            Assert.Equal(200_000, loadedProvider.Models[0].ContextWindowTokens);
            Assert.Equal(100_000, loadedProvider.Models[1].ContextWindowTokens);
            Assert.Equal("Sonnet", loadedProvider.Models[0].DisplayName);

            Assert.Equal(2, loaded.McpServers!.Count);
            var fetch = loaded.McpServers[0];
            Assert.Equal("fetch", fetch.Name);
            Assert.Equal("uvx", fetch.Command);
            Assert.Equal(["mcp-server-fetch"], fetch.Args);
            Assert.Equal("30", fetch.Env!["HTTP_TIMEOUT"]);
            var filesystem = loaded.McpServers[1];
            Assert.Equal("filesystem", filesystem.Name);
            Assert.Equal("npx", filesystem.Command);
            Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "C:\\ws"], filesystem.Args);
            Assert.Equal("strict", filesystem.Env!["MCP_MODE"]);

            Assert.Equal("light", loaded.Theme);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_RejectsUnknownProviderKind()
    {
        var path = TempConfigPath();
        try
        {
            var viewModel = new SettingsViewModel(new WorkbenchConfig([]));
            viewModel.AddProviderCommand.Execute(null);
            var provider = Assert.Single(viewModel.Providers);
            provider.Kind = "gemini";
            provider.AddModelCommand.Execute(null);
            provider.Models[0].ModelId = "gemini-pro";

            var saved = await viewModel.SaveAsync(path);

            Assert.Null(saved);
            Assert.False(string.IsNullOrEmpty(viewModel.Error));
            Assert.Contains("kind", viewModel.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_RejectsEmptyModelId()
    {
        var path = TempConfigPath();
        try
        {
            var viewModel = new SettingsViewModel(SampleConfig());
            var provider = Assert.Single(viewModel.Providers);
            provider.AddModelCommand.Execute(null);
            provider.Models[1].ModelId = "   ";

            var saved = await viewModel.SaveAsync(path);

            Assert.Null(saved);
            Assert.Contains("model id", viewModel.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public async Task SaveAsync_RejectsNonPositiveContextWindows(string tokens)
    {
        var path = TempConfigPath();
        try
        {
            var viewModel = new SettingsViewModel(SampleConfig());
            var provider = Assert.Single(viewModel.Providers);
            provider.Models[0].ContextWindowTokens = tokens;

            var saved = await viewModel.SaveAsync(path);

            Assert.Null(saved);
            Assert.Contains("context window", viewModel.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_ClearsErrorAfterValidSave()
    {
        var path = TempConfigPath();
        try
        {
            var viewModel = new SettingsViewModel(SampleConfig());
            viewModel.Providers[0].Kind = "bogus";
            Assert.Null(await viewModel.SaveAsync(path));
            Assert.True(viewModel.HasError);

            viewModel.Providers[0].Kind = "openai-compatible";
            var saved = await viewModel.SaveAsync(path);

            Assert.NotNull(saved);
            Assert.Equal("openai-compatible", saved!.Providers[0].Kind);
            Assert.Null(viewModel.Error);
            Assert.False(viewModel.HasError);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void LoadOrDefault_LegacyConfigWithoutTheme_DefaultsToDark()
    {
        var path = TempConfigPath();
        try
        {
            File.WriteAllText(path, """{"providers":[]}""");

            var loaded = WorkbenchConfig.LoadOrDefault(path);

            Assert.Equal("dark", loaded.Theme);
            Assert.Empty(loaded.Providers);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Save_CreatesMissingDirectoryAndWritesLoadableFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "alcoler-settings-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "nested", "config.json");
        try
        {
            new WorkbenchConfig([], Theme: "light").Save(path);

            Assert.True(File.Exists(path));
            Assert.Equal("light", WorkbenchConfig.LoadOrDefault(path).Theme);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public void ThemeSelector_Apply_SwitchesVariantWithBothDictionariesMerged()
    {
        var application = Application.Current;
        Assert.NotNull(application);

        Assert.Contains(application.Resources.MergedDictionaries,
            d => d is ResourceDictionary { ThemeDictionaries.Count: 2 });

        ThemeSelector.Apply(ThemeSelector.Light);
        Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);

        var window = new Window();
        window.Show();
        Assert.True(window.TryFindResource("AlcolerBackground", ThemeVariant.Light, out var lightBrush));
        Assert.Equal(Color.Parse("#FFF6F7F9"), Assert.IsType<SolidColorBrush>(lightBrush).Color);
        window.Close();

        ThemeSelector.Apply(ThemeSelector.Dark);
        Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);

        ThemeSelector.Apply("bogus");
        Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
    }

    [AvaloniaFact]
    public void SettingsDialog_ConstructsFromCurrentConfig()
    {
        var dialog = new Workbench.Views.SettingsDialog(SampleConfig());
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var providers = dialog.GetVisualDescendants().OfType<TextBox>().Count();
        Assert.True(providers > 0);
        dialog.Close();
    }
}
