using System.Net;
using System.Text;
using System.Text.Json;
using Alcoler.Adapters.Tools;
using Alcoler.Adapters.Web;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

internal sealed class FakeAuxModelClient : IAuxModelClient
{
    public Func<AuxModelRequest, string>? Responder { get; set; }
    public Exception? Throw { get; set; }
    public List<AuxModelRequest> Requests { get; } = [];

    public ValueTask<string> CompleteAsync(AuxModelRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Throw is not null)
            throw Throw;
        return ValueTask.FromResult(Responder is null ? "summarized page" : Responder(request));
    }
}

internal sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}

public sealed class WebFetchToolTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    private ToolExecutionContext Context(
        string json,
        IAuxModelClient? auxModel = null,
        TimeProvider? clock = null) =>
        new(
            new SessionId("sess_webfetch"),
            _workspace.Root,
            JsonDocument.Parse(json).RootElement.Clone(),
            _workspace.Workspace,
            clock ?? TimeProvider.System,
            AuxModel: auxModel);

    private static string Call(string url, string prompt = "Summarize the page") =>
        $$"""{ "url": "{{url}}", "prompt": "{{prompt}}" }""";

    private static HttpResponseMessage TextResponse(
        string body,
        string contentType = "text/html; charset=utf-8",
        HttpStatusCode status = HttpStatusCode.OK,
        string? reasonPhrase = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType.Split(';', 2)[0].Trim()),
        };
        if (reasonPhrase is not null)
            response.ReasonPhrase = reasonPhrase;
        return response;
    }

    private static HttpResponseMessage RedirectResponse(string location, HttpStatusCode status = HttpStatusCode.Found, string reasonPhrase = "Found")
    {
        var response = new HttpResponseMessage(status) { ReasonPhrase = reasonPhrase };
        response.Headers.Location = location.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(location)
            : new Uri(location, UriKind.Relative);
        return response;
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void MetadataRequiresApprovalAndFixedTimeout()
    {
        using var tool = new WebFetchTool();
        Assert.Equal("WebFetch", tool.Contract.Name);
        Assert.True(tool.Metadata.NeedsApproval);
        Assert.False(tool.Metadata.ReadOnly);
        Assert.Equal(RiskLevel.Medium, tool.Metadata.RiskLevel);
        Assert.Equal(SideEffectScope.Network, tool.Metadata.SideEffectScope);
        Assert.Equal(60_000, tool.Metadata.DefaultTimeoutMs);
        Assert.Equal(60_000, tool.Metadata.MaxTimeoutMs);
    }

    [Fact]
    public void DescriptionMatchesContractText()
    {
        using var tool = new WebFetchTool();
        Assert.Equal(
            """
            Fetches a URL, converts the page to markdown, and answers `prompt` against it using a small fast model.

            - Fails on authenticated/private URLs — use an authenticated MCP tool or `gh` for those instead.
            - HTTP is upgraded to HTTPS. Cross-host redirects are returned to you rather than followed; call again with the redirect URL.
            - Responses are cached for 15 minutes per URL.
            """,
            tool.Contract.Description);
    }

    [Theory]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("http://172.16.3.4/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://127.0.0.1:8080/")]
    public async Task RejectsNonPublicIPv4Hosts(string url)
    {
        var handler = new FakeHttpMessageHandler(_ => TextResponse("<h1>x</h1>"));
        using var tool = new WebFetchTool(handler);
        var result = await tool.ExecuteAsync(Context(Call(url)), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("WebFetch blocks non-public IPv4 hosts", result.SingleText());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd00::1]/")]
    public async Task RejectsNonPublicIPv6Hosts(string url)
    {
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse("<h1>x</h1>")));
        var result = await tool.ExecuteAsync(Context(Call(url)), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("WebFetch blocks non-public IPv6 hosts", result.SingleText());
    }

    [Theory]
    [InlineData("http://localhost/dev")]
    [InlineData("http://api.localhost/v1")]
    [InlineData("http://printer.local/")]
    public async Task RejectsLocalHostnames(string url)
    {
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse("<h1>x</h1>")));
        var result = await tool.ExecuteAsync(Context(Call(url)), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("WebFetch requires a public hostname", result.SingleText());
    }

    [Theory]
    [InlineData("ftp://example.com/file", "WebFetch only supports http and https URLs")]
    [InlineData("https://user:pass@example.com/page", "WebFetch URLs must not include credentials")]
    [InlineData("not a url", "Invalid URL: not a url")]
    [InlineData("https://intranet/", "Invalid URL")]
    public async Task RejectsInvalidUrls(string url, string expectedError)
    {
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse("<h1>x</h1>")));
        var result = await tool.ExecuteAsync(Context(Call(url)), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(expectedError, result.SingleText());
    }

    [Fact]
    public async Task RejectsUrlsBeyondTwoThousandCharacters()
    {
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse("<h1>x</h1>")));
        var result = await tool.ExecuteAsync(Context(Call(new string('a', 2_001))), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("URL is too long", result.SingleText());
    }

    [Fact]
    public async Task UpgradesHttpToHttpsBeforeFetching()
    {
        var handler = new FakeHttpMessageHandler(_ => TextResponse("<h1>Page</h1>"));
        using var tool = new WebFetchTool(handler);
        var result = await tool.ExecuteAsync(
            Context(Call("http://example.com/page"), new FakeAuxModelClient()),
            CancellationToken.None);
        Assert.False(result.IsError);
        var requested = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal("https", requested.Scheme);
        Assert.Equal("example.com/page", requested.Host + requested.AbsolutePath);
    }

    [Fact]
    public async Task ReturnsCrossHostRedirectTemplate()
    {
        var handler = new FakeHttpMessageHandler(_ => RedirectResponse("https://other.com/b", HttpStatusCode.MovedPermanently, "Moved Permanently"));
        using var tool = new WebFetchTool(handler);
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/a", "What is this page about?")), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(
            """
            REDIRECT DETECTED: The URL redirects to a different host.

            Original URL: https://example.com/a
            Redirect URL: https://other.com/b
            Status: 301 Moved Permanently

            To complete your request, I need to fetch content from the redirected URL. Please use WebFetch again with these parameters:
            - url: "https://other.com/b"
            - prompt: "What is this page about?"
            """,
            result.SingleText());
    }

    [Fact]
    public async Task FollowsSameHostRedirectsIncludingWwwAndRelativeLocations()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath switch
            {
                "/start" => RedirectResponse("next"),
                "/next" => RedirectResponse("https://www.example.com/landing", HttpStatusCode.MovedPermanently, "Moved Permanently"),
                _ => TextResponse("<h1>Landing</h1>"),
            });
        var aux = new FakeAuxModelClient();
        using var tool = new WebFetchTool(handler);
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/start"), aux), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("# Landing", aux.Requests.Single().UserPrompt);
    }

    [Fact]
    public async Task ReturnsHttpErrorTextForErrorStatuses()
    {
        var handler = new FakeHttpMessageHandler(_ => TextResponse("missing", status: HttpStatusCode.NotFound, reasonPhrase: "Not Found"));
        using var tool = new WebFetchTool(handler);
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/missing")), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(
            """
            The server returned HTTP 404 Not Found.

            The response body was not retrieved. If this URL requires authentication, use an authenticated tool (e.g. `gh` for GitHub, or an MCP-provided fetch tool) instead of WebFetch.
            """,
            result.SingleText());
    }

    [Fact]
    public async Task HttpErrorIncludesNumericRetryAfterHeader()
    {
        var response = TextResponse("rate limited", status: (HttpStatusCode)429, reasonPhrase: "Too Many Requests");
        response.Headers.TryAddWithoutValidation("Retry-After", "120");
        var handler = new FakeHttpMessageHandler(_ => response);
        using var tool = new WebFetchTool(handler);
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/search")), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.StartsWith("The server returned HTTP 429 Too Many Requests.\nRetry-After: 120\n\n", result.SingleText());
    }

    [Fact]
    public async Task ConvertsHtmlToMarkdownBeforePromptProcessing()
    {
        var html = """
            <html><head><title>T</title><style>body { color: red; }</style><script>window.x = 1;</script></head>
            <body>
            <h1>Page Title</h1>
            <p>Hello &amp; welcome</p>
            <a href="https://example.com/docs">the docs</a>
            <ul><li>alpha</li><li>beta</li></ul>
            <!-- hidden comment -->
            </body></html>
            """;
        var aux = new FakeAuxModelClient();
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse(html)));
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/page"), aux), CancellationToken.None);
        Assert.False(result.IsError);
        var userPrompt = aux.Requests.Single().UserPrompt;
        Assert.Contains("# Page Title", userPrompt);
        Assert.Contains("[the docs](https://example.com/docs)", userPrompt);
        Assert.Contains("- alpha", userPrompt);
        Assert.Contains("- beta", userPrompt);
        Assert.Contains("Hello & welcome", userPrompt);
        Assert.DoesNotContain("window.x", userPrompt);
        Assert.DoesNotContain("color: red", userPrompt);
        Assert.DoesNotContain("hidden comment", userPrompt);
        Assert.DoesNotContain("<h1>", userPrompt);
    }

    [Fact]
    public async Task TruncatesContentBeforePromptProcessing()
    {
        var aux = new FakeAuxModelClient();
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse(
            new string('x', 150_000),
            contentType: "text/plain; charset=utf-8")));
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/big", "p"), aux), CancellationToken.None);
        Assert.False(result.IsError);
        var request = aux.Requests.Single();
        Assert.Equal(string.Empty, request.SystemPrompt);
        var userPrompt = request.UserPrompt;
        Assert.StartsWith("\nWeb page content:\n---\n", userPrompt);
        Assert.Contains("[WebFetch content truncated before prompt processing]", userPrompt);
        Assert.EndsWith("Never produce or reproduce exact song lyrics.\n", userPrompt);
        // ZCode caps the page content at 100k chars; the surrounding template adds its own
        // fixed overhead on top, so the final prompt runs slightly past the cap.
        Assert.InRange(userPrompt.Length, 100_000, 100_600);
    }

    [Fact]
    public async Task FallsBackToRawPreviewWithoutAuxiliaryModel()
    {
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse(
            new string('y', 6_000),
            contentType: "text/plain; charset=utf-8")));
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/plain", "p")), CancellationToken.None);
        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.StartsWith(new string('y', 5_000), text);
        Assert.Contains("no auxiliary model is configured for this session", text);
    }

    [Fact]
    public async Task ReturnsAuxiliaryModelFailureAsError()
    {
        var aux = new FakeAuxModelClient { Throw = new InvalidOperationException("aux exploded") };
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse("<h1>Page</h1>")));
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/page"), aux), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("aux exploded", result.SingleText());
    }

    [Fact]
    public async Task ReportsEmptyAuxiliaryModelAnswers()
    {
        var aux = new FakeAuxModelClient { Responder = _ => "   " };
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse("<h1>Page</h1>")));
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/page"), aux), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal("WebFetch completed, but the extraction model returned no text.", result.SingleText());
    }

    [Fact]
    public async Task CachesContentPerUrlUntilTtlExpires()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new FakeHttpMessageHandler(_ => TextResponse("<h1>Page</h1>"));
        var aux = new FakeAuxModelClient();
        using var tool = new WebFetchTool(handler);

        var first = await tool.ExecuteAsync(Context(Call("https://example.com/page", "first"), aux, clock), CancellationToken.None);
        var second = await tool.ExecuteAsync(Context(Call("https://example.com/page", "second"), aux, clock), CancellationToken.None);
        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Single(handler.Requests);
        Assert.Equal(2, aux.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(16));
        var third = await tool.ExecuteAsync(Context(Call("https://example.com/page", "third"), aux, clock), CancellationToken.None);
        Assert.False(third.IsError);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task EvictsOldestCacheEntriesBeyondCapacity()
    {
        var handler = new FakeHttpMessageHandler(_ => TextResponse("<h1>Page</h1>"));
        using var tool = new WebFetchTool(handler, new WebFetchCache(capacity: 2));
        var aux = new FakeAuxModelClient();

        await tool.ExecuteAsync(Context(Call("https://example.com/one", "p"), aux), CancellationToken.None);
        await tool.ExecuteAsync(Context(Call("https://example.com/two", "p"), aux), CancellationToken.None);
        await tool.ExecuteAsync(Context(Call("https://example.com/three", "p"), aux), CancellationToken.None);
        await tool.ExecuteAsync(Context(Call("https://example.com/one", "p"), aux), CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsUnsupportedContentTypesAndDoesNotCacheFailures()
    {
        var handler = new FakeHttpMessageHandler(_ => TextResponse("binary", contentType: "application/octet-stream"));
        using var tool = new WebFetchTool(handler);

        var first = await tool.ExecuteAsync(Context(Call("https://example.com/blob")), CancellationToken.None);
        var second = await tool.ExecuteAsync(Context(Call("https://example.com/blob")), CancellationToken.None);

        Assert.True(first.IsError);
        Assert.Equal("Unsupported WebFetch content type: application/octet-stream", first.SingleText());
        Assert.Equal(first.SingleText(), second.SingleText());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsResponsesBeyondTenMegabytes()
    {
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(_ => TextResponse(
            new string('a', 10 * 1024 * 1024 + 1),
            contentType: "text/plain; charset=utf-8")));
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/huge", "p")), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("maximum", result.SingleText());
    }

    [Fact]
    public async Task ReportsNetworkFailures()
    {
        HttpResponseMessage Throw(HttpRequestMessage request) => throw new HttpRequestException("connection refused");
        using var tool = new WebFetchTool(new FakeHttpMessageHandler(Throw));
        var result = await tool.ExecuteAsync(Context(Call("https://example.com/down", "p")), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("WebFetch request failed: connection refused", result.SingleText());
    }
}
