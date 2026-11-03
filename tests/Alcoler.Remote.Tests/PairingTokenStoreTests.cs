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

    [Fact]
    public void IssuedToken_ExpiresAfterTenMinutes()
    {
        var store = new PairingTokenStore();
        var now = DateTimeOffset.UtcNow;
        var token = store.Issue(now);

        Assert.Equal(now.AddMinutes(10), store.ExpiresAt);
        Assert.False(store.TryRedeem(token, now.AddMinutes(10)));
    }

    [Fact]
    public void IssueNewToken_InvalidatesPreviousToken()
    {
        var store = new PairingTokenStore();
        var now = DateTimeOffset.UtcNow;
        var previous = store.Issue(now);
        var current = store.Issue(now.AddSeconds(1));

        Assert.False(store.TryRedeem(previous, now.AddSeconds(2)));
        Assert.True(store.TryRedeem(current, now.AddSeconds(2)));
    }

    [Fact]
    public void ConfiguredCode_IsReusableBeforeIssueAndDoesNotExpire()
    {
        const string code = "My-custom_code123";
        var store = new PairingTokenStore(code);
        var now = DateTimeOffset.UtcNow;

        Assert.Null(store.ExpiresAt);
        Assert.True(store.TryRedeem(code, now));
        Assert.True(store.TryRedeem(code, now.AddMinutes(11)));
        Assert.Equal(code, store.Issue(now.AddYears(1)));
        Assert.True(store.TryRedeem(code, now.AddYears(2)));
        Assert.Null(store.ExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UnconfiguredCode_RetainsSingleUseTokenFlow(string? code)
    {
        var store = new PairingTokenStore(code);
        var now = DateTimeOffset.UtcNow;

        Assert.False(store.TryRedeem("", now));
        var token = store.Issue(now);
        Assert.Equal(now.AddMinutes(10), store.ExpiresAt);
        Assert.True(store.TryRedeem(token, now));
        Assert.False(store.TryRedeem(token, now));
    }

    [Fact]
    public void GenerateConnectionCode_UsesExistingEightCharacterAlphabet()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var code = RemoteConnectionCode.Generate();

            Assert.Equal(8, code.Length);
            Assert.All(code, character => Assert.Contains(character, alphabet));
            Assert.True(RemoteConnectionCode.IsValid(code));
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("OnlyText", true)]
    [InlineData("12345678", true)]
    [InlineData("aB09-_cd", true)]
    [InlineData("IilLoOuU", true)]
    [InlineData("_-_-_-_-", true)]
    [InlineData(" spaced1", false)]
    [InlineData("space123 ", false)]
    [InlineData("newline1\n", false)]
    [InlineData("tabs123\t", false)]
    [InlineData("bad/code", false)]
    [InlineData("bad.code", false)]
    [InlineData("bad+code", false)]
    [InlineData("bad=code", false)]
    [InlineData("abcdefg\u00e9", false)]
    [InlineData("digits0\u0661", false)]
    [InlineData("width12\uFF21", false)]
    public void IsValidConnectionCode_RequiresAsciiLettersDigitsHyphenOrUnderscore(string? code, bool expected)
    {
        Assert.Equal(expected, RemoteConnectionCode.IsValid(code));
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void IsValidConnectionCode_RequiresEightToSixtyFourCharacters(int length, bool expected)
    {
        Assert.Equal(expected, RemoteConnectionCode.IsValid(new string('a', length)));
    }
}
