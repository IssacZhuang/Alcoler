using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Alcoler.Adapters.ProviderUsage;

public sealed class ProviderUsageService : IProviderUsageService
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly Lazy<HttpClient> SharedClient = new(CreateHttpClient);
    private readonly HttpClient _httpClient;

    // Injected clients must disable automatic redirects and must not carry default credentials or API headers.
    public ProviderUsageService(HttpClient? httpClient = null) => _httpClient = httpClient ?? SharedClient.Value;

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<ProviderUsageSnapshot> GetUsageAsync(
        string providerId,
        string? baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var adapterId = ProviderUsageDetection.DetectAdapter(baseUrl);
        var kind = adapterId is { } adapter ? ProviderUsageDetection.GetKind(adapter) : (ProviderUsageKind?)null;

        if (adapterId is not { } id)
        {
            return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.Unsupported, "Usage is not supported for this provider.");
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.MissingApiKey, "An API key is required.");
        }
        for (var i = 0; i < apiKey.Length; i++)
        {
            if (char.IsControl(apiKey[i]))
                return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.AuthFailed, "The API key is not valid.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var token = timeout.Token;
        try
        {
            var endpoint = ProviderUsageAdapters.GetEndpoint(id, baseUrl!);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("Authorization", id == ProviderUsageAdapterId.Zhipu ? apiKey : $"Bearer {apiKey}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (id == ProviderUsageAdapterId.Zhipu)
            {
                request.Headers.AcceptLanguage.ParseAdd("en-US,en");
                request.Content = new ByteArrayContent([]);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.AuthFailed,
                    $"Authentication failed (HTTP {(int)response.StatusCode}).");
            }
            if (!response.IsSuccessStatusCode)
            {
                return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.BadResponse,
                    $"Usage query failed (HTTP {(int)response.StatusCode}).", credentialValid: true);
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.BadResponse, "The usage response is too large.", credentialValid: true);
            }

            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var tiers = ProviderUsageAdapters.Parse(id, endpoint, json.RootElement);
            return new ProviderUsageSnapshot(providerId, id, kind, true, tiers, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.Network, "The usage query timed out. Try again.", credentialValid: true);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.BadResponse, "The usage response is too large.", credentialValid: true);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.Network, "Could not reach the usage service. Try again.", credentialValid: true);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            return Failure(providerId, adapterId, kind, ProviderUsageErrorCode.BadResponse, "The provider returned an invalid usage response.", credentialValid: true);
        }
    }

    private static ProviderUsageSnapshot Failure(string providerId, ProviderUsageAdapterId? adapterId,
        ProviderUsageKind? kind, ProviderUsageErrorCode code, string message, bool credentialValid = false) =>
        new(providerId, adapterId, kind, credentialValid, [], DateTimeOffset.UtcNow, new(code, message));
}
