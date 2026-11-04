using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("api-gateway")]
public sealed class ResponseCompressionTests
{
    private readonly GatewayHarness _harness;

    public ResponseCompressionTests(GatewayHarness harness) => _harness = harness;

    private Task<HttpResponseMessage> GetAsync(string path, string? acceptEncoding = null, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _harness.Client.SendAsync(request);
    }

    private static async Task<string> DecompressAsync(HttpResponseMessage response)
    {
        await using var compressed = await response.Content.ReadAsStreamAsync();
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        await gzip.CopyToAsync(decompressed);
        decompressed.Position = 0;
        using var reader = new StreamReader(decompressed);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task ShellStyles_CompressForGzipClientsAndStayPlainForOthers()
    {
        using var plain = await GetAsync("styles.css");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        var raw = await plain.Content.ReadAsStringAsync();

        using var compressed = await GetAsync("styles.css", "gzip");
        Assert.Equal(HttpStatusCode.OK, compressed.StatusCode);
        Assert.Contains("gzip", compressed.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", compressed.Headers.Vary.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(raw, await DecompressAsync(compressed));
    }

    [Fact]
    public async Task RootShell_CompressesThroughTheMinimalApiResultPath()
    {
        using var response = await GetAsync(string.Empty, "gzip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
        Assert.Contains("<!doctype", await DecompressAsync(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmbeddedFonts_ServeAsFontTtfAndCompress()
    {
        using var response = await GetAsync("fonts/JetBrainsMono-Regular.ttf", "gzip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("font/ttf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task SessionList_CompressesJsonForAuthenticatedGzipClients()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await GetAsync("api/sessions?limit=1", "gzip", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
        using var document = JsonDocument.Parse(await DecompressAsync(response));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
    }
}
