using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public class ModelRequestOptionsTests
{
    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("anthropic")]
    public async Task ConfiguredOptions_MapAfterEncodingAndRespectHardCap(string format)
    {
        var properties = new ModelProperties(DefaultMaxOutputTokens: 2048, ReasoningLevels: ["disabled", "high"],
            ReasoningMapping: "reasoningLevel == 'disabled' ? {'thinking':null,'reasoning':{'enabled':false}} : {'thinking':{'type':'adaptive'},'reasoning':{'effort':reasoningLevel}}",
            MaxOutputTokensMapping: "{'custom_output_limit': maxOutputTokens, 'max_tokens': maxOutputTokens * 2}");
        var handler = Handler(format);
        var model = Model(format, handler, properties);
        await Collect(model, Request(new ModelCallOptions("high", 5000)));
        var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies)!).RootElement;
        Assert.Equal("test", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal(2048, body.GetProperty("custom_output_limit").GetInt32());
        Assert.Equal(2048, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("adaptive", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(body.TryGetProperty(format == "responses" ? "input" : "messages", out _));
        Assert.False(body.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.TryGetProperty("max_output_tokens", out _));

        await Collect(model, Request(new ModelCallOptions("disabled", 1024)));
        body = JsonDocument.Parse(handler.RequestBodies[1]!).RootElement;
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.False(body.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.Equal(1024, body.GetProperty("custom_output_limit").GetInt32());
    }

    [Theory]
    [InlineData("chat", "max_completion_tokens")]
    [InlineData("responses", "max_output_tokens")]
    [InlineData("anthropic", "max_tokens")]
    public async Task AbsentMaps_PreserveNativeDefaultsWithoutInventingForeignFields(string format, string maxKey)
    {
        var handler = Handler(format);
        var properties = new ModelProperties(SupportsJsonSchemaOutput: true, SupportsNativeWebSearch: true);
        await Collect(Model(format, handler, properties), Request(new ModelCallOptions("high")));
        var body = JsonDocument.Parse(handler.RequestBodies[0]!).RootElement;
        Assert.Equal(32768, body.GetProperty(maxKey).GetInt32());
        foreach (var key in new[] { "thinking", "reasoning", "reasoning_effort", "response_format", "tools", "text" })
            Assert.False(body.TryGetProperty(key, out _));
    }

    [Theory]
    [InlineData("chat", "max_completion_tokens")]
    [InlineData("responses", "max_output_tokens")]
    [InlineData("anthropic", "max_tokens")]
    public async Task UncataloguedModel_PreservesLegacyDefaultWithoutImposingNewHardCap(string format, string maxKey)
    {
        var handler = Handler(format);
        var model = Model(format, handler, null);
        await Collect(model, Request(new ModelCallOptions()));
        await Collect(model, Request(new ModelCallOptions(MaxOutputTokens: 65536)));
        Assert.Equal(32768, JsonDocument.Parse(handler.RequestBodies[0]!).RootElement.GetProperty(maxKey).GetInt32());
        Assert.Equal(65536, JsonDocument.Parse(handler.RequestBodies[1]!).RootElement.GetProperty(maxKey).GetInt32());
    }

    [Theory]
    [InlineData("chat", "max_completion_tokens")]
    [InlineData("responses", "max_output_tokens")]
    [InlineData("anthropic", "max_tokens")]
    public async Task ConfiguredDefault_IsDefaultAndHardCap(string format, string maxKey)
    {
        var handler = Handler(format);
        var model = Model(format, handler, new ModelProperties(DefaultMaxOutputTokens: 1200));
        await Collect(model, Request(new ModelCallOptions()));
        await Collect(model, Request(new ModelCallOptions(MaxOutputTokens: 9999)));
        await Collect(model, Request(new ModelCallOptions(MaxOutputTokens: 500)));
        Assert.Equal(new[] { 1200, 1200, 500 }, handler.RequestBodies.Select(b => JsonDocument.Parse(b!).RootElement.GetProperty(maxKey).GetInt32()));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("anthropic")]
    public async Task InvalidOptionsOrMappings_DoNotSendHttp(string format)
    {
        var handler = Handler(format);
        var model = Model(format, handler, new ModelProperties(ReasoningLevels: ["high"]));
        foreach (var options in new[] { new ModelCallOptions("low"), new ModelCallOptions(MaxOutputTokens: 0), new ModelCallOptions(MaxOutputTokens: -10) })
        {
            var failure = Assert.IsType<ModelStreamFailed>(Assert.Single(await Collect(model, Request(options)))).Failure;
            Assert.Equal(ModelFailureKind.InvalidRequest, failure.Kind);
            Assert.False(failure.IsRetryable);
        }
        model = Model(format, handler, new ModelProperties(MaxOutputTokensMapping: "{'model':'evil'}"));
        Assert.IsType<ModelStreamFailed>(Assert.Single(await Collect(model, Request(new ModelCallOptions()))));
        foreach (var invalid in new[] { "{'max_tokens':0}", "{'max_tokens':'2048'}", "{'max_tokens':[]}" })
        {
            model = Model(format, handler, new ModelProperties(MaxOutputTokensMapping: invalid));
            Assert.Throws<ArgumentException>(() => model.ValidateOptions(new ModelCallOptions()));
            Assert.IsType<ModelStreamFailed>(Assert.Single(await Collect(model, Request(new ModelCallOptions()))));
        }
        model = Model(format, handler, new ModelProperties(SupportsToolCall: false));
        var withTools = Request(new ModelCallOptions()) with { Tools = [new ToolContract("tool", "description", JsonSerializer.SerializeToElement(new { type = "object" }))] };
        Assert.IsType<ModelStreamFailed>(Assert.Single(await Collect(model, withTools)));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("chat", true, "system")]
    [InlineData("responses", true, "system")]
    [InlineData("chat", false, "user")]
    [InlineData("responses", false, "user")]
    [InlineData("anthropic", true, "system")]
    [InlineData("anthropic", false, "user")]
    public async Task MidConversationSystem_IsNotHoisted(string format, bool supported, string expectedRole)
    {
        var handler = Handler(format);
        var request = new ModelRequest([
            new ModelInputMessage(MessageRole.System, [new InputTextBlock("initial")]),
            new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")]),
            new ModelInputMessage(MessageRole.System, [new InputTextBlock("middle")]),
            new ModelInputMessage(MessageRole.Assistant, [new InputTextBlock("ok")])], [], new ModelCallOptions());
        await Collect(Model(format, handler, new ModelProperties(SupportsMidConversationSystem: supported)), request);
        var body = JsonDocument.Parse(handler.RequestBodies[0]!).RootElement;
        var messages = body.GetProperty(format == "responses" ? "input" : "messages");
        var mid = messages[format == "anthropic" ? 1 : 2];
        Assert.Equal(expectedRole, mid.GetProperty("role").GetString());
        if (format == "anthropic") Assert.Single(body.GetProperty("system").EnumerateArray());
    }

    [Fact]
    public void Encode_KeepsMarkdownAndNonAsciiLiteral()
    {
        var encoded = ModelRequestOptions.Encode(
            new Dictionary<string, object?> { ["value"] = "don't use `x` — 中文" },
            new ModelProperties(),
            new ModelCallOptions(),
            "max_tokens",
            128);

        Assert.Contains("\"value\":\"don't use `x` — 中文\"", encoded);
        Assert.DoesNotContain("\\u0027", encoded);
        Assert.DoesNotContain("\\u0060", encoded);
        Assert.DoesNotContain("\\u2014", encoded);
    }

    [Theory]
    [InlineData("chat", "max_completion_tokens")]
    [InlineData("chat-legacy", "max_tokens")]
    [InlineData("responses", "max_output_tokens")]
    [InlineData("anthropic", "max_tokens")]
    public async Task RequestContent_PreservesLegacyWireBodyAndHeaders(string format, string maxKey)
    {
        const string text = "don't use `x` — 中文 <>& \ud834\udd1e";
        var payload = new Dictionary<string, object?> { ["model"] = "test" };
        if (format != "responses") payload["stream"] = true;
        var item = new Dictionary<string, object?> { ["role"] = "user" };
        switch (format)
        {
            case "responses":
                item["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = text } };
                payload["input"] = new object[] { item };
                payload["stream"] = true;
                payload["store"] = false;
                payload["include"] = new[] { "reasoning.encrypted_content" };
                break;
            case "anthropic":
                item["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } };
                payload["messages"] = new object[] { item };
                break;
            default:
                item["content"] = text;
                payload["messages"] = new object[] { item };
                payload["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
                break;
        }
        var properties = new ModelProperties();
        var options = new ModelCallOptions(MaxOutputTokens: 512);
        using var expected = new StringContent(ModelRequestOptions.Encode(payload, properties, options, maxKey, 32768), Encoding.UTF8, "application/json");
        var handler = Handler(format);
        IChatModel model = format == "chat-legacy"
            ? new OpenAiCompatibleChatModel(new HttpClient(handler), new("test", "key", "https://test/v1", UseLegacyMaxTokens: true), properties)
            : Model(format, handler, properties);
        await Collect(model, new([new(MessageRole.User, [new InputTextBlock(text)])], [], options));
        var content = Assert.IsType<ByteArrayContent>(Assert.Single(handler.Requests).Content);
        Assert.Equal(await expected.ReadAsStringAsync(), Assert.Single(handler.RequestBodies));
        Assert.Equal(expected.Headers.ContentLength, content.Headers.ContentLength);
        Assert.Equal(expected.Headers.ContentType, content.Headers.ContentType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateContent_MatchesLegacyEncodingWithRawValuesAndNestedOverrides(bool account)
    {
        var properties = new ModelProperties(DefaultMaxOutputTokens: 128, ReasoningLevels: ["high"],
            ReasoningMapping: "{'custom':{'remove':null,'replace':2,'added':{'level':reasoningLevel}},'obsolete':null,'store':true,'max_tokens':999}",
            MaxOutputTokensMapping: "{'custom':{'added':{'enabled':true}},'max_output_tokens':maxOutputTokens*2,'budget_tokens':maxOutputTokens*2}");
        var options = new ModelCallOptions("high", 256);
        var expected = ModelRequestOptions.Encode(RawPayload(), properties, options, "max_output_tokens", 32768);
        if (account) expected = LegacyAccountShape(expected, "affinity-key");

        var body = ModelRequestOptions.Prepare(RawPayload(), properties, options, "max_output_tokens", 32768);
        if (account) OpenAiResponsesAccountShaping.Shape(body, "affinity-key");
        using var content = ModelRequestOptions.CreateContent(body);
        var bytes = await content.ReadAsByteArrayAsync();

        Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(bytes)));
        Assert.Equal("application/json; charset=utf-8", content.Headers.ContentType!.ToString());
        Assert.Equal(bytes.Length, content.Headers.ContentLength);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var custom = root.GetProperty("custom");
        Assert.False(custom.TryGetProperty("remove", out _));
        Assert.Equal("kept", custom.GetProperty("keep").GetString());
        Assert.Equal(2, custom.GetProperty("replace").GetInt32());
        Assert.Equal("high", custom.GetProperty("added").GetProperty("level").GetString());
        Assert.True(custom.GetProperty("added").GetProperty("enabled").GetBoolean());
        Assert.False(root.TryGetProperty("obsolete", out _));
        Assert.Equal(128, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(128, root.GetProperty("budget_tokens").GetInt32());
        Assert.Equal(!account, root.GetProperty("store").GetBoolean());
        Assert.Equal(account ? "developer" : "system", root.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.Equal("keep nested metadata", root.GetProperty("raw").GetProperty("metadata").GetString());
        if (account)
        {
            Assert.Equal("affinity-key", root.GetProperty("prompt_cache_key").GetString());
            Assert.False(root.TryGetProperty("max_output_tokens", out _));
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.Equal("rules", root.GetProperty("instructions").GetString());
        }
        else
        {
            Assert.Equal(128, root.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("prompt_cache_key").ValueKind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateContent_MalformedUtf16PreservesDecodedStringValues(bool account)
    {
        Dictionary<string, object?> Payload() => new() { ["text"] = "prefix \ud800 middle \udfff suffix" };
        var expected = ModelRequestOptions.Encode(Payload(), new(), new(), "max_tokens", 128);
        if (account) expected = LegacyAccountShape(expected, "affinity-key");
        var body = ModelRequestOptions.Prepare(Payload(), new(), new(), "max_tokens", 128);
        if (account) OpenAiResponsesAccountShaping.Shape(body, "affinity-key");
        using var content = ModelRequestOptions.CreateContent(body);
        var actual = await content.ReadAsByteArrayAsync();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)));
        using var document = JsonDocument.Parse(actual);
        Assert.Equal("prefix \ufffd middle \ufffd suffix", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void RawJsonValues_PreserveCustomizedNodeErrorsWithoutBindingSharedOptions()
    {
        var node = JsonValue.Create(new Dictionary<string, string> { ["PascalCase"] = "中文 <>&" })!;
        var expected = Record.Exception(() => node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var actual = Record.Exception(() =>
        {
            var payload = new Dictionary<string, object?> { ["custom"] = node };
            using var content = ModelRequestOptions.CreateContent(ModelRequestOptions.Prepare(payload, new(), new(), "max_tokens", 128));
        });
        Assert.IsType<InvalidOperationException>(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Message, actual.Message);
        Assert.Null(ModelRequestOptions.RequestJsonOptions.TypeInfoResolver);
    }

    [Theory]
    [InlineData(null, "affinity-key")]
    [InlineData("explicit-key", "explicit-key")]
    [InlineData("", "")]
    public async Task AccountShaping_PreservesExplicitCacheKeyAndAppliesMappingsBeforeShaping(string? explicitKey, string expectedKey)
    {
        var properties = new ModelProperties(ReasoningLevels: ["high"],
            ReasoningMapping: "{'prompt_cache_key':'reasoning-key','store':true,'temperature':0.5}",
            MaxOutputTokensMapping: explicitKey is null ? "{'prompt_cache_key':null}" : $"{{'prompt_cache_key':'{explicitKey}'}}");
        var body = ModelRequestOptions.Prepare(RawPayload(), properties, new("high"), "max_output_tokens", 128);
        OpenAiResponsesAccountShaping.Shape(body, "affinity-key");
        using var content = ModelRequestOptions.CreateContent(body);
        using var document = JsonDocument.Parse(await content.ReadAsByteArrayAsync());
        Assert.Equal(expectedKey, document.RootElement.GetProperty("prompt_cache_key").GetString());
        Assert.False(document.RootElement.GetProperty("store").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CreateContent_PreservesPayloadErrors(int invalidKind)
    {
        Dictionary<string, object?> Payload() => new()
        {
            ["value"] = invalidKind switch
            {
                0 => (object)(short)1,
                1 => new object(),
                2 => new Dictionary<string, string> { ["key"] = "value" },
                3 => double.NaN,
                4 => JsonValue.Create(double.PositiveInfinity),
                _ => NestedNode(64),
            },
        };
        var expected = Record.Exception(() => ModelRequestOptions.Encode(Payload(), new(), new(), "max_tokens", 128));
        var actual = Record.Exception(() =>
        {
            using var content = ModelRequestOptions.CreateContent(ModelRequestOptions.Prepare(Payload(), new(), new(), "max_tokens", 128));
        });
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Message, actual.Message);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 5)]
    [InlineData(true, 5)]
    public void CreateContent_LargePayloadAllocatesOnlyOneOwnedUtf8Body(bool account, int inputMiB)
    {
        var text = new string('x', inputMiB * 1024 * 1024);
        var payload = new Dictionary<string, object?>
        {
            ["input"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = "rules" },
                new Dictionary<string, object?> { ["type"] = "function_call_output", ["output"] = text },
            },
            ["stream"] = true,
        };
        var properties = new ModelProperties();
        var options = new ModelCallOptions();
        HttpContent EncodeContent()
        {
            var body = ModelRequestOptions.Prepare(payload, properties, options, "max_output_tokens", 32768);
            if (account) OpenAiResponsesAccountShaping.Shape(body, "affinity-key");
            return ModelRequestOptions.CreateContent(body);
        }
        for (var i = 0; i < 3; i++)
        {
            using var warmup = EncodeContent();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var content = EncodeContent();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(content.Headers.ContentLength!.Value, text.Length, text.Length + 1024L);
        Assert.InRange(allocated, 0, text.Length + 64 * 1024L);
    }

    [Fact]
    public async Task CreateContent_IsReplayableAndHonorsCancellationAndDisposal()
    {
        var body = ModelRequestOptions.Prepare(RawPayload(), new(), new(), "max_tokens", 128);
        var content = ModelRequestOptions.CreateContent(body);
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        await content.CopyToAsync(first);
        await content.CopyToAsync(second);
        Assert.Equal(first.ToArray(), second.ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => content.CopyToAsync(second, cancellation.Token));
        content.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task CreateContent_CompressesLargeBodiesOnlyWhenSupported()
    {
        var text = string.Concat(Enumerable.Repeat("gzip round-trip probe ", 512));
        Dictionary<string, object?> Payload() => new() { ["input"] = new object[] { text }, ["stream"] = true };

        using var plain = ModelRequestOptions.CreateContent(ModelRequestOptions.Prepare(Payload(), new(), new(), "max_tokens", 128));
        using var compressed = ModelRequestOptions.CreateContent(ModelRequestOptions.Prepare(Payload(), new(), new(), "max_tokens", 128), gzipRequests: true);
        var plainBytes = await plain.ReadAsByteArrayAsync();
        var compressedBytes = await compressed.ReadAsByteArrayAsync();

        Assert.Null(plain.Headers.ContentEncoding.FirstOrDefault());
        Assert.Equal("gzip", Assert.Single(compressed.Headers.ContentEncoding));
        Assert.True(compressedBytes.Length < plainBytes.Length / 2);
        Assert.Equal(plainBytes, Gunzip(compressedBytes));
        Assert.Equal(plain.Headers.ContentType, compressed.Headers.ContentType);

        using var small = ModelRequestOptions.CreateContent(
            ModelRequestOptions.Prepare(new Dictionary<string, object?> { ["input"] = new object[] { "tiny" } }, new(), new(), "max_tokens", 128),
            gzipRequests: true);
        Assert.Empty(small.Headers.ContentEncoding);
        Assert.Equal("application/json; charset=utf-8", small.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task GzipRequests_FlowFromPropertiesToHttpRequest()
    {
        var handler = Handler("responses");
        var model = Model("responses", handler, new ModelProperties(SupportsGzipRequests: true));
        await Collect(model, new([new(MessageRole.User, [new InputTextBlock(new string('a', 8192))])], [], new ModelCallOptions()));
        Assert.Equal("gzip", Assert.Single(Assert.Single(handler.Requests).Content!.Headers.ContentEncoding));
    }

    private static byte[] Gunzip(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static JsonNode NestedNode(int depth)
    {
        JsonNode node = JsonValue.Create(1)!;
        for (var i = 0; i < depth; i++) node = new JsonArray(node);
        return node;
    }

    private static Dictionary<string, object?> RawPayload()
    {
        using var document = JsonDocument.Parse("""{"number":1.2300e2,"metadata":"keep nested metadata","nested":[null,{"text":"\\u4e2d\\u6587"}]}""");
        return new()
        {
            ["model"] = "test",
            ["input"] = new object[] { new Dictionary<string, object?> { ["role"] = "system", ["content"] = "rules" } },
            ["stream"] = true,
            ["instructions"] = "rules",
            ["raw"] = document.RootElement.Clone(),
            ["node"] = JsonNode.Parse("""{"list":[true,null,"value"],"value":1.00}"""),
            ["values"] = new object?[]
            {
                null, default(JsonElement), true, 7, long.MaxValue, 1.25, 12.30m,
                JsonValue.Create((short)3), JsonValue.Create(1.25f), JsonValue.Create(ulong.MaxValue),
                JsonValue.Create(new DateTime(2026, 11, 4, 12, 34, 56, DateTimeKind.Utc)),
            },
            ["text"] = "don't use `x` — 中文 <>& \\\"\n\r\t\0 \u2028\u2029 \ud834\udd1e",
            ["custom"] = new Dictionary<string, object?> { ["remove"] = true, ["keep"] = "kept", ["replace"] = new object[] { 1, 2 } },
            ["obsolete"] = true,
            ["prompt_cache_key"] = null,
            ["temperature"] = 0.75,
        };
    }

    private static string LegacyAccountShape(string body, string sessionCacheKey)
    {
        var root = JsonNode.Parse(body)!.AsObject();
        foreach (var field in new[]
        {
            "background", "conversation", "max_output_tokens", "max_tool_calls", "metadata", "moderation", "multi_agent",
            "prompt", "prompt_cache_retention", "safety_identifier", "temperature", "top_logprobs", "top_p", "truncation", "user", "previous_response_id",
        })
            root.Remove(field);
        root["store"] = false;
        if (root["prompt_cache_key"] is null) root["prompt_cache_key"] = sessionCacheKey;
        if (root["input"] is JsonArray input)
            foreach (var item in input)
                if (item is JsonObject message && (string?)message["role"] == "system")
                    message["role"] = "developer";
        return root.ToJsonString(ModelRequestOptions.RequestJsonOptions);
    }

    [Fact]
    public void MfjsSchemas_HoistNonDefsLocalReferencesWithoutChangingContract()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "string" }, alias = new Dictionary<string, string> { ["$ref"] = "#/properties/value" } } });
        var normalized = ModelRequestOptions.ToolSchema(schema, new ModelProperties(RequiresMfjsToolSchema: true));
        var reference = normalized.GetProperty("properties").GetProperty("alias").GetProperty("$ref").GetString()!;
        Assert.StartsWith("#/$defs/", reference);
        Assert.Equal("string", normalized.GetProperty("$defs").GetProperty(reference[8..]).GetProperty("type").GetString());
        Assert.Equal("#/properties/value", schema.GetProperty("properties").GetProperty("alias").GetProperty("$ref").GetString());
    }

    private static ModelRequest Request(ModelCallOptions options) => new([new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")])], [], options);

    private static RecordingHandler Handler(string format) => new((_, _) => SegmentedStream.ToResponse(format switch
    {
        "chat" or "chat-legacy" => "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
        "responses" => "data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n",
        _ => "data: {\"type\":\"message_stop\"}\n\n",
    }, 7));

    private static IChatModel Model(string format, RecordingHandler handler, ModelProperties? properties) => format switch
    {
        "chat" => new OpenAiCompatibleChatModel(new HttpClient(handler), new("test", "key", "https://test/v1"), properties),
        "responses" => new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "key", "https://test/v1"), properties),
        _ => new AnthropicChatModel(new HttpClient(handler), new("test", "key", "https://test"), properties),
    };

    private static async Task<List<ModelStreamEvent>> Collect(IChatModel model, ModelRequest request)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var e in model.StreamAsync(request, CancellationToken.None)) result.Add(e);
        return result;
    }
}
