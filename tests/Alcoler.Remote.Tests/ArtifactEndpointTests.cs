using System.Net;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("artifact-gateway")]
public sealed class ArtifactEndpointTests
{
    private readonly ArtifactGatewayHarness _harness;

    public ArtifactEndpointTests(ArtifactGatewayHarness harness) => _harness = harness;

    [Fact]
    public async Task Artifact_LoadedByDelegate_ReturnsBytesWithProvidedMime()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.GetWithTokenAsync(
            $"api/artifacts?uri={Uri.EscapeDataString(ArtifactGatewayHarness.KnownArtifactUri)}&mime=image%2Fpng",
            token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            ArtifactGatewayHarness.KnownArtifactBytes,
            await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Artifact_WithoutMime_DefaultsToOctetStream()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.GetWithTokenAsync(
            $"api/artifacts?uri={Uri.EscapeDataString(ArtifactGatewayHarness.KnownArtifactUri)}", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Artifact_UnknownToLoader_IsNotFound()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.GetWithTokenAsync(
            $"api/artifacts?uri={Uri.EscapeDataString("alcoler-artifact://sess_fixture/missing.bin")}", token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.Equal("not_found", document.RootElement.GetProperty("error").GetString());
        Assert.Equal("artifact", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Artifact_WithForeignScheme_IsRejected()
    {
        var (_, token) = await _harness.PairAsync();

        using var response = await _harness.GetWithTokenAsync(
            $"api/artifacts?uri={Uri.EscapeDataString("http://example.com/report.bin")}", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.Equal("bad_request", document.RootElement.GetProperty("error").GetString());
        Assert.Equal("uri", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Artifact_WithoutConfiguredLoader_IsNotImplemented()
    {
        var harness = new GatewayHarness();
        await harness.InitializeAsync();
        try
        {
            var (_, token) = await harness.PairAsync();

            using var response = await harness.GetWithTokenAsync(
                $"api/artifacts?uri={Uri.EscapeDataString("alcoler-artifact://sess_x/y.bin")}", token);

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            using var document = await response.ReadJsonAsync();
            Assert.Equal("not_supported", document.RootElement.GetProperty("error").GetString());
            Assert.Equal("artifact_loader", document.RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }
}
