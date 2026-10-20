using System.Net;
using System.Text.Json;

namespace Alcoler.Adapters.Web;

public sealed record WebSearchResult(string? Title, string Url, string? Description);

/// <summary>
/// Brave Search API failure surfaced to the tool so the status code reaches the
/// model-facing error text.
/// </summary>
public sealed class BraveSearchApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class BraveWebSearchClient : IDisposable
{
    private const string SearchEndpoint = "https://api.search.brave.com/res/v1/web/search";
    private const int ResultCount = 20;

    private readonly HttpClient _httpClient;

    public BraveWebSearchClient(string apiKey, HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null
            ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
            : new HttpClient(handler, disposeHandler: false);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Subscription-Token", apiKey);
    }

    public async ValueTask<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var url = $"{SearchEndpoint}?q={Uri.EscapeDataString(query)}&count={ResultCount}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase?.Trim();
            throw new BraveSearchApiException(
                status,
                $"Brave Search API returned HTTP {status}{(string.IsNullOrEmpty(reason) ? "" : $" {reason}")}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<WebSearchResult>();
        if (document.RootElement.TryGetProperty("web", out var web)
            && web.TryGetProperty("results", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
                    continue;
                results.Add(new WebSearchResult(
                    ReadString(item, "title"),
                    urlElement.GetString()!,
                    ReadString(item, "description")));
            }
        }
        return results;
    }

    public void Dispose() => _httpClient.Dispose();

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
