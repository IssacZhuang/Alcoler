using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

internal static class ModelRequestOptions
{
    private const int MaxCachedToolSchemas = 1024;
    // Below this size the transfer saving is noise, so the CPU and byte identity
    // of a plain body are kept for the many small requests in a tool loop.
    private const int GzipRequestThreshold = 4096;
    // Request-body serialization grows its buffer by doubling; a deep-context body
    // leaves power-of-two arrays on the LOH on every request (ArrayPool.Shared
    // buckets stop at 1 MiB). Writers are retained per thread so growth amortizes
    // to once per capacity tier; the capacity cap stops a one-off giant body from
    // pinning its buffer for the life of a pool thread.
    private const int InitialBodyCapacity = 64 * 1024;
    private const int MaxThreadBodyWriterCapacity = 16 * 1024 * 1024;
    [ThreadStatic]
    private static BodyBufferWriter? ThreadBodyWriter;
    private static readonly string[] OutputLimitKeys = ["max_tokens", "max_completion_tokens", "max_output_tokens", "budget_tokens"];
    private static readonly ConcurrentDictionary<JsonElement, JsonElement> RewrittenToolSchemas = new();

    // Matches JSON.stringify: the body is sent as application/json, never embedded in HTML.
    internal static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The serializer resolves an unset MaxDepth (0) to 64 on its internal writer;
    // a manually built writer treats 0 as unlimited, so the limit must be mapped.
    private static readonly JsonWriterOptions BodyWriterOptions = new()
    {
        Encoder = RequestJsonOptions.Encoder,
        MaxDepth = RequestJsonOptions.MaxDepth is 0 ? 64 : RequestJsonOptions.MaxDepth,
    };

    internal static int EffectiveMax(ModelProperties properties, ModelCallOptions options, int fallback)
    {
        properties.ValidateOptions(options);
        var requested = options.MaxOutputTokens ?? properties.DefaultMaxOutputTokens ?? fallback;
        return properties.DefaultMaxOutputTokens is { } cap ? Math.Min(requested, cap) : requested;
    }

    internal static void Validate(ModelProperties properties, ModelCallOptions options, int fallback)
    {
        var max = EffectiveMax(properties, options, fallback);
        if (properties.ReasoningMapping is { } reasoning)
        {
            ModelOptionMapping.Validate(reasoning);
            if (options.ReasoningLevel is { } level)
                CapOutputLimits(ModelOptionMapping.Evaluate(reasoning, level, max), max);
        }
        if (properties.MaxOutputTokensMapping is { } output)
            CapOutputLimits(ModelOptionMapping.Evaluate(output, options.ReasoningLevel, max), max);
    }

    internal static string Encode(Dictionary<string, object?> payload, ModelProperties properties, ModelCallOptions options, string nativeMaxKey, int fallback) =>
        Prepare(payload, properties, options, nativeMaxKey, fallback).ToJsonString(RequestJsonOptions);

    internal static HttpContent CreateContent(JsonObject body, bool gzipRequests = false)
    {
        try
        {
            var writer = RentBodyWriter();
            try
            {
                using (var json = new Utf8JsonWriter(writer, BodyWriterOptions))
                {
                    JsonSerializer.Serialize(json, body, RequestJsonContext.JsonObject);
                }
                var plain = writer.WrittenSpan;
                var gzipped = gzipRequests && plain.Length >= GzipRequestThreshold;
                var payload = gzipped ? Gzip(plain) : plain.ToArray();
                var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                if (gzipped)
                    content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
                return content;
            }
            finally
            {
                ReturnBodyWriter(writer);
            }
        }
        catch (JsonException ex) when (ex.InnerException is InvalidOperationException inner)
        {
            // JsonNode.ToJsonString exposed writer depth errors without the serializer wrapper.
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    private static byte[] Gzip(ReadOnlySpan<byte> plain)
    {
        using var output = new MemoryStream(plain.Length);
        using (var compressor = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(plain);
        return output.ToArray();
    }

    private static BodyBufferWriter RentBodyWriter()
    {
        var writer = ThreadBodyWriter;
        ThreadBodyWriter = null;
        return writer ?? new BodyBufferWriter(InitialBodyCapacity);
    }

    private static void ReturnBodyWriter(BodyBufferWriter writer)
    {
        if (writer.Capacity > MaxThreadBodyWriterCapacity || ThreadBodyWriter is not null)
        {
            writer.Dispose();
            return;
        }
        writer.ResetWrittenCount();
        ThreadBodyWriter = writer;
    }

    /// <summary>
    /// An <see cref="IBufferWriter{T}"/> whose growth rents from ArrayPool.Shared, retained
    /// per thread by this class so large bodies stop paying doubling allocations per call.
    /// </summary>
    private sealed class BodyBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer;
        private int _written;

        public BodyBufferWriter(int initialCapacity) => _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);

        public int Capacity => _buffer.Length;

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

        public void ResetWrittenCount() => _written = 0;

        public void Advance(int count) => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint > 0 ? sizeHint <= _buffer.Length - _written : _written < _buffer.Length)
                return;
            var required = Math.Max((long)_buffer.Length * 2, _written + Math.Max(sizeHint, 1));
            var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(required, Array.MaxLength));
            _buffer.AsSpan(0, _written).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
            _written = 0;
        }
    }

    private static readonly ModelRequestJsonContext RequestJsonContext = new(new JsonSerializerOptions(RequestJsonOptions)
    {
        Converters = { new RequestBodyConverter() },
    });

    private sealed class RequestBodyConverter : JsonConverter<JsonObject>
    {
        public override JsonObject? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        // Customized JsonValues must retain the original options' resolver and error behavior.
        public override void Write(Utf8JsonWriter writer, JsonObject value, JsonSerializerOptions options) =>
            value.WriteTo(writer, RequestJsonOptions);
    }

    internal static JsonObject Prepare(Dictionary<string, object?> payload, ModelProperties properties, ModelCallOptions options, string nativeMaxKey, int fallback)
    {
        Validate(properties, options, fallback);
        var max = EffectiveMax(properties, options, fallback);
        if (properties.MaxOutputTokensMapping is null)
            payload[nativeMaxKey] = max;
        var body = ToNode(payload)!.AsObject();
        if (properties.ReasoningMapping is { } reasoning && options.ReasoningLevel is { } level)
            ModelOptionMapping.MergePatch(body, ModelOptionMapping.Evaluate(reasoning, level, max));
        if (properties.MaxOutputTokensMapping is { } output)
            ModelOptionMapping.MergePatch(body, ModelOptionMapping.Evaluate(output, options.ReasoningLevel, max));
        CapOutputLimits(body, max);
        return body;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        JsonElement { ValueKind: JsonValueKind.Undefined } => null,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        IDictionary<string, object?> map => new JsonObject(map.Select(pair => KeyValuePair.Create(pair.Key, ToNode(pair.Value)))),
        IEnumerable<object?> values => new JsonArray(values.Select(ToNode).ToArray()),
        _ => throw new NotSupportedException($"Unsupported request payload value {value.GetType().Name}."),
    };

    private static void CapOutputLimits(JsonObject body, int max)
    {
        foreach (var key in OutputLimitKeys)
        {
            if (body[key] is null) continue;
            var value = body[key];
            if (value is not JsonValue jsonValue || !TryGetTokenCount(jsonValue, out var tokens) || tokens <= 0 || Math.Truncate(tokens) != tokens)
                throw new ArgumentException($"Mapped {key} must be a positive integer.");
            if (tokens > max) body[key] = max;
        }
    }

    private static bool TryGetTokenCount(JsonValue value, out double tokens)
    {
        if (value.TryGetValue(out double number)) { tokens = number; return true; }
        if (value.TryGetValue(out int integer)) { tokens = integer; return true; }
        if (value.TryGetValue(out long longNumber)) { tokens = longNumber; return true; }
        if (value.TryGetValue(out float single)) { tokens = single; return true; }
        if (value.TryGetValue(out decimal decimalNumber)) { tokens = (double)decimalNumber; return true; }
        return double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out tokens);
    }

    internal static void ValidateRequest(ModelRequest request, ModelProperties properties)
    {
        if (!properties.SupportsToolCall && (request.Tools.Count > 0 || request.Messages.Any(m => m.Content.Any(b => b is InputToolCallBlock or InputToolResultBlock))))
            throw new ArgumentException("This model does not support tool calls. Choose a tool-capable model or remove tools from the request.");
        if (!properties.SupportsNativeWebSearch && request.Tools.Any(t => t.ProviderNative is not null))
            throw new ArgumentException("This model does not support provider-native WebSearch.");
        foreach (var message in request.Messages)
            foreach (var block in message.Content)
                if (block is not (InputTextBlock or InputReasoningBlock or InputImageBlock or InputToolCallBlock or InputToolResultBlock))
                    throw new ArgumentException($"Unsupported model input block '{block.GetType().Name}'.");
    }

    internal static JsonElement ToolSchema(JsonElement schema, ModelProperties properties)
    {
        if (!properties.RequiresMfjsToolSchema) return schema;
        if (RewrittenToolSchemas.TryGetValue(schema, out var cached)) return cached;
        var rewritten = RewriteToolSchema(schema);
        if (RewrittenToolSchemas.Count < MaxCachedToolSchemas)
            RewrittenToolSchemas.TryAdd(schema, rewritten);
        return rewritten;
    }

    private static JsonElement RewriteToolSchema(JsonElement schema)
    {
        var root = JsonNode.Parse(schema.GetRawText()) ?? throw new ArgumentException("Tool schema must be a JSON object.");
        if (root is not JsonObject obj) throw new ArgumentException("Tool schema must be a JSON object.");
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(root);
        if (refs.Count == 0) return schema;
        var definitions = obj["$defs"] as JsonObject ?? new JsonObject();
        var targets = new Dictionary<string, JsonNode>();
        var index = 0;
        foreach (var reference in refs.Keys.ToArray())
        {
            JsonNode? target = root;
            foreach (var encoded in reference[2..].Split('/'))
            {
                var segment = Uri.UnescapeDataString(encoded).Replace("~1", "/").Replace("~0", "~");
                target = target switch
                {
                    JsonObject o => o[segment],
                    JsonArray a when int.TryParse(segment, out var i) && i >= 0 && i < a.Count => a[i],
                    _ => null,
                };
                if (target is null) throw new ArgumentException($"Tool schema contains an unresolvable local reference '{reference}'.");
            }
            string name;
            do { name = $"alcoler_ref_{index++}"; } while (definitions.ContainsKey(name));
            refs[reference] = name;
            targets[name] = target!.DeepClone();
        }
        Rewrite(root);
        foreach (var (name, target) in targets)
        {
            Rewrite(target);
            definitions[name] = target;
        }
        if (obj["$defs"] is null) obj["$defs"] = definitions;
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();

        void Collect(JsonNode? node)
        {
            if (node is JsonObject o)
            {
                if (o["$ref"] is JsonValue v && v.TryGetValue<string>(out var r) && r.StartsWith("#/") && !r.StartsWith("#/$defs/"))
                    refs.TryAdd(r, "");
                foreach (var child in o) Collect(child.Value);
            }
            else if (node is JsonArray a)
                foreach (var child in a) Collect(child);
        }
        void Rewrite(JsonNode? node)
        {
            if (node is JsonObject o)
            {
                if (o["$ref"] is JsonValue v && v.TryGetValue<string>(out var r) && refs.TryGetValue(r, out var name))
                    o["$ref"] = $"#/$defs/{name}";
                foreach (var child in o) Rewrite(child.Value);
            }
            else if (node is JsonArray a)
                foreach (var child in a) Rewrite(child);
        }
    }
}

[JsonSerializable(typeof(JsonObject))]
internal sealed partial class ModelRequestJsonContext : JsonSerializerContext;
