using System.Net;
using System.Net.Sockets;
using System.Text;
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

public sealed class ConnectionCodePairingTests
{
    private const string ConnectionCode = "My-custom_code123";

    [Fact]
    public async Task Pair_ConfiguredCode_IsReusableAndExposedWithoutExpiry()
    {
        var harness = new GatewayHarness { ConnectionCode = ConnectionCode };
        try
        {
            await harness.InitializeAsync();
            var first = await PairWithCodeAsync(harness, ConnectionCode);
            var second = await PairWithCodeAsync(harness, ConnectionCode);

            Assert.NotEqual(first.DeviceId, second.DeviceId);
            Assert.NotEqual(first.DeviceToken, second.DeviceToken);
            Assert.NotEqual(ConnectionCode, first.DeviceToken);
            Assert.Equal(ConnectionCode, harness.Gateway.GeneratePairingToken());
            Assert.Equal(ConnectionCode, harness.Gateway.GeneratePairingToken());
            Assert.Null(harness.Gateway.PairingTokenExpiresAt);
            using var firstAllowed = await harness.GetWithTokenAsync("api/sessions", first.DeviceToken);
            using var secondAllowed = await harness.GetWithTokenAsync("api/sessions", second.DeviceToken);
            using var codeDenied = await harness.GetWithTokenAsync("api/sessions", ConnectionCode);
            Assert.Equal(HttpStatusCode.OK, firstAllowed.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondAllowed.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, codeDenied.StatusCode);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pair_ConfiguredCode_RejectsWrongEmptyMissingAndCaseChangedCodes()
    {
        var harness = new GatewayHarness { ConnectionCode = ConnectionCode };
        try
        {
            await harness.InitializeAsync();
            string?[] wrongCodes = ["WRONGTKN", "", null, ConnectionCode.ToLowerInvariant(), " " + ConnectionCode];
            foreach (var pairingToken in wrongCodes)
            {
                using var response = await harness.PostJsonAsync(
                    "api/pair", GatewayHarness.Serialize(new { pairingToken }));
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            await PairWithCodeAsync(harness, ConnectionCode);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pair_ReplacingCodeAndRestarting_InvalidatesOldCodeButKeepsDeviceCredentials()
    {
        const string replacement = "Replacement-code_456";
        var harness = new GatewayHarness { ConnectionCode = ConnectionCode };
        try
        {
            await harness.InitializeAsync();
            var originalDevice = await PairWithCodeAsync(harness, ConnectionCode);

            await harness.Gateway.RestartAsync(new RemoteGatewayOptions
            {
                Runtime = () => harness.Runtime,
                Port = new Uri(harness.BaseAddress).Port,
                DataDirectory = harness.DataDirectory,
                ConnectionCode = replacement,
            });

            Assert.True(harness.Gateway.IsRunning);
            Assert.Equal(replacement, harness.Gateway.GeneratePairingToken());
            Assert.Null(harness.Gateway.PairingTokenExpiresAt);
            using var oldCode = await harness.PostJsonAsync(
                "api/pair", GatewayHarness.Serialize(new { pairingToken = ConnectionCode }));
            Assert.Equal(HttpStatusCode.Unauthorized, oldCode.StatusCode);
            var newDevice = await PairWithCodeAsync(harness, replacement);
            using var originalAllowed = await harness.GetWithTokenAsync("api/sessions", originalDevice.DeviceToken);
            using var newAllowed = await harness.GetWithTokenAsync("api/sessions", newDevice.DeviceToken);
            Assert.Equal(HttpStatusCode.OK, originalAllowed.StatusCode);
            Assert.Equal(HttpStatusCode.OK, newAllowed.StatusCode);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pair_ClearingCodeAndRestarting_RestoresLegacySingleUseTokens()
    {
        var harness = new GatewayHarness { ConnectionCode = ConnectionCode };
        try
        {
            await harness.InitializeAsync();
            await harness.Gateway.RestartAsync(new RemoteGatewayOptions
            {
                Runtime = () => harness.Runtime,
                Port = new Uri(harness.BaseAddress).Port,
                DataDirectory = harness.DataDirectory,
            });

            using var oldCode = await harness.PostJsonAsync(
                "api/pair", GatewayHarness.Serialize(new { pairingToken = ConnectionCode }));
            Assert.Equal(HttpStatusCode.Unauthorized, oldCode.StatusCode);
            var pairingToken = harness.Gateway.GeneratePairingToken();
            Assert.NotNull(harness.Gateway.PairingTokenExpiresAt);
            await PairWithCodeAsync(harness, pairingToken);
            using var reused = await harness.PostJsonAsync(
                "api/pair", GatewayHarness.Serialize(new { pairingToken }));
            Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pair_ConfiguredCode_IsStillRateLimitedAfterTenFailures()
    {
        var harness = new GatewayHarness { ConnectionCode = ConnectionCode };
        try
        {
            await harness.InitializeAsync();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var response = await harness.PostJsonAsync(
                    "api/pair", GatewayHarness.Serialize(new { pairingToken = "WRONGTKN" }));
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using var blocked = await harness.PostJsonAsync(
                "api/pair", GatewayHarness.Serialize(new { pairingToken = ConnectionCode }));
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("1234567")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    [InlineData("        ")]
    [InlineData("bad+code")]
    [InlineData("abcdefg\u00e9")]
    public async Task Start_InvalidNonemptyConfiguredCode_FailsWithoutListening(string connectionCode)
    {
        await using var gateway = new RemoteGateway(new RemoteGatewayOptions
        {
            Runtime = static () => throw new InvalidOperationException("Runtime must not be used."),
            Port = 0,
            ConnectionCode = connectionCode,
        });

        var error = await Assert.ThrowsAsync<ArgumentException>(() => gateway.StartAsync(CancellationToken.None));

        Assert.Equal("connectionCode", error.ParamName);
        Assert.Contains("8 and 64 ASCII letters", error.Message, StringComparison.Ordinal);
        Assert.False(gateway.IsRunning);
        Assert.Empty(gateway.BoundAddresses);
    }

    private static async Task<(string DeviceId, string DeviceToken)> PairWithCodeAsync(
        GatewayHarness harness, string pairingToken)
    {
        using var response = await harness.PostJsonAsync(
            "api/pair", GatewayHarness.Serialize(new { pairingToken }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        return (
            document.RootElement.GetProperty("deviceId").GetString()!,
            document.RootElement.GetProperty("deviceToken").GetString()!);
    }
}

public sealed class GatewayBindingTests
{
    private const string ConnectionCode = "Binding-test_code";

    [Fact]
    public async Task Start_ExclusiveIpv4Port_FailsAndSameGatewayCanRetryAfterRelease()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "alcoler-remote-tests", Guid.NewGuid().ToString("N"));
        var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Server.ExclusiveAddressUse = true;
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            await using var gateway = new RemoteGateway(new RemoteGatewayOptions
            {
                Runtime = static () => throw new InvalidOperationException("Runtime must not be used."),
                Port = port,
                DataDirectory = dataDirectory,
                ConnectionCode = ConnectionCode,
            });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await Assert.ThrowsAnyAsync<IOException>(() => gateway.StartAsync(cancellation.Token));

            Assert.False(gateway.IsRunning);
            Assert.Empty(gateway.BoundAddresses);
            blocker.Stop();

            await gateway.StartAsync(cancellation.Token);

            Assert.True(gateway.IsRunning);
            Assert.NotEmpty(gateway.BoundAddresses);
            using var client = CreateClient();
            await AssertPairingOnBothAddressFamiliesAsync(client, port);
        }
        finally
        {
            blocker.Stop();
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StopStartAndRestart_AfterHttpTraffic_RebindBothAddressFamiliesImmediately()
    {
        var harness = new GatewayHarness { ConnectionCode = ConnectionCode };
        try
        {
            await harness.InitializeAsync();
            var port = new Uri(harness.BaseAddress).Port;
            using var client = CreateClient();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await AssertPairingOnBothAddressFamiliesAsync(client, port);
                await harness.Gateway.StopAsync();
                Assert.False(harness.Gateway.IsRunning);
                Assert.Empty(harness.Gateway.BoundAddresses);

                await harness.Gateway.StartAsync(CancellationToken.None);
                Assert.True(harness.Gateway.IsRunning);
                await AssertPairingOnBothAddressFamiliesAsync(client, port);

                await harness.Gateway.RestartAsync(new RemoteGatewayOptions
                {
                    Runtime = () => harness.Runtime,
                    Port = port,
                    DataDirectory = harness.DataDirectory,
                    ConnectionCode = ConnectionCode,
                });
                Assert.True(harness.Gateway.IsRunning);
                await AssertPairingOnBothAddressFamiliesAsync(client, port);
            }
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static async Task AssertPairingOnBothAddressFamiliesAsync(HttpClient client, int port)
    {
        await AssertPairingAsync(client, new UriBuilder("http", "127.0.0.1", port).Uri);
        if (Socket.OSSupportsIPv6)
            await AssertPairingAsync(client, new UriBuilder("http", "::1", port).Uri);
    }

    private static async Task AssertPairingAsync(HttpClient client, Uri address)
    {
        using var body = new StringContent(
            GatewayHarness.Serialize(new { pairingToken = ConnectionCode }), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri(address, "api/pair"), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("deviceId").GetString()));
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("deviceToken").GetString()));
    }
}
