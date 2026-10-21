using System.Text.Json;
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
        "chat" => "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
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
