using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Serialization;

using Alcoler.Kernel.Models;

namespace Alcoler.Adapters.Models;

internal static class SseHttp
{
    internal const string FailureEventName = "alcoler-failure";

    internal static ModelFailure DeserializeFailure(string json) =>
        AlcolerJson.Deserialize<ModelFailure>(json)
        ?? new ModelFailure(ModelFailureKind.StreamProtocolError, "unreadable failure payload");

    internal static int? ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.Number &&
        element.TryGetInt32(out var value)
            ? value
            : null;

    /// <summary>
    /// Reads an HTTP response body as SSE frames. Exceptions translate into a failure
    /// result instead of propagating; end of body flushes the parser. A non-success
    /// status is classified from the full body. Never yields and never throws.
    /// </summary>
    public static async IAsyncEnumerable<SseFrame> ReadFramesAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var failure = ModelFailureClassifier.FromHttpResponse(
                (int)response.StatusCode,
                Header(response, "retry-after"),
                errorBody,
                Header(response, "retry-after-ms"),
                Header(response, "x-should-retry"));
            yield return new SseFrame(FailureEventName, Alcoler.Contracts.Serialization.AlcolerJson.Serialize(failure));
            yield break;
        }

        var parser = new SseParser();
        var buffer = new byte[8192];
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            IReadOnlyList<SseFrame>? frames = null;
            ModelFailure? failure = null;
            var endOfStream = false;
            try
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0)
                    frames = parser.Feed(buffer.AsSpan(0, read));
                else
                {
                    endOfStream = true;
                    frames = parser.Flush();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ModelFailureClassifier.FromException(ex, cancellationToken);
            }
            if (failure is not null)
            {
                yield return FailureFrame(failure);
                yield break;
            }
            foreach (var frame in frames!)
                yield return frame;
            if (endOfStream)
                yield break;
        }

        static SseFrame FailureFrame(ModelFailure failure) =>
            new(FailureEventName, Alcoler.Contracts.Serialization.AlcolerJson.Serialize(failure));

        static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }
}
