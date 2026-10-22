using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Alcoler.Auth;

/// <summary>
/// Protocol constants and wire logic for the official Sign in with ChatGPT (SIWC)
/// token-sharing channel. Endpoints and constraints follow the published OpenAI
/// docs; the channel only serves the standard platform API, never ChatGPT's
/// backend-api, and the agent name hint must identify this app honestly.
/// </summary>
internal static class SiwcProtocol
{
    public const string AuthorizeUrl = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenUrl = "https://auth.openai.com/api/accounts/oauth/token";
    public const string DiscoveryUrl = "https://auth.openai.com/.well-known/openid-configuration";
    public const string Issuer = "https://auth.openai.com";
    public const string Resource = "https://api.openai.com/v1";
    public const string DynamicClientId = "dynamic_agent_client";
    public const string AgentNameHint = "Alcoler";
    public const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    public const string DirectTokenScope = "chatgpt.tokens.use.direct";
    public const string CallbackHost = "127.0.0.1";
    public const string CallbackPath = "/auth/callback";
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);

    private static string Base64Url(byte[] data) => System.Buffers.Text.Base64Url.EncodeToString(data);

    private static byte[] Base64UrlDecode(string value) => System.Buffers.Text.Base64Url.DecodeFromChars(value);

    public static SiwcPkce CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new SiwcPkce(verifier, challenge);
    }

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string CreateNonce() => Base64Url(RandomNumberGenerator.GetBytes(24));

    // The host id is a per-install opaque identifier the server requires to stay
    // stable across sign-ins; it is not a credential.
    public static string CreateHostId() => $"urn:uuid:{Guid.NewGuid():D}";

    public static string BuildAuthorizeUrl(SiwcAuthorizeInput input)
    {
        var registration = input.IssuedClientId is null;
        var query = new StringBuilder()
            .Append("response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(registration ? DynamicClientId : input.IssuedClientId!))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(input.CallbackUrl))
            .Append("&scope=").Append(Uri.EscapeDataString(Scope))
            .Append("&state=").Append(Uri.EscapeDataString(input.State))
            .Append("&nonce=").Append(Uri.EscapeDataString(input.Nonce))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(input.Pkce.CodeChallenge))
            .Append("&code_challenge_method=S256")
            .Append("&resource=").Append(Uri.EscapeDataString(Resource));
        // Registration-only parameters announce the agent identity; later sign-ins
        // must reuse the issued client id without them.
        if (registration)
        {
            query.Append("&agent_name_hint=").Append(Uri.EscapeDataString(AgentNameHint));
            query.Append("&ext_agent_host_id=").Append(Uri.EscapeDataString(input.HostId));
        }
        if (input.LoginHint is { } loginHint)
            query.Append("&login_hint=").Append(Uri.EscapeDataString(loginHint));
        return $"{AuthorizeUrl}?{query}";
    }

    /// <summary>
    /// Parses the loopback callback. The listener has already validated state and
    /// access_denied; here the SIWC-specific rule is that a registration callback
    /// must carry the issued client id and a re-authorization must not swap it.
    /// </summary>
    internal static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => pair[0], pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "", StringComparer.Ordinal);

    public static SiwcCallbackResult ParseCallbackUrl(string url, string? savedIssuedClientId)
    {
        var parsed = new Uri(url);
        var parameters = ParseQuery(parsed);
        var code = parameters.GetValueOrDefault("code") ?? parameters.GetValueOrDefault("authCode") ?? "";
        if (code.Length == 0)
            throw new SiwcProtocolException("missing-code", "SIWC callback is missing an authorization code.");
        var clientId = parameters.GetValueOrDefault("client_id") ?? "";
        if (savedIssuedClientId is null)
        {
            if (clientId.Length == 0)
                throw new SiwcProtocolException("missing-issued-client-id", "SIWC registration callback is missing the issued client id.");
            return new SiwcCallbackResult(code, clientId);
        }
        if (clientId.Length > 0 && clientId != savedIssuedClientId)
            throw new SiwcProtocolException("client-id-mismatch", "SIWC callback returned a different client id than the one issued for this host.");
        return new SiwcCallbackResult(code, clientId.Length > 0 ? clientId : null);
    }

    public static async Task<SiwcTokenResponse> ExchangeCodeAsync(HttpClient http, SiwcCodeExchange exchange, CancellationToken ct)
    {
        return await PostTokenRequestAsync(http, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = exchange.Code,
            ["client_id"] = exchange.ClientId,
            ["code_verifier"] = exchange.CodeVerifier,
            ["redirect_uri"] = exchange.RedirectUri,
            ["resource"] = Resource,
        }, ct);
    }

    // The refresh grant must omit scope so the original grant is retained.
    public static async Task<SiwcTokenResponse> RefreshTokensAsync(HttpClient http, string clientId, string refreshToken, CancellationToken ct)
    {
        return await PostTokenRequestAsync(http, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
            ["resource"] = Resource,
        }, ct);
    }

    private static async Task<SiwcTokenResponse> PostTokenRequestAsync(HttpClient http, Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SiwcNetworkException("SIWC token request failed.", ex);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonDocument? document = null;
            try
            {
                document = text.Length > 0 ? JsonDocument.Parse(text) : null;
            }
            catch (JsonException) { }
            if (!response.IsSuccessStatusCode)
            {
                var error = document?.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                throw new SiwcOAuthException(error is { Length: > 0 } ? error : "http_error",
                    $"SIWC token endpoint returned {(int)response.StatusCode}.", (int)response.StatusCode);
            }
            if (document?.RootElement.ValueKind is not JsonValueKind.Object)
                throw new SiwcProtocolException("invalid_response", "SIWC token endpoint returned an invalid body.");
            var root = document.RootElement;
            var accessToken = ReadString(root, "access_token");
            if (string.IsNullOrEmpty(accessToken))
                throw new SiwcProtocolException("invalid_response", "SIWC token response is missing access_token.");
            return new SiwcTokenResponse(
                accessToken!,
                ReadString(root, "refresh_token"),
                root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.ValueKind == JsonValueKind.Number
                    && expiresIn.TryGetInt64(out var seconds) && seconds >= 0 ? (int)Math.Min(seconds, int.MaxValue) : null,
                ReadString(root, "id_token"),
                ReadString(root, "scope") ?? "");
        }
    }

    public static async Task<SiwcDiscovery> FetchDiscoveryAsync(HttpClient http, CancellationToken ct)
    {
        string text;
        try
        {
            using var response = await http.GetAsync(DiscoveryUrl, ct);
            if (!response.IsSuccessStatusCode)
                throw new SiwcOAuthException("http_error", $"SIWC discovery returned {(int)response.StatusCode}.", (int)response.StatusCode);
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SiwcOAuthException)
        {
            throw new SiwcNetworkException("SIWC discovery request failed.", ex);
        }
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement
            : throw new SiwcProtocolException("invalid_response", "SIWC discovery returned an invalid body.");
        var issuer = ReadString(root, "issuer");
        if (issuer != Issuer)
            throw new SiwcProtocolException("invalid_response", "SIWC discovery returned an unexpected issuer.");
        var jwksUri = ReadString(root, "jwks_uri");
        if (jwksUri is null || !jwksUri.StartsWith("https://", StringComparison.Ordinal))
            throw new SiwcProtocolException("invalid_response", "SIWC discovery returned an invalid jwks_uri.");
        return new SiwcDiscovery(issuer, jwksUri, ReadString(root, "revocation_endpoint"));
    }

    /// <summary>
    /// Verifies the id_token signature against the published JWKS and validates
    /// issuer, audience, expiry and nonce. Verification failure must fail the
    /// sign-in; the token must never be trusted by payload decoding alone.
    /// </summary>
    public static async Task<SiwcIdTokenClaims> VerifyIdTokenAsync(
        HttpClient http, SiwcDiscovery discovery, string idToken, string issuedClientId, string nonce, CancellationToken ct)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
            throw new SiwcIdTokenVerificationException("SIWC id_token is not a JWS compact token.");
        JsonDocument headerDocument;
        JsonDocument payloadDocument;
        try
        {
            headerDocument = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            payloadDocument = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new SiwcIdTokenVerificationException("SIWC id_token segments are not valid JSON.");
        }
        using (headerDocument)
        using (payloadDocument)
        {
            var header = headerDocument.RootElement;
            var payload = payloadDocument.RootElement;
            var algorithm = ReadString(header, "alg");
            var keyId = ReadString(header, "kid");
            if (keyId is null || keyId.Length == 0)
                throw new SiwcIdTokenVerificationException("SIWC id_token header is missing kid.");
            var signature = Base64UrlDecode(parts[2]);
            var signedContent = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            var key = await FindJwksVerificationKeyAsync(http, discovery, keyId, algorithm, ct);
            if (!VerifyJwsSignature(key, algorithm, signedContent, signature))
                throw new SiwcIdTokenVerificationException("SIWC id_token signature verification failed.");
            if (ReadString(payload, "iss") != Issuer)
                throw new SiwcIdTokenVerificationException("SIWC id_token issuer mismatch.");
            if (!AudienceMatches(payload, issuedClientId))
                throw new SiwcIdTokenVerificationException("SIWC id_token audience mismatch.");
            if (!payload.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expiresAt))
                throw new SiwcIdTokenVerificationException("SIWC id_token is missing exp.");
            if (expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60)
                throw new SiwcIdTokenVerificationException("SIWC id_token is expired.");
            if (ReadString(payload, "nonce") != nonce)
                throw new SiwcIdTokenVerificationException("SIWC id_token nonce mismatch.");
            var sub = ReadString(payload, "sub");
            if (string.IsNullOrEmpty(sub))
                throw new SiwcIdTokenVerificationException("SIWC id_token is missing sub.");
            string? accountId = null;
            string? planType = null;
            if (payload.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
            {
                accountId = ReadString(auth, "chatgpt_account_id");
                planType = ReadString(auth, "chatgpt_plan_type");
            }
            return new SiwcIdTokenClaims(
                sub!,
                ReadString(payload, "email"),
                payload.TryGetProperty("email_verified", out var verified) && verified.ValueKind == JsonValueKind.True,
                ReadString(payload, "name"),
                accountId,
                planType);
        }
    }

    private static async Task<JsonElement> FindJwksVerificationKeyAsync(HttpClient http, SiwcDiscovery discovery, string keyId, string? algorithm, CancellationToken ct)
    {
        string text;
        try
        {
            using var response = await http.GetAsync(discovery.JwksUri, ct);
            if (!response.IsSuccessStatusCode)
                throw new SiwcIdTokenVerificationException($"SIWC JWKS returned {(int)response.StatusCode}.");
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SiwcIdTokenVerificationException)
        {
            throw new SiwcIdTokenVerificationException("SIWC JWKS request failed.", ex);
        }
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            throw new SiwcIdTokenVerificationException("SIWC JWKS returned an invalid body.");
        foreach (var key in keys.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object || ReadString(key, "kid") != keyId) continue;
            if (ReadString(key, "use") is { } use && use != "sig") continue;
            if (algorithm == "RS256" && ReadString(key, "kty") != "RSA") continue;
            if (algorithm is "ES256" or "ES384" or "ES512" && ReadString(key, "kty") != "EC") continue;
            return key.Clone();
        }
        throw new SiwcIdTokenVerificationException("SIWC JWKS does not contain the id_token signing key.");
    }

    private static bool VerifyJwsSignature(JsonElement key, string? algorithm, byte[] signedContent, byte[] signature)
    {
        var kty = ReadString(key, "kty");
        try
        {
            if (algorithm == "RS256" && kty == "RSA")
            {
                using var rsa = RSA.Create(new RSAParameters
                {
                    Modulus = Base64UrlDecode(RequiredString(key, "n")),
                    Exponent = Base64UrlDecode(RequiredString(key, "e")),
                });
                return rsa.VerifyData(signedContent, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            if (algorithm is ("ES256" or "ES384" or "ES512") && kty == "EC")
            {
                var curve = ReadString(key, "crv") switch
                {
                    "P-256" => ECCurve.NamedCurves.nistP256,
                    "P-384" => ECCurve.NamedCurves.nistP384,
                    "P-521" => ECCurve.NamedCurves.nistP521,
                    _ => throw new SiwcIdTokenVerificationException($"SIWC id_token uses unsupported curve '{ReadString(key, "crv")}'."),
                };
                using var ecdsa = ECDsa.Create(new ECParameters
                {
                    Curve = curve,
                    Q = new ECPoint
                    {
                        X = Base64UrlDecode(RequiredString(key, "x")),
                        Y = Base64UrlDecode(RequiredString(key, "y")),
                    },
                });
                var hash = algorithm switch
                {
                    "ES256" => HashAlgorithmName.SHA256,
                    "ES384" => HashAlgorithmName.SHA384,
                    _ => HashAlgorithmName.SHA512,
                };
                // JWS signatures are raw R||S, not DER sequences.
                return ecdsa.VerifyData(signedContent, signature, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
        }
        catch (SiwcIdTokenVerificationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            throw new SiwcIdTokenVerificationException("SIWC JWKS key material is invalid.");
        }
        throw new SiwcIdTokenVerificationException($"SIWC id_token uses unsupported algorithm '{algorithm}'.");
    }

    private static bool AudienceMatches(JsonElement payload, string issuedClientId)
    {
        if (!payload.TryGetProperty("aud", out var audience)) return false;
        if (audience.ValueKind == JsonValueKind.String) return audience.GetString() == issuedClientId;
        if (audience.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in audience.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() == issuedClientId)
                return true;
        return false;
    }

    private static string RequiredString(JsonElement element, string property) =>
        ReadString(element, property) ?? throw new SiwcIdTokenVerificationException($"SIWC JWKS key is missing '{property}'.");

    public static bool HasDirectTokenScope(string scope) =>
        scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(DirectTokenScope);

    /// <summary>
    /// Fetches the plan model catalog. The account channel returns a models array
    /// whose visibility=="list" entries are the plan-usable slugs, in server order.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FetchModelCatalogAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        string text;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Resource}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new SiwcOAuthException("http_error", $"SIWC model catalog returned {(int)response.StatusCode}.", (int)response.StatusCode);
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SiwcOAuthException)
        {
            throw new SiwcNetworkException("SIWC model catalog request failed.", ex);
        }
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new SiwcProtocolException("invalid_response", "SIWC model catalog is missing the models array.");
        List<string> slugs = [];
        foreach (var entry in models.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var slug = ReadString(entry, "slug");
            if (string.IsNullOrEmpty(slug) || ReadString(entry, "visibility") != "list") continue;
            if (!slugs.Contains(slug)) slugs.Add(slug);
        }
        return slugs;
    }

    // Sign-out revocation: an empty 200 means success, even for already-invalid
    // tokens, so failure here never blocks local credential removal.
    public static async Task RevokeRefreshTokenAsync(HttpClient http, string revocationEndpoint, string refreshToken, string clientId, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync(revocationEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = refreshToken,
                ["token_type_hint"] = "refresh_token",
                ["client_id"] = clientId,
            }), ct);
        }
        catch (Exception)
        {
            // Revocation is best effort; the local record is removed regardless.
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : null;
}

internal sealed record SiwcPkce(string CodeVerifier, string CodeChallenge);

internal sealed record SiwcAuthorizeInput(
    string CallbackUrl,
    string HostId,
    string Nonce,
    SiwcPkce Pkce,
    string State,
    string? IssuedClientId = null,
    string? LoginHint = null);

internal sealed record SiwcCallbackResult(string Code, string? IssuedClientId);

internal sealed record SiwcCodeExchange(string ClientId, string Code, string CodeVerifier, string RedirectUri);

internal sealed record SiwcTokenResponse(
    string AccessToken,
    string? RefreshToken,
    int? ExpiresInSeconds,
    string? IdToken,
    string Scope);

internal sealed record SiwcDiscovery(string Issuer, string JwksUri, string? RevocationEndpoint);

internal sealed record SiwcIdTokenClaims(
    string Sub,
    string? Email,
    bool EmailVerified,
    string? Name,
    string? ChatgptAccountId,
    string? ChatgptPlanType);

internal sealed class SiwcProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class SiwcOAuthException(string oauthErrorCode, string message, int? httpStatus = null) : Exception(message)
{
    public string OAuthErrorCode { get; } = oauthErrorCode;
    public int? HttpStatus { get; } = httpStatus;
}

internal sealed class SiwcNetworkException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class SiwcIdTokenVerificationException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed class SiwcCallbackDeniedException(string oauthError) : Exception($"OAuth authorization was rejected: {oauthError}")
{
    public string OauthError { get; } = oauthError;
}
