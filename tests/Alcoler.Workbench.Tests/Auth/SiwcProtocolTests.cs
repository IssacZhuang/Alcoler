using System.Net;
using System.Security.Cryptography;
using System.Text;
using Alcoler.Workbench.Auth;
using Xunit;
using static Alcoler.Workbench.Auth.SiwcProtocol;

namespace Alcoler.Workbench.Tests.Auth;

public class SiwcProtocolTests
{
    [Fact]
    public void AuthorizeUrl_RegistrationUsesDynamicClientAndAgentIdentity()
    {
        var url = BuildAuthorizeUrl(new SiwcAuthorizeInput(
            "http://127.0.0.1:5000/auth/callback", "urn:uuid:1234", "nonce-1",
            new SiwcPkce("verifier", "challenge"), "state-1"));
        var query = Query(url);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(DynamicClientId, query["client_id"]);
        Assert.Equal("http://127.0.0.1:5000/auth/callback", query["redirect_uri"]);
        Assert.Equal(Scope, query["scope"]);
        Assert.Equal("state-1", query["state"]);
        Assert.Equal("nonce-1", query["nonce"]);
        Assert.Equal("challenge", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Resource, query["resource"]);
        Assert.Equal("Alcoler", query["agent_name_hint"]);
        Assert.Equal("urn:uuid:1234", query["ext_agent_host_id"]);
    }

    [Fact]
    public void AuthorizeUrl_ReauthorizationUsesIssuedClientWithoutRegistrationParams()
    {
        var url = BuildAuthorizeUrl(new SiwcAuthorizeInput(
            "http://127.0.0.1:5000/auth/callback", "urn:uuid:1234", "nonce-1",
            new SiwcPkce("verifier", "challenge"), "state-1",
            IssuedClientId: "oaiapp_issued", LoginHint: "user@example.com"));
        var query = Query(url);
        Assert.Equal("oaiapp_issued", query["client_id"]);
        Assert.Equal("user@example.com", query["login_hint"]);
        Assert.DoesNotContain("agent_name_hint", query);
        Assert.DoesNotContain("ext_agent_host_id", query);
    }

    [Fact]
    public void Pkce_ChallengeIsBase64UrlSha256OfVerifier()
    {
        var pkce = CreatePkce();
        Assert.Equal(
            System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.CodeVerifier))),
            pkce.CodeChallenge);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("authCode")]
    public void ParseCallback_AcceptsBothCodeParameters(string parameter)
    {
        var result = ParseCallbackUrl($"http://127.0.0.1:5/auth/callback?{parameter}=abc&client_id=oaiapp_1", null);
        Assert.Equal("abc", result.Code);
        Assert.Equal("oaiapp_1", result.IssuedClientId);
    }

    [Fact]
    public void ParseCallback_RegistrationWithoutIssuedClientFails()
    {
        Assert.Throws<SiwcProtocolException>(() => ParseCallbackUrl("http://127.0.0.1:5/auth/callback?code=abc", null));
    }

    [Fact]
    public void ParseCallback_SwappedClientIdIsRejected()
    {
        Assert.Throws<SiwcProtocolException>(
            () => ParseCallbackUrl("http://127.0.0.1:5/auth/callback?code=abc&client_id=oaiapp_other", "oaiapp_saved"));
    }

    [Fact]
    public void ParseCallback_MatchingSavedClientIsKept()
    {
        var result = ParseCallbackUrl("http://127.0.0.1:5/auth/callback?code=abc&client_id=oaiapp_saved", "oaiapp_saved");
        Assert.Equal("oaiapp_saved", result.IssuedClientId);
    }

    [Fact]
    public async Task ExchangeCode_PostsFormEncodedGrantWithoutSecret()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(RecordingHandler.Json(
            """{"access_token":"at","refresh_token":"rt","expires_in":3600,"id_token":"id","scope":"openid profile email offline_access resource.invoke chatgpt.tokens.use.direct"}""")));
        var tokens = await ExchangeCodeAsync(new HttpClient(handler), new SiwcCodeExchange(
            "oaiapp_1", "the-code", "the-verifier", "http://127.0.0.1:5/auth/callback"), CancellationToken.None);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(TokenUrl, request.RequestUri!.ToString());
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        var body = Assert.Single(handler.Bodies)!;
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=the-code", body);
        Assert.Contains("client_id=oaiapp_1", body);
        Assert.Contains("code_verifier=the-verifier", body);
        Assert.Contains($"resource={Uri.EscapeDataString(Resource)}", body);
        Assert.DoesNotContain("client_secret", body);
        Assert.Equal("at", tokens.AccessToken);
        Assert.Equal("rt", tokens.RefreshToken);
        Assert.Equal(3600, tokens.ExpiresInSeconds);
        Assert.Equal("id", tokens.IdToken);
        Assert.True(HasDirectTokenScope(tokens.Scope));
    }

    [Fact]
    public async Task Refresh_OmitsScopeAndSendsIssuedClient()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(RecordingHandler.Json("""{"access_token":"at2","scope":"x"}""")));
        await RefreshTokensAsync(new HttpClient(handler), "oaiapp_1", "rt-1", CancellationToken.None);
        var body = Assert.Single(handler.Bodies)!;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=rt-1", body);
        Assert.Contains("client_id=oaiapp_1", body);
        Assert.DoesNotContain("scope=", body);
    }

    [Fact]
    public async Task TokenEndpoint_ErrorSurfacesOAuthCode()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(RecordingHandler.Json(
            """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest)));
        var error = await Assert.ThrowsAsync<SiwcOAuthException>(
            () => RefreshTokensAsync(new HttpClient(handler), "oaiapp_1", "rt", CancellationToken.None));
        Assert.Equal("invalid_grant", error.OAuthErrorCode);
    }

    [Fact]
    public async Task Discovery_RejectsForeignIssuer()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(RecordingHandler.Json(
            """{"issuer":"https://evil.example","jwks_uri":"https://evil.example/jwks"}""")));
        await Assert.ThrowsAsync<SiwcProtocolException>(() => FetchDiscoveryAsync(new HttpClient(handler), CancellationToken.None));
    }

    [Fact]
    public async Task Discovery_ReturnsEndpoints()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(RecordingHandler.Json(
            """{"issuer":"https://auth.openai.com","jwks_uri":"https://auth.openai.com/jwks.json","revocation_endpoint":"https://auth.openai.com/api/accounts/oauth/revoke"}""")));
        var discovery = await FetchDiscoveryAsync(new HttpClient(handler), CancellationToken.None);
        Assert.Equal("https://auth.openai.com/jwks.json", discovery.JwksUri);
        Assert.Equal("https://auth.openai.com/api/accounts/oauth/revoke", discovery.RevocationEndpoint);
    }

    [Fact]
    public async Task VerifyIdToken_AcceptsSignedTokenWithMatchingClaims()
    {
        var (key, jwks) = SiwcTestJwt.CreateRsaJwks();
        var idToken = SiwcTestJwt.CreateIdToken(key, "key-1", "nonce-1");
        RecordingHandler handler = new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("jwks.json")
            ? RecordingHandler.Json(jwks)
            : throw new InvalidOperationException($"unexpected {request.RequestUri}")));
        var claims = await VerifyIdTokenAsync(new HttpClient(handler),
            new SiwcDiscovery(Issuer, "https://auth.openai.com/jwks.json", null), idToken, "oaiapp_test", "nonce-1",
            CancellationToken.None);
        Assert.Equal("user-sub-1", claims.Sub);
        Assert.Equal("user@example.com", claims.Email);
        Assert.Equal("plus", claims.ChatgptPlanType);
    }

    [Fact]
    public async Task VerifyIdToken_RejectsTamperedPayload()
    {
        var (key, jwks) = SiwcTestJwt.CreateRsaJwks();
        var idToken = SiwcTestJwt.CreateIdToken(key, "key-1", "nonce-1");
        var parts = idToken.Split('.');
        var tampered = $"{parts[0]}.{System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"iss":"https://auth.openai.com"}"""))}.{parts[2]}";
        RecordingHandler handler = new((_, _) => Task.FromResult(RecordingHandler.Json(jwks)));
        await Assert.ThrowsAsync<SiwcIdTokenVerificationException>(() => VerifyIdTokenAsync(
            new HttpClient(handler), new SiwcDiscovery(Issuer, "https://auth.openai.com/jwks.json", null), tampered,
            "oaiapp_test", "nonce-1", CancellationToken.None));
    }

    [Fact]
    public async Task VerifyIdToken_RejectsNonceMismatch()
    {
        var (key, jwks) = SiwcTestJwt.CreateRsaJwks();
        var idToken = SiwcTestJwt.CreateIdToken(key, "key-1", "nonce-1");
        RecordingHandler handler = new((_, _) => Task.FromResult(RecordingHandler.Json(jwks)));
        await Assert.ThrowsAsync<SiwcIdTokenVerificationException>(() => VerifyIdTokenAsync(
            new HttpClient(handler), new SiwcDiscovery(Issuer, "https://auth.openai.com/jwks.json", null), idToken,
            "oaiapp_test", "nonce-other", CancellationToken.None));
    }

    [Fact]
    public async Task VerifyIdToken_RejectsWrongAudience()
    {
        var (key, jwks) = SiwcTestJwt.CreateRsaJwks();
        var idToken = SiwcTestJwt.CreateIdToken(key, "key-1", "nonce-1", audience: "oaiapp_other");
        RecordingHandler handler = new((_, _) => Task.FromResult(RecordingHandler.Json(jwks)));
        await Assert.ThrowsAsync<SiwcIdTokenVerificationException>(() => VerifyIdTokenAsync(
            new HttpClient(handler), new SiwcDiscovery(Issuer, "https://auth.openai.com/jwks.json", null), idToken,
            "oaiapp_test", "nonce-1", CancellationToken.None));
    }

    [Fact]
    public async Task ModelCatalog_KeepsListVisibilityInServerOrder()
    {
        RecordingHandler handler = new((_, _) => Task.FromResult(RecordingHandler.Json(
            """{"models":[{"slug":"gpt-a","visibility":"list","display_name":"A"},{"slug":"gpt-hidden","visibility":"hidden"},{"slug":"gpt-b","visibility":"list"},{"slug":"gpt-a","visibility":"list"}]}""")));
        var slugs = await FetchModelCatalogAsync(new HttpClient(handler), "token", CancellationToken.None);
        Assert.Equal(["gpt-a", "gpt-b"], slugs);
        Assert.Equal(Resource + "/models", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    private static Dictionary<string, string> Query(string url) => ParseQuery(new Uri(url));
}
