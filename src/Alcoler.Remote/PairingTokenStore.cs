namespace Alcoler.Remote;

internal sealed class PairingTokenStore
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly string? _connectionCode;
    private readonly List<(string Token, DateTimeOffset ExpiresAt)> _outstanding = [];

    public PairingTokenStore(string? connectionCode = null)
    {
        if (!string.IsNullOrEmpty(connectionCode) && !RemoteConnectionCode.IsValid(connectionCode))
            throw new ArgumentException(
                "Connection code must contain between 8 and 64 ASCII letters, digits, '-' or '_'.",
                nameof(connectionCode));
        _connectionCode = string.IsNullOrEmpty(connectionCode) ? null : connectionCode;
    }

    public DateTimeOffset? ExpiresAt
    {
        get
        {
            lock (_lock)
            {
                return _outstanding.Count > 0 ? _outstanding[^1].ExpiresAt : null;
            }
        }
    }

    public string Issue(DateTimeOffset now)
    {
        if (_connectionCode is not null)
            return _connectionCode;
        var token = RemoteConnectionCode.Generate();
        lock (_lock)
        {
            // Issuing a new code invalidates every prior one; only the latest is redeemable.
            _outstanding.Clear();
            _outstanding.Add((token, now.Add(TimeToLive)));
        }
        return token;
    }

    public bool TryRedeem(string token, DateTimeOffset now)
    {
        if (token.Length == 0)
            return false;
        if (_connectionCode is not null)
            return TokenHasher.FixedTimeTextEquals(token, _connectionCode);
        lock (_lock)
        {
            _outstanding.RemoveAll(entry => entry.ExpiresAt <= now);
            for (var i = 0; i < _outstanding.Count; i++)
            {
                if (!TokenHasher.FixedTimeTextEquals(token, _outstanding[i].Token))
                    continue;
                _outstanding.RemoveAt(i);
                return true;
            }
            return false;
        }
    }
}
