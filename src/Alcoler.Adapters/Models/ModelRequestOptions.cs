using System.Text.Json;
using System.Text.Json.Nodes;
using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

internal static class ModelRequestOptions
{
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

    internal static string Encode(Dictionary<string, object?> payload, ModelProperties properties, ModelCallOptions options, string nativeMaxKey, int fallback)
    {
        Validate(properties, options, fallback);
        var max = EffectiveMax(properties, options, fallback);
        if (properties.MaxOutputTokensMapping is null)
            payload[nativeMaxKey] = max;
        var body = JsonSerializer.SerializeToNode(payload)!.AsObject();
        if (properties.ReasoningMapping is { } reasoning && options.ReasoningLevel is { } level)
            ModelOptionMapping.MergePatch(body, ModelOptionMapping.Evaluate(reasoning, level, max));
        if (properties.MaxOutputTokensMapping is { } output)
            ModelOptionMapping.MergePatch(body, ModelOptionMapping.Evaluate(output, options.ReasoningLevel, max));
        CapOutputLimits(body, max);
        return body.ToJsonString();
    }

    private static void CapOutputLimits(JsonObject body, int max)
    {
        foreach (var key in new[] { "max_tokens", "max_completion_tokens", "max_output_tokens", "budget_tokens" })
        {
            if (body[key] is null) continue;
            var value = body[key];
            if (value is not JsonValue || !double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var tokens) || tokens <= 0 || Math.Truncate(tokens) != tokens)
                throw new ArgumentException($"Mapped {key} must be a positive integer.");
            if (tokens > max) body[key] = max;
        }
    }

    internal static void ValidateRequest(ModelRequest request, ModelProperties properties)
    {
        if (!properties.SupportsToolCall && (request.Tools.Count > 0 || request.Messages.Any(m => m.Content.Any(b => b is InputToolCallBlock or InputToolResultBlock))))
            throw new ArgumentException("This model does not support tool calls. Choose a tool-capable model or remove tools from the request.");
        foreach (var message in request.Messages)
            foreach (var block in message.Content)
                if (block is not (InputTextBlock or InputReasoningBlock or InputImageBlock or InputToolCallBlock or InputToolResultBlock))
                    throw new ArgumentException($"Unsupported model input block '{block.GetType().Name}'.");
    }

    internal static JsonElement ToolSchema(JsonElement schema, ModelProperties properties)
    {
        if (!properties.RequiresMfjsToolSchema) return schema;
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
        return JsonSerializer.SerializeToElement(root);

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
