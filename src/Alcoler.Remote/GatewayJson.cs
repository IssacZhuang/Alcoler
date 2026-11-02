using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Serialization;
using Microsoft.AspNetCore.Http;

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

    public static IResult Raw(byte[] content, string contentType) => new BytesResult(content, contentType);

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

    public static byte[] Wrap(string type, string property, ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WritePropertyName(property);
            writer.WriteRawValue(payload);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private sealed class BytesResult(byte[] content, string contentType, int statusCode = 200) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = contentType;
            httpContext.Response.ContentLength = content.Length;
            await httpContext.Response.Body.WriteAsync(content).ConfigureAwait(false);
        }
    }
}
