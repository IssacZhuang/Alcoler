using System.Net;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("api-gateway")]
public sealed class AuthenticationTests
{
    private readonly GatewayHarness _harness;

    public AuthenticationTests(GatewayHarness harness) => _harness = harness;

    [Theory]
    [InlineData("api/sessions")]
    [InlineData("api/models")]
    [InlineData("api/slash-catalog")]
    [InlineData("api/sessions/sess_unknown/slash-catalog")]
    public async Task ApiRequest_WithoutToken_IsUnauthorized(string path)
    {
        using var response = await _harness.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = await response.ReadJsonAsync();
        Assert.Equal("unauthorized", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ApiRequest_WithBearerToken_IsAccepted()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.GetWithTokenAsync("api/sessions", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiRequest_WithQueryToken_IsAccepted()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.Client.GetAsync(
            $"api/sessions?token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiRequest_WithRevokedDeviceToken_IsUnauthorized()
    {
        var (deviceId, token) = await _harness.PairAsync();
        using var allowed = await _harness.GetWithTokenAsync("api/sessions", token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        await _harness.Gateway.RevokeDeviceAsync(deviceId);

        using var denied = await _harness.GetWithTokenAsync("api/sessions", token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task InfoEndpoint_IsPublic()
    {
        using var response = await _harness.Client.GetAsync("api/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.Equal("Alcoler", document.RootElement.GetProperty("product").GetString());
        Assert.Equal(GatewayHarness.Version, document.RootElement.GetProperty("version").GetString());
    }
}
