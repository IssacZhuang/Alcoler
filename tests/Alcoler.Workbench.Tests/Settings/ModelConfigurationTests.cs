using System.Text.Json;
using Alcoler.Workbench.Controls;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;

namespace Alcoler.Workbench.Tests.Settings;

public class ModelConfigurationTests
{
    private const string ReasoningMap = "reasoningLevel == 'off' ? {} : {'reasoning_effort': reasoningLevel}";
    private const string MaxMap = "{'max_tokens': maxOutputTokens}";

    private static WorkbenchConfig Config(WorkbenchModelConfig? model = null) => new(
        [new WorkbenchProviderConfig("openai-compatible", "https://example.invalid/v1", "secret",
            [model ?? new WorkbenchModelConfig("custom-model")], "custom-provider", "My provider", false,
            "https://example.invalid/keys", "custom-template")], McpServers: []);

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "alcoler-model-settings-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public async Task Settings_RoundTripsAllProviderAndModelFields()
    {
        var model = new WorkbenchModelConfig("custom-model", 12345, "Display", false, false, 20000,
            true, false, true, false, true, false, true, ["off", "low", "high"], ReasoningMap, MaxMap);
        var path = TempPath();
        try
        {
            var config = Config(model);
            using var settings = new SettingsViewModel(config);
            Assert.NotNull(await settings.SaveAsync(path));
            var loaded = WorkbenchConfig.LoadOrDefault(path);
            Assert.Equal(JsonSerializer.Serialize(config), JsonSerializer.Serialize(loaded));
            var provider = loaded.Providers[0];
            Assert.False(provider.Enabled);
            Assert.Equal("custom-provider", provider.EffectiveId);
            Assert.Equal("My provider", provider.Label);
            Assert.False(provider.Models[0].Enabled);
            Assert.Equal(20000, provider.Models[0].MaxOutputTokens);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LegacyDefaults_StaySparseAndKeepIdentityWhenProtocolChanges()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """{"providers":[{"kind":"anthropic","baseUrl":"https://example.invalid","models":[{"modelId":"legacy-model"}]}]}""");
            using var settings = new SettingsViewModel(WorkbenchConfig.LoadOrDefault(path));
            var provider = settings.Providers[0];
            var config = provider.ToConfig();
            Assert.Null(config.Id);
            Assert.Equal("anthropic", config.EffectiveId);
            Assert.True(config.Enabled);
            Assert.True(config.Models[0].Enabled);
            Assert.True(config.Models[0].SmartConfiguration);
            Assert.Null(config.Models[0].SupportsImage);
            Assert.Null(config.Models[0].ReasoningLevels);
            provider.SelectedApiFormat = SettingsViewModel.ApiFormats[2];
            Assert.Equal("anthropic", provider.EffectiveId);
            Assert.Equal("openai-responses", provider.Kind);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Providers_NewIdsAreUniqueStableAndTemplatesApplyMetadata()
    {
        using var settings = new SettingsViewModel(new WorkbenchConfig([]));
        settings.AddProviderCommand.Execute(null);
        var first = settings.Providers[0];
        settings.AddProviderCommand.Execute(null);
        var second = settings.Providers[1];
        Assert.NotEqual(first.Id, second.Id);
        var id = first.Id;
        first.Kind = "anthropic";
        first.Name = "Renamed";
        first.Kind = "openai-responses";
        Assert.Equal(id, first.Id);
        var template = WorkbenchModelCatalog.Templates.First(item => item.ModelIds.Count > 0);
        settings.AddProviderFromTemplate(template);
        var provider = settings.Providers[^1].ToConfig();
        Assert.Equal(template.Kind, provider.Kind);
        Assert.Equal(template.Name, provider.Name);
        Assert.Equal(template.BaseUrl, provider.BaseUrl);
        Assert.Equal(template.Id, provider.TemplateId);
        Assert.Equal(template.ApiKeyManagementUrl, provider.ApiKeyManagementUrl);
        Assert.Equal(template.ModelIds, provider.Models.Select(model => model.ModelId));
    }

    [Fact]
    public void SmartEditor_UnchangedSaveIsSparseAndBooleanClickRecordsExplicitOverride()
    {
        using var settings = new SettingsViewModel(Config());
        var provider = settings.Providers[0];
        var original = provider.Models[0];
        var editor = provider.CreateModelEditor(original);
        Assert.Equal(string.Empty, editor.Draft.ContextWindowTokens);
        Assert.Equal(original.Defaults.SupportsImage, editor.Draft.EffectiveSupportsImage);
        Assert.True(editor.TrySave());
        Assert.Null(original.ToConfig().ContextWindowTokens);
        Assert.Null(original.ToConfig().SupportsImage);
        Assert.Null(original.ToConfig().ReasoningLevels);
        editor = provider.CreateModelEditor(original);
        editor.Draft.EffectiveSupportsImage = editor.Draft.EffectiveSupportsImage;
        Assert.True(editor.TrySave());
        Assert.NotNull(original.ToConfig().SupportsImage);
        Assert.Null(original.ToConfig().SupportsPdf);
    }

    [Fact]
    public void ManualEditor_FreezesEditableLeavesAndRestoreKeepsHiddenMetadataAndEnabled()
    {
        var model = new WorkbenchModelConfig("custom-model", DisplayName: "Preserved", Enabled: false,
            SupportsAudio: true, MaxOutputTokensMapping: MaxMap);
        using var settings = new SettingsViewModel(Config(model));
        var provider = settings.Providers[0];
        var editor = provider.CreateModelEditor(provider.Models[0]);
        editor.Draft.SmartConfiguration = false;
        Assert.False(editor.Draft.SmartConfiguration);
        Assert.NotEmpty(editor.Draft.ContextWindowTokens);
        Assert.NotEmpty(editor.Draft.MaxOutputTokens);
        Assert.NotNull(editor.Draft.SupportsImage);
        Assert.NotNull(editor.Draft.SupportsVideo);
        Assert.NotNull(editor.Draft.SupportsPdf);
        Assert.NotNull(editor.Draft.SupportsJsonSchemaOutput);
        Assert.NotNull(editor.Draft.SupportsNativeWebSearch);
        Assert.NotNull(editor.Draft.SupportsMidConversationSystem);
        Assert.NotNull(editor.Draft.ReasoningLevels);
        Assert.NotEmpty(editor.Draft.ReasoningMapping);
        Assert.True(editor.TrySave());
        var manual = provider.Models[0].ToConfig();
        Assert.False(manual.SmartConfiguration);
        Assert.NotNull(manual.ContextWindowTokens);
        Assert.NotNull(manual.MaxOutputTokens);
        Assert.NotNull(manual.SupportsPdf);
        editor.RestoreDefaultsCommand.Execute(null);
        var reset = editor.Draft.ToConfig();
        Assert.True(reset.SmartConfiguration);
        Assert.Equal(model.ModelId, reset.ModelId);
        Assert.False(reset.Enabled);
        Assert.Null(reset.ContextWindowTokens);
        Assert.Null(reset.MaxOutputTokens);
        Assert.Null(reset.SupportsImage);
        Assert.Null(reset.ReasoningLevels);
        Assert.Null(reset.ReasoningMapping);
        Assert.Equal(model.DisplayName, reset.DisplayName);
        Assert.Equal(model.SupportsAudio, reset.SupportsAudio);
        Assert.Equal(model.MaxOutputTokensMapping, reset.MaxOutputTokensMapping);
    }

    [Fact]
    public void SmartModeAfterManual_ClearsEditableOverridesAndTracksNewIdentityDefaults()
    {
        using var settings = new SettingsViewModel(Config());
        var provider = settings.Providers[0];
        var editor = provider.CreateModelEditor(provider.Models[0]);
        editor.Draft.SmartConfiguration = false;
        editor.Draft.ContextWindowTokens = "321";
        editor.Draft.SmartConfiguration = true;
        Assert.Empty(editor.Draft.ContextWindowTokens);
        Assert.Null(editor.Draft.SupportsImage);
        Assert.Null(editor.Draft.ReasoningLevels);
        editor.Draft.ModelId = "gpt-5";
        var defaults = WorkbenchModelCatalog.Defaults(provider.ToConfig(), "gpt-5");
        Assert.Equal(defaults.ContextWindowTokens, editor.Draft.EffectiveProperties.ContextWindowTokens);
        Assert.Equal(defaults.ReasoningLevels, editor.ReasoningLevels.Select(level => level.Value));
    }

    [Fact]
    public void ManualEditor_RejectsClearedFrozenTokenLimit()
    {
        using var settings = new SettingsViewModel(Config());
        var editor = settings.Providers[0].CreateModelEditor(settings.Providers[0].Models[0]);
        editor.Draft.SmartConfiguration = false;
        editor.Draft.MaxOutputTokens = string.Empty;
        Assert.False(editor.TrySave());
        Assert.Contains("required in manual mode", editor.Error);
    }

    [Fact]
    public void ModelDrafts_CancelDoesNotMutateAndSaveAddsOrEditsOnlyOnce()
    {
        using var settings = new SettingsViewModel(Config());
        var provider = settings.Providers[0];
        var original = provider.Models[0];
        var canceled = provider.CreateModelEditor(original);
        canceled.Draft.ModelId = "discarded";
        canceled.Draft.ContextWindowTokens = "999";
        Assert.Equal("custom-model", original.ModelId);
        Assert.Empty(original.ContextWindowTokens);
        var add = provider.CreateModelEditor();
        add.Draft.ModelId = "new-model";
        Assert.Single(provider.Models);
        Assert.True(add.TrySave());
        Assert.Equal(2, provider.Models.Count);
        Assert.False(add.TrySave());
        var edit = provider.CreateModelEditor(original);
        edit.Draft.ModelId = "edited-model";
        edit.Draft.MaxOutputTokens = "120000";
        edit.Draft.ContextWindowTokens = "1024";
        Assert.True(edit.TrySave());
        Assert.Equal("edited-model", original.ModelId);
        Assert.Equal("120000", original.MaxOutputTokens);
    }

    [Theory]
    [InlineData("0", "")]
    [InlineData("abc", "")]
    [InlineData("", "-1")]
    [InlineData("", "1.5")]
    public void Editor_RejectsInvalidTokenValues(string context, string output)
    {
        using var settings = new SettingsViewModel(Config());
        var editor = settings.Providers[0].CreateModelEditor();
        editor.Draft.ModelId = "new";
        editor.Draft.ContextWindowTokens = context;
        editor.Draft.MaxOutputTokens = output;
        Assert.False(editor.TrySave());
        Assert.Contains("positive integer", editor.Error);
    }

    [Fact]
    public void Editor_ValidatesCelSyntaxEveryReasoningLevelAndObjectBranches()
    {
        using var settings = new SettingsViewModel(Config());
        var editor = settings.Providers[0].CreateModelEditor();
        editor.Draft.ModelId = "new";
        editor.Draft.ReasoningLevels = ["off", "low", "high"];
        editor.Draft.ReasoningMapping = ReasoningMap;
        Assert.True(editor.TrySave());
        foreach (var invalid in new[]
        {
            "reasoningLevel == 'off' ? {} : 2",
            "{'reasoning_effort': unknown}",
            "reasoningLevel == 'off' ? {} : {'budget': 1 / 0}",
            "{'messages': []}",
            "{'value':}",
        })
        {
            var draft = settings.Providers[0].CreateModelEditor(settings.Providers[0].Models[^1]);
            draft.Draft.ReasoningMapping = invalid;
            Assert.False(draft.TrySave());
            Assert.Contains("Reasoning mapping", draft.Error);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("same")]
    public void Editor_RejectsEmptyOrDuplicateReasoningLevels(string second)
    {
        using var settings = new SettingsViewModel(Config());
        var editor = settings.Providers[0].CreateModelEditor();
        editor.Draft.ModelId = "new";
        editor.Draft.ReasoningLevels = ["same", second];
        Assert.False(editor.TrySave());
        Assert.Contains("unique, nonempty", editor.Error);
    }

    [Fact]
    public void ReasoningChips_AddEditReorderAndRemovePreserveOrder()
    {
        using var settings = new SettingsViewModel(Config(new WorkbenchModelConfig("custom-model", ReasoningLevels: ["off", "high"], ReasoningMapping: ReasoningMap)));
        var editor = settings.Providers[0].CreateModelEditor(settings.Providers[0].Models[0]);
        editor.NewReasoningLevel = "low";
        editor.AddReasoningLevelCommand.Execute(null);
        var added = editor.ReasoningLevels[^1];
        added.MoveUpCommand.Execute(null);
        Assert.Equal(["off", "low", "high"], editor.Draft.ReasoningLevels);
        added.Value = "medium";
        added.MoveDownCommand.Execute(null);
        Assert.Equal(["off", "high", "medium"], editor.Draft.ReasoningLevels);
        added.RemoveCommand.Execute(null);
        Assert.Equal(["off", "high"], editor.Draft.ReasoningLevels);
    }

    [Fact]
    public async Task Settings_RejectDuplicateProviderAndModelIdsEvenDisabled()
    {
        var path = TempPath();
        try
        {
            using var settings = new SettingsViewModel(Config());
            settings.AddProviderCommand.Execute(null);
            var added = settings.Providers[1];
            added.Kind = "openai-responses";
            added.Id = settings.Providers[0].Id;
            Assert.Null(await settings.SaveAsync(path));
            Assert.Contains("duplicate provider ID", settings.Error);
            added.Id = "other";
            var provider = settings.Providers[0];
            provider.AddModelCommand.Execute(null);
            provider.Models[1].ModelId = provider.Models[0].ModelId;
            provider.Models[1].Enabled = false;
            Assert.Null(await settings.SaveAsync(path));
            Assert.Contains("duplicate model ID", settings.Error);
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("https://example.invalid/keys", true)]
    [InlineData("http://example.invalid/keys", false)]
    [InlineData("https://user:secret@example.invalid/keys", false)]
    [InlineData("file:///C:/secret", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/keys", false)]
    public void ApiKeyLink_OnlyAllowsSafeHttps(string url, bool allowed)
    {
        using var settings = new SettingsViewModel(Config());
        settings.Providers[0].ApiKeyManagementUrl = url;
        Assert.Equal(allowed, settings.Providers[0].HasApiKeyManagementUrl);
    }

    [AvaloniaFact]
    public void ProviderDetails_UseProtocolDropdownEyeAndCompactRows()
    {
        var dialog = new SettingsDialog(Config());
        try
        {
            dialog.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var provider = Assert.IsType<SettingsViewModel>(dialog.DataContext).Providers[0];
            var controls = dialog.GetVisualDescendants().ToArray();
            var apiFormat = controls.OfType<ComboBox>().Single(box => box.ItemsSource == provider.ApiFormats);
            Assert.Equal(3, apiFormat.Items.Count);
            Assert.Contains(SettingsViewModel.ApiFormats, choice => choice.Label == "OpenAI Responses (/responses)");
            apiFormat.SelectedItem = SettingsViewModel.ApiFormats[2];
            Assert.Equal("openai-responses", provider.Kind);
            var eye = controls.OfType<Button>().Single(button => button.Command == provider.ToggleApiKeyVisibilityCommand);
            Assert.NotEmpty(eye.GetVisualDescendants().OfType<Icon>());
            var point = eye.TranslatePoint(new Point(eye.Bounds.Width / 2, eye.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(point, MouseButton.Left);
            dialog.MouseUp(point, MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(provider.IsApiKeyVisible);
            Assert.Equal('\0', provider.ApiKeyPasswordChar);
            Assert.DoesNotContain(controls.OfType<CheckBox>(), box => Equals(box.Content, "Show"));
            Assert.DoesNotContain(controls.OfType<TextBox>(), box => box.Watermark == "Model ID");
            Assert.Contains(controls.OfType<TextBlock>(), block => block.Text == "custom-model");
            var modelToggle = controls.OfType<ToggleSwitch>().Single(toggle => toggle.DataContext == provider.Models[0]);
            Assert.Equal(32, modelToggle.Width);
            Assert.Equal(string.Empty, modelToggle.OnContent);
            Assert.Equal(string.Empty, modelToggle.OffContent);
            var togglePoint = modelToggle.TranslatePoint(new Point(modelToggle.Bounds.Width / 2, modelToggle.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(togglePoint, MouseButton.Left);
            dialog.MouseUp(togglePoint, MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(provider.Models[0].Enabled);
            modelToggle.Focus();
            dialog.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            dialog.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(provider.Models[0].Enabled);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task AddModelButton_OpensDraftDialogCancelAndSaveAreTransactional()
    {
        var settingsDialog = new SettingsDialog(Config());
        settingsDialog.Show();
        try
        {
            var provider = Assert.IsType<SettingsViewModel>(settingsDialog.DataContext).Providers[0];
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var addButton = settingsDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Add model"));
            addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var editor = Assert.IsType<ModelConfigurationDialog>(Assert.Single(settingsDialog.OwnedWindows));
            editor.FindControl<TextBox>("ModelIdBox")!.Text = "canceled-model";
            Assert.Single(provider.Models);
            editor.FindControl<Button>("CancelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(provider.Models);
            Assert.Empty(settingsDialog.OwnedWindows);
            var task = ModelConfigurationDialog.ShowAsync(settingsDialog, provider);
            editor = Assert.IsType<ModelConfigurationDialog>(Assert.Single(settingsDialog.OwnedWindows));
            editor.FindControl<TextBox>("ModelIdBox")!.Text = "saved-model";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            editor.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await task);
            Assert.Equal(2, provider.Models.Count);
            Assert.Equal("saved-model", provider.Models[1].ModelId);
            task = ModelConfigurationDialog.ShowAsync(settingsDialog, provider, provider.Models[1]);
            editor = Assert.IsType<ModelConfigurationDialog>(Assert.Single(settingsDialog.OwnedWindows));
            editor.FindControl<TextBox>("ModelIdBox")!.Text = "edited-model";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            editor.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await task);
            Assert.Equal("edited-model", provider.Models[1].ModelId);
        }
        finally { settingsDialog.Close(); }
    }

    [AvaloniaFact]
    public void ModelDialog_CheckedChipsAndExpandedHeaderUseNeutralThemeSurfaces()
    {
        using var settings = new SettingsViewModel(Config());
        var dialog = new ModelConfigurationDialog(settings.Providers[0].CreateModelEditor(settings.Providers[0].Models[0]));
        try
        {
            dialog.Show();
            var advanced = dialog.FindControl<Expander>("AdvancedExpander")!;
            advanced.IsExpanded = true;
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                dialog.RequestedThemeVariant = variant;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var disclosure = advanced.GetVisualDescendants().OfType<ToggleButton>()
                    .Single(button => button.Classes.Contains("disclosure"));
                Assert.True(disclosure.IsChecked);
                var disclosureSurface = disclosure.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "DisclosureSurface");
                Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(disclosureSurface.Background).Color.A);
                Assert.True(dialog.TryFindResource("AlcolerSelected", variant, out var selected));
                var expected = Assert.IsAssignableFrom<ISolidColorBrush>(selected).Color;
                foreach (var name in new[] { "ImageToggle", "VideoToggle", "PdfToggle", "StructuredOutputToggle", "NativeWebSearchToggle", "MidConversationSystemToggle" })
                {
                    var chip = dialog.FindControl<ToggleButton>(name)!;
                    chip.IsChecked = true;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    var surface = chip.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "ChipSurface");
                    Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color);
                }
            }
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void ModelDialog_IsScrollableAdvancedCollapsedAndHasEveryCapabilityControl()
    {
        using var settings = new SettingsViewModel(Config());
        var editor = settings.Providers[0].CreateModelEditor(settings.Providers[0].Models[0]);
        var dialog = new ModelConfigurationDialog(editor);
        try
        {
            dialog.Show();
            Assert.Equal("Edit model configuration", dialog.Title);
            Assert.NotNull(dialog.FindControl<ScrollViewer>("EditorScroll"));
            Assert.False(dialog.FindControl<Expander>("AdvancedExpander")!.IsExpanded);
            foreach (var name in new[] { "ImageToggle", "VideoToggle", "PdfToggle", "StructuredOutputToggle", "NativeWebSearchToggle", "MidConversationSystemToggle" })
                Assert.NotNull(dialog.FindControl<ToggleButton>(name));
            Assert.NotNull(dialog.FindControl<TextBox>("MaxOutputTokensBox"));
            Assert.NotNull(dialog.FindControl<TextBox>("ReasoningMappingBox"));
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("cache", StringComparison.OrdinalIgnoreCase) == true);
        }
        finally { dialog.Close(); }
    }
}
