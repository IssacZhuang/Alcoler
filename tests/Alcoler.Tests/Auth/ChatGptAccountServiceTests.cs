using System.Net;
using Alcoler.Auth;
using Xunit;
using static Alcoler.Auth.SiwcProtocol;

namespace Alcoler.Tests.Auth;

public class ChatGptAccountServiceTests : IDisposable
{
    private readonly string _storePath;
    private readonly string _directory;

    public ChatGptAccountServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"alcoler-siwc-service-{Guid.NewGuid():N}");
        _storePath = Path.Combine(_directory, "auth.json");
    }

    [Fact]
    public async Task SignIn_CompletesAndStoresEntitledAccountWithModels()
    {
        string? nonce = null;
        var (key, jwks) = SiwcTestJwt.CreateRsaJwks();
        int tokenCalls = 0;
        using var service = CreateService(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == DiscoveryUrl)
                return DiscoveryResponse();
            if (url.EndsWith("jwks.json"))
                return RecordingHandler.Json(jwks);
            if (url == TokenUrl)
            {
                tokenCalls++;
                var body = request.Content!.ReadAsStringAsync(CancellationToken.None).Result;
                Assert.Contains("grant_type=authorization_code", body);
                Assert.Contains("client_id=oaiapp_1", body);
                Assert.Contains("code=auth-code", body);
                return RecordingHandler.Json($$"""
                    {"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600,
                     "id_token":"{{SiwcTestJwt.CreateIdToken(key, "key-1", nonce ?? throw new InvalidOperationException("nonce missing"), audience: "oaiapp_1")}}",
                     "scope":"openid profile email offline_access resource.invoke {{DirectTokenScope}}"}
                    """);
            }
            if (url == Resource + "/models")
                return RecordingHandler.Json("""{"models":[{"slug":"gpt-plan-a","visibility":"list"},{"slug":"gpt-hidden","visibility":"hidden"}]}""");
            throw new InvalidOperationException($"unexpected {url}");
        });
        var transaction = service.StartSignIn();
        var query = ParseQuery(new Uri(transaction.AuthorizeUrl));
        Assert.Equal(DynamicClientId, query["client_id"]);
        Assert.Equal("Alcoler", query["agent_name_hint"]);
        nonce = query["nonce"];

        await HitCallbackAsync(query["redirect_uri"], $"code=auth-code&state={query["state"]}&client_id=oaiapp_1");
        var outcome = await transaction.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(outcome.Completed);
        Assert.True(outcome.Entitled);
        Assert.Equal("user@example.com", outcome.Account!.Email);
        Assert.Equal("plus", outcome.Account.PlanType);
        Assert.Equal(["gpt-plan-a"], outcome.ModelIds);
        Assert.Equal(1, tokenCalls);

        var status = service.GetStatus();
        Assert.True(status.SignedIn);
        Assert.True(status.Entitled);
        Assert.Equal("access-1", await service.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SignIn_IgnoresUnmatchedStateAndServesDenied()
    {
        using var service = CreateService(_ => throw new InvalidOperationException("no network expected"));
        var transaction = service.StartSignIn();
        var query = ParseQuery(new Uri(transaction.AuthorizeUrl));
        using var http = new HttpClient();
        await HitCallbackAsync(query["redirect_uri"], "code=x&state=wrong-state");
        await HitCallbackAsync(query["redirect_uri"], $"error=access_denied&state={query["state"]}");
        var outcome = await transaction.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(outcome.Completed);
        Assert.Equal(ChatGptSignInFailure.Denied, outcome.Failure);
        Assert.False(service.GetStatus().SignedIn);
    }

    [Fact]
    public async Task SignIn_CancelSettlesAsCancelled()
    {
        using var service = CreateService(_ => throw new InvalidOperationException("no network expected"));
        var transaction = service.StartSignIn();
        transaction.Cancel();
        var outcome = await transaction.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ChatGptSignInFailure.Cancelled, outcome.Failure);
    }

    [Fact]
    public async Task SignIn_ReauthorizationReusesIssuedClientAndLoginHint()
    {
        SeedStore(Record(scope: DirectTokenScope, expiresInSeconds: 3600, email: "user@example.com"));
        string? nonce = null;
        var (key, jwks) = SiwcTestJwt.CreateRsaJwks();
        using var service = CreateService(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == DiscoveryUrl) return DiscoveryResponse();
            if (url.EndsWith("jwks.json")) return RecordingHandler.Json(jwks);
            if (url == TokenUrl)
                return RecordingHandler.Json($$"""
                    {"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600,
                     "id_token":"{{SiwcTestJwt.CreateIdToken(key, "key-1", nonce ?? "x", audience: "oaiapp_1")}}",
                     "scope":"{{DirectTokenScope}}"}
                    """);
            if (url == Resource + "/models") return RecordingHandler.Json("""{"models":[{"slug":"gpt-plan-a","visibility":"list"}]}""");
            throw new InvalidOperationException($"unexpected {url}");
        });
        var transaction = service.StartSignIn();
        var query = ParseQuery(new Uri(transaction.AuthorizeUrl));
        Assert.Equal("oaiapp_1", query["client_id"]);
        Assert.Equal("user@example.com", query["login_hint"]);
        Assert.DoesNotContain("agent_name_hint", query);
        nonce = query["nonce"];
        await HitCallbackAsync(query["redirect_uri"], $"code=code-2&state={query["state"]}");
        var outcome = await transaction.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(outcome.Completed);
        Assert.Equal("access-2", await service.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetAccessToken_RefreshesNearExpiryOnceUnderGate()
    {
        SeedStore(Record(scope: DirectTokenScope, expiresInSeconds: 1));
        int refreshCalls = 0;
        using var service = CreateService(request =>
        {
            Assert.Equal(TokenUrl, request.RequestUri!.ToString());
            Interlocked.Increment(ref refreshCalls);
            return RecordingHandler.Json("""{"access_token":"access-fresh","expires_in":3600}""");
        });
        var tokens = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => service.GetAccessTokenAsync(CancellationToken.None).AsTask()));
        Assert.All(tokens, token => Assert.Equal("access-fresh", token));
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public async Task GetAccessToken_RejectedRefreshDeletesCredentials()
    {
        SeedStore(Record(scope: DirectTokenScope, expiresInSeconds: 1));
        using var service = CreateService(_ => RecordingHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<SiwcAuthRequiredException>(() => service.GetAccessTokenAsync(CancellationToken.None).AsTask());
        Assert.False(service.GetStatus().SignedIn);
    }

    [Fact]
    public async Task GetAccessToken_UnentitledAccountFailsClosed()
    {
        SeedStore(Record(scope: "openid profile email", expiresInSeconds: 3600));
        using var service = CreateService(_ => throw new InvalidOperationException("no network expected"));
        await Assert.ThrowsAsync<SiwcAuthRequiredException>(() => service.GetAccessTokenAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SignOut_RevokesRefreshTokenAndKeepsHostId()
    {
        SeedStore(Record(scope: DirectTokenScope, expiresInSeconds: 3600));
        string? revokedBody = null;
        using var service = CreateService(request =>
        {
            if (request.RequestUri!.ToString() == DiscoveryUrl) return DiscoveryResponse();
            revokedBody = request.Content!.ReadAsStringAsync(CancellationToken.None).Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await service.SignOutAsync();
        Assert.NotNull(revokedBody);
        Assert.Contains("token=refresh-1", revokedBody);
        Assert.Contains("client_id=oaiapp_1", revokedBody);
        Assert.False(service.GetStatus().SignedIn);
    }

    private static HttpResponseMessage DiscoveryResponse() => RecordingHandler.Json(
        $$"""{"issuer":"{{Issuer}}","jwks_uri":"https://auth.openai.com/jwks.json","revocation_endpoint":"https://auth.openai.com/api/accounts/oauth/revoke"}""");

    private static async Task HitCallbackAsync(string redirectUri, string query)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync($"{redirectUri}?{query}");
        // A wrong-state probe answers 400 without settling; the caller checks the
        // outcome, not the status.
        await response.Content.ReadAsStringAsync();
    }

    private ChatGptAccountService CreateService(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(_storePath, new RecordingHandler((request, _) => Task.FromResult(respond(request))));

    private void SeedStore(Alcoler.Auth.SiwcCredentialRecord record) =>
        new Alcoler.Auth.SiwcCredentialStore(_storePath).Save(record);

    private static Alcoler.Auth.SiwcCredentialRecord Record(string scope, int expiresInSeconds, string? email = null) =>
        Alcoler.Auth.SiwcCredentialRecord.FromTokens(
            new SiwcIdTokenClaims("user-sub-1", email, true, "Test User", "acc_1", "plus"),
            "urn:uuid:host-1", "oaiapp_1",
            new SiwcTokenResponse("access-1", "refresh-1", expiresInSeconds, "id-token-1", scope));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
