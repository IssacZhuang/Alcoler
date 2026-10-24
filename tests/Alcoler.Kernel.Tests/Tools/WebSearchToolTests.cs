using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public sealed class WebSearchToolTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    private ToolExecutionContext Context(string json, IProviderWebSearch? providerSearch = null) =>
        new(
            new SessionId("sess_websearch"),
            _workspace.Root,
            JsonDocument.Parse(json).RootElement.Clone(),
            _workspace.Workspace,
            TimeProvider.System,
            ProviderSearch: providerSearch);

    private static string QueryCall(
        string query,
        string? allowedDomains = null,
        string? blockedDomains = null)
    {
        var extras = "";
        if (allowedDomains is not null)
            extras += $", \"allowed_domains\": {allowedDomains}";
        if (blockedDomains is not null)
            extras += $", \"blocked_domains\": {blockedDomains}";
        return $$"""{ "query": "{{query}}"{{extras}} }""";
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void MetadataIsReadOnlyWithoutApproval()
    {
        using var tool = new WebSearchTool("key");
        Assert.Equal("WebSearch", tool.Contract.Name);
        Assert.True(tool.Metadata.ReadOnly);
        Assert.False(tool.Metadata.NeedsApproval);
        Assert.Equal(SideEffectScope.Network, tool.Metadata.SideEffectScope);
        Assert.Equal(30_000, tool.Metadata.DefaultTimeoutMs);
    }

    [Fact]
    public void DescriptionContainsCurrentMonth()
    {
        using var tool = new WebSearchTool("key", clock: new MutableTimeProvider(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(
            string.Join("\n",
                "Search the web. Returns result blocks with titles and URLs. US-only.",
                "",
                "- The current month is October 2026 — use this when searching for recent information.",
                "- `allowed_domains` / `blocked_domains` filter results.",
                "- After answering from results, end with a \"Sources:\" list of the URLs you used as markdown links."),
            tool.Contract.Description);
    }

    [Fact]
    public async Task SendsBraveRequestWithTokenAndQuery()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"web":{"results":[]}}""", Encoding.UTF8, "application/json"),
        });
        using var tool = new WebSearchTool("test-key", handler);
        var result = await tool.ExecuteAsync(Context(QueryCall("dotnet tests")), CancellationToken.None);
        Assert.False(result.IsError);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api.search.brave.com", request.RequestUri!.Host);
        Assert.Equal("/res/v1/web/search", request.RequestUri.AbsolutePath);
        Assert.Contains("q=dotnet%20tests", request.RequestUri.Query);
        Assert.Contains("count=20", request.RequestUri.Query);
        Assert.Equal("test-key", request.Headers.GetValues("X-Subscription-Token").Single());
    }

    [Fact]
    public async Task FormatsResultsForTheModel()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"web":{"results":[
                  {"type":"search_result","title":"First","url":"https://a.example/one","description":"First result description."},
                  {"title":"Second","url":"https://b.example/two","description":"Second result description."}
                ]}}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        using var tool = new WebSearchTool("key", handler);
        var result = await tool.ExecuteAsync(Context(QueryCall("dotnet tests")), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(
            """
            Web search results for query: "dotnet tests"

            Summary:
            First result description.
            Second result description.

            Links:
            - [First](https://a.example/one)
            - [Second](https://b.example/two)

            REMINDER: You MUST include the sources above in your response to the user using markdown hyperlinks.
            """,
            result.SingleText());
    }

    [Fact]
    public async Task CapsLinksAtTwentyAfterDeduplication()
    {
        var results = Enumerable.Range(0, 24)
            .Select(i => $"{{\"title\":\"Result {i}\",\"url\":\"https://a.example/{i}\",\"description\":\"d{i}\"}}")
            .ToList();
        results.Add("""{"title":"Duplicate","url":"https://a.example/0","description":"dup"}""");
        var body = "{\"web\":{\"results\":[" + string.Join(",", results) + "]}}";
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        using var tool = new WebSearchTool("key", handler);
        var result = await tool.ExecuteAsync(Context(QueryCall("many")), CancellationToken.None);
        Assert.False(result.IsError);

        var text = result.SingleText();
        var linkCount = Regex.Matches(text, "(?m)^- \\[").Count;
        Assert.Equal(20, linkCount);
        Assert.Single(Regex.Matches(text, @"https://a\.example/0\)"));
        Assert.DoesNotContain("https://a.example/23", text);
    }

    [Fact]
    public async Task FiltersResultsToAllowedDomains()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"web":{"results":[
                  {"title":"Keep","url":"https://example.com/a","description":"keep"},
                  {"title":"KeepSub","url":"https://docs.example.com/b","description":"keep sub"},
                  {"title":"Drop","url":"https://other.org/c","description":"drop"}
                ]}}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        using var tool = new WebSearchTool("key", handler);
        var result = await tool.ExecuteAsync(Context(QueryCall("x", allowedDomains: """["example.com"]""")), CancellationToken.None);
        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.Contains("(https://example.com/a)", text);
        Assert.Contains("(https://docs.example.com/b)", text);
        Assert.DoesNotContain("other.org", text);
    }

    [Fact]
    public async Task ExcludesBlockedDomains()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"web":{"results":[
                  {"title":"Keep","url":"https://example.com/a","description":"keep"},
                  {"title":"Drop","url":"https://nope.example/b","description":"drop"}
                ]}}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        using var tool = new WebSearchTool("key", handler);
        var result = await tool.ExecuteAsync(Context(QueryCall("x", blockedDomains: """["nope.example"]""")), CancellationToken.None);
        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.Contains("(https://example.com/a)", text);
        Assert.DoesNotContain("nope.example", text);
    }

    [Fact]
    public async Task RejectsMutuallyExclusiveDomainFilters()
    {
        using var tool = new WebSearchTool("key");
        var issue = await tool.ValidateInputAsync(
            JsonDocument.Parse(QueryCall("x", allowedDomains: """["a.com"]""", blockedDomains: """["b.com"]""")).RootElement.Clone(),
            CancellationToken.None);
        Assert.NotNull(issue);
        Assert.Equal("blocked_domains", issue!.Code);
        Assert.Equal("allowed_domains and blocked_domains cannot both be specified", issue.Message);
    }

    [Fact]
    public async Task ReturnsErrorWithStatusWhenApiFails()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)429)
        {
            ReasonPhrase = "Too Many Requests",
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var tool = new WebSearchTool("key", handler);
        var result = await tool.ExecuteAsync(Context(QueryCall("x")), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("429", result.SingleText());
    }

    [Fact]
    public async Task ReturnsNoResultsMessageForEmptyResponses()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"web":{"results":[]}}""", Encoding.UTF8, "application/json"),
        });
        using var tool = new WebSearchTool("key", handler);
        var result = await tool.ExecuteAsync(Context(QueryCall("zzz")), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(string.Join("\n",
            "Web search results for query: \"zzz\"",
            "",
            "Links:",
            "- No links found.",
            "",
            "REMINDER: You MUST include the sources above in your response to the user using markdown hyperlinks."),
            result.SingleText());
    }

    [Fact]
    public void AvailabilityRequiresNativeSupportOrBraveKey()
    {
        using var withoutBackends = new WebSearchTool(null);
        Assert.False(withoutBackends.IsAvailable(new ModelProperties()));
        Assert.True(withoutBackends.IsAvailable(new ModelProperties(SupportsNativeWebSearch: true)));

        using var withBrave = new WebSearchTool("key");
        Assert.True(withBrave.IsAvailable(new ModelProperties()));
    }

    [Fact]
    public void RegistryHidesWebSearchWhenTheModelHasNoBackend()
    {
        using var tool = new WebSearchTool(null);
        var registry = new ToolRegistry([tool]);
        Assert.Empty(registry.VisibleContractsFor(new ModelProperties()));
        Assert.Single(registry.VisibleContractsFor(new ModelProperties(SupportsNativeWebSearch: true)));
        Assert.Single(registry.VisibleContractsFor(null));
    }

    [Fact]
    public void RegistryConcurrentModelSelections_KeepAvailabilityMatchedToModel()
    {
        using var tool = new WebSearchTool(null);
        var registry = new ToolRegistry([tool]);
        var supported = new ModelProperties(SupportsNativeWebSearch: true);
        var unsupported = new ModelProperties();
        var mismatches = 0;

        Parallel.For(0, 200_000, index =>
        {
            var available = index % 2 == 0;
            var contracts = registry.VisibleContractsFor(available ? supported : unsupported);
            if (contracts.Count != (available ? 1 : 0))
                Interlocked.Increment(ref mismatches);
        });

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public async Task UsesProviderNativeSearchWhenAvailable()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Brave must not be called."));
        using var tool = new WebSearchTool("key", handler);
        var provider = new FakeProviderSearch(new ProviderWebSearchResponse(
            "Native summary.",
            [
                new ProviderWebSearchSource("https://native.example/a", "Native A"),
                new ProviderWebSearchSource("https://native.example/b", "Native B"),
            ],
            1));

        var result = await tool.ExecuteAsync(
            Context(QueryCall("native query", allowedDomains: """["native.example"]"""), provider),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.Contains("Native summary.", text);
        Assert.Contains("- [Native A](https://native.example/a)", text);
        Assert.Contains("- [Native B](https://native.example/b)", text);
        Assert.NotNull(provider.Request);
        Assert.Equal("native query", provider.Request!.Query);
        Assert.Equal(["native.example"], provider.Request.AllowedDomains);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadsMarkdownLinksFromNativeSummary()
    {
        using var tool = new WebSearchTool(null);
        var provider = new FakeProviderSearch(new ProviderWebSearchResponse(
            "See [Example](https://example.com/a) for details.",
            []));

        var result = await tool.ExecuteAsync(Context(QueryCall("native"), provider), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("- [Example](https://example.com/a)", result.SingleText());
    }

    [Fact]
    public async Task DedupesNativeSourcesAgainstSummaryLinks()
    {
        using var tool = new WebSearchTool(null);
        var provider = new FakeProviderSearch(new ProviderWebSearchResponse(
            "See [Example](https://example.com/a).",
            [new ProviderWebSearchSource("https://example.com/a", "Example")]));

        var result = await tool.ExecuteAsync(Context(QueryCall("native"), provider), CancellationToken.None);

        Assert.Single(Regex.Matches(result.SingleText(), @"(?m)^- \[[^\]]+\]\(https://example\.com/a\)"));
    }

    [Fact]
    public async Task FallsBackToBraveWhenNativeSearchIsUnavailable()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"web":{"results":[{"title":"Brave","url":"https://brave.example/a","description":"fallback"}]}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var tool = new WebSearchTool("key", handler);
        var provider = new FakeProviderSearch(new ProviderWebSearchResponse("", []), available: false);

        var result = await tool.ExecuteAsync(Context(QueryCall("fallback"), provider), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("(https://brave.example/a)", result.SingleText());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReturnsErrorWhenNoBackendIsAvailable()
    {
        using var tool = new WebSearchTool(null);
        var result = await tool.ExecuteAsync(Context(QueryCall("x")), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("no Brave API key", result.SingleText());
    }

    private sealed class FakeProviderSearch(ProviderWebSearchResponse response, bool available = true) : IProviderWebSearch
    {
        public bool IsAvailable { get; } = available;

        public ProviderWebSearchRequest? Request { get; private set; }

        public ValueTask<ProviderWebSearchResponse> SearchAsync(
            ProviderWebSearchRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(response);
        }
    }
}
