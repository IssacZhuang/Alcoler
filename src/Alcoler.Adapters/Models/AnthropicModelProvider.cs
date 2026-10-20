using System.Net.Http;

namespace Alcoler.Adapters.Models;

internal static class AnthropicEndpoint
{
    private const string DefaultBaseUrl = "https://api.anthropic.com";
    private const string ApiPrefix = "/v1";

    internal static string NormalizeBaseUrl(string? baseUrl)
    {
        var trimmed = baseUrl?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            trimmed = DefaultBaseUrl;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            var builder = new UriBuilder(url);
            // The path must end in exactly one /v1, whatever casing or trailing
            // slashes the configured root carries; the messages endpoint is
            // "<normalized base>/messages".
            var path = url.AbsolutePath.TrimEnd('/');
            builder.Path = path.EndsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase) ? path : path + ApiPrefix;
            return builder.Uri.ToString();
        }

        var withoutTrailingSlashes = trimmed.TrimEnd('/');
        return withoutTrailingSlashes.EndsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase)
            ? withoutTrailingSlashes
            : withoutTrailingSlashes + ApiPrefix;
    }

    internal static void AddAuthHeaders(HttpRequestMessage message, string apiKey)
    {
        message.Headers.Add("x-api-key", apiKey);
        // Anthropic-compatible gateways accept either scheme, so both must be sent.
        message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
    }
}
