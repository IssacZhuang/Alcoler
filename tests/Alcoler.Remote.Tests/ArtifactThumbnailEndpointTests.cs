using System.Net;
using System.Net.Http.Headers;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

public sealed class ArtifactThumbnailEndpointTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public async Task Thumbnail_RequiresAuthenticationBeforeLoading(string? token)
    {
        await using var harness = new ThumbnailGatewayHarness();
        await harness.InitializeAsync();
        using var response = await harness.Client.GetAsync(PreviewPath() + (token is null ? "" : "&token=" + token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, harness.Loads);
        Assert.Equal(0, harness.Generations);
    }

    [Fact]
    public async Task Thumbnail_IsOnDemand_UsesGeneratedMimeAndBytes_AndReusesAcrossCallerMimes()
    {
        await using var harness = new ThumbnailGatewayHarness();
        await harness.InitializeAsync();
        var (_, token) = await harness.PairAsync();
        using var info = await harness.GetWithTokenAsync("api/info", token);
        using var sessions = await harness.GetWithTokenAsync("api/sessions", token);
        Assert.Equal(0, harness.Loads);
        Assert.Equal(0, harness.Generations);
        using var first = await harness.Client.GetAsync(PreviewPath() + "&mime=image%2Fsvg%2Bxml&token=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ThumbnailGatewayHarness.PreviewBytes, await first.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", Assert.Single(first.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", Assert.Single(first.Headers.GetValues("X-Content-Type-Options")));
        using var second = await harness.GetWithTokenAsync(PreviewPath() + "&mime=application%2Foctet-stream", token);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, harness.Loads);
        Assert.Equal(1, harness.Generations);
        using var original = await harness.GetWithTokenAsync(OriginalPath() + "&mime=image%2Fjpeg", token);
        Assert.Equal(ThumbnailGatewayHarness.SourceBytes, await original.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", original.Content.Headers.ContentType?.MediaType);
        Assert.Equal(2, harness.Loads);
        Assert.Equal(1, harness.Generations);
    }

    [Fact]
    public async Task CachedThumbnail_StillRequiresNonRevokedDevice()
    {
        await using var harness = new ThumbnailGatewayHarness();
        await harness.InitializeAsync();
        var (device, token) = await harness.PairAsync();
        using var first = await harness.GetWithTokenAsync(PreviewPath(), token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await harness.Gateway.RevokeDeviceAsync(device);
        using var revoked = await harness.GetWithTokenAsync(PreviewPath(), token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(1, harness.Loads);
        Assert.Equal(1, harness.Generations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("true")]
    [InlineData("1&thumbnail=1")]
    public async Task Thumbnail_RejectsInvalidOrDuplicateVariantWithoutLoading(string variant)
    {
        await using var harness = new ThumbnailGatewayHarness();
        await harness.InitializeAsync();
        var (_, token) = await harness.PairAsync();
        using var response = await harness.GetWithTokenAsync(OriginalPath() + "&thumbnail=" + variant, token);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "bad_request", "thumbnail");
        Assert.Equal(0, harness.Loads);
        Assert.Equal(0, harness.Generations);
    }

    [Theory]
    [InlineData("https://example.com/image.png")]
    [InlineData("file:///C:/private/image.png")]
    [InlineData("C:\\private\\image.png")]
    [InlineData("")]
    public async Task Thumbnail_RejectsForeignSchemeOrMissingUriWithoutLoading(string uri)
    {
        await using var harness = new ThumbnailGatewayHarness();
        await harness.InitializeAsync();
        var (_, token) = await harness.PairAsync();
        using var response = await harness.GetWithTokenAsync("api/artifacts?thumbnail=1&uri=" + Uri.EscapeDataString(uri), token);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "bad_request", "uri");
        Assert.Equal(0, harness.Loads);
    }

    [Theory]
    [InlineData("&width=4096", "query")]
    [InlineData("&height=4096", "query")]
    [InlineData("&uri=alcoler-artifact%3A%2F%2Fsession%2Fother.png", "uri")]
    public async Task Thumbnail_RejectsAmbiguousOrVariableDimensionQueries(string extra, string reason)
    {
        await using var harness = new ThumbnailGatewayHarness();
        await harness.InitializeAsync();
        var (_, token) = await harness.PairAsync();
        using var response = await harness.GetWithTokenAsync(PreviewPath() + extra, token);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "bad_request", reason);
        Assert.Equal(0, harness.Loads);
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.NotFound, "not_found", "artifact")]
    [InlineData("unsupported", HttpStatusCode.UnsupportedMediaType, "unsupported_media_type", "thumbnail")]
    [InlineData("generator_error", HttpStatusCode.InternalServerError, "internal_error", "thumbnail")]
    [InlineData("loader_error", HttpStatusCode.InternalServerError, "internal_error", "thumbnail")]
    [InlineData("not_configured", HttpStatusCode.NotImplemented, "not_supported", "artifact_thumbnail")]
    public async Task Thumbnail_FailuresNeverFallBackToOriginalOrBecomeCached(
        string failure, HttpStatusCode status, string error, string reason)
    {
        await using var harness = new ThumbnailGatewayHarness
        {
            Loader = (_, _) =>
            {
                if (failure == "loader_error") throw new IOException("unavailable source");
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(failure == "missing" ? (ReadOnlyMemory<byte>?)null : ThumbnailGatewayHarness.SourceBytes);
            },
            Generator = failure == "not_configured" ? null : (_, _) =>
            {
                if (failure == "generator_error") throw new InvalidDataException("invalid image");
                return ValueTask.FromResult<ArtifactThumbnail?>(failure == "unsupported"
                    ? null : new ArtifactThumbnail(ThumbnailGatewayHarness.PreviewBytes, "image/png"));
            },
        };
        await harness.InitializeAsync();
        var (_, token) = await harness.PairAsync();
        for (var index = 0; index < 2; index++)
        {
            using var response = await harness.GetWithTokenAsync(PreviewPath(), token);
            await AssertErrorAsync(response, status, error, reason);
        }
        Assert.Equal(failure == "not_configured" ? 0 : 2, harness.Loads);
        if (failure == "not_configured")
        {
            using var original = await harness.GetWithTokenAsync(OriginalPath(), token);
            Assert.Equal(ThumbnailGatewayHarness.SourceBytes, await original.Content.ReadAsByteArrayAsync());
            Assert.Equal(0, harness.Generations);
        }
    }

    [Fact]
    public async Task Thumbnail_RequestCancellationCancelsUnusedLoader_AndRetryStartsFresh()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var harness = new ThumbnailGatewayHarness
        {
            Loader = async (_, token) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { canceled.TrySetResult(); }
                }
                return ThumbnailGatewayHarness.SourceBytes;
            },
        };
        await harness.InitializeAsync();
        var (_, token) = await harness.PairAsync();
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, PreviewPath());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var pending = harness.Client.SendAsync(request, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var retry = await harness.GetWithTokenAsync(PreviewPath(), token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, harness.Loads);
        Assert.Equal(1, harness.Generations);
    }

    [Fact]
    public async Task GatewayStopAndStart_DropsEncodedThumbnailCache()
    {
        await using var harness = new ThumbnailGatewayHarness();
        await harness.InitializeAsync();
        var (_, token) = await harness.PairAsync();
        using var first = await harness.GetWithTokenAsync(PreviewPath(), token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await harness.Gateway.StopAsync();
        await harness.Gateway.StartAsync(CancellationToken.None);
        using var restarted = await harness.GetWithTokenAsync(PreviewPath(), token);
        Assert.Equal(HttpStatusCode.OK, restarted.StatusCode);
        Assert.Equal(2, harness.Loads);
        Assert.Equal(2, harness.Generations);
    }

    private static string OriginalPath() =>
        "api/artifacts?uri=" + Uri.EscapeDataString(ThumbnailGatewayHarness.ArtifactUri);

    private static string PreviewPath() => OriginalPath() + "&thumbnail=1";

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error, string reason)
    {
        Assert.Equal(status, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.Equal(error, document.RootElement.GetProperty("error").GetString());
        Assert.Equal(reason, document.RootElement.GetProperty("reason").GetString());
    }

    private sealed class ThumbnailGatewayHarness : GatewayHarness, IAsyncDisposable
    {
        public const string ArtifactUri = "alcoler-artifact://sess_thumbnail/image.png";
        public static readonly byte[] SourceBytes = [1, 2, 3, 4];
        public static readonly byte[] PreviewBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6XcAAAAASUVORK5CYII=");

        public int Loads;
        public int Generations;
        public Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> Loader { get; init; } =
            (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(SourceBytes);
        public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ArtifactThumbnail?>>? Generator { get; init; } =
            (_, _) => ValueTask.FromResult<ArtifactThumbnail?>(new ArtifactThumbnail(PreviewBytes, "image/png"));

        protected override RemoteGatewayOptions CreateOptions(int port) => new()
        {
            Runtime = () => Runtime,
            Port = port,
            DataDirectory = DataDirectory,
            ArtifactLoader = (uri, token) => { Interlocked.Increment(ref Loads); return Loader(uri, token); },
            ArtifactThumbnailGenerator = Generator is null ? null : (bytes, token) =>
            {
                Interlocked.Increment(ref Generations);
                return Generator(bytes, token);
            },
        };

        ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());
    }
}
