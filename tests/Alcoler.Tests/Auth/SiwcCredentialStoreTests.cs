using Alcoler.Auth;
using Xunit;
using static Alcoler.Auth.SiwcProtocol;

namespace Alcoler.Tests.Auth;

public class SiwcCredentialStoreTests : IDisposable
{
    private readonly string _path;

    public SiwcCredentialStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"alcoler-siwc-store-{Guid.NewGuid():N}", "auth.json");
    }

    [Fact]
    public void HostId_IsStableAcrossInstancesAndSignOut()
    {
        var store = new Alcoler.Auth.SiwcCredentialStore(_path);
        var hostId = store.ResolveOrCreateHostId();
        Assert.StartsWith("urn:uuid:", hostId);
        Assert.Equal(hostId, new Alcoler.Auth.SiwcCredentialStore(_path).ResolveOrCreateHostId());
        store.DeleteChatGpt();
        Assert.Equal(hostId, new Alcoler.Auth.SiwcCredentialStore(_path).ResolveOrCreateHostId());
    }

    [Fact]
    public void CredentialRecord_RoundTripsAndKeepsTokensOutOfConfigShape()
    {
        var store = new Alcoler.Auth.SiwcCredentialStore(_path);
        store.Save(Record(scope: "openid profile " + DirectTokenScope, expiresInSeconds: 3600));
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("oaiapp_1", loaded!.IssuedClientId);
        Assert.Equal("access-1", loaded.AccessToken);
        Assert.Equal("refresh-1", loaded.RefreshToken);
        Assert.True(loaded.IsEntitled);
        Assert.False(loaded.IsNearExpiry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        if (OperatingSystem.IsWindows())
        {
            // The token payload must not be readable in the file: DPAPI protection.
            Assert.DoesNotContain("access-1", File.ReadAllText(_path));
        }
    }

    [Fact]
    public void FromTokens_RefreshCarriesForwardRotatableFields()
    {
        var original = Record(scope: DirectTokenScope, expiresInSeconds: 1);
        var refreshed = Alcoler.Auth.SiwcCredentialRecord.FromTokens(null, original.HostId, original.IssuedClientId,
            new SiwcTokenResponse("access-2", null, null, null, ""), previous: original);
        Assert.Equal("refresh-1", refreshed.RefreshToken);
        Assert.Equal(DirectTokenScope, refreshed.Scope);
        Assert.Equal(original.Account, refreshed.Account);
        Assert.Null(refreshed.ExpiresAtMs);
        Assert.True(refreshed.IsNearExpiry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void NearExpiry_UsesThreeMinuteMargin()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fresh = Record(scope: DirectTokenScope, expiresInSeconds: 301);
        var stale = Record(scope: DirectTokenScope, expiresInSeconds: 60);
        Assert.False(fresh.IsNearExpiry(now));
        Assert.True(stale.IsNearExpiry(now));
    }

    [Fact]
    public void Delete_RemovesCredentialsOnly()
    {
        var store = new Alcoler.Auth.SiwcCredentialStore(_path);
        var hostId = store.ResolveOrCreateHostId();
        store.Save(Record(scope: DirectTokenScope, expiresInSeconds: 3600));
        store.DeleteChatGpt();
        Assert.Null(store.Load());
        Assert.Equal(hostId, store.ResolveOrCreateHostId());
    }

    private Alcoler.Auth.SiwcCredentialRecord Record(string scope, int expiresInSeconds) =>
        Alcoler.Auth.SiwcCredentialRecord.FromTokens(
            new SiwcIdTokenClaims("user-sub-1", "user@example.com", true, "Test User", "acc_1", "plus"),
            "urn:uuid:host-1", "oaiapp_1",
            new SiwcTokenResponse("access-1", "refresh-1", expiresInSeconds, "id-token-1", scope));

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is not null)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }
}
