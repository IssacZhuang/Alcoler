using System.Net;
using System.Text.Json;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("pairing-gateway")]
public sealed class PairingTests
{
    private readonly GatewayHarness _harness;

    public PairingTests(GatewayHarness harness) => _harness = harness;

    [Fact]
    public async Task Pair_WithInvalidToken_IsUnauthorized()
    {
        using var response = await _harness.PostJsonAsync(
            "api/pair", GatewayHarness.Serialize(new { pairingToken = "WRONGTKN" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.Equal("unauthorized", document.RootElement.GetProperty("error").GetString());
        Assert.Equal("pairing_token", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Pair_WithIssuedToken_ReturnsDeviceCredentialsWithoutHash()
    {
        var pairingToken = _harness.Gateway.GeneratePairingToken();

        using var response = await _harness.PostJsonAsync(
            "api/pair", GatewayHarness.Serialize(new { pairingToken, deviceName = "test-phone" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("deviceId").GetString()));
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("deviceToken").GetString()));
        Assert.DoesNotContain("tokenHash", body, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenHasher.HashText(pairingToken), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pair_IssuedToken_IsSingleUse()
    {
        var pairingToken = _harness.Gateway.GeneratePairingToken();
        var body = GatewayHarness.Serialize(new { pairingToken });

        using var first = await _harness.PostJsonAsync("api/pair", body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await _harness.PostJsonAsync("api/pair", body);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Pair_DeviceRegistryOnDisk_StoresHashesOnly()
    {
        var (deviceId, deviceToken) = await _harness.PairAsync();

        var path = Path.Combine(_harness.DataDirectory, "devices.json");
        Assert.True(File.Exists(path), $"expected {path} to exist");
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains(deviceId, content, StringComparison.Ordinal);
        Assert.Contains("tokenHash", content, StringComparison.Ordinal);
        Assert.DoesNotContain(deviceToken, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pair_MoreThanTenFailuresWithinAMinute_IsRateLimited()
    {
        var harness = new GatewayHarness();
        await harness.InitializeAsync();
        try
        {
            var body = GatewayHarness.Serialize(new { pairingToken = "WRONGTKN" });
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var response = await harness.PostJsonAsync("api/pair", body);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using var blocked = await harness.PostJsonAsync("api/pair", body);
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            using var document = await blocked.ReadJsonAsync();
            Assert.Equal("rate_limited", document.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }
}
