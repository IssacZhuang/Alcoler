using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Alcoler.Controls;
using Alcoler.Localization;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Alcoler.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit.Abstractions;
using Assert = Xunit.Assert;

namespace Alcoler.Tests.Views;

public sealed class RemoteControlDialogTests(ITestOutputHelper output)
{
    private sealed class Harness : IAsyncDisposable
    {
        public Harness()
        {
            TempRoot = Path.Combine(Path.GetTempPath(), "alcoler-remote-dialog-" + Guid.NewGuid().ToString("N"));
            ConfigPath = Path.Combine(TempRoot, "config.json");
            Config = new WorkbenchConfig([], BraveApiKey: "preserve-unrelated-setting",
                Remote: new WorkbenchRemoteConfig(false, AllocatePort(), "Original-code_123"));
            Config.Save(ConfigPath);
            Remote = new RemoteAccessViewModel(
                static () => throw new InvalidOperationException("Pairing must not require the agent runtime."),
                Path.Combine(TempRoot, "remote"),
                static (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null),
                "headless-tests");
            Dialog = new RemoteControlDialog(Config, Remote, ConfigPath);
            Dialog.Closed += (_, _) => IsClosed = true;
        }

        public string TempRoot { get; }
        public string ConfigPath { get; }
        public WorkbenchConfig Config { get; }
        public RemoteAccessViewModel Remote { get; }
        public RemoteControlDialog Dialog { get; }
        public RemoteSettingsViewModel ViewModel => Assert.IsType<RemoteSettingsViewModel>(Dialog.DataContext);
        public bool IsClosed { get; private set; }
        public ToggleSwitch Toggle => Find<ToggleSwitch>(Dialog, "RemoteEnabledToggle");
        public TextBox Port => Find<TextBox>(Dialog, "RemotePortBox");
        public TextBox Code => Find<TextBox>(Dialog, "ConnectionCodeBox");
        public Button Dice => Find<Button>(Dialog, "GenerateConnectionCodeButton");
        public Button Close => Find<Button>(Dialog, "CloseButton");

        public async Task ShowAsync()
        {
            Dialog.Show();
            await PrepareAsync(Dialog);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Dialog.IsVisible)
                {
                    await WaitUntilAsync(() => !ViewModel.IsApplying, () => "Dialog was still applying during cleanup.");
                    Dialog.Close();
                    await WaitUntilAsync(() => IsClosed, () => $"Dialog did not close during cleanup: {ViewModel.Error}");
                }
            }
            finally
            {
                await Remote.DisposeAsync();
                Directory.Delete(TempRoot, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task SettingsDialog_DoesNotContainRemoteSettings()
    {
        var dialog = new SettingsDialog(new WorkbenchConfig([], Remote: new WorkbenchRemoteConfig(false, 8757, "Original-code_123")));
        dialog.Show();
        try
        {
            await PrepareAsync(dialog);
            var controls = dialog.GetVisualDescendants().OfType<Control>().ToArray();
            Assert.Contains(controls, control => control.Name == "ProvidersTab");
            Assert.DoesNotContain(controls, control => control.Name is
                "RemoteTab" or "RemoteSection" or "RemoteEnabledToggle" or "RemotePortBox" or "ConnectionCodeBox");
            Assert.DoesNotContain(controls.OfType<RadioButton>(), tab => Equals(tab.Tag, "remote"));
            Assert.DoesNotContain(controls.OfType<TextBlock>(), text => text.Text == Loc.Get("settings_remote"));
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task Dialog_HasOnlyTopRightCloseNavigation_AndPlainPortTextBox()
    {
        await using var harness = new Harness();
        await harness.ShowAsync();

        Assert.Equal(SystemDecorations.None, harness.Dialog.SystemDecorations);
        Assert.IsType<TextBox>(harness.Port);
        Assert.Empty(harness.Dialog.GetVisualDescendants().OfType<NumericUpDown>());
        Assert.Equal(5, harness.Port.MaxLength);
        Assert.Equal(64, harness.Code.MaxLength);
        var buttons = harness.Dialog.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.DoesNotContain(buttons, button => button.Name is "SaveButton" or "CancelButton" or "BackButton");
        Assert.DoesNotContain(buttons, button => button.IsDefault || button.IsCancel);
        Assert.DoesNotContain(buttons, button => Equals(button.Content, Loc.Get("settings_save_changes"))
            || Equals(button.Content, Loc.Get("common_cancel")) || Equals(button.Content, Loc.Get("common_back")));
        Assert.Same(harness.Close, Assert.Single(buttons, button => Equals(ToolTip.GetTip(button), Loc.Get("settings_close"))));
        var closeOrigin = harness.Close.TranslatePoint(default, harness.Dialog)!.Value;
        Assert.InRange(closeOrigin.X, harness.Dialog.Bounds.Width - 80, harness.Dialog.Bounds.Width);
        Assert.InRange(closeOrigin.Y, 0, 48);
        Assert.True(harness.Close.IsEnabled);
        Assert.True(harness.Toggle.IsEnabled);
        AssertEditable(harness, true);

        await SaveScreenshotAsync(harness.Dialog, "disabled");
    }

    [AvaloniaFact]
    public async Task Toggle_StartsStopsAndRestartsLivePairing_AndClosingKeepsServiceRunning()
    {
        await using var harness = new Harness();
        using var client = CreateClient();
        await harness.ShowAsync();
        var originalPort = harness.Config.Remote!.EffectivePort;
        var originalCode = harness.Code.Text!;

        await ClickAsync(harness.Dialog, harness.Toggle);
        await WaitForAppliedStateAsync(harness, true);

        AssertEditable(harness, false);
        Assert.True(harness.Toggle.IsEnabled);
        Assert.Equal(originalPort, harness.Remote.Port);
        Assert.Equal(originalCode, harness.Remote.PairingToken);
        Assert.Null(harness.Remote.PairingTokenExpiresAt);
        AssertPersisted(harness, true, originalPort, originalCode);
        var track = Find<Border>(harness.Toggle, "SwitchTrack");
        AssertThemeBrush(harness.Dialog, "AlcolerSuccess", track.Background);
        await PairAsync(client, originalPort, originalCode);
        await SaveScreenshotAsync(harness.Dialog, "enabled");
        var replacementPort = AllocatePort();

        await ClickAsync(harness.Dialog, harness.Toggle);
        await WaitForAppliedStateAsync(harness, false);

        AssertEditable(harness, true);
        AssertPersisted(harness, false, originalPort, originalCode);
        await AssertListenerStoppedAsync(client, originalPort);
        const string replacementCode = "Replacement-code_456";
        harness.Port.SetCurrentValue(TextBox.TextProperty, replacementPort.ToString(CultureInfo.InvariantCulture));
        harness.Code.SetCurrentValue(TextBox.TextProperty, replacementCode);
        await PumpAsync();
        Assert.Equal(replacementPort.ToString(CultureInfo.InvariantCulture), harness.ViewModel.Port);
        Assert.Equal(replacementCode, harness.ViewModel.ConnectionCode);

        await ClickAsync(harness.Dialog, harness.Toggle);
        await WaitForAppliedStateAsync(harness, true);

        AssertEditable(harness, false);
        Assert.Equal(replacementPort, harness.Remote.Port);
        Assert.Equal(replacementCode, harness.Remote.PairingToken);
        AssertPersisted(harness, true, replacementPort, replacementCode);
        await AssertListenerStoppedAsync(client, originalPort);
        using (var obsoleteCode = await PostPairAsync(client, replacementPort, originalCode))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, obsoleteCode.StatusCode);
        }
        await PairAsync(client, replacementPort, replacementCode);

        await ClickAsync(harness.Dialog, harness.Close);
        await WaitUntilAsync(() => harness.IsClosed, () => $"Enabled dialog did not close: {harness.ViewModel.Error}");

        Assert.True(harness.Remote.IsRunning);
        AssertPersisted(harness, true, replacementPort, replacementCode);
        await PairAsync(client, replacementPort, replacementCode);
    }

    [AvaloniaFact]
    public async Task Close_WithFocusedDisabledDrafts_PersistsWithoutSave()
    {
        await using var harness = new Harness();
        await harness.ShowAsync();
        var replacementPort = AllocatePort();
        const string replacementCode = "Draft-code_789";
        Assert.True(harness.Code.Focus());
        harness.Port.SetCurrentValue(TextBox.TextProperty, replacementPort.ToString(CultureInfo.InvariantCulture));
        harness.Code.SetCurrentValue(TextBox.TextProperty, replacementCode);
        await PumpAsync();
        AssertPersisted(harness, false, harness.Config.Remote!.EffectivePort, harness.Config.Remote.ConnectionCode!);

        await ClickAsync(harness.Dialog, harness.Close);
        await WaitUntilAsync(() => harness.IsClosed,
            () => $"One close click must persist and close the disabled dialog: {harness.ViewModel.Error}");

        Assert.False(harness.Remote.IsRunning);
        AssertPersisted(harness, false, replacementPort, replacementCode);
        Assert.Equal(harness.Config.BraveApiKey, WorkbenchConfig.LoadOrDefault(harness.ConfigPath).BraveApiKey);
        var reopened = new RemoteControlDialog(WorkbenchConfig.LoadOrDefault(harness.ConfigPath), harness.Remote, harness.ConfigPath);
        var closed = false;
        reopened.Closed += (_, _) => closed = true;
        reopened.Show();
        try
        {
            await PrepareAsync(reopened);
            Assert.False(Find<ToggleSwitch>(reopened, "RemoteEnabledToggle").IsChecked);
            Assert.Equal(replacementPort.ToString(CultureInfo.InvariantCulture), Find<TextBox>(reopened, "RemotePortBox").Text);
            Assert.Equal(replacementCode, Find<TextBox>(reopened, "ConnectionCodeBox").Text);
        }
        finally
        {
            reopened.Close();
            await WaitUntilAsync(() => closed, () => "Reopened dialog did not close.");
        }
    }

    [AvaloniaFact]
    public async Task Dice_ClickReplacesAndPersistsCodeWithoutEnablingService()
    {
        await using var harness = new Harness();
        await harness.ShowAsync();
        var originalCode = harness.Code.Text;

        await ClickAsync(harness.Dialog, harness.Dice);
        await WaitUntilAsync(() => !harness.ViewModel.IsApplying && harness.Code.Text != originalCode,
            () => $"Dice click did not generate a connection code: {harness.ViewModel.Error}");

        Assert.Matches("^[A-Za-z0-9_-]{8,64}$", harness.Code.Text!);
        AssertPersisted(harness, false, harness.Config.Remote!.EffectivePort, harness.Code.Text!);
        Assert.False(harness.Remote.IsRunning);
        AssertEditable(harness, true);
    }

    [AvaloniaFact]
    public async Task Sidebar_RemoteButtonIsBesidePreferencesWithoutShortcut_AndGlyphTracksRunningService()
    {
        await using var harness = new Harness();
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance,
            harness.Config, remoteAccess: harness.Remote);
        var window = new MainWindow(workbench);
        window.Show();
        try
        {
            await PrepareAsync(window);
            var sidebar = Assert.Single(window.GetVisualDescendants().OfType<SidebarView>());
            var settings = Find<Button>(sidebar, "PART_Settings");
            var remote = Find<Button>(sidebar, "PART_RemoteControl");
            var glyph = Assert.Single(remote.GetVisualDescendants().OfType<Icon>());
            Assert.Same(settings.Parent, remote.Parent);
            Assert.Equal(0, Grid.GetColumn(settings));
            Assert.Equal(1, Grid.GetColumn(remote));
            Assert.True(remote.IsEnabled);
            Assert.Equal(Loc.Get("settings_remote"), ToolTip.GetTip(remote));
            var settingsOrigin = settings.TranslatePoint(default, window)!.Value;
            var remoteOrigin = remote.TranslatePoint(default, window)!.Value;
            Assert.True(remoteOrigin.X >= settingsOrigin.X + settings.Bounds.Width);
            Assert.Equal(settingsOrigin.Y + settings.Bounds.Height / 2, remoteOrigin.Y + remote.Bounds.Height / 2, precision: 1);
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("Ctrl+,", StringComparison.OrdinalIgnoreCase) == true);
            Assert.DoesNotContain("Ctrl+,", ToolTip.GetTip(settings)?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            settings.Focus();
            window.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.Comma, RawInputModifiers.Control);
            await PumpAsync();
            Assert.Empty(window.OwnedWindows);
            Assert.DoesNotContain("running", remote.Classes);
            var idleColor = Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Stroke).Color;

            await harness.Remote.ApplyConfigAsync(harness.Config.Remote! with { Enabled = true });
            Assert.True(harness.Remote.IsRunning, harness.Remote.Error);
            await PrepareAsync(window);
            Assert.Contains("running", remote.Classes);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                await PumpAsync();
                AssertThemeBrush(window, "AlcolerSuccess", glyph.Stroke);
                window.MouseMove(remote.TranslatePoint(new Point(remote.Bounds.Width / 2, remote.Bounds.Height / 2), window)!.Value);
                await PumpAsync();
                AssertThemeBrush(window, "AlcolerSuccess", glyph.Stroke);
            }

            await harness.Remote.ApplyConfigAsync(harness.Config.Remote! with { Enabled = false });
            await PumpAsync();
            Assert.False(harness.Remote.IsRunning);
            Assert.DoesNotContain("running", remote.Classes);
            window.MouseMove(new Point(400, 100));
            await PumpAsync();
            Assert.Equal(idleColor, Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Stroke).Color);
        }
        finally
        {
            window.Close();
        }
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static async Task PumpAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    private static async Task PrepareAsync(Window window)
    {
        await PumpAsync();
        window.UpdateLayout();
        var rendered = ElementComposition.GetElementVisual(window)!.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await rendered.WaitAsync(TimeSpan.FromSeconds(5));
        await PumpAsync();
    }

    private static async Task ClickAsync(Window window, Control control)
    {
        await PrepareAsync(window);
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.IsEffectivelyEnabled);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        await PumpAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> failure)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            await PumpAsync();
            if (condition())
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.True(condition(), failure());
    }

    private static Task WaitForAppliedStateAsync(Harness harness, bool enabled) => WaitUntilAsync(
        () => !harness.ViewModel.IsApplying && harness.ViewModel.Enabled == enabled
            && harness.Remote.IsRunning == enabled && harness.Toggle.IsChecked == enabled,
        () => $"Toggle did not apply enabled={enabled}; checked={harness.Toggle.IsChecked}, enabled={harness.ViewModel.Enabled}, "
            + $"running={harness.Remote.IsRunning}, applying={harness.ViewModel.IsApplying}, error={harness.ViewModel.Error ?? harness.Remote.Error}");

    private static void AssertEditable(Harness harness, bool expected)
    {
        Assert.Equal(expected, harness.ViewModel.CanEdit);
        Assert.Equal(expected, harness.Port.IsEffectivelyEnabled);
        Assert.Equal(expected, harness.Code.IsEffectivelyEnabled);
        Assert.Equal(expected, harness.Dice.IsEffectivelyEnabled);
        Assert.Equal(expected, harness.Dice.Command!.CanExecute(null));
    }

    private static void AssertPersisted(Harness harness, bool enabled, int port, string code)
    {
        var remote = Assert.IsType<WorkbenchRemoteConfig>(WorkbenchConfig.LoadOrDefault(harness.ConfigPath).Remote);
        Assert.Equal(enabled, remote.Enabled);
        Assert.Equal(port, remote.Port);
        Assert.Equal(code, remote.ConnectionCode);
    }

    private static void AssertThemeBrush(Window window, string key, IBrush? actual)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var expected));
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(expected).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
    }

    private static int AllocatePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static Task<HttpResponseMessage> PostPairAsync(HttpClient client, int port, string code) =>
        client.PostAsJsonAsync($"http://127.0.0.1:{port}/api/pair", new { pairingToken = code, deviceName = "headless-phone" });

    private static async Task PairAsync(HttpClient client, int port, string code)
    {
        using var response = await PostPairAsync(client, port, code);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Pairing on port {port} returned {response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("deviceId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("deviceToken").GetString()));
    }

    private static async Task AssertListenerStoppedAsync(HttpClient client, int port) =>
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/api/info"));

    private async Task SaveScreenshotAsync(Window window, string state)
    {
        await PrepareAsync(window);
        var directory = Path.Combine(Path.GetTempPath(), "alcoler-remote-control-renders");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"remote-control-{state}-{Guid.NewGuid():N}.png");
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Remote dialog did not render a headless frame.");
        frame.Save(path);
        Assert.True(new FileInfo(path).Length > 0);
        output.WriteLine("Remote control screenshot: " + path);
    }
}
