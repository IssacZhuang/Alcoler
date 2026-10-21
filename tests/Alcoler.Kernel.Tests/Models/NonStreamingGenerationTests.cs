using System.Net;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.Models;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public class NonStreamingGenerationTests
{
    private static ModelRequest Request => new([new(MessageRole.User, [new InputTextBlock("summarize")])], [], new(MaxOutputTokens: 4321));

    [Theory]
    [InlineData("anthropic")]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task Generate_SendsJsonRequestAndDecodesTextReasoningToolsAndUsage(string format)
    {
        var handler = new RecordingHandler((_, _) => JsonResponse(Completion(format)));
        var model = Model(format, handler);
        Assert.True(model.SupportsNonStreamingGeneration);
        var events = await model.GenerateAsync(Request, CancellationToken.None);
        using var body = JsonDocument.Parse(handler.RequestBodies.Single()!);
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(4321, body.RootElement.GetProperty(format switch { "anthropic" => "max_tokens", "chat" => "max_completion_tokens", _ => "max_output_tokens" }).GetInt32());
        Assert.False(body.RootElement.TryGetProperty("stream_options", out _));
        Assert.Equal("application/json", Assert.Single(handler.Requests).Headers.Accept.Single().MediaType);
        Assert.Equal("summary", Assert.Single(events.OfType<ModelTextDelta>()).Delta);
        Assert.Single(events.OfType<ModelTextEnded>());
        Assert.Equal("thought", Assert.Single(events.OfType<ModelReasoningDelta>()).Delta);
        Assert.Single(events.OfType<ModelReasoningEnded>());
        var call = Assert.Single(events.OfType<ModelToolCallReady>()).ToolCall;
        Assert.Equal("call", call.Id.Value);
        Assert.Equal(1, call.Input.GetProperty("n").GetInt32());
        var finish = Assert.Single(events.OfType<ModelStreamFinished>());
        Assert.Equal(ModelFinishReason.ToolCalls, finish.FinishReason);
        Assert.Equal(10, finish.Usage!.InputTokens);
        Assert.NotNull(finish.RawFinishReason);
        if (format == "anthropic")
            Assert.Equal("sig", events.OfType<ModelReasoningEnded>().Single().ProviderSignature);
        if (format == "responses")
            Assert.Equal("cipher", events.OfType<ModelReasoningEnded>().Single().EncryptedContent);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task Generate_HttpFailureCarriesSetupPhaseAndStatus(string format)
    {
        var handler = new RecordingHandler((_, _) => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"type":"invalid_request_error","message":"bad request"}}"""),
        });
        var events = await Model(format, handler).GenerateAsync(Request, CancellationToken.None);
        var failure = Assert.Single(events.OfType<ModelStreamFailed>()).Failure;
        Assert.Equal(ModelFailurePhase.RequestSetup, failure.Phase);
        Assert.Equal(400, failure.HttpStatusCode);
        Assert.Equal(ModelFailureKind.InvalidRequest, failure.Kind);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task Generate_MalformedBodyCarriesBodyPhaseAndStatus(string format)
    {
        var handler = new RecordingHandler((_, _) => JsonResponse("not-json"));
        var events = await Model(format, handler).GenerateAsync(Request, CancellationToken.None);
        var failure = Assert.Single(events.OfType<ModelStreamFailed>()).Failure;
        Assert.Equal(ModelFailurePhase.ResponseBody, failure.Phase);
        Assert.Equal(200, failure.HttpStatusCode);
        Assert.Equal(ModelFailureKind.StreamProtocolError, failure.Kind);
    }

    [Fact]
    public async Task AnthropicGenerate_RepairsSignedCopyWithoutRenormalizingOrMutatingRequest()
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) => ++calls == 1
            ? new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"signature in thinking block invalid"}}""") }
            : JsonResponse("""{"content":[{"type":"text","text":"summary"}],"stop_reason":"end_turn"}"""));
        var original = new ModelInputMessage(MessageRole.Assistant,
            [new InputReasoningBlock("signed", "sig"), new InputReasoningBlock("unsigned"), new InputTextBlock("  ")]);
        var request = Request with { Messages = [original, Request.Messages[0]] };
        var events = await Model("anthropic", handler).GenerateAsync(request, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Single(events.OfType<ModelStreamFinished>());
        using var repaired = JsonDocument.Parse(handler.RequestBodies[1]!);
        var content = repaired.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("unsigned", content[0].GetProperty("thinking").GetString());
        Assert.Equal("[Thinking removed]", content[1].GetProperty("text").GetString());
        Assert.Equal(3, original.Content.Count);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task Compaction_BodyFailureFallsBackToOneActualJsonRequest(string format)
    {
        var calls = 0;
        var hidden = format switch
        {
            "anthropic" => "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}\n\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hidden\"}}\n\n",
            "chat" => "data: {\"choices\":[{\"delta\":{\"content\":\"hidden\"}}]}\n\n",
            _ => "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"m\",\"delta\":\"hidden\"}\n\n",
        };
        var handler = new RecordingHandler((_, _) => ++calls == 1
            ? SegmentedStream.ToResponse(hidden + "data: not-json\n\n")
            : JsonResponse(TextCompletion(format)));
        var request = Request;
        var originalJson = Alcoler.Contracts.Serialization.AlcolerJson.Serialize(request);
        var result = await CompactSummaryModelRunner.RunAsync(Model(format, handler), request, CancellationToken.None,
            new ModelRunnerOptions { MaxAttempts = 1 });
        Assert.Null(result.Failure);
        Assert.Equal("summary", result.Text);
        Assert.Equal(2, calls);
        Assert.Equal(originalJson, Alcoler.Contracts.Serialization.AlcolerJson.Serialize(request));
        using var first = JsonDocument.Parse(handler.RequestBodies[0]!);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]!);
        Assert.True(first.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(second.RootElement.GetProperty("stream").GetBoolean());
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    [InlineData(404, true)]
    public async Task Compaction_SetupFailureOnly404AllowsJsonFallback(int status, bool fallsBack)
    {
        var calls = 0;
        var handler = new RecordingHandler((_, _) => ++calls == 1
            ? new((HttpStatusCode)status) { Content = new StringContent("""{"error":{"message":"endpoint unavailable"}}""") }
            : JsonResponse(TextCompletion("chat")));
        var result = await CompactSummaryModelRunner.RunAsync(Model("chat", handler), Request, CancellationToken.None,
            new ModelRunnerOptions { MaxAttempts = 1 });
        Assert.Equal(fallsBack ? 2 : 1, calls);
        Assert.Equal(fallsBack, result.Failure is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compaction_UsageOnlyWithoutGenuineFinish_FallsBackOnlyBeforeCommittedBlock(bool committed)
    {
        var calls = 0;
        var frames = committed
            ? "data: {\"choices\":[{\"delta\":{\"content\":\"committed\"},\"finish_reason\":\"vendor_unknown\"}]}\n\n"
            : "";
        frames += "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10}}\n\ndata: [DONE]\n\n";
        var handler = new RecordingHandler((_, _) => ++calls == 1
            ? SegmentedStream.ToResponse(frames)
            : JsonResponse(TextCompletion("chat")));
        var result = await CompactSummaryModelRunner.RunAsync(Model("chat", handler), Request, CancellationToken.None,
            new ModelRunnerOptions { MaxAttempts = 1 });
        Assert.Null(result.Failure);
        Assert.Equal(committed ? "committed" : "summary", result.Text);
        Assert.Equal(committed ? 1 : 2, calls);
    }

    [Fact]
    public async Task Compaction_CommittedTextWithoutRawFinish_DoesNotFallback()
    {
        var handler = new RecordingHandler((_, _) => SegmentedStream.ToResponse("""
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text"}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"committed"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"message_stop"}

            """));
        var result = await CompactSummaryModelRunner.RunAsync(Model("anthropic", handler), Request, CancellationToken.None,
            new ModelRunnerOptions { MaxAttempts = 1 });
        Assert.Null(result.Failure);
        Assert.Equal("committed", result.Text);
        Assert.Single(handler.Requests);
    }

    private static IChatModel Model(string format, RecordingHandler handler) => format switch
    {
        "anthropic" => new AnthropicChatModel(new(handler), new("model", "key", "https://unit.test")),
        "chat" => new OpenAiCompatibleChatModel(new(handler), new("model", "key", "https://unit.test")),
        _ => new OpenAiResponsesChatModel(new(handler), new("model", "key", "https://unit.test")),
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string TextCompletion(string format) => format switch
    {
        "anthropic" => """{"content":[{"type":"text","text":"summary"}],"stop_reason":"end_turn"}""",
        "chat" => """{"choices":[{"message":{"role":"assistant","content":"summary"},"finish_reason":"stop"}]}""",
        _ => """{"status":"completed","output":[{"id":"m","type":"message","content":[{"type":"output_text","text":"summary"}]}]}""",
    };

    private static string Completion(string format) => format switch
    {
        "anthropic" => """{"content":[{"type":"thinking","thinking":"thought","signature":"sig"},{"type":"text","text":"summary"},{"type":"tool_use","id":"call","name":"Read","input":{"n":1}}],"stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":2}}""",
        "chat" => """{"choices":[{"message":{"role":"assistant","reasoning_content":"thought","content":"summary","tool_calls":[{"id":"call","type":"function","function":{"name":"Read","arguments":"{\"n\":1}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""",
        _ => """{"status":"completed","output":[{"id":"r","type":"reasoning","encrypted_content":"cipher","summary":[{"type":"summary_text","text":"thought"}]},{"id":"m","type":"message","content":[{"type":"output_text","text":"summary"}]},{"id":"f","type":"function_call","call_id":"call","name":"Read","arguments":"{\"n\":1}"}],"usage":{"input_tokens":10,"output_tokens":2}}""",
    };
}
