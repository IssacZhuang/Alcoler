using System.Net.NetworkInformation;
using System.Net.Sockets;
using Alcoler.Contracts.Projections;
using Alcoler.Localization;
using Alcoler.Remote;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;

namespace Alcoler.ViewModel;

public sealed partial class RemotePairedDeviceViewModel(Action<RemotePairedDeviceViewModel> revoke)
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
    public string LastSeenText => RelativeTime.Format(LastSeenAt);

    [RelayCommand]
    private void Revoke() => revoke(this);
}

// The gateway is lazy; lifecycle failures must not escape startup or UI configuration updates.
public sealed partial class RemoteAccessViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<IAgentRuntime> _runtimeFactory;
    private readonly string _dataDirectory;
    private readonly Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> _artifactLoader;
    private readonly string? _productVersion;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly string _fallbackConnectionCode = RemoteConnectionCode.Generate();
    private RemoteGateway? _gateway;
    private RemoteGatewayOptions? _gatewayOptions;
    private int _appliedPort = WorkbenchRemoteConfig.DefaultPort;
    private bool _disposed;

    public RemoteAccessViewModel(
        Func<IAgentRuntime> runtimeFactory,
        string dataDirectory,
        Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> artifactLoader,
        string? productVersion)
    {
        _runtimeFactory = runtimeFactory;
        _dataDirectory = dataDirectory;
        _artifactLoader = artifactLoader;
        _productVersion = productVersion;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(GeneratePairingTokenCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairingUrl))]
    private IReadOnlyList<string> _pairingUrls = [];

    [ObservableProperty]
    private IReadOnlyList<RemotePairedDeviceViewModel> _devices = [];

    private IImage? _qrImage;
    public IImage? QrImage
    {
        get => _qrImage;
        private set
        {
            if (ReferenceEquals(_qrImage, value))
            {
                return;
            }
            var previous = _qrImage;
            _qrImage = value;
            OnPropertyChanged();
            (previous as IDisposable)?.Dispose();
        }
    }

    public string? PairingToken { get; private set; }

    public DateTimeOffset? PairingTokenExpiresAt { get; private set; }

    public string? PairingUrl => PairingUrls.Count > 0 ? PairingUrls[0] : null;

    public int Port => _appliedPort;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public string StatusText
    {
        get
        {
            if (IsRunning)
            {
                return Loc.Get("settings_remote_status_running", _appliedPort);
            }
            return Error is { Length: > 0 } error
                ? Loc.Get("settings_remote_status_error", error)
                : Loc.Get("settings_remote_status_stopped");
        }
    }

    public string PairingExpiryText
    {
        get
        {
            if (!IsRunning || PairingToken is null)
            {
                return string.Empty;
            }
            if (PairingTokenExpiresAt is not { } expiresAt)
            {
                return string.Empty;
            }
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            return remaining <= TimeSpan.Zero
                ? Loc.Get("settings_remote_pairing_expired")
                : Loc.Get("settings_remote_pairing_expires", $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}");
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        if (!value)
        {
            ClearPairing();
        }
        else
        {
            OnPropertyChanged(nameof(PairingExpiryText));
        }
    }

    public async Task ApplyConfigAsync(WorkbenchRemoteConfig? config, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_disposed)
            {
                return;
            }
            var enabled = config?.Enabled == true;
            var port = config?.EffectivePort ?? WorkbenchRemoteConfig.DefaultPort;
            var connectionCode = config?.ConnectionCode ?? _fallbackConnectionCode;
            try
            {
                if (!enabled)
                {
                    if (_gateway is { IsRunning: true } stopping)
                    {
                        await stopping.StopAsync().ConfigureAwait(true);
                    }
                }
                else
                {
                    if (_gateway is { } previous && (_gatewayOptions?.Port != port
                        || !string.Equals(_gatewayOptions?.ConnectionCode, connectionCode, StringComparison.Ordinal)))
                    {
                        await previous.DisposeAsync().ConfigureAwait(true);
                        _gateway = null;
                        _gatewayOptions = null;
                        IsRunning = false;
                        ClearPairing();
                    }
                    if (_gateway is null)
                    {
                        var options = BuildOptions(port, connectionCode);
                        _gateway = new RemoteGateway(options);
                        _gatewayOptions = options;
                    }
                    if (!_gateway.IsRunning)
                    {
                        ClearPairing();
                        await _gateway.StartAsync(cancellationToken).ConfigureAwait(true);
                        Console.WriteLine($"remote gateway listening on port {port}");
                    }
                }
                Error = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Error = exception.Message;
                Console.WriteLine($"remote gateway configuration failed on port {port}: {exception}");
            }
            finally
            {
                SetPort(_gateway is { IsRunning: true } && _gatewayOptions is { } options ? options.Port : port);
                RefreshStateCore();
            }
            if (enabled && IsRunning && PairingToken is null)
            {
                GeneratePairingTokenCore();
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    // Live status refreshes must not rotate the current pairing code or race lifecycle changes.
    public void RefreshState()
    {
        if (!_operations.Wait(0))
        {
            return;
        }
        try
        {
            if (!_disposed)
            {
                RefreshStateCore();
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    private void RefreshStateCore()
    {
        IsRunning = _gateway?.IsRunning == true;
        if (!IsRunning)
        {
            ClearPairing();
        }
        if (_gateway is not { } gateway)
        {
            Devices = [];
            return;
        }
        try
        {
            Devices = gateway.Devices.Select(device => new RemotePairedDeviceViewModel(RemoveDevice)
            {
                Id = device.Id,
                Name = device.Name,
                LastSeenAt = device.LastSeenAt,
            }).ToArray();
            if (PairingToken is not null && PairingTokenExpiresAt is { } expiresAt
                && expiresAt - DateTimeOffset.UtcNow <= TimeSpan.Zero)
            {
                ClearPairing();
            }
            else
            {
                OnPropertyChanged(nameof(PairingExpiryText));
            }
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPair))]
    private void GeneratePairingToken()
    {
        if (!_operations.Wait(0))
        {
            return;
        }
        try
        {
            if (!_disposed)
            {
                GeneratePairingTokenCore();
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    private void GeneratePairingTokenCore()
    {
        if (_gateway is not { IsRunning: true } gateway)
        {
            return;
        }
        try
        {
            PairingToken = gateway.GeneratePairingToken();
            PairingTokenExpiresAt = gateway.PairingTokenExpiresAt;
            OnPropertyChanged(nameof(PairingToken));
            OnPropertyChanged(nameof(PairingTokenExpiresAt));
            RebuildPairingUrls();
        }
        catch (Exception exception)
        {
            ClearPairing();
            Error = exception.Message;
        }
    }

    private bool CanPair() => !_disposed && IsRunning;

    private void RemoveDevice(RemotePairedDeviceViewModel device) => _ = RevokeDeviceAsync(device);

    private async Task RevokeDeviceAsync(RemotePairedDeviceViewModel device)
    {
        await _operations.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_gateway is not { } gateway || _disposed)
            {
                return;
            }
            try
            {
                await gateway.RevokeDeviceAsync(device.Id).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Error = Loc.Get("settings_remote_revoke_error", exception.Message);
            }
            RefreshStateCore();
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ClearPairing();
            if (_gateway is { } gateway)
            {
                try
                {
                    await gateway.DisposeAsync().ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // Shutdown must proceed even if the listener is already gone.
                }
            }
            IsRunning = _gateway?.IsRunning == true;
            Devices = [];
            _gateway = null;
            _gatewayOptions = null;
        }
        finally
        {
            // Queued callers must still acquire the gate after disposal to observe _disposed.
            _operations.Release();
        }
    }

    private RemoteGatewayOptions BuildOptions(int port, string? connectionCode) => new()
    {
        Runtime = _runtimeFactory,
        Port = port,
        ConnectionCode = connectionCode,
        DataDirectory = _dataDirectory,
        ArtifactLoader = _artifactLoader,
        ProductVersion = _productVersion,
    };

    private void SetPort(int port)
    {
        if (_appliedPort == port)
        {
            return;
        }
        _appliedPort = port;
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(StatusText));
    }

    private void RebuildPairingUrls()
    {
        if (PairingToken is null || _gateway is not { IsRunning: true })
        {
            ClearPairing();
            return;
        }
        var token = Uri.EscapeDataString(PairingToken);
        PairingUrls = EnumeratePrivateIPv4Addresses()
            .Select(address => $"http://{address}:{_appliedPort}/#pair={token}")
            .ToArray();
        QrImage = PairingUrls.Count > 0 ? RenderQr(PairingUrls[0]) : null;
        OnPropertyChanged(nameof(PairingExpiryText));
    }

    private void ClearPairing()
    {
        PairingToken = null;
        PairingTokenExpiresAt = null;
        PairingUrls = [];
        QrImage = null;
        OnPropertyChanged(nameof(PairingToken));
        OnPropertyChanged(nameof(PairingTokenExpiresAt));
        OnPropertyChanged(nameof(PairingExpiryText));
    }

    private static IImage? RenderQr(string payload)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // A failed render leaves the text URL as the pairing fallback.
            return null;
        }
    }

    private static IEnumerable<System.Net.IPAddress> EnumeratePrivateIPv4Addresses()
    {
        var seen = new HashSet<System.Net.IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || !IsPrivateIPv4(ip) || !seen.Add(ip))
                {
                    continue;
                }
                yield return ip;
            }
        }
    }

    private static bool IsPrivateIPv4(System.Net.IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}
