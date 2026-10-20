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

public class OpenAiCompatibleChatModelTests
{
    private static OpenAiCompatibleChatModel CreateModel(RecordingHandler handler) =>
        new(new HttpClient(handler), new OpenAiCompatibleModelOptions("gpt-test", "sk-test", "https://unit.test/api"));

    private static ModelRequest BasicRequest() => new(
        [new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")])],
        [],
        new ModelCallOptions());

    private static async Task<List<ModelStreamEvent>> CollectAsync(OpenAiCompatibleChatModel model) =>
        await CollectAsync(model, BasicRequest());

    private static async Task<List<ModelStreamEvent>> CollectAsync(
        OpenAiCompatibleChatModel model, ModelRequest request)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var streamEvent in model.StreamAsync(request, CancellationToken.None))
            events.Add(streamEvent);
        return events;
    }

    private static HttpResponseMessage SseResponse(string body) => SegmentedStream.ToResponse(body, segmentSize: 11);

    [Fact]
    public async Task HappyPath_AssemblesChunkedToolCallsAndUsage()
    {
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"role":"assistant","content":""}}]}""",
            "",
            """data: {"choices":[{"delta":{"content":"Hi"}}]}""",
            "",
            """data: {"choices":[{"delta":{"reasoning_content":"thinking hard"}}]}""",
            "",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":""}}]}}]}""",
            "",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":"}}]}}]}""",
            "",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"Oslo\"}"}}]}}]}""",
            "",
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "",
            """data: {"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":4}}}""",
            "",
            "data: [DONE]",
            "",
            "");
        var handler = new RecordingHandler((_, _) => SseResponse(sse));
        var model = CreateModel(handler);

        var events = await CollectAsync(model);

        Assert.IsType<ModelTextStarted>(events[0]);
        var textDelta = Assert.IsType<ModelTextDelta>(events[1]);
        Assert.Equal("Hi", textDelta.Delta);
        var reasoning = Assert.IsType<ModelReasoningDelta>(events[2]);
        Assert.Equal("thinking hard", reasoning.Delta);
        var toolStart = Assert.IsType<ModelToolInputStarted>(events[3]);
        Assert.Equal("call_1", toolStart.Id.Value);
        Assert.Equal("get_weather", toolStart.ToolName);
        Assert.Equal("{\"city\":", Assert.IsType<ModelToolInputDelta>(events[4]).DeltaJson);
        Assert.Equal("\"Oslo\"}", Assert.IsType<ModelToolInputDelta>(events[5]).DeltaJson);
        Assert.IsType<ModelTextEnded>(events[6]);
        var toolEnd = Assert.IsType<ModelToolInputEnded>(events[7]);
        Assert.Equal("call_1", toolEnd.Id.Value);
        var ready = Assert.IsType<ModelToolCallReady>(events[8]);
        Assert.Equal("Oslo", ready.ToolCall.Input.GetProperty("city").GetString());
        var finish = Assert.IsType<ModelStreamFinished>(events.Last());
        Assert.Equal(ModelFinishReason.ToolCalls, finish.FinishReason);
        var usage = finish.Usage;
        Assert.NotNull(usage);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(4, usage.CacheReadTokens);
        Assert.Equal(14, usage.ContextUsageTokens);
    }

    [Fact]
    public async Task Request_MapsSystemToolsAndMessageShapes()
    {
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""",
            "",
            """data: {"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":1}}""",
            "",
            "data: [DONE]",
            "",
            "");
        var handler = new RecordingHandler((_, _) => SseResponse(sse));
        var model = CreateModel(handler);

        var toolInput = JsonSerializer.SerializeToElement(new { path = "a.cs" });
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var request = new ModelRequest(
        [
            ModelRequestSystemBlocks.ToMessage(["system prompt"]),
            new ModelInputMessage(MessageRole.Assistant,
            [
                new InputTextBlock("running tool"),
                new InputToolCallBlock(new ToolCallId("call_7"), "read_file", toolInput),
            ]),
            new ModelInputMessage(MessageRole.User,
            [
                new InputToolResultBlock(new ToolCallId("call_7"), "file body"),
                new InputTextBlock("continue"),
            ]),
        ],
        [new ToolContract("read_file", "Reads a file", schema)],
        new ModelCallOptions(MaxOutputTokens: 1234));

        await CollectAsync(model, request);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://unit.test/api/chat/completions"), sent.RequestUri);
        Assert.Equal("Bearer sk-test", sent.Headers.Authorization?.ToString());

        var body = JsonDocument.Parse(handler.RequestBodies[0]!).RootElement;
        Assert.Equal("gpt-test", body.GetProperty("model").GetString());
        Assert.Equal(1234, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        var messages = body.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt", messages[0].GetProperty("content").GetString());

        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("running tool", messages[1].GetProperty("content").GetString());
        var toolCall = messages[1].GetProperty("tool_calls")[0];
        Assert.Equal("call_7", toolCall.GetProperty("id").GetString());
        Assert.Equal("function", toolCall.GetProperty("type").GetString());
        Assert.Equal("read_file", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"path\":\"a.cs\"}", toolCall.GetProperty("function").GetProperty("arguments").GetString());

        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_7", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("file body", messages[2].GetProperty("content").GetString());

        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        Assert.Equal("continue", messages[3].GetProperty("content").GetString());

        var tool = body.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task DoneWithoutUsageOrFinish_FailsWithProtocolError()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\ndata: [DONE]\n\n";
        var handler = new RecordingHandler((_, _) => SseResponse(sse));
        var model = CreateModel(handler);

        var events = await CollectAsync(model);

        var failed = Assert.IsType<ModelStreamFailed>(events.Last());
        Assert.Equal(ModelFailureKind.StreamProtocolError, failed.Failure.Kind);
    }

    [Fact]
    public async Task DoneWithUsageButNoFinishReason_SynthesizesStopFinish()
    {
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"content":"hi"}}]}""",
            "",
            """data: {"choices":[],"usage":{"prompt_tokens":2,"completion_tokens":1}}""",
            "",
            "data: [DONE]",
            "",
            "");
        var handler = new RecordingHandler((_, _) => SseResponse(sse));
        var model = CreateModel(handler);

        var events = await CollectAsync(model);

        var finish = Assert.IsType<ModelStreamFinished>(events.Last());
        Assert.Equal(ModelFinishReason.Stop, finish.FinishReason);
        Assert.Equal(2, finish.Usage!.InputTokens);
    }

    [Fact]
    public async Task AuthError_IsClassified()
    {
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"error":{"message":"bad key","type":"invalid_request_error"}}""",
                Encoding.UTF8,
                "application/json"),
        });
        var model = CreateModel(handler);

        var events = await CollectAsync(model);

        var failed = Assert.IsType<ModelStreamFailed>(Assert.Single(events));
        Assert.Equal(ModelFailureKind.AuthenticationFailed, failed.Failure.Kind);
        Assert.Equal("bad key", failed.Failure.Message);
    }
}
