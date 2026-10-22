using System.Buffers;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
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

    internal static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    internal static bool IsNullOrWhiteSpace(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
            return true;
        foreach (var value in utf8)
        {
            if (value >= 0x80)
                return string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(utf8));
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r'))
                return false;
        }
        return true;
    }

    internal static void AppendUtf8(ArrayBufferWriter<byte> writer, string value)
    {
        if (value.Length == 0)
            return;
        var count = Encoding.UTF8.GetByteCount(value);
        Span<byte> encoded = count <= 512 ? stackalloc byte[512] : new byte[count];
        var written = Encoding.UTF8.GetBytes(value, encoded);
        writer.Write(encoded[..written]);
    }

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
                Header(response, "x-should-retry")) with
            {
                Phase = ModelFailurePhase.RequestSetup,
                HttpStatusCode = (int)response.StatusCode,
            };
            yield return new SseFrame(FailureEventName, Alcoler.Contracts.Serialization.AlcolerJson.Serialize(failure));
            yield break;
        }

        var parser = new SseParser();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            Stream? bodyStream = null;
            ModelFailure? bodyFailure = null;
            try
            {
                bodyStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                bodyFailure = ModelFailureClassifier.FromException(ex, cancellationToken) with
                {
                    Phase = ModelFailurePhase.ResponseBody,
                    HttpStatusCode = (int)response.StatusCode,
                };
            }
            if (bodyFailure is not null)
            {
                yield return FailureFrame(bodyFailure);
                yield break;
            }
            await using var stream = bodyStream!;

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
                    failure = ModelFailureClassifier.FromException(ex, cancellationToken) with
                    {
                        Phase = ModelFailurePhase.ResponseBody,
                        HttpStatusCode = (int)response.StatusCode,
                    };
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
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            parser.Dispose();
        }

        static SseFrame FailureFrame(ModelFailure failure) =>
            new(FailureEventName, Alcoler.Contracts.Serialization.AlcolerJson.Serialize(failure));

        static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }
}
