using System.Net;
using System.Text;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Xunit;

namespace Alcoler.Workbench.Tests.Auth;

public class OpenAiResponsesAccountTests
{
    [Fact]
    public async Task AccountRequests_ResolveBearerPerCallAndShapeBody()
    {
        var handler = Frames("""{"type":"response.completed","response":{"output":[]}}""");
        int resolutions = 0;
        var model = new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "placeholder", "https://test/v1"),
            new ModelProperties(),
            accessTokenSource: _ =>
            {
                Interlocked.Increment(ref resolutions);
                return ValueTask.FromResult("oauth-token");
            });
        await Collect(model, Request(system: "rules"));
        await Collect(model, Request(system: "rules"));
        Assert.Equal(2, resolutions);
        Assert.Equal(2, handler.Requests.Count);
        var bearer = handler.Requests.Select(request => request.Headers.Authorization!).ToList();
        Assert.All(bearer, auth =>
        {
            Assert.Equal("Bearer", auth.Scheme);
            Assert.Equal("oauth-token", auth.Parameter);
        });
        var body = JsonDocument.Parse(handler.Bodies[0]!);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        var systemMessage = body.RootElement.GetProperty("input").EnumerateArray()
            .Single(item => item.TryGetProperty("role", out var role) && role.GetString() == "developer");
        Assert.Equal("rules", systemMessage.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AccountRequests_MissingCredentialsFailClosedWithoutHttpCall()
    {
        var handler = Frames("unused");
        var model = new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "", "https://test/v1"),
            new ModelProperties(),
            accessTokenSource: _ => ValueTask.FromException<string>(new Alcoler.Workbench.Auth.SiwcAuthRequiredException("Sign in with ChatGPT to use this provider.")));
        var events = await Collect(model, Request());
        var failure = Assert.IsType<ModelStreamFailed>(Assert.Single(events));
        Assert.Equal(ModelFailureKind.AuthenticationFailed, failure.Failure.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AccountGenerate_ForcesStreamingAndAggregates()
    {
        var handler = Frames(
            """{"type":"response.output_text.delta","item_id":"m","content_index":0,"delta":"Hi"}""",
            """{"type":"response.completed","response":{"output":[{"id":"m","type":"message","content":[{"type":"output_text","text":"Hi"}]}]}}""");
        var model = new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "", "https://test/v1"),
            new ModelProperties(), accessTokenSource: _ => ValueTask.FromResult("oauth-token"));
        var events = await model.GenerateAsync(Request(), CancellationToken.None);
        var body = JsonDocument.Parse(handler.Bodies[0]!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Contains("text/event-stream", handler.Requests[0].Headers.Accept.ToString());
        Assert.Equal("Hi", Assert.Single(events.OfType<ModelTextDelta>()).Delta);
        Assert.Single(events.OfType<ModelStreamFinished>());
    }

    [Fact]
    public async Task StaticKeyRequests_KeepUnshapedBody()
    {
        var handler = Frames("""{"type":"response.completed","response":{"output":[]}}""");
        var model = new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "static-key", "https://test/v1"),
            new ModelProperties());
        await Collect(model, Request(system: "rules"));
        var request = handler.Requests[0];
        Assert.Equal("static-key", request.Headers.Authorization!.Parameter);
        var body = JsonDocument.Parse(handler.Bodies[0]!);
        Assert.True(body.RootElement.TryGetProperty("max_output_tokens", out _));
        Assert.Contains(body.RootElement.GetProperty("input").EnumerateArray(),
            item => item.TryGetProperty("role", out var role) && role.GetString() == "system");
    }

    private static ModelRequest Request(string? system = null)
    {
        List<ModelInputMessage> messages = [];
        if (system is not null)
            messages.Add(new(MessageRole.System, [new InputTextBlock(system)]));
        messages.Add(new(MessageRole.User, [new InputTextBlock("hi")]));
        return new ModelRequest([.. messages], [], new ModelCallOptions());
    }

    private static async Task<List<ModelStreamEvent>> Collect(IChatModel model, ModelRequest request)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var e in model.StreamAsync(request, CancellationToken.None)) events.Add(e);
        return events;
    }

    private static RecordingHandler Frames(params string[] frames) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join("", frames.Select(f => "data: " + f + "\n\n")), Encoding.UTF8, "text/event-stream"),
    }));
}
