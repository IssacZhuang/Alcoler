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

// Owns the desktop-side RemoteGateway. The gateway is created lazily on first enable so the
// settings section stays functional while remote control is off; lifecycle failures are
// captured in Error instead of propagating to startup or the UI thread.
public sealed partial class RemoteAccessViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<IAgentRuntime> _runtimeFactory;
    private readonly string _dataDirectory;
    private readonly Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> _artifactLoader;
    private readonly string? _productVersion;
    private RemoteGateway? _gateway;
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
            _qrImage = value;
            OnPropertyChanged();
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
                return Loc.Get("settings_remote_pairing_expires_static");
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
        if (_disposed)
        {
            return;
        }
        var enabled = config?.Enabled == true;
        var port = config?.EffectivePort ?? WorkbenchRemoteConfig.DefaultPort;
        if (!enabled)
        {
            _appliedPort = port;
            OnPropertyChanged(nameof(Port));
            if (_gateway is { IsRunning: true } stopping)
            {
                await RunGatewayOperationAsync(stopping.StopAsync).ConfigureAwait(true);
            }
            RefreshState();
            return;
        }
        if (_gateway is null)
        {
            _appliedPort = port;
            OnPropertyChanged(nameof(Port));
            _gateway = new RemoteGateway(BuildOptions(port));
            await StartAsync(cancellationToken).ConfigureAwait(true);
            RefreshState();
            return;
        }
        if (_appliedPort != port)
        {
            _appliedPort = port;
            OnPropertyChanged(nameof(Port));
            var restarting = _gateway;
            await RunGatewayOperationAsync(() => restarting.RestartAsync(BuildOptions(port))).ConfigureAwait(true);
            if (!restarting.IsRunning)
            {
                await StartAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        else if (!_gateway.IsRunning)
        {
            await StartAsync(cancellationToken).ConfigureAwait(true);
        }
        RefreshState();
    }

    // Re-reads gateway state for the UI; the settings dialog ticks this while its remote
    // section is visible so expiry and last-seen values stay current.
    public void RefreshState()
    {
        if (_disposed)
        {
            return;
        }
        if (_gateway is not { } gateway)
        {
            IsRunning = false;
            Devices = [];
            return;
        }
        try
        {
            IsRunning = gateway.IsRunning;
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = Loc.Get("settings_remote_status_error", exception.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPair))]
    private void GeneratePairingToken()
    {
        if (_gateway is not { IsRunning: true } gateway)
        {
            return;
        }
        PairingToken = gateway.GeneratePairingToken();
        PairingTokenExpiresAt = gateway.PairingTokenExpiresAt;
        RebuildPairingUrls();
    }

    private bool CanPair() => _gateway is { IsRunning: true };

    private void RemoveDevice(RemotePairedDeviceViewModel device) => _ = RevokeDeviceAsync(device);

    private async Task RevokeDeviceAsync(RemotePairedDeviceViewModel device)
    {
        if (_gateway is not { } gateway || _disposed)
        {
            return;
        }
        try
        {
            await gateway.RevokeDeviceAsync(device.Id).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = Loc.Get("settings_remote_revoke_error", exception.Message);
        }
        RefreshState();
    }

    public async ValueTask DisposeAsync()
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
                await gateway.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutdown must proceed even if the listener is already gone.
            }
        }
        IsRunning = false;
    }

    private RemoteGatewayOptions BuildOptions(int port) => new()
    {
        Runtime = _runtimeFactory,
        Port = port,
        DataDirectory = _dataDirectory,
        ArtifactLoader = _artifactLoader,
        ProductVersion = _productVersion,
    };

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_gateway is not { } gateway)
        {
            return;
        }
        try
        {
            await gateway.StartAsync(cancellationToken).ConfigureAwait(true);
            Error = null;
            Console.WriteLine($"remote gateway listening on port {_appliedPort}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
            Console.WriteLine($"remote gateway failed to start on port {_appliedPort}: {exception}");
        }
    }

    private async Task RunGatewayOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            Console.WriteLine($"remote gateway operation failed: {exception}");
        }
    }

    private void RebuildPairingUrls()
    {
        if (PairingToken is null || _gateway is not { IsRunning: true })
        {
            ClearPairing();
            return;
        }
        var token = PairingToken;
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
        OnPropertyChanged(nameof(PairingUrl));
        OnPropertyChanged(nameof(PairingExpiryText));
    }

    private static IImage? RenderQr(string payload)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            return new Bitmap(new MemoryStream(png));
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
