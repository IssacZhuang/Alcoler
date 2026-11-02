using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

// Internal store unit tests: the TTL expiry path is not reachable through the
// HTTP surface, but the store itself is visible to this assembly.
public sealed class PairingTokenStoreTests
{
    [Fact]
    public void IssuedToken_RedeemsOnce()
    {
        var store = new PairingTokenStore();
        var now = DateTimeOffset.UtcNow;
        var token = store.Issue(now);

        Assert.True(store.TryRedeem(token, now.AddSeconds(1)));
        Assert.False(store.TryRedeem(token, now.AddSeconds(2)));
    }

    [Fact]
    public void ExpiredToken_IsNotRedeemable()
    {
        var store = new PairingTokenStore();
        var now = DateTimeOffset.UtcNow;
        var token = store.Issue(now);

        Assert.False(store.TryRedeem(token, now.AddMinutes(11)));
    }

    [Fact]
    public void EmptyToken_IsRejected()
    {
        var store = new PairingTokenStore();

        Assert.False(store.TryRedeem("", DateTimeOffset.UtcNow));
    }
}
