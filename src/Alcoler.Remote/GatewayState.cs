namespace Alcoler.Remote;

internal sealed class GatewayState(
    RemoteGatewayOptions options,
    DeviceRegistry devices,
    PairingTokenStore pairingTokens,
    AuditLog audit,
    FailureRateLimiter pairRateLimit)
{
    private const int MaxPairFailuresPerMinute = 10;

    public RemoteGatewayOptions Options { get; } = options;
    public DeviceRegistry Devices { get; } = devices;
    public PairingTokenStore PairingTokens { get; } = pairingTokens;
    public AuditLog Audit { get; } = audit;
    public FailureRateLimiter PairRateLimit { get; } = pairRateLimit;

    public static GatewayState Create(RemoteGatewayOptions options)
    {
        var pairingTokens = new PairingTokenStore(options.ConnectionCode);
        var dataDirectory = options.DataDirectory ?? DefaultDataDirectory();
        return new GatewayState(
            options,
            DeviceRegistry.Load(dataDirectory),
            pairingTokens,
            new AuditLog(dataDirectory),
            new FailureRateLimiter(MaxPairFailuresPerMinute, TimeSpan.FromMinutes(1)));
    }

    internal static string DefaultDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alcoler",
            "remote");
}
