using System.Globalization;
using Alcoler.Localization;
using Alcoler.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.ViewModel;

public sealed partial class RemoteSettingsViewModel : ObservableObject
{
    private readonly WorkbenchConfig _config;
    private readonly string _configPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkbenchRemoteConfig _savedRemote;
    private bool _needsConnectionCodeMigration;
    private bool _enabled;
    private bool _isApplying;

    public RemoteSettingsViewModel(
        WorkbenchConfig config,
        RemoteAccessViewModel remoteAccess,
        string? configPath = null)
    {
        _config = config;
        _configPath = configPath ?? WorkbenchConfig.DefaultConfigPath;
        RemoteAccess = remoteAccess;
        _enabled = config.Remote?.Enabled == true;
        var port = config.Remote?.EffectivePort ?? WorkbenchRemoteConfig.DefaultPort;
        _port = port.ToString(CultureInfo.InvariantCulture);
        _connectionCode = config.Remote?.ConnectionCode
            ?? (remoteAccess.IsRunning ? remoteAccess.PairingToken : null)
            ?? RemoteConnectionCode.Generate();
        _needsConnectionCodeMigration = config.Remote?.ConnectionCode is null;
        _savedRemote = (config.Remote ?? new WorkbenchRemoteConfig()) with
        {
            Port = port,
            ConnectionCode = _connectionCode,
        };
    }

    public RemoteAccessViewModel RemoteAccess { get; }

    public bool Enabled
    {
        get => _enabled;
        private set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                GenerateConnectionCodeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    [ObservableProperty]
    private string _port;

    [ObservableProperty]
    private string _connectionCode;

    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (SetProperty(ref _isApplying, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                GenerateConnectionCodeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanEdit => !Enabled && !IsApplying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public async Task SetEnabledAsync(bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            IsApplying = true;
            Error = null;
            if (!enabled)
            {
                await ApplyAsync(_savedRemote with { Enabled = false });
                await PersistAsync(remote => remote with { Enabled = Enabled });
                return;
            }
            if (Enabled && RemoteAccess.IsRunning)
            {
                return;
            }
            if (!await SaveDraftAsync(enabled: true))
            {
                return;
            }

            await ApplyAsync(_savedRemote);
            if (!Enabled)
            {
                Error ??= Loc.Get("settings_remote_status_error", Loc.Get("settings_remote_status_stopped"));
                await PersistAsync(remote => remote with { Enabled = false });
            }
        }
        finally
        {
            IsApplying = false;
            _gate.Release();
        }
    }

    public async Task<bool> SaveEditsAsync()
    {
        // LostFocus saves must not disable the control receiving the same click.
        await _gate.WaitAsync();
        try
        {
            if (Enabled)
            {
                if (!_needsConnectionCodeMigration || !RemoteAccess.IsRunning
                    || RemoteAccess.PairingToken is not { } code)
                {
                    return true;
                }
                Error = null;
                return await PersistAsync(remote => remote.ConnectionCode is null
                    ? remote with { ConnectionCode = code }
                    : remote);
            }
            Error = null;
            return await SaveDraftAsync(enabled: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task GenerateConnectionCodeAsync()
    {
        if (!CanEdit)
        {
            return;
        }
        await _gate.WaitAsync();
        try
        {
            if (Enabled)
            {
                return;
            }
            IsApplying = true;
            Error = null;
            ConnectionCode = RemoteConnectionCode.Generate();
            await SaveDraftAsync(enabled: false);
        }
        finally
        {
            IsApplying = false;
            _gate.Release();
        }
    }

    private async Task<bool> SaveDraftAsync(bool enabled)
    {
        var portText = Port;
        var codeText = ConnectionCode;
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            Error = Loc.Get("settings_remote_invalid_port");
            return false;
        }
        var code = codeText?.Trim() ?? string.Empty;
        if (!RemoteConnectionCode.IsValid(code))
        {
            Error = Loc.Get("settings_remote_invalid_code");
            return false;
        }
        if (!await PersistAsync(remote => remote with
            {
                Enabled = enabled,
                Port = port,
                ConnectionCode = code,
            }))
        {
            return false;
        }
        if (Port == portText)
        {
            Port = port.ToString(CultureInfo.InvariantCulture);
        }
        if (ConnectionCode == codeText)
        {
            ConnectionCode = code;
        }
        return true;
    }

    private async Task ApplyAsync(WorkbenchRemoteConfig remote)
    {
        try
        {
            await RemoteAccess.ApplyConfigAsync(remote);
            if (RemoteAccess.HasError)
            {
                Error = Loc.Get("settings_remote_status_error", RemoteAccess.Error);
            }
        }
        catch (Exception exception)
        {
            Error = Loc.Get("settings_remote_status_error", exception.Message);
        }
        Enabled = RemoteAccess.IsRunning;
    }

    private async Task<bool> PersistAsync(Func<WorkbenchRemoteConfig, WorkbenchRemoteConfig> update)
    {
        try
        {
            var fallbackRemote = _savedRemote;
            _savedRemote = await Task.Run(() =>
            {
                var current = File.Exists(_configPath) ? WorkbenchConfig.LoadOrDefault(_configPath) : _config;
                var remote = update(current.Remote ?? fallbackRemote);
                (current with { Remote = remote }).Save(_configPath);
                return remote;
            });
            _needsConnectionCodeMigration = _savedRemote.ConnectionCode is null;
            return true;
        }
        catch (Exception exception)
        {
            var saveError = Loc.Get("settings_could_not_save", exception.Message);
            Error = HasError ? Error + Environment.NewLine + saveError : saveError;
            return false;
        }
    }
}
