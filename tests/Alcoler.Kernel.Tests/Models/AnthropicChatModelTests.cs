using System.Net;
using System.Text;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public class AnthropicChatModelTests
{
    private static AnthropicChatModel CreateModel(RecordingHandler handler) =>
        new(new HttpClient(handler), new AnthropicModelOptions("claude-sonnet-4", "test-key", "https://unit.test"));

    private static async Task<List<ModelStreamEvent>> CollectAsync(
        AnthropicChatModel model, ModelRequest request)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in model.StreamAsync(request, CancellationToken.None))
            events.Add(streamEvent);
        return events;
    }

    private static ModelRequest BasicRequest() => new(
        [new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")])],
        [],
        new ModelCallOptions());

    private const string TerminalSse = """
        data: {"type":"message_start","message":{"usage":{"input_tokens":1}}}

        data: {"type":"message_stop"}

        """;

    [Fact]
    public async Task HappyPath_MapsTextToolUseAndUsage()
    {
        var sse = string.Join("\n",
            "event: message_start",
            """data: {"type":"message_start","message":{"id":"m1","usage":{"input_tokens":100,"cache_read_input_tokens":50,"cache_creation_input_tokens":25,"output_tokens":1}}}""",
            "",
            "event: content_block_start",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}""",
            "",
            "event: content_block_stop",
            """data: {"type":"content_block_stop","index":0}""",
            "",
            "event: content_block_start",
            """data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{}}}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"Paris\"}"}}""",
            "",
            "event: content_block_stop",
            """data: {"type":"content_block_stop","index":1}""",
            "",
            "event: message_delta",
            """data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":42}}""",
            "",
            "event: message_stop",
            """data: {"type":"message_stop"}""",
            "",
            "");
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(sse, segmentSize: 13));
        var model = CreateModel(handler);

        var events = await CollectAsync(model, BasicRequest());

        Assert.IsType<ModelStreamStarted>(events[0]);
        Assert.IsType<ModelTextStarted>(events[1]);
        var delta = Assert.IsType<ModelTextDelta>(events[2]);
        Assert.Equal("Hello", delta.Delta);
        Assert.IsType<ModelTextEnded>(events[3]);
        var toolStart = Assert.IsType<ModelToolInputStarted>(events[4]);
        Assert.Equal("toolu_1", toolStart.Id.Value);
        Assert.Equal("get_weather", toolStart.ToolName);
        var jsonDelta1 = Assert.IsType<ModelToolInputDelta>(events[5]);
        var jsonDelta2 = Assert.IsType<ModelToolInputDelta>(events[6]);
        Assert.Equal("{\"city\":", jsonDelta1.DeltaJson);
        Assert.Equal("\"Paris\"}", jsonDelta2.DeltaJson);
        var toolEnd = Assert.IsType<ModelToolInputEnded>(events[7]);
        Assert.Equal("toolu_1", toolEnd.Id.Value);
        var ready = Assert.IsType<ModelToolCallReady>(events[8]);
        Assert.Equal("toolu_1", ready.ToolCall.Id.Value);
        Assert.Equal("get_weather", ready.ToolCall.Name);
        Assert.Equal("Paris", ready.ToolCall.Input.GetProperty("city").GetString());
        var finish = Assert.IsType<ModelStreamFinished>(events[9]);
        Assert.Equal(ModelFinishReason.ToolCalls, finish.FinishReason);
        var usage = finish.Usage;
        Assert.NotNull(usage);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(42, usage.OutputTokens);
        Assert.Equal(50, usage.CacheReadTokens);
        Assert.Equal(25, usage.CacheWriteTokens);
        Assert.Equal(175, usage.ContextUsageTokens);
    }

    [Fact]
    public async Task ThinkingBlock_EmitsReasoningWithSignature()
    {
        var sse = string.Join("\n",
            """data: {"type":"message_start","message":{"usage":{"input_tokens":10}}}""",
            "",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
            "",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"pondering"}}""",
            "",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc"}}""",
            "",
            """data: {"type":"content_block_stop","index":0}""",
            "",
            """data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":7}}""",
            "",
            """data: {"type":"message_stop"}""",
            "",
            "");
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(sse));
        var model = CreateModel(handler);

        var events = await CollectAsync(model, BasicRequest());

        Assert.Contains(events, e => e is ModelReasoningDelta { Delta: "pondering" });
        var ended = Assert.IsType<ModelReasoningEnded>(
            events.Single(e => e is ModelReasoningEnded));
        Assert.Equal("sig-abc", ended.ProviderSignature);
        var finish = Assert.IsType<ModelStreamFinished>(events.Last());
        Assert.Equal(ModelFinishReason.Stop, finish.FinishReason);
    }

    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://proxy.example/anthropic", "https://proxy.example/anthropic/v1/messages")]
    [InlineData("https://unit.test/V1", "https://unit.test/V1/messages")]
    public async Task MessagesUrl_AppendsV1ExactlyOncePerBaseURLShape(string baseUrl, string expectedUrl)
    {
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(TerminalSse));
        var model = new AnthropicChatModel(
            new HttpClient(handler), new AnthropicModelOptions("claude-sonnet-4", "test-key", baseUrl));

        await CollectAsync(model, BasicRequest());

        Assert.Equal(new Uri(expectedUrl), Assert.Single(handler.Requests).RequestUri);
    }

    [Fact]
    public async Task Request_SendsBothAnthropicAuthHeaderSchemes()
    {
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(TerminalSse));
        var model = CreateModel(handler);

        await CollectAsync(model, BasicRequest());

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("test-key", Assert.Single(sent.Headers.GetValues("x-api-key")));
        Assert.Equal("Bearer test-key", Assert.Single(sent.Headers.GetValues("Authorization")));
    }

    [Theory]
    [InlineData("", "https://api.anthropic.com/v1")]
    [InlineData("   https://relay.test/anthropic/   ", "https://relay.test/anthropic/v1")]
    [InlineData("relay.local/anthropic", "relay.local/anthropic/v1")]
    [InlineData("relay.local/v1/", "relay.local/v1")]
    public void NormalizeBaseUrl_TrimsAndFallsBackForNonAbsoluteRoots(string baseUrl, string expected)
    {
        Assert.Equal(expected, AnthropicEndpoint.NormalizeBaseUrl(baseUrl));
    }

    [Fact]
    public async Task Request_MapsSystemBlocksMessagesToolsAndDefaults()
    {
        var sse = string.Join("\n",
            """data: {"type":"message_start","message":{"usage":{"input_tokens":1}}}""",
            "",
            """data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}""",
            "",
            """data: {"type":"message_stop"}""",
            "",
            "");
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(sse));
        var model = CreateModel(handler);

        var toolInput = JsonSerializer.SerializeToElement(new { city = "Paris" });
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { city = new { type = "string" } },
        });
        var request = new ModelRequest(
        [
            ModelRequestSystemBlocks.ToMessage(["cli prefix", "dynamic rules"]),
            new ModelInputMessage(MessageRole.Assistant,
            [
                new InputToolCallBlock(new ToolCallId("toolu_9"), "get_weather", toolInput),
            ]),
            new ModelInputMessage(MessageRole.User,
            [
                new InputToolResultBlock(new ToolCallId("toolu_9"), "sunny", IsError: true),
                new InputTextBlock("and tomorrow?"),
            ], CacheControl: new CacheControl()),
        ],
        [
            new ToolContract("get_weather", "Gets weather", schema),
        ],
        new ModelCallOptions());

        await CollectAsync(model, request);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://unit.test/v1/messages"), sent.RequestUri);
        Assert.Equal("test-key", Assert.Single(sent.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(sent.Headers.GetValues("anthropic-version")));

        var body = JsonDocument.Parse(handler.RequestBodies[0]!).RootElement;
        Assert.Equal("claude-sonnet-4", body.GetProperty("model").GetString());
        Assert.Equal(32768, body.GetProperty("max_tokens").GetInt32());
        Assert.True(body.GetProperty("stream").GetBoolean());

        var system = body.GetProperty("system");
        Assert.Equal(2, system.GetArrayLength());
        Assert.Equal("cli prefix", system[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("dynamic rules", system[1].GetProperty("text").GetString());

        var messages = body.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("assistant", messages[0].GetProperty("role").GetString());
        var toolUse = messages[0].GetProperty("content")[0];
        Assert.Equal("tool_use", toolUse.GetProperty("type").GetString());
        Assert.Equal("toolu_9", toolUse.GetProperty("id").GetString());
        Assert.Equal("Paris", toolUse.GetProperty("input").GetProperty("city").GetString());

        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        var toolResult = messages[1].GetProperty("content")[0];
        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("toolu_9", toolResult.GetProperty("tool_use_id").GetString());
        Assert.Equal("sunny", toolResult.GetProperty("content").GetString());
        Assert.True(toolResult.GetProperty("is_error").GetBoolean());
        var lastBlock = messages[1].GetProperty("content")[1];
        Assert.Equal("and tomorrow?", lastBlock.GetProperty("text").GetString());
        Assert.Equal("ephemeral", lastBlock.GetProperty("cache_control").GetProperty("type").GetString());

        var tool = body.GetProperty("tools")[0];
        Assert.Equal("get_weather", tool.GetProperty("name").GetString());
        Assert.Equal("Gets weather", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ErrorResponse_IsClassifiedAsRateLimited()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""",
                Encoding.UTF8,
                "application/json"),
            Headers = { { "retry-after", "17" } },
        });
        var model = CreateModel(handler);

        var events = await CollectAsync(model, BasicRequest());

        var failed = Assert.IsType<ModelStreamFailed>(Assert.Single(events));
        Assert.Equal(ModelFailureKind.RateLimited, failed.Failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(17), failed.Failure.RetryAfter);
        Assert.Equal("rate_limit_error", failed.Failure.ProviderCode);
    }

    [Fact]
    public async Task StreamWithoutMessageStop_FailsWithProtocolError()
    {
        var sse = """data: {"type":"message_start","message":{"usage":{"input_tokens":1}}}""" + "\n\n";
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(sse));
        var model = CreateModel(handler);

        var events = await CollectAsync(model, BasicRequest());

        var failed = Assert.IsType<ModelStreamFailed>(events.Last());
        Assert.Equal(ModelFailureKind.StreamProtocolError, failed.Failure.Kind);
    }

    [Fact]
    public async Task ProviderErrorEvent_ProducesOnlyOneTerminalFailure()
    {
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse("""
            data: {"type":"error","error":{"type":"invalid_request_error","message":"invalid thinking options"}}

            """));
        var events = await CollectAsync(CreateModel(handler), BasicRequest());
        var failure = Assert.IsType<ModelStreamFailed>(Assert.Single(events)).Failure;
        Assert.Equal(ModelFailureKind.InvalidRequest, failure.Kind);
        Assert.False(failure.IsRetryable);
    }

    [Fact]
    public async Task InvalidToolJson_BecomesEmptyObjectInput()
    {
        var sse = string.Join("\n",
            """data: {"type":"message_start","message":{"usage":{"input_tokens":1}}}""",
            "",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_2","name":"broken","input":{}}}""",
            "",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{not-json"}}""",
            "",
            """data: {"type":"content_block_stop","index":0}""",
            "",
            """data: {"type":"message_stop"}""",
            "",
            "");
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse(sse));
        var model = CreateModel(handler);

        var events = await CollectAsync(model, BasicRequest());

        var ready = Assert.IsType<ModelToolCallReady>(
            events.Single(e => e is ModelToolCallReady));
        Assert.Equal(JsonValueKind.Object, ready.ToolCall.Input.ValueKind);
        Assert.Empty(ready.ToolCall.Input.EnumerateObject());
    }
}
