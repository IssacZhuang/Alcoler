using Alcoler.Workbench.Themes;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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
        Assert.Equal(Color.Parse("#FFF8F8F8"), Assert.IsType<SolidColorBrush>(lightBrush).Color);
        window.Close();

        ThemeSelector.Apply(ThemeSelector.Dark);
        Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);

        ThemeSelector.Apply("bogus");
        Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
    }

    [Fact]
    public void Providers_SelectNewAndAdjacentItemsWithoutLosingDrafts()
    {
        var viewModel = new SettingsViewModel(SampleConfig());
        var original = Assert.Single(viewModel.Providers);
        Assert.Same(original, viewModel.SelectedProvider);

        viewModel.AddProviderCommand.Execute(null);
        var added = Assert.IsType<ProviderViewModel>(viewModel.SelectedProvider);
        Assert.NotSame(original, added);
        Assert.Equal("New provider", added.Label);
        added.Kind = "openai-compatible";
        added.BaseUrl = "https://example.com/v1";
        Assert.Equal("openai-compatible", added.Label);

        viewModel.SelectedProvider = original;
        viewModel.SelectedProvider = added;
        Assert.Equal("https://example.com/v1", added.BaseUrl);
        added.RemoveProviderCommand.Execute(null);
        Assert.Same(original, viewModel.SelectedProvider);
        original.RemoveProviderCommand.Execute(null);
        Assert.Empty(viewModel.Providers);
        Assert.Null(viewModel.SelectedProvider);
    }

    [Fact]
    public void Provider_ApiKeyVisibilityDoesNotChangeStoredValue()
    {
        var provider = new SettingsViewModel(SampleConfig()).Providers[0];
        Assert.NotEqual('\0', provider.ApiKeyPasswordChar);
        provider.IsApiKeyVisible = true;
        Assert.Equal('\0', provider.ApiKeyPasswordChar);
        Assert.Equal("sk-test", provider.ApiKey);
        provider.IsApiKeyVisible = false;
        Assert.NotEqual('\0', provider.ApiKeyPasswordChar);
    }

    [Fact]
    public async Task SaveAsync_ReportsWriteFailureAndAllowsRetry()
    {
        var blockingFile = TempConfigPath();
        var recoveryPath = TempConfigPath();
        try
        {
            File.WriteAllText(blockingFile, "blocked");
            var viewModel = new SettingsViewModel(SampleConfig());
            Assert.Null(await viewModel.SaveAsync(Path.Combine(blockingFile, "config.json")));
            Assert.True(viewModel.HasError);
            Assert.Contains("Could not save settings", viewModel.Error);
            Assert.NotNull(await viewModel.SaveAsync(recoveryPath));
            Assert.False(viewModel.HasError);
        }
        finally
        {
            File.Delete(blockingFile);
            File.Delete(recoveryPath);
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_SwitchesSupportedSectionsAndPreservesProviderDraft()
    {
        var dialog = new Workbench.Views.SettingsDialog(SampleConfig());
        try
        {
            dialog.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var viewModel = Assert.IsType<SettingsViewModel>(dialog.DataContext);
            var providerList = dialog.FindControl<ListBox>("ProviderList")!;
            Assert.Same(viewModel.Providers[0], providerList.SelectedItem);
            var baseUrl = dialog.GetVisualDescendants().OfType<TextBox>()
                .Single(box => box.Watermark == "https://api.example.com");
            Assert.Equal(36, baseUrl.MinHeight);
            baseUrl.Text = "https://edited.example.com";

            dialog.FindControl<RadioButton>("McpTab")!.IsChecked = true;
            Assert.True(dialog.FindControl<ScrollViewer>("McpPanel")!.IsVisible);
            Assert.False(dialog.FindControl<Grid>("ProvidersPanel")!.IsVisible);
            Assert.Equal("MCP servers", dialog.FindControl<TextBlock>("SectionTitle")!.Text);

            dialog.FindControl<RadioButton>("AppearanceTab")!.IsChecked = true;
            Assert.True(dialog.FindControl<ScrollViewer>("AppearancePanel")!.IsVisible);
            Assert.False(dialog.FindControl<ScrollViewer>("McpPanel")!.IsVisible);
            dialog.FindControl<ComboBox>("ThemePicker")!.SelectedItem = "light";
            Assert.Equal("light", viewModel.Theme);

            dialog.FindControl<RadioButton>("ProvidersTab")!.IsChecked = true;
            Assert.True(dialog.FindControl<Grid>("ProvidersPanel")!.IsVisible);
            Assert.Equal("https://edited.example.com", viewModel.Providers[0].BaseUrl);
            Assert.Equal("https://edited.example.com", baseUrl.Text);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_ProviderCrudUpdatesDetailAndEmptyState()
    {
        var dialog = new Workbench.Views.SettingsDialog(new WorkbenchConfig([]));
        try
        {
            dialog.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var viewModel = Assert.IsType<SettingsViewModel>(dialog.DataContext);
            Assert.True(dialog.FindControl<StackPanel>("ProviderEmptyState")!.IsVisible);
            viewModel.AddProviderCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<StackPanel>("ProviderEmptyState")!.IsVisible);
            Assert.Same(viewModel.SelectedProvider, dialog.FindControl<ListBox>("ProviderList")!.SelectedItem);
            var provider = Assert.IsType<ProviderViewModel>(viewModel.SelectedProvider);
            provider.AddModelCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBox>(), box => box.Watermark == "Model ID");
            provider.Models[0].RemoveCommand.Execute(null);
            Assert.Empty(provider.Models);
            provider.RemoveProviderCommand.Execute(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<StackPanel>("ProviderEmptyState")!.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsDialog_InvalidSaveStaysOpenWithVisibleValidation()
    {
        var dialog = new Workbench.Views.SettingsDialog(SampleConfig());
        try
        {
            dialog.Show();
            var viewModel = Assert.IsType<SettingsViewModel>(dialog.DataContext);
            viewModel.Providers[0].Kind = "unsupported";
            dialog.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.IsVisible);
            Assert.True(dialog.FindControl<TextBlock>("SaveError")!.IsVisible);
            Assert.Contains("kind", dialog.FindControl<TextBlock>("SaveError")!.Text);
            Assert.True(dialog.FindControl<Button>("SaveButton")!.IsEnabled);
            Assert.True(dialog.FindControl<Button>("CancelButton")!.IsEnabled);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task SettingsDialog_CloseCancelAndBackDiscardDrafts()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            foreach (var buttonName in new[] { "CancelButton", "CloseButton", "BackButton" })
            {
                var config = SampleConfig();
                var result = Workbench.Views.SettingsDialog.ShowAsync(owner, config);
                var dialog = Assert.IsType<Workbench.Views.SettingsDialog>(Assert.Single(owner.OwnedWindows));
                var viewModel = Assert.IsType<SettingsViewModel>(dialog.DataContext);
                viewModel.Providers[0].ApiKey = "unsaved";
                dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Null(await result);
                Assert.Equal("sk-test", config.Providers[0].ApiKey);
            }
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void Dialogs_UseDynamicThemeSurfaces()
    {
        foreach (var dialog in new Window[]
                 {
                     new Workbench.Views.SettingsDialog(SampleConfig()),
                     new Workbench.Views.NewSessionDialog(),
                 })
        {
            try
            {
                dialog.RequestedThemeVariant = ThemeVariant.Dark;
                dialog.Show();
                var dark = Assert.IsType<SolidColorBrush>(dialog.Background).Color;
                dialog.RequestedThemeVariant = ThemeVariant.Light;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var light = Assert.IsType<SolidColorBrush>(dialog.Background).Color;
                Assert.NotEqual(dark, light);
            }
            finally
            {
                dialog.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task NewSessionDialog_ValidatesWorkspaceAndReturnsTrimmedRequest()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var result = Workbench.Views.NewSessionDialog.ShowAsync(owner, "  ");
            var dialog = Assert.IsType<Workbench.Views.NewSessionDialog>(Assert.Single(owner.OwnedWindows));
            var workspace = dialog.FindControl<TextBox>("WorkspaceBox")!;
            dialog.FindControl<Button>("CreateButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(result.IsCompleted);
            Assert.True(dialog.FindControl<TextBlock>("WorkspaceError")!.IsVisible);

            workspace.Text = "  C:\\workspace  ";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.FindControl<TextBlock>("WorkspaceError")!.IsVisible);
            dialog.FindControl<TextBox>("TitleBox")!.Text = "  Investigate issue  ";
            dialog.FindControl<Button>("CreateButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var request = Assert.IsType<Workbench.Views.NewSessionRequest>(await result);
            Assert.Equal("C:\\workspace", request.WorkspaceRoot);
            Assert.Equal("Investigate issue", request.Title);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task NewSessionDialog_PreservesDefaultWorkspaceAndOptionalTitle()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var result = Workbench.Views.NewSessionDialog.ShowAsync(owner, "C:\\workspace");
            var dialog = Assert.IsType<Workbench.Views.NewSessionDialog>(Assert.Single(owner.OwnedWindows));
            Assert.Equal("C:\\workspace", dialog.FindControl<TextBox>("WorkspaceBox")!.Text);
            dialog.FindControl<TextBox>("TitleBox")!.Text = "  ";
            dialog.FindControl<Button>("CreateButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var request = Assert.IsType<Workbench.Views.NewSessionRequest>(await result);
            Assert.Equal("C:\\workspace", request.WorkspaceRoot);
            Assert.Null(request.Title);

            foreach (var buttonName in new[] { "CancelButton", "CloseButton" })
            {
                var canceled = Workbench.Views.NewSessionDialog.ShowAsync(owner, "C:\\workspace");
                dialog = Assert.IsType<Workbench.Views.NewSessionDialog>(Assert.Single(owner.OwnedWindows));
                dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Null(await canceled);
            }
        }
        finally
        {
            owner.Close();
        }
    }
}
