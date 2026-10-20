using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Alcoler.Contracts.Models;

namespace Alcoler.Kernel.Models;

public static partial class ModelFailureClassifier
{
    [GeneratedRegex(@"(\d+)\s*tokens?\s*>\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokensGreaterThanRegex();

    [GeneratedRegex(@"(\d+)\s*>\s*(\d+)\s*maximum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GreaterThanMaximumRegex();

    [GeneratedRegex(@"maximum context length is (\d+) tokens?.*?requested (\d+) tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex OpenAiContextLengthRegex();

    public static ModelFailure FromHttpResponse(int statusCode, string? retryAfterHeader, string body)
    {
        var (providerCode, providerMessage) = ReadErrorBody(body);
        var message = providerMessage ?? Truncate(body);

        if (statusCode == 401 || statusCode == 403)
            return new ModelFailure(ModelFailureKind.AuthenticationFailed, message, providerCode);

        if (statusCode == 408)
            return new ModelFailure(ModelFailureKind.Timeout, message, providerCode);

        if (statusCode == 429)
            return new ModelFailure(ModelFailureKind.RateLimited, message, providerCode, ParseRetryAfter(retryAfterHeader));

        if (statusCode == 400 && TryParseContextOverflow(body, out var gap))
            return new ModelFailure(ModelFailureKind.ContextOverflow, message, providerCode, DeclaredTokenGap: gap);

        if (statusCode is >= 400 and < 500)
        {
            var kind = statusCode is 400 or 404 or 405 or 406 or 413 or 414 or 415 or 422
                ? ModelFailureKind.InvalidRequest
                : ModelFailureKind.ProviderError;
            return new ModelFailure(kind, message, providerCode);
        }

        return new ModelFailure(ModelFailureKind.ProviderError, message, providerCode);
    }

    public static ModelFailure FromException(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? new ModelFailure(ModelFailureKind.Cancelled, exception.Message)
                : new ModelFailure(ModelFailureKind.Timeout, exception.Message);
        }
        if (exception is HttpRequestException)
            return new ModelFailure(ModelFailureKind.NetworkError, exception.Message);
        if (exception is TimeoutException)
            return new ModelFailure(ModelFailureKind.Timeout, exception.Message);
        if (exception is JsonException)
            return new ModelFailure(ModelFailureKind.StreamProtocolError, exception.Message);

        return new ModelFailure(ModelFailureKind.ProviderError, exception.Message);
    }

    public static TimeSpan? ParseRetryAfter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);

        if (DateTimeOffset.TryParseExact(
                value, "R", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            var delta = date - DateTimeOffset.UtcNow;
            return delta <= TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        return null;
    }

    public static bool TryParseContextOverflow(string body, out int? declaredTokenGap)
    {
        declaredTokenGap = null;
        if (!body.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("context length", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("maximum context", StringComparison.OrdinalIgnoreCase))
            return false;

        var requestedMax = FindRequestedAndMax(body);
        if (requestedMax is { } pair && pair.Requested > pair.Max)
            declaredTokenGap = pair.Requested - pair.Max;
        return true;
    }

    private static (int Requested, int Max)? FindRequestedAndMax(string body)
    {
        var match = TokensGreaterThanRegex().Match(body);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var requested1) &&
            int.TryParse(match.Groups[2].Value, out var max1))
            return (requested1, max1);

        match = GreaterThanMaximumRegex().Match(body);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var requested2) &&
            int.TryParse(match.Groups[2].Value, out var max2))
            return (requested2, max2);

        match = OpenAiContextLengthRegex().Match(body);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var max3) &&
            int.TryParse(match.Groups[2].Value, out var requested3))
            return (requested3, max3);

        return null;
    }

    private static (string? Code, string? Message) ReadErrorBody(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error))
                return (null, null);

            string? code = null;
            if (error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
                code = codeElement.GetString();
            if (code is null &&
                error.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
                code = typeElement.GetString();

            string? message = null;
            if (error.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
                message = messageElement.GetString();

            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string Truncate(string body) => body.Length <= 300 ? body : body[..300];
}
