using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Alcoler.Workbench.Tests.Auth;

internal sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string?> Bodies { get; } = [];

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        return await Respond(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

internal static class SiwcTestJwt
{
    public static (RSA Key, string JwksJson) CreateRsaJwks(string keyId = "key-1")
    {
        var key = RSA.Create(2048);
        var parameters = key.ExportParameters(false);
        return (key, $$"""
            {"keys":[{"kty":"RSA","kid":"{{keyId}}","use":"sig","alg":"RS256","n":"{{B64(parameters.Modulus!)}}","e":"{{B64(parameters.Exponent!)}}"}]}
            """);
    }

    public static string CreateIdToken(
        RSA key,
        string keyId,
        string nonce,
        string issuer = "https://auth.openai.com",
        string audience = "oaiapp_test",
        string sub = "user-sub-1",
        DateTimeOffset? expiry = null)
    {
        var header = JsonSerializer.Serialize(new { alg = "RS256", kid = keyId, typ = "JWT" });
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["sub"] = sub,
            ["nonce"] = nonce,
            ["exp"] = (expiry ?? DateTimeOffset.UtcNow.AddMinutes(5)).ToUnixTimeSeconds(),
            ["email"] = "user@example.com",
            ["email_verified"] = true,
            ["name"] = "Test User",
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "acc_1",
                ["chatgpt_plan_type"] = "plus",
            },
        });
        var signingInput = $"{B64(Encoding.ASCII.GetBytes(header))}.{B64(Encoding.ASCII.GetBytes(payload))}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{B64(signature)}";
    }

    private static string B64(byte[] data) => System.Buffers.Text.Base64Url.EncodeToString(data);
}
