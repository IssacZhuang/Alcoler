using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Alcoler.Remote;

internal static class GatewayJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // The contract source context must win so the wire format matches AlcolerJson;
            // the reflection resolver is the fallback so collection wrappers of registered
            // types (e.g. SessionSummary[]) resolve without duplicate registrations.
            TypeInfoResolver = JsonTypeInfoResolver.Combine(AlcolerJsonContext.Default, new DefaultJsonTypeInfoResolver()),
        };
        options.Converters.Add(new StrongIdJsonConverterFactory());
        return options;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static IResult Data<T>(T value) => Raw(Serialize(value), "application/json");

    public static IResult Plain(byte[] json) => Raw(json, "application/json");

    public static IResult Raw(ReadOnlyMemory<byte> content, string contentType, bool enableRanges = false) =>
        new BytesResult(content, contentType, enableRanges: enableRanges);

    public static IResult Error(int statusCode, string error, string? reason = null) =>
        new BytesResult(
            Object(writer =>
            {
                writer.WriteString("error", error);
                if (reason is not null)
                    writer.WriteString("reason", reason);
            }),
            "application/json",
            statusCode);

    public static byte[] Object(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static byte[] Frame(string type, string? code = null) =>
        Object(writer =>
        {
            writer.WriteString("type", type);
            if (code is not null)
                writer.WriteString("code", code);
        });

    public static byte[] SnapshotFrame(ConversationSnapshot snapshot) => Serialize(new SnapshotEnvelope("snapshot", snapshot));

    public static byte[] DeltaFrame(ConversationDelta delta) => Serialize(new DeltaEnvelope("delta", delta));

    private sealed record SnapshotEnvelope(string Type, ConversationSnapshot Snapshot);

    private sealed record DeltaEnvelope(string Type, ConversationDelta Delta);

    private sealed class BytesResult(
        ReadOnlyMemory<byte> content, string contentType, int statusCode = 200, bool enableRanges = false) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            var body = content;
            if (enableRanges)
            {
                response.Headers.AcceptRanges = "bytes";
                if (!httpContext.Request.Headers.ContainsKey(HeaderNames.IfRange)
                    && RangeHeaderValue.TryParse(httpContext.Request.Headers.Range.ToString(), out var range)
                    && string.Equals(range.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase)
                    && range.Ranges.Count == 1)
                {
                    var item = range.Ranges.Single();
                    var start = item.From ?? Math.Max(0, content.Length - item.To!.Value);
                    var end = item.From.HasValue ? Math.Min(item.To ?? content.Length - 1L, content.Length - 1L) : content.Length - 1L;
                    if (start >= content.Length || end < start || (item.From is null && item.To == 0))
                    {
                        response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                        response.Headers.ContentRange = $"bytes */{content.Length}";
                        response.ContentLength = 0;
                        return;
                    }
                    body = content.Slice((int)start, (int)(end - start + 1));
                    response.StatusCode = StatusCodes.Status206PartialContent;
                    response.Headers.ContentRange = $"bytes {start}-{end}/{content.Length}";
                }
            }
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
