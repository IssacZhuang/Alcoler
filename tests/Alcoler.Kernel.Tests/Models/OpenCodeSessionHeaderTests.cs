using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Xunit;

namespace Alcoler.Kernel.Tests.Models;

public class OpenCodeSessionHeaderTests
{
    private const string ChatSse = "data: [DONE]\n\n";
    private const string ResponsesSse = "data: {\"type\":\"response.completed\",\"response\":{\"output\":[]}}\n\n";
    private const string AnthropicSse =
        "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":1}}}\n\n" +
        "data: {\"type\":\"message_stop\"}\n\n";

    [Fact]
    public async Task ChatCompletions_SendsSessionHeaderWhenRequired()
    {
        var handler = Handler(ChatSse);
        var model = new OpenAiCompatibleChatModel(new HttpClient(handler),
            new OpenAiCompatibleModelOptions("test", "key", "https://opencode.ai/zen/go/v1"),
            new ModelProperties(RequiresOpenCodeSession: true));

        await Collect(model, Request());

        Assert.Equal("sess_test-session", Header(handler, OpenCodeSessionHeader.Name));
        Assert.StartsWith("Alcoler/", Header(handler, "User-Agent"));
    }

    [Fact]
    public async Task Responses_SendsSessionHeaderWhenRequired()
    {
        var handler = Handler(ResponsesSse);
        var model = new OpenAiResponsesChatModel(new HttpClient(handler),
            new OpenAiResponsesModelOptions("test", "key", "https://opencode.ai/zen/go/v1"),
            new ModelProperties(RequiresOpenCodeSession: true));

        await Collect(model, Request());

        Assert.Equal("sess_test-session", Header(handler, OpenCodeSessionHeader.Name));
        Assert.StartsWith("Alcoler/", Header(handler, "User-Agent"));
    }

    [Fact]
    public async Task AnthropicMessages_SendsSessionHeaderWhenRequired()
    {
        var handler = Handler(AnthropicSse);
        var model = new AnthropicChatModel(new HttpClient(handler),
            new AnthropicModelOptions("test", "key", "https://opencode.ai/inference/go/anthropic"),
            new ModelProperties(RequiresOpenCodeSession: true));

        await Collect(model, Request());

        Assert.Equal("sess_test-session", Header(handler, OpenCodeSessionHeader.Name));
        Assert.StartsWith("Alcoler/", Header(handler, "User-Agent"));
    }

    [Fact]
    public async Task RequiredSession_OmitsHeaderWithoutAffinity()
    {
        var handler = Handler(ChatSse);
        var model = new OpenAiCompatibleChatModel(new HttpClient(handler),
            new OpenAiCompatibleModelOptions("test", "key", "https://opencode.ai/zen/go/v1"),
            new ModelProperties(RequiresOpenCodeSession: true));

        await Collect(model, Request(affinity: null));

        Assert.Null(Header(handler, OpenCodeSessionHeader.Name));
    }

    [Fact]
    public async Task OtherProviders_DoNotSendSessionHeader()
    {
        var handler = Handler(ChatSse);
        var model = new OpenAiCompatibleChatModel(new HttpClient(handler),
            new OpenAiCompatibleModelOptions("test", "key", "https://api.example.com/v1"));

        await Collect(model, Request());

        Assert.Null(Header(handler, OpenCodeSessionHeader.Name));
        Assert.Null(Header(handler, "User-Agent"));
    }

    private static RecordingHandler Handler(string sse) => new((_, _) => SegmentedStream.ToResponse(sse));

    private static ModelRequest Request() => Request(new ModelCacheAffinity("sess_test-session"));

    private static ModelRequest Request(ModelCacheAffinity? affinity) => new(
        [new ModelInputMessage(MessageRole.User, [new InputTextBlock("hi")])],
        [],
        new ModelCallOptions(),
        affinity);

    private static string? Header(RecordingHandler handler, string name) =>
        handler.Requests.Single().Headers.TryGetValues(name, out var values)
            ? values.Single()
            : null;

    private static async Task Collect(IChatModel model, ModelRequest request)
    {
        await foreach (var _ in model.StreamAsync(request, CancellationToken.None))
        {
        }
    }
}
