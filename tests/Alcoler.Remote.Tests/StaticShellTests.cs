using System.Net;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("api-gateway")]
public sealed class StaticShellTests
{
    private readonly GatewayHarness _harness;

    public StaticShellTests(GatewayHarness harness) => _harness = harness;

    [Fact]
    public async Task Root_ServesHtmlShell()
    {
        using var response = await _harness.Client.GetAsync(string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<!doctype", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WwwRoot_ServesJavaScript()
    {
        using var response = await _harness.Client.GetAsync("wwwroot/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "javascript",
            response.Content.Headers.ContentType?.MediaType ?? "",
            StringComparison.OrdinalIgnoreCase);
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrEmpty(body));
    }

    [Fact]
    public async Task WwwRoot_ServesManifestAsManifestJson()
    {
        using var response = await _harness.Client.GetAsync("wwwroot/manifest.webmanifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
    }
}
