using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Kernel.Skills;
using Alcoler.Localization;
using Alcoler.Remote;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests.Settings;

public sealed class RemoteSettingsTests
{
    private const string FirstCode = "First_code-123";
    private const string SecondCode = "Second-code_456";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Constructor_UsesSavedStateWithoutStartingOrSaving(bool enabled)
    {
        await using var context = new RemoteTestContext();
        var config = SampleConfig(new WorkbenchRemoteConfig(enabled, 0, FirstCode));

        var settings = new RemoteSettingsViewModel(config, context.RemoteAccess, context.ConfigPath);

        Assert.Same(context.RemoteAccess, settings.RemoteAccess);
        Assert.Equal(enabled, settings.Enabled);
        Assert.Equal(WorkbenchRemoteConfig.DefaultPort.ToString(CultureInfo.InvariantCulture), settings.Port);
        Assert.Equal(FirstCode, settings.ConnectionCode);
        Assert.Equal(!enabled, settings.CanEdit);
        Assert.Equal(!enabled, settings.GenerateConnectionCodeCommand.CanExecute(null));
        Assert.False(settings.IsApplying);
        Assert.False(settings.HasError);
        Assert.False(context.RemoteAccess.IsRunning);
        Assert.False(File.Exists(context.ConfigPath));
    }

    [Fact]
    public async Task Constructor_MissingRemoteSectionGeneratesValidEditableCode()
    {
        await using var context = new RemoteTestContext();
        var settings = new RemoteSettingsViewModel(SampleConfig(), context.RemoteAccess, context.ConfigPath);

        Assert.False(settings.Enabled);
        Assert.True(settings.CanEdit);
        Assert.True(RemoteConnectionCode.IsValid(settings.ConnectionCode));
        Assert.False(File.Exists(context.ConfigPath));
        Assert.False(context.RemoteAccess.IsRunning);
    }

    [Fact]
    public async Task SaveEdits_RoundTripsTrimmedDisabledDraftAndPreservesConstructorSnapshot()
    {
        await using var context = new RemoteTestContext();
        var config = SampleConfig();
        var settings = new RemoteSettingsViewModel(config, context.RemoteAccess, context.ConfigPath)
        {
            Port = "23456",
            ConnectionCode = "  " + FirstCode + "  ",
        };

        Assert.True(await settings.SaveEditsAsync());

        var saved = WorkbenchConfig.LoadOrDefault(context.ConfigPath);
        Assert.Equal(new WorkbenchRemoteConfig(false, 23456, FirstCode), saved.Remote);
        AssertUnrelatedSettingsEqual(config, saved);
        Assert.Equal(FirstCode, settings.ConnectionCode);
        Assert.False(settings.HasError);
        Assert.False(settings.IsApplying);
        Assert.False(context.RemoteAccess.IsRunning);
    }

    [Fact]
    public async Task SaveEdits_ReloadsLatestConfigForEverySave()
    {
        await using var context = new RemoteTestContext();
        var original = SampleConfig(new WorkbenchRemoteConfig(false, 23456, FirstCode));
        original.Save(context.ConfigPath);
        var settings = new RemoteSettingsViewModel(original, context.RemoteAccess, context.ConfigPath);
        var latest = original with
        {
            Theme = "dark",
            Language = "zh",
            BraveApiKey = "new-search-key",
            Providers = [new WorkbenchProviderConfig("openai-compatible", "https://latest.example/v1", "new-key", [])],
            Composer = new WorkbenchComposerConfig(PermissionMode.Yolo),
            Browser = new WorkbenchBrowserConfig(true, "C:/latest-browser", false),
        };
        latest.Save(context.ConfigPath);
        settings.Port = "34567";
        settings.ConnectionCode = SecondCode;

        Assert.True(await settings.SaveEditsAsync());
        var firstSave = WorkbenchConfig.LoadOrDefault(context.ConfigPath);
        AssertUnrelatedSettingsEqual(latest, firstSave);
        Assert.Equal(new WorkbenchRemoteConfig(false, 34567, SecondCode), firstSave.Remote);

        latest = firstSave with { Theme = "light", AutoArchiveOlderThanDays = 30, DisabledSkillPaths = ["new-skill"] };
        latest.Save(context.ConfigPath);
        settings.Port = "45678";

        Assert.True(await settings.SaveEditsAsync());
        var secondSave = WorkbenchConfig.LoadOrDefault(context.ConfigPath);
        AssertUnrelatedSettingsEqual(latest, secondSave);
        Assert.Equal(new WorkbenchRemoteConfig(false, 45678, SecondCode), secondSave.Remote);
        Assert.False(context.RemoteAccess.IsRunning);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public async Task SaveEdits_AcceptsExplicitPortBoundariesWithoutStarting(int port)
    {
        await using var context = new RemoteTestContext();
        var settings = context.CreateSettings();
        settings.Port = port.ToString(CultureInfo.InvariantCulture);

        Assert.True(await settings.SaveEditsAsync());

        Assert.Equal(port, WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote!.Port);
        Assert.False(context.RemoteAccess.IsRunning);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("2147483648")]
    [InlineData("1.5")]
    [InlineData("8e3")]
    [InlineData("abc")]
    public async Task InvalidPort_RejectsSaveAndEnableWithoutStartingOrCoercing(string port)
    {
        await using var context = new RemoteTestContext();
        var settings = context.CreateSettings();
        settings.Port = port;

        Assert.False(await settings.SaveEditsAsync());
        Assert.Equal(Loc.Get("settings_remote_invalid_port"), settings.Error);
        await settings.SetEnabledAsync(true);

        Assert.Equal(port, settings.Port);
        Assert.Equal(Loc.Get("settings_remote_invalid_port"), settings.Error);
        Assert.True(settings.HasError);
        Assert.False(settings.Enabled);
        Assert.False(settings.IsApplying);
        Assert.True(settings.CanEdit);
        Assert.False(File.Exists(context.ConfigPath));
        Assert.False(context.RemoteAccess.IsRunning);
        Assert.False(Directory.Exists(context.RemoteDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("       ")]
    [InlineData("1234567")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    [InlineData("has space")]
    [InlineData("bad+code")]
    [InlineData("bad.code")]
    [InlineData("abcdefg\u00e9")]
    public async Task InvalidConnectionCode_RejectsSaveAndEnableWithoutStarting(string code)
    {
        await using var context = new RemoteTestContext();
        var settings = context.CreateSettings();
        settings.ConnectionCode = code;

        Assert.False(await settings.SaveEditsAsync());
        Assert.Equal(Loc.Get("settings_remote_invalid_code"), settings.Error);
        await settings.SetEnabledAsync(true);

        Assert.Equal(Loc.Get("settings_remote_invalid_code"), settings.Error);
        Assert.False(settings.Enabled);
        Assert.False(settings.IsApplying);
        Assert.False(File.Exists(context.ConfigPath));
        Assert.False(context.RemoteAccess.IsRunning);
        Assert.False(Directory.Exists(context.RemoteDirectory));
    }

    [Fact]
    public async Task Error_IsSettableAndNotifiesHasError()
    {
        await using var context = new RemoteTestContext();
        var settings = context.CreateSettings();
        var changes = new List<string?>();
        settings.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        settings.Error = "test error";
        Assert.True(settings.HasError);
        settings.Error = null;

        Assert.False(settings.HasError);
        Assert.Equal(2, changes.Count(name => name == nameof(RemoteSettingsViewModel.HasError)));
    }

    [Fact]
    public async Task GenerateConnectionCode_ChangesAndAutoSavesDisabledDraft()
    {
        await using var context = new RemoteTestContext();
        var config = SampleConfig(new WorkbenchRemoteConfig(false, 23456, FirstCode));
        var settings = new RemoteSettingsViewModel(config, context.RemoteAccess, context.ConfigPath);

        await settings.GenerateConnectionCodeCommand.ExecuteAsync(null);

        Assert.NotEqual(FirstCode, settings.ConnectionCode);
        Assert.True(RemoteConnectionCode.IsValid(settings.ConnectionCode));
        var saved = WorkbenchConfig.LoadOrDefault(context.ConfigPath);
        Assert.Equal(new WorkbenchRemoteConfig(false, 23456, settings.ConnectionCode), saved.Remote);
        AssertUnrelatedSettingsEqual(config, saved);
        Assert.False(settings.Enabled);
        Assert.False(settings.IsApplying);
        Assert.True(settings.CanEdit);
        Assert.False(context.RemoteAccess.IsRunning);
    }

    [Fact]
    public async Task GenerateConnectionCode_InvalidPortKeepsNewCodeWithoutPersisting()
    {
        await using var context = new RemoteTestContext();
        var settings = context.CreateSettings();
        settings.Port = "invalid";

        await settings.GenerateConnectionCodeCommand.ExecuteAsync(null);

        Assert.NotEqual(FirstCode, settings.ConnectionCode);
        Assert.True(RemoteConnectionCode.IsValid(settings.ConnectionCode));
        Assert.Equal(Loc.Get("settings_remote_invalid_port"), settings.Error);
        Assert.False(File.Exists(context.ConfigPath));
        Assert.False(settings.IsApplying);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task SaveEdits_DoesNotLockFocusTransferActions()
    {
        await using var context = new RemoteTestContext();
        var settings = context.CreateSettings();
        var applyingChanges = new List<bool>();
        settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteSettingsViewModel.IsApplying))
                applyingChanges.Add(settings.IsApplying);
        };

        var saving = settings.SaveEditsAsync();

        Assert.False(settings.IsApplying);
        Assert.True(settings.CanEdit);
        Assert.True(settings.GenerateConnectionCodeCommand.CanExecute(null));
        Assert.True(await saving);
        Assert.Empty(applyingChanges);
    }

    [Fact]
    public async Task SaveEdits_PersistenceFailureReportsLocalizedErrorAndAllowsRetry()
    {
        await using var context = new RemoteTestContext();
        Directory.CreateDirectory(context.ConfigPath);
        var settings = context.CreateSettings();

        Assert.False(await settings.SaveEditsAsync());
        Assert.True(settings.HasError);
        Assert.Contains(Loc.Get("settings_could_not_save", string.Empty).Trim(), settings.Error);
        Assert.False(settings.IsApplying);
        Assert.True(settings.CanEdit);

        Directory.Delete(context.ConfigPath);

        Assert.True(await settings.SaveEditsAsync());
        Assert.Null(settings.Error);
        Assert.False(context.RemoteAccess.IsRunning);
    }

    [Fact]
    public async Task Enable_PersistenceFailureDoesNotStartListener()
    {
        await using var context = new RemoteTestContext();
        Directory.CreateDirectory(context.ConfigPath);
        var settings = context.CreateSettings(AllocateFreePort());

        await settings.SetEnabledAsync(true);

        Assert.False(settings.Enabled);
        Assert.False(context.RemoteAccess.IsRunning);
        Assert.True(settings.HasError);
        Assert.False(settings.IsApplying);
        Assert.True(settings.CanEdit);
        Assert.False(Directory.Exists(context.RemoteDirectory));
    }

    [Fact]
    public async Task Enable_InvalidLatestConfigReportsFailureWithoutOverwritingOrStarting()
    {
        await using var context = new RemoteTestContext();
        const string invalidJson = "{invalid config";
        await File.WriteAllTextAsync(context.ConfigPath, invalidJson);
        var settings = context.CreateSettings(AllocateFreePort());

        await settings.SetEnabledAsync(true);

        Assert.False(settings.Enabled);
        Assert.False(context.RemoteAccess.IsRunning);
        Assert.True(settings.HasError);
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(context.ConfigPath));
    }

    [Fact]
    public async Task Enable_PersistsBeforeStartingAndImmediatelyAcceptsReusableHttpPairing()
    {
        await using var context = new RemoteTestContext();
        var port = AllocateFreePort();
        var config = SampleConfig(new WorkbenchRemoteConfig(false, port, FirstCode));
        var settings = new RemoteSettingsViewModel(config, context.RemoteAccess, context.ConfigPath);
        var persistedBeforeStartup = false;
        context.RemoteAccess.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteAccessViewModel.IsRunning) && context.RemoteAccess.IsRunning)
                persistedBeforeStartup = WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote
                    == new WorkbenchRemoteConfig(true, port, FirstCode);
        };

        await settings.SetEnabledAsync(true);

        Assert.True(persistedBeforeStartup);
        Assert.True(settings.Enabled);
        Assert.True(context.RemoteAccess.IsRunning);
        Assert.False(settings.CanEdit);
        Assert.False(settings.IsApplying);
        Assert.False(settings.GenerateConnectionCodeCommand.CanExecute(null));
        Assert.Null(settings.Error);
        Assert.Equal(FirstCode, context.RemoteAccess.PairingToken);
        Assert.Null(context.RemoteAccess.PairingTokenExpiresAt);
        AssertPairingUrls(context.RemoteAccess, port, FirstCode);
        var firstDevice = await PairAsync(port, FirstCode);
        var secondDevice = await PairAsync(port, FirstCode);
        Assert.NotEqual(firstDevice, secondDevice);
        AssertUnrelatedSettingsEqual(config, WorkbenchConfig.LoadOrDefault(context.ConfigPath));
    }

    [Fact]
    public async Task Enabled_SaveEditsAndDiceDoNotChangeSavedOrLiveSettings()
    {
        await using var context = new RemoteTestContext();
        var port = AllocateFreePort();
        var settings = context.CreateSettings(port);
        await settings.SetEnabledAsync(true);
        var savedText = await File.ReadAllTextAsync(context.ConfigPath);
        settings.Port = "invalid";
        settings.ConnectionCode = "invalid";

        Assert.True(await settings.SaveEditsAsync());
        await settings.GenerateConnectionCodeCommand.ExecuteAsync(null);

        Assert.Equal("invalid", settings.Port);
        Assert.Equal("invalid", settings.ConnectionCode);
        Assert.Equal(savedText, await File.ReadAllTextAsync(context.ConfigPath));
        Assert.True(settings.Enabled);
        Assert.False(settings.IsApplying);
        Assert.Equal(FirstCode, context.RemoteAccess.PairingToken);
        await PairAsync(port, FirstCode);
    }

    [Fact]
    public async Task LegacyEnabledConfig_UsesLiveCodeInTextAndUrlsAndMigratesOnlyCode()
    {
        await using var context = new RemoteTestContext();
        var port = AllocateFreePort();
        var legacy = SampleConfig(new WorkbenchRemoteConfig(true, port));
        legacy.Save(context.ConfigPath);
        await context.RemoteAccess.ApplyConfigAsync(legacy.Remote);
        Assert.True(context.RemoteAccess.IsRunning);
        var liveCode = Assert.IsType<string>(context.RemoteAccess.PairingToken);
        var settings = new RemoteSettingsViewModel(legacy, context.RemoteAccess, context.ConfigPath);
        var latest = legacy with { Theme = "dark", BraveApiKey = "latest-search-key" };
        latest.Save(context.ConfigPath);
        var stoppedDuringSave = false;
        context.RemoteAccess.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteAccessViewModel.IsRunning) && !context.RemoteAccess.IsRunning)
                stoppedDuringSave = true;
        };

        Assert.Equal(liveCode, settings.ConnectionCode);
        AssertPairingUrls(context.RemoteAccess, port, settings.ConnectionCode);
        Assert.True(await settings.SaveEditsAsync());

        var migrated = WorkbenchConfig.LoadOrDefault(context.ConfigPath);
        Assert.Equal(legacy.Remote! with { ConnectionCode = liveCode }, migrated.Remote);
        AssertUnrelatedSettingsEqual(latest, migrated);
        Assert.Equal(liveCode, settings.ConnectionCode);
        Assert.Equal(liveCode, context.RemoteAccess.PairingToken);
        Assert.Null(context.RemoteAccess.PairingTokenExpiresAt);
        Assert.False(stoppedDuringSave);
        await PairAsync(port, liveCode);
        await PairAsync(port, liveCode);
    }

    [Fact]
    public async Task LegacyEnabledConfig_MigrationPreservesNewerExplicitSavedCode()
    {
        await using var context = new RemoteTestContext();
        var port = AllocateFreePort();
        var legacy = SampleConfig(new WorkbenchRemoteConfig(true, port));
        await context.RemoteAccess.ApplyConfigAsync(legacy.Remote);
        var settings = new RemoteSettingsViewModel(legacy, context.RemoteAccess, context.ConfigPath);
        var latest = legacy with { Remote = legacy.Remote! with { ConnectionCode = SecondCode } };
        latest.Save(context.ConfigPath);

        Assert.True(await settings.SaveEditsAsync());

        Assert.Equal(SecondCode, WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote!.ConnectionCode);
        Assert.Equal(context.RemoteAccess.PairingToken, settings.ConnectionCode);
        Assert.True(context.RemoteAccess.IsRunning);
    }

    [Fact]
    public async Task Disable_EditPortAndCode_ReenableUsesNewHttpListenerAndCode()
    {
        await using var context = new RemoteTestContext();
        var firstPort = AllocateFreePort();
        var settings = context.CreateSettings(firstPort);
        await settings.SetEnabledAsync(true);
        await PairAsync(firstPort, FirstCode);
        var secondPort = AllocateFreePort();

        await settings.SetEnabledAsync(false);

        Assert.False(settings.Enabled);
        Assert.False(context.RemoteAccess.IsRunning);
        Assert.True(settings.CanEdit);
        Assert.Null(context.RemoteAccess.PairingToken);
        Assert.Empty(context.RemoteAccess.PairingUrls);
        Assert.False(WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote!.Enabled);
        await AssertListenerStoppedAsync(firstPort);
        settings.Port = secondPort.ToString(CultureInfo.InvariantCulture);
        settings.ConnectionCode = SecondCode;
        Assert.True(await settings.SaveEditsAsync());
        Assert.False(context.RemoteAccess.IsRunning);

        await settings.SetEnabledAsync(true);

        Assert.True(settings.Enabled);
        Assert.Equal(secondPort, context.RemoteAccess.Port);
        Assert.Equal(SecondCode, context.RemoteAccess.PairingToken);
        AssertPairingUrls(context.RemoteAccess, secondPort, SecondCode);
        await AssertPairingRejectedAsync(secondPort, FirstCode);
        await PairAsync(secondPort, SecondCode);
        Assert.Equal(new WorkbenchRemoteConfig(true, secondPort, SecondCode),
            WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote);
    }

    [Fact]
    public async Task Enable_BusyPortRollsBackUiAndPersistenceAndAllowsRetry()
    {
        await using var context = new RemoteTestContext();
        await using var blocker = new RemoteTestContext();
        var port = AllocateFreePort();
        var blockingConfig = new WorkbenchRemoteConfig(true, port, SecondCode);
        await blocker.RemoteAccess.ApplyConfigAsync(blockingConfig);
        Assert.True(blocker.RemoteAccess.IsRunning);
        Assert.Null(blocker.RemoteAccess.Error);
        await PairAsync(port, SecondCode);
        var settings = context.CreateSettings(port);

        await settings.SetEnabledAsync(true);

        Assert.False(settings.Enabled);
        Assert.False(context.RemoteAccess.IsRunning);
        Assert.True(context.RemoteAccess.HasError);
        Assert.True(settings.HasError);
        Assert.False(settings.IsApplying);
        Assert.True(settings.CanEdit);
        Assert.True(settings.GenerateConnectionCodeCommand.CanExecute(null));
        Assert.Equal(new WorkbenchRemoteConfig(false, port, FirstCode),
            WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote);
        Assert.True(blocker.RemoteAccess.IsRunning);
        await AssertPairingRejectedAsync(port, FirstCode);
        await PairAsync(port, SecondCode);

        await blocker.RemoteAccess.ApplyConfigAsync(blockingConfig with { Enabled = false });
        Assert.False(blocker.RemoteAccess.IsRunning);
        await AssertListenerStoppedAsync(port);
        await settings.SetEnabledAsync(true);

        Assert.True(settings.Enabled);
        Assert.True(context.RemoteAccess.IsRunning);
        Assert.Null(context.RemoteAccess.Error);
        Assert.Null(settings.Error);
        Assert.True(WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote!.Enabled);
        await AssertPairingRejectedAsync(port, SecondCode);
        await PairAsync(port, FirstCode);
    }

    [Fact]
    public async Task Enable_ExclusiveIpv4PortRollsBackUiAndPersistenceAndAllowsRetry()
    {
        await using var context = new RemoteTestContext();
        var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Server.ExclusiveAddressUse = true;
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            var settings = context.CreateSettings(port);

            await settings.SetEnabledAsync(true);

            Assert.False(settings.Enabled);
            Assert.False(context.RemoteAccess.IsRunning);
            Assert.True(settings.HasError);
            Assert.False(settings.IsApplying);
            Assert.True(settings.CanEdit);
            Assert.True(settings.GenerateConnectionCodeCommand.CanExecute(null));
            Assert.Equal(new WorkbenchRemoteConfig(false, port, FirstCode),
                WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote);
            blocker.Stop();

            await settings.SetEnabledAsync(true);

            Assert.True(settings.Enabled);
            Assert.Null(settings.Error);
            Assert.True(WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote!.Enabled);
            await PairAsync(port, FirstCode);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public async Task Disable_PersistenceFailureStillStopsListenerAndUnlocksEditing()
    {
        await using var context = new RemoteTestContext();
        var port = AllocateFreePort();
        var settings = context.CreateSettings(port);
        await settings.SetEnabledAsync(true);
        Assert.True(context.RemoteAccess.IsRunning);
        File.Delete(context.ConfigPath);
        Directory.CreateDirectory(context.ConfigPath);

        await settings.SetEnabledAsync(false);

        Assert.False(context.RemoteAccess.IsRunning);
        Assert.False(settings.Enabled);
        Assert.False(settings.IsApplying);
        Assert.True(settings.CanEdit);
        Assert.True(settings.HasError);
        Assert.Contains(Loc.Get("settings_could_not_save", string.Empty).Trim(), settings.Error);
        await AssertListenerStoppedAsync(port);
    }

    [Fact]
    public async Task Disable_InvalidProgrammaticDraftCannotPreventStopping()
    {
        await using var context = new RemoteTestContext();
        var port = AllocateFreePort();
        var settings = context.CreateSettings(port);
        await settings.SetEnabledAsync(true);
        settings.Port = "invalid";
        settings.ConnectionCode = string.Empty;

        await settings.SetEnabledAsync(false);

        Assert.False(settings.Enabled);
        Assert.False(context.RemoteAccess.IsRunning);
        Assert.Null(settings.Error);
        Assert.Equal(new WorkbenchRemoteConfig(false, port, FirstCode),
            WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote);
        await AssertListenerStoppedAsync(port);
    }

    [Fact]
    public async Task SetEnabled_SerializesRapidEnableSaveAndDisableAndLocksOperationalActions()
    {
        await using var context = new RemoteTestContext();
        var settings = context.CreateSettings(AllocateFreePort());
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteSettingsViewModel.IsApplying) && settings.IsApplying
                && Interlocked.Exchange(ref paused, 1) == 0)
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            }
        };
        var enabling = Task.Run(() => settings.SetEnabledAsync(true));
        Task<bool>? saving = null;
        Task? disabling = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(settings.IsApplying);
            Assert.False(settings.CanEdit);
            Assert.False(settings.GenerateConnectionCodeCommand.CanExecute(null));
            saving = settings.SaveEditsAsync();
            disabling = settings.SetEnabledAsync(false);
            Assert.False(saving.IsCompleted);
            Assert.False(disabling.IsCompleted);
            var code = settings.ConnectionCode;
            await settings.GenerateConnectionCodeCommand.ExecuteAsync(null);
            Assert.Equal(code, settings.ConnectionCode);
        }
        finally
        {
            release.Set();
            await enabling;
            if (saving is not null)
                Assert.True(await saving);
            if (disabling is not null)
                await disabling;
        }

        Assert.False(settings.Enabled);
        Assert.False(context.RemoteAccess.IsRunning);
        Assert.False(settings.IsApplying);
        Assert.True(settings.CanEdit);
        Assert.False(WorkbenchConfig.LoadOrDefault(context.ConfigPath).Remote!.Enabled);
    }

    [Fact]
    public async Task RemoteAccess_ModelCatalogReadsLatestConfigWithoutRestarting()
    {
        var provider = new WorkbenchProviderConfig("openai-responses", "https://private-provider.example/v1", "private-provider-key",
            [
                new("named-model", DisplayName: "Friendly model", ReasoningLevels: ["low", "high"]),
                new("no-reasoning", ReasoningLevels: []),
                new("gpt-6.1-terra"),
                new("disabled-model", Enabled: false),
            ], Id: "custom-provider", Name: "Friendly provider");
        var currentConfig = SampleConfig() with
        {
            Providers =
            [
                new("anthropic", "https://disabled-provider.example", "disabled-key",
                    [new("disabled-provider-model")], Id: "disabled-provider", Enabled: false),
                provider,
            ],
            DefaultModel = new("custom-provider", "named-model"),
        };
        var calls = 0;
        await using var context = new RemoteTestContext(() =>
        {
            Interlocked.Increment(ref calls);
            return Composition.BuildRemoteModelCatalog(Volatile.Read(ref currentConfig));
        });
        var port = AllocateFreePort();
        await context.RemoteAccess.ApplyConfigAsync(new WorkbenchRemoteConfig(true, port, FirstCode));
        Assert.True(context.RemoteAccess.IsRunning);
        Assert.Equal(0, Volatile.Read(ref calls));
        var token = await PairAsync(port, FirstCode);
        using var client = CreateClient(port);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        using (var response = await client.GetAsync("api/models"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-provider-key", body, StringComparison.Ordinal);
            Assert.DoesNotContain("https://private-provider.example", body, StringComparison.Ordinal);
            Assert.DoesNotContain("search-key", body, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(body);
            var models = document.RootElement.GetProperty("models").EnumerateArray().ToArray();
            Assert.Equal(["custom-provider", "custom-provider", "custom-provider"],
                models.Select(model => model.GetProperty("providerId").GetString()));
            Assert.All(models, model => Assert.Equal("Friendly provider", model.GetProperty("providerLabel").GetString()));
            Assert.Equal(["named-model", "no-reasoning", "gpt-6.1-terra"],
                models.Select(model => model.GetProperty("modelId").GetString()));
            Assert.Equal(["Friendly model", "custom-provider:no-reasoning", "custom-provider:gpt-6.1-terra"],
                models.Select(model => model.GetProperty("displayName").GetString()));
            Assert.Equal(["low", "high"],
                models[0].GetProperty("reasoningLevels").EnumerateArray().Select(level => level.GetString()));
            Assert.Empty(models[1].GetProperty("reasoningLevels").EnumerateArray());
            Assert.Equal(WorkbenchModelCatalog.Resolve(provider, provider.Models[2]).ReasoningLevels ?? [],
                models[2].GetProperty("reasoningLevels").EnumerateArray().Select(level => level.GetString()));
            var selection = document.RootElement.GetProperty("defaultSelection");
            Assert.Equal("custom-provider", selection.GetProperty("providerId").GetString());
            Assert.Equal("named-model", selection.GetProperty("modelId").GetString());
            Assert.Equal("high", selection.GetProperty("options").GetProperty("reasoningLevel").GetString());
        }
        Assert.Equal(1, Volatile.Read(ref calls));

        Volatile.Write(ref currentConfig, currentConfig with
        {
            Providers = [new("anthropic", "https://latest-provider.example", "latest-key", [new("latest-model", ReasoningLevels: [])])],
            DefaultModel = new("disabled-provider", "disabled-provider-model"),
        });

        using (var response = await client.GetAsync("api/models"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var model = Assert.Single(document.RootElement.GetProperty("models").EnumerateArray());
            Assert.Equal("anthropic", model.GetProperty("providerId").GetString());
            Assert.Equal("anthropic", model.GetProperty("providerLabel").GetString());
            Assert.Equal("latest-model", model.GetProperty("modelId").GetString());
            Assert.Equal("anthropic:latest-model", model.GetProperty("displayName").GetString());
            Assert.Empty(model.GetProperty("reasoningLevels").EnumerateArray());
            var selection = document.RootElement.GetProperty("defaultSelection");
            Assert.Equal("anthropic", selection.GetProperty("providerId").GetString());
            Assert.Equal("latest-model", selection.GetProperty("modelId").GetString());
            Assert.Equal(JsonValueKind.Null, selection.GetProperty("options").GetProperty("reasoningLevel").ValueKind);
        }
        Assert.Equal(2, Volatile.Read(ref calls));

        Volatile.Write(ref currentConfig, currentConfig with { Providers = [] });

        using var empty = await client.GetAsync("api/models");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal("""{"models":[],"defaultSelection":null}""", await empty.Content.ReadAsStringAsync());
        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.True(context.RemoteAccess.IsRunning);
    }

    [Fact]
    public async Task RemoteAccess_SlashCatalogReadsLiveSkillsAndDisabledConfigWithoutLeakingPrivateData()
    {
        var currentConfig = SampleConfig() with { DisabledSkillPaths = [] };
        ComposerSkillCatalog? skills = null;
        var calls = 0;
        await using var context = new RemoteTestContext(slashCatalog: (sessionId, workspaceRoot, _) =>
        {
            Assert.Null(sessionId);
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult<RemoteSlashCatalog?>(Composition.BuildRemoteSlashCatalog(
                workspaceRoot, Volatile.Read(ref currentConfig).DisabledSkillPaths, skills));
        });
        var directory = Path.GetDirectoryName(context.ConfigPath)!;
        var workspace = Path.Combine(directory, "workspace");
        var userBase = Path.Combine(directory, "user");
        skills = new ComposerSkillCatalog(new SkillsCatalog(userBase));
        var report = WriteSkill(Path.Combine(workspace, ".alcoler", "skills"), "report", "Research topics.");
        var review = WriteSkill(Path.Combine(userBase, ".agents", "skills"), "review", "Inspect changes.");
        var port = AllocateFreePort();
        await context.RemoteAccess.ApplyConfigAsync(new WorkbenchRemoteConfig(true, port, FirstCode));
        Assert.True(context.RemoteAccess.IsRunning);
        Assert.Equal(0, Volatile.Read(ref calls));
        using var client = CreateClient(port);
        var path = "api/slash-catalog?workspaceRoot=" + Uri.EscapeDataString(workspace);
        using (var unauthorized = await client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal(0, Volatile.Read(ref calls));
        }
        var token = await PairAsync(port, FirstCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        using (var first = await client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
            var body = await first.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-skill-body", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SKILL.md", body, StringComparison.Ordinal);
            Assert.DoesNotContain("provider-key", body, StringComparison.Ordinal);
            Assert.DoesNotContain("https://api.anthropic.com", body, StringComparison.Ordinal);
            Assert.DoesNotContain("search-key", body, StringComparison.Ordinal);
            Assert.DoesNotContain(FirstCode, body, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(body);
            var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(new[] { "plan", "orchestrator", "report", "review" }, entries.Select(entry => entry.GetProperty("name").GetString()));
            Assert.Equal(new[] { "plan", "orchestrator", "skill", "skill" }, entries.Select(entry => entry.GetProperty("kind").GetString()));
            Assert.All(entries, entry => Assert.Equal(new[] { "name", "description", "kind" }, entry.EnumerateObject().Select(property => property.Name)));
        }
        Volatile.Write(ref currentConfig, currentConfig with { DisabledSkillPaths = [report] });
        await File.WriteAllTextAsync(review, "---\nname: review\ndescription: Updated live review metadata.\n---\nprivate-skill-body");

        using (var disabled = await client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            using var document = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync());
            var entry = Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray(),
                entry => entry.GetProperty("kind").GetString() == "skill");
            Assert.Equal("review", entry.GetProperty("name").GetString());
            Assert.Equal("Updated live review metadata.", entry.GetProperty("description").GetString());
        }
        Volatile.Write(ref currentConfig, currentConfig with { DisabledSkillPaths = [] });

        using var enabled = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using var restored = JsonDocument.Parse(await enabled.Content.ReadAsStringAsync());
        Assert.Contains(restored.RootElement.GetProperty("entries").EnumerateArray(), entry => entry.GetProperty("name").GetString() == "report");
        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.True(context.RemoteAccess.IsRunning);

        static string WriteSkill(string root, string name, string description)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "SKILL.md");
            File.WriteAllText(path, $"---\nname: {name}\ndescription: {description}\n---\nprivate-skill-body");
            return path;
        }
    }

    [Fact]
    public async Task RemoteAccess_ApplyDisabledNewPortAndCode_ReenableBindsNewListener()
    {
        await using var context = new RemoteTestContext();
        var firstPort = AllocateFreePort();
        await context.RemoteAccess.ApplyConfigAsync(new WorkbenchRemoteConfig(true, firstPort, FirstCode));
        await PairAsync(firstPort, FirstCode);
        var secondPort = AllocateFreePort();
        var replacement = new WorkbenchRemoteConfig(false, secondPort, SecondCode);

        await context.RemoteAccess.ApplyConfigAsync(replacement);

        Assert.False(context.RemoteAccess.IsRunning);
        Assert.Equal(secondPort, context.RemoteAccess.Port);
        Assert.Null(context.RemoteAccess.PairingToken);
        await AssertListenerStoppedAsync(firstPort);

        await context.RemoteAccess.ApplyConfigAsync(replacement with { Enabled = true });

        Assert.True(context.RemoteAccess.IsRunning);
        Assert.Equal(secondPort, context.RemoteAccess.Port);
        Assert.Equal(SecondCode, context.RemoteAccess.PairingToken);
        AssertPairingUrls(context.RemoteAccess, secondPort, SecondCode);
        await AssertPairingRejectedAsync(secondPort, FirstCode);
        await PairAsync(secondPort, SecondCode);
        await AssertListenerStoppedAsync(firstPort);
    }

    [Fact]
    public async Task RemoteAccess_ConcurrentEnableAndDisableAreSerializedAndEndStopped()
    {
        await using var context = new RemoteTestContext();
        var port = AllocateFreePort();
        var config = new WorkbenchRemoteConfig(true, port, FirstCode);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.RemoteAccess.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteAccessViewModel.IsRunning) && context.RemoteAccess.IsRunning)
            {
                started.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            }
        };
        var enabling = Task.Run(() => context.RemoteAccess.ApplyConfigAsync(config));
        Task? disabling = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            disabling = context.RemoteAccess.ApplyConfigAsync(config with { Enabled = false });
            Assert.False(disabling.IsCompleted);
        }
        finally
        {
            release.Set();
            await enabling;
            if (disabling is not null)
                await disabling;
        }

        Assert.False(context.RemoteAccess.IsRunning);
        Assert.Null(context.RemoteAccess.Error);
        Assert.Null(context.RemoteAccess.PairingToken);
        Assert.Empty(context.RemoteAccess.PairingUrls);
        await AssertListenerStoppedAsync(port);
    }

    private static WorkbenchConfig SampleConfig(WorkbenchRemoteConfig? remote = null) => new(
        [new WorkbenchProviderConfig("anthropic", "https://api.anthropic.com", "provider-key",
            [new WorkbenchModelConfig("test-model", 200_000)])],
        new WorkbenchDefaultModelConfig("anthropic", "test-model"),
        [new WorkbenchMcpServerConfig("fetch", "uvx", ["mcp-server-fetch"],
            new Dictionary<string, string> { ["TIMEOUT"] = "30" })],
        Theme: "light",
        Language: "en",
        BraveApiKey: "search-key",
        Composer: new WorkbenchComposerConfig(PermissionMode.Edit,
            new Dictionary<string, PermissionMode> { ["session"] = PermissionMode.Edit }, DefaultPlanEnabled: true),
        DisabledSkillPaths: ["C:/skills/disabled/SKILL.md"],
        AutoArchiveSessions: true,
        AutoArchiveOlderThanDays: 14,
        Browser: new WorkbenchBrowserConfig(false, "C:/browser", true),
        Remote: remote);

    private static void AssertUnrelatedSettingsEqual(WorkbenchConfig expected, WorkbenchConfig actual) =>
        Assert.Equal(
            JsonSerializer.Serialize(expected with { Remote = null }, WorkbenchConfig.JsonOptions),
            JsonSerializer.Serialize(actual with { Remote = null }, WorkbenchConfig.JsonOptions));

    private static void AssertPairingUrls(RemoteAccessViewModel remote, int port, string code)
    {
        Assert.All(remote.PairingUrls, url =>
        {
            var uri = new Uri(url);
            Assert.Equal(port, uri.Port);
            Assert.Equal("#pair=" + Uri.EscapeDataString(code), uri.Fragment);
        });
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static HttpClient CreateClient(int port) => new(new HttpClientHandler { UseProxy = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static async Task<string> PairAsync(int port, string code)
    {
        using var client = CreateClient(port);
        using var content = new StringContent(
            JsonSerializer.Serialize(new { pairingToken = code, deviceName = "settings-test-phone" }),
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("api/pair", content);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Pairing returned {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("deviceId").GetString()));
        var token = document.RootElement.GetProperty("deviceToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        Assert.NotEqual(code, token);
        return token!;
    }

    private static async Task AssertPairingRejectedAsync(int port, string code)
    {
        using var client = CreateClient(port);
        using var content = new StringContent(JsonSerializer.Serialize(new { pairingToken = code }),
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("api/pair", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task AssertListenerStoppedAsync(int port)
    {
        using var client = CreateClient(port);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("/"));
    }

    private sealed class RemoteTestContext : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), "alcoler-remote-settings-tests-" + Guid.NewGuid().ToString("N"));

        public RemoteTestContext(
            Func<RemoteModelCatalog>? modelCatalog = null,
            Func<SessionId?, string?, CancellationToken, ValueTask<RemoteSlashCatalog?>>? slashCatalog = null)
        {
            Directory.CreateDirectory(_directory);
            RemoteAccess = new RemoteAccessViewModel(
                static () => throw new InvalidOperationException("Runtime not needed"),
                RemoteDirectory,
                static (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null),
                "remote-settings-tests",
                modelCatalog,
                slashCatalog);
        }

        public string ConfigPath => Path.Combine(_directory, "config.json");
        public string RemoteDirectory => Path.Combine(_directory, "remote");
        public RemoteAccessViewModel RemoteAccess { get; }

        public RemoteSettingsViewModel CreateSettings(int port = 23456) => new(
            SampleConfig(new WorkbenchRemoteConfig(false, port, FirstCode)), RemoteAccess, ConfigPath);

        public async ValueTask DisposeAsync()
        {
            await RemoteAccess.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
