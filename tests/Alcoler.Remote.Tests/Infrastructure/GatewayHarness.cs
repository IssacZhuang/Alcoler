using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Sessions;
using Alcoler.Remote.Tests.Fakes;
using Xunit;

namespace Alcoler.Remote.Tests.Infrastructure;

public class GatewayHarness : IAsyncLifetime
{
    public const string Version = "9.9.9-remote-tests";

    public FakeAgentRuntime Runtime { get; } = new();
    public RemoteGateway Gateway { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string BaseAddress { get; private set; } = "";
    public string DataDirectory { get; } = Path.Combine(
        Path.GetTempPath(), "alcoler-remote-tests", Guid.NewGuid().ToString("N"));

    protected virtual RemoteGatewayOptions CreateOptions(int port) => new()
    {
        Runtime = () => Runtime,
        Port = port,
        DataDirectory = DataDirectory,
        ProductVersion = Version,
    };

    public async Task InitializeAsync()
    {
        Exception? failure = null;
        for (var attempt = 0; attempt < 3 && Gateway is null; attempt++)
        {
            var gateway = new RemoteGateway(CreateOptions(AllocateFreePort()));
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await gateway.StartAsync(cancellation.Token);
                Gateway = gateway;
            }
            catch (Exception error)
            {
                failure = error;
                await gateway.DisposeAsync();
            }
        }
        if (Gateway is null)
            throw failure ?? new InvalidOperationException("The gateway failed to start.");
        BaseAddress = LoopbackBaseAddress(Gateway.BoundAddresses);
        Client = new HttpClient { BaseAddress = new Uri(BaseAddress) };
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Gateway is not null)
            await Gateway.DisposeAsync();
        await Runtime.DisposeAsync();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, GatewayJson.Options);

    public Task<HttpResponseMessage> PostJsonAsync(string path, string json) =>
        Client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    public async Task<HttpResponseMessage> SendWithTokenAsync(
        HttpMethod method, string path, string token, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetWithTokenAsync(string path, string token) =>
        SendWithTokenAsync(HttpMethod.Get, path, token);

    public Task<HttpResponseMessage> PostCommandAsync(
        SessionId sessionId, ConversationCommand command, string token) =>
        SendWithTokenAsync(
            HttpMethod.Post, $"api/sessions/{sessionId.Value}/commands", token, Serialize<ConversationCommand>(command));

    public async Task<(string DeviceId, string DeviceToken)> PairAsync(string? deviceName = null)
    {
        var pairingToken = Gateway.GeneratePairingToken();
        using var response = await PostJsonAsync("api/pair", Serialize(new { pairingToken, deviceName }));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Pairing failed with HTTP {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return (
            document.RootElement.GetProperty("deviceId").GetString() ?? "",
            document.RootElement.GetProperty("deviceToken").GetString() ?? "");
    }

    public async Task<SessionId> CreateSessionAsync(
        string deviceToken, string? workspaceRoot = null, string? title = null, string mode = "yolo")
    {
        workspaceRoot ??= $@"C:\remote-tests\{Guid.NewGuid():N}";
        using var response = await SendWithTokenAsync(
            HttpMethod.Post, "api/sessions", deviceToken, Serialize(new { workspaceRoot, title, mode }));
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Session create failed with HTTP {(int)response.StatusCode}: {body}");
        var handle = JsonSerializer.Deserialize<SessionHandle>(body, GatewayJson.Options)
            ?? throw new InvalidOperationException("Session create returned no handle.");
        return handle.Id;
    }

    public async Task<(SessionId SessionId, string DeviceToken)> NewSessionAsync()
    {
        var (_, deviceToken) = await PairAsync();
        return (await CreateSessionAsync(deviceToken), deviceToken);
    }

    // BoundAddresses reports the wildcard host; clients connect over loopback.
    private static string LoopbackBaseAddress(IReadOnlyList<string> boundAddresses)
    {
        var bound = boundAddresses.Count > 0
            ? boundAddresses[0]
            : throw new InvalidOperationException("The gateway reported no bound addresses.");
        if (!Uri.TryCreate(bound, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"The gateway reported an invalid bound address: {bound}");
        return new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.ToString();
    }

    // The gateway reports the configured port, not the bound one, so an explicitly
    // allocated free port is used instead of port 0.
    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

public class ArtifactGatewayHarness : GatewayHarness
{
    public const string KnownArtifactUri = "alcoler-artifact://sess_fixture/report.bin";
    public static readonly byte[] KnownArtifactBytes = [0x61, 0x6c, 0x63, 0x6f, 0x6c, 0x65, 0x72, 0x00];

    protected override RemoteGatewayOptions CreateOptions(int port) => new()
    {
        Runtime = () => Runtime,
        Port = port,
        DataDirectory = DataDirectory,
        ProductVersion = Version,
        ArtifactLoader = LoadArtifact,
    };

    private static ValueTask<ReadOnlyMemory<byte>?> LoadArtifact(string uri, CancellationToken cancellationToken)
    {
        // The ternary needs an explicit cast: an uncast null arm would type the
        // conditional as ReadOnlyMemory<byte> and yield a non-null empty payload.
        ReadOnlyMemory<byte>? payload = uri == KnownArtifactUri ? KnownArtifactBytes : (ReadOnlyMemory<byte>?)null;
        return ValueTask.FromResult(payload);
    }
}

internal static class HttpResponseMessageJson
{
    public static async Task<JsonDocument> ReadJsonAsync(this HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}

[CollectionDefinition("pairing-gateway")]
public sealed class PairingGatewayCollection : ICollectionFixture<GatewayHarness>
{
}

[CollectionDefinition("api-gateway")]
public sealed class ApiGatewayCollection : ICollectionFixture<GatewayHarness>
{
}

[CollectionDefinition("stream-gateway")]
public sealed class StreamGatewayCollection : ICollectionFixture<GatewayHarness>
{
}

[CollectionDefinition("artifact-gateway")]
public sealed class ArtifactGatewayCollection : ICollectionFixture<ArtifactGatewayHarness>
{
}
