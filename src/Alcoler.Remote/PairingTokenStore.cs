using System.Security.Cryptography;

namespace Alcoler.Remote;

internal sealed class PairingTokenStore
{
    private const int TokenLength = 8;
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);
    // Crockford base32: no I, L, O or U, so 0/O and 1/I cannot be confused on screen.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly object _lock = new();
    private readonly List<(string Token, DateTimeOffset ExpiresAt)> _outstanding = [];

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
        var token = new string(RandomNumberGenerator.GetItems<char>(Alphabet, TokenLength));
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
