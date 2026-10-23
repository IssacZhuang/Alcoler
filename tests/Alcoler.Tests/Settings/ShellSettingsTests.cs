using System.Text.Json;
using Alcoler.Contracts.Tools;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Alcoler.Tests.Settings;

public sealed class ShellSettingsTests
{
    private static readonly ShellEnvironment Cmd = new(@"C:\Windows\System32\cmd.exe", ShellDialect.Cmd);
    private static readonly ShellEnvironment GitBash = new(@"C:\Program Files\Git\bin\bash.exe", ShellDialect.GitBash);

    [Theory]
    [InlineData(null)]
    [InlineData(ShellDialect.Cmd)]
    [InlineData(ShellDialect.GitBash)]
    public async Task SelectedShell_RoundTripsWithoutChangingOtherSettings(ShellDialect? dialect)
    {
        var path = Path.Combine(Path.GetTempPath(), $"alcoler-shell-settings-{Guid.NewGuid():N}.json");
        try
        {
            var config = new WorkbenchConfig([], Theme: "light", BraveApiKey: "preserved");
            using var settings = new SettingsViewModel(config, [Cmd, GitBash], isWindows: true);
            settings.SelectedShell = settings.ShellChoices.Single(choice => choice.Environment?.Dialect == dialect);

            var saved = await settings.SaveAsync(path);
            var loaded = WorkbenchConfig.LoadOrDefault(path);

            Assert.NotNull(saved);
            Assert.Equal(settings.SelectedShell.Environment, loaded.IntegratedTerminalShell);
            Assert.Equal("light", loaded.Theme);
            Assert.Equal("preserved", loaded.BraveApiKey);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (dialect is not null)
            {
                var shell = json.RootElement.GetProperty("integratedTerminalShell");
                Assert.Equal(JsonValueKind.String, shell.GetProperty("dialect").ValueKind);
                Assert.False(shell.TryGetProperty("name", out _));
                Assert.False(shell.TryGetProperty("arguments", out _));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyConfig_DefaultsToAuto_AndInstalledShellsAreListed()
    {
        using var settings = new SettingsViewModel(new WorkbenchConfig([]), [Cmd, GitBash], isWindows: true);

        Assert.True(settings.SupportsShellSelection);
        Assert.Null(settings.SelectedShell!.Environment);
        Assert.Equal(["Auto (Git Bash, then CMD)", "CMD", "Git Bash"], settings.ShellChoices.Select(choice => choice.Label));
    }

    [Fact]
    public async Task MissingSavedShell_IsPreservedUntilUserSelectsAuto()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alcoler-shell-settings-{Guid.NewGuid():N}.json");
        try
        {
            using var settings = new SettingsViewModel(new WorkbenchConfig([], IntegratedTerminalShell: GitBash),
                [Cmd], isWindows: true);

            Assert.Equal(GitBash, settings.SelectedShell!.Environment);
            Assert.Contains("unavailable", settings.SelectedShell.Label);
            Assert.Equal(GitBash, (await settings.SaveAsync(path))!.IntegratedTerminalShell);
            settings.SelectedShell = settings.ShellChoices[0];
            Assert.Null((await settings.SaveAsync(path))!.IntegratedTerminalShell);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NonWindows_HidesPickerAndPreservesWindowsPreference()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alcoler-shell-settings-{Guid.NewGuid():N}.json");
        try
        {
            using var settings = new SettingsViewModel(new WorkbenchConfig([], IntegratedTerminalShell: Cmd),
                [], isWindows: false);
            Assert.False(settings.SupportsShellSelection);
            settings.SelectedShell = settings.ShellChoices[0];
            Assert.Equal(Cmd, (await settings.SaveAsync(path))!.IntegratedTerminalShell);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AppliedSettingsBanner_IgnoresSkillAndThemeChanges()
    {
        var config = new WorkbenchConfig([]);

        Assert.Null(SidebarView.AppliedSettingsBanner(config, config with { DisabledSkillPaths = ["C:/skills/demo/SKILL.md"] }));
        Assert.Null(SidebarView.AppliedSettingsBanner(config, config with { Theme = "light" }));
        Assert.Contains("Web search", SidebarView.AppliedSettingsBanner(config, config with { BraveApiKey = "key" }));
        Assert.Contains("MCP", SidebarView.AppliedSettingsBanner(config, config with
        {
            McpServers = [new WorkbenchMcpServerConfig("server", "command")],
        }));
        Assert.Contains("Shell", SidebarView.AppliedSettingsBanner(config, config with { IntegratedTerminalShell = Cmd }));
    }

    [Fact]
    public void AppliedSettingsBanner_LanguageChangeRequiresRestart()
    {
        var config = new WorkbenchConfig([], Language: "en");

        Assert.Null(SidebarView.AppliedSettingsBanner(config, config with { Language = "en" }));
        var banner = SidebarView.AppliedSettingsBanner(config, config with { Language = "zh" });
        Assert.NotNull(banner);
        Assert.Contains("Restart", banner);
    }

    [Fact]
    public void ShellAndMcpOnlyChanges_DoNotCountAsProviderChanges()
    {
        var config = new WorkbenchConfig([]);
        Assert.False(SidebarView.ProvidersChanged(config, config with { IntegratedTerminalShell = Cmd }));
        Assert.False(SidebarView.ProvidersChanged(config, config with { Theme = "light", IntegratedTerminalShell = GitBash }));
        Assert.False(SidebarView.ProvidersChanged(config, config with
        {
            McpServers = [new WorkbenchMcpServerConfig("server", "command")],
        }));
        Assert.True(SidebarView.McpServersChanged(config, config with
        {
            McpServers = [new WorkbenchMcpServerConfig("server", "command")],
        }));
        Assert.True(SidebarView.ProvidersChanged(config, config with
        {
            Providers = [new WorkbenchProviderConfig("openai-compatible", "https://example.test/v1", "key",
                [new WorkbenchModelConfig("model")])],
        }));
    }

    [AvaloniaFact]
    public void SettingsDialog_RuntimeShellPickerUpdatesSelectionAndPreservesDraft()
    {
        var dialog = new SettingsDialog(new WorkbenchConfig([]));
        try
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            var settings = Assert.IsType<SettingsViewModel>(dialog.DataContext);
            var tab = dialog.FindControl<RadioButton>("RuntimeTab")!;
            Assert.Equal(OperatingSystem.IsWindows(), tab.IsVisible);
            if (!settings.SupportsShellSelection)
                return;

            tab.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var panel = dialog.FindControl<ScrollViewer>("RuntimePanel")!;
            var picker = dialog.FindControl<ComboBox>("ShellPicker")!;
            Assert.True(panel.IsVisible);
            Assert.Equal("Runtime", dialog.FindControl<TextBlock>("SectionTitle")!.Text);
            Assert.True(picker.Bounds.Width > 0);
            var selected = settings.ShellChoices.First(choice => choice.Environment?.Dialect == ShellDialect.Cmd);
            picker.SelectedItem = selected;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(selected, settings.SelectedShell);

            dialog.FindControl<RadioButton>("AppearanceTab")!.IsChecked = true;
            Assert.False(panel.IsVisible);
            tab.IsChecked = true;
            Assert.Same(selected, picker.SelectedItem);
        }
        finally
        {
            dialog.Close();
        }
    }
}
