using System.Net;
using System.Net.Http.Headers;
using Alcoler.Remote.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
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

    [Theory]
    [InlineData("image/png")]
    [InlineData("audio/wav")]
    [InlineData("video/webm; codecs=vp9")]
    [InlineData("application/octet-stream")]
    public async Task Artifact_LargeSlicedMemory_PreservesOriginalBytesAndMime(string mime)
    {
        var (_, token) = await _harness.PairAsync();
        using var response = await _harness.GetWithTokenAsync(
            $"api/artifacts?uri={Uri.EscapeDataString(ArtifactGatewayHarness.LargeArtifactUri)}&mime={Uri.EscapeDataString(mime)}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mime, response.Content.Headers.ContentType?.ToString());
        Assert.Equal(ArtifactGatewayHarness.LargeArtifactBytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("bytes", Assert.Single(response.Headers.AcceptRanges));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal(ArtifactGatewayHarness.LargeArtifactBytes.ToArray(), await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("bytes=0-63", 0, 64)]
    [InlineData("bytes=1048576-1048703", 1048576, 128)]
    [InlineData("bytes=-57", -57, 57)]
    [InlineData("bytes=2097152-", 2097152, 17)]
    public async Task Artifact_NativeMediaRange_ReturnsOnlyRequestedOriginalBytes(string range, int offset, int count)
    {
        var (_, token) = await _harness.PairAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/artifacts?uri={Uri.EscapeDataString(ArtifactGatewayHarness.LargeArtifactUri)}&mime=video%2Fwebm&token={Uri.EscapeDataString(token)}");
        request.Headers.Range = RangeHeaderValue.Parse(range);
        using var response = await _harness.Client.SendAsync(request);
        var bytes = ArtifactGatewayHarness.LargeArtifactBytes;
        if (offset < 0) offset += bytes.Length;
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes {offset}-{offset + count - 1}/{bytes.Length}", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(count, response.Content.Headers.ContentLength);
        Assert.Equal("video/webm", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes.Slice(offset, count).ToArray(), await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("bytes=999999999-")]
    [InlineData("bytes=-0")]
    public async Task Artifact_UnsatisfiableRange_ReturnsEmpty416(string range)
    {
        var (_, token) = await _harness.PairAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/artifacts?uri={Uri.EscapeDataString(ArtifactGatewayHarness.LargeArtifactUri)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Range = RangeHeaderValue.Parse(range);
        using var response = await _harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal($"bytes */{ArtifactGatewayHarness.LargeArtifactBytes.Length}", response.Content.Headers.ContentRange?.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Artifact_MultipleRangesOrHeaders_FallBackToOriginalFullResponse(bool multipleHeaders)
    {
        var (_, token) = await _harness.PairAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/artifacts?uri={Uri.EscapeDataString(ArtifactGatewayHarness.KnownArtifactUri)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.True(request.Headers.TryAddWithoutValidation("Range", multipleHeaders
            ? new[] { "bytes=0-1", "bytes=3-4" }
            : new[] { "bytes=0-1, 3-4" }));
        using var response = await _harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.Equal(ArtifactGatewayHarness.KnownArtifactBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Artifact_QueryTokenRequiresValidPairedDevice()
    {
        using var response = await _harness.Client.GetAsync(
            $"api/artifacts?uri={Uri.EscapeDataString(ArtifactGatewayHarness.KnownArtifactUri)}&token=invalid-device");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RawResult_WritesOriginalMemoryAndRequestCancellationWithoutCopying()
    {
        var context = new DefaultHttpContext();
        using var cancellation = new CancellationTokenSource();
        context.RequestAborted = cancellation.Token;
        await using var stream = new RecordingMemoryStream();
        context.Response.Body = stream;
        var content = ArtifactGatewayHarness.LargeArtifactBytes;
        await GatewayJson.Raw(content, "application/octet-stream").ExecuteAsync(context);
        Assert.Equal(content, stream.Memory);
        Assert.Equal(cancellation.Token, stream.Cancellation);
        Assert.Equal(content.Length, context.Response.ContentLength);
        context.Request.Headers.Range = "bytes=17-48";
        await GatewayJson.Raw(content, "application/octet-stream", enableRanges: true).ExecuteAsync(context);
        Assert.Equal(content.Slice(17, 32), stream.Memory);
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal(32, context.Response.ContentLength);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GatewayJson.Raw(content, "application/octet-stream").ExecuteAsync(context));
    }

    private sealed class RecordingMemoryStream : MemoryStream
    {
        public ReadOnlyMemory<byte> Memory { get; private set; }
        public CancellationToken Cancellation { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Memory = buffer;
            Cancellation = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
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
    public async Task Artifact_ExistingEmptyPayload_IsSuccessfulAndNotMissing()
    {
        var (_, token) = await _harness.PairAsync();
        using var response = await _harness.GetWithTokenAsync(
            $"api/artifacts?uri={Uri.EscapeDataString(ArtifactGatewayHarness.EmptyArtifactUri)}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
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
