using System.Net;
using System.Text;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Xunit;

namespace Alcoler.Tests.Auth;

public class SiwcCacheAffinityTests
{
    [Fact]
    public async Task MainSession_DerivesSessionIdHeaderAndCacheKeyFromOwnSession()
    {
        var handler = Frames(TerminalFrame);
        await Collect(Model(handler), Request(new("sess_main-session-1")));
        Assert.Equal("main-session-1", Header(handler, "session-id"));
        Assert.Null(Header(handler, "thread-id"));
        Assert.Equal("main-session-1", CacheKey(handler));
    }

    [Fact]
    public async Task SubagentSession_InheritsParentAffinityKeyAndReportsThread()
    {
        var handler = Frames(TerminalFrame);
        await Collect(Model(handler), Request(new("sess_child-7", "sess_parent-9")));
        Assert.Equal("subagent:parent-9", Header(handler, "session-id"));
        Assert.Equal("parent-9", Header(handler, "thread-id"));
        Assert.Equal("subagent:parent-9", CacheKey(handler));
    }

    [Fact]
    public async Task MissingAffinity_SendsNoHeadersAndNoCacheKey()
    {
        var handler = Frames(TerminalFrame);
        await Collect(Model(handler), new ModelRequest([new(MessageRole.User, [new InputTextBlock("hi")])], [], new ModelCallOptions()));
        Assert.Null(Header(handler, "session-id"));
        Assert.Null(Header(handler, "thread-id"));
        Assert.Null(CacheKey(handler));
    }

    [Fact]
    public async Task AffinityValues_AreClampedTo64CodePointsPrefixIncluded_ThreadIdUnclamped()
    {
        var longId = "sess_" + new string('a', 70);
        var handler = Frames(TerminalFrame);
        await Collect(Model(handler), Request(new(longId)));
        Assert.Equal(new string('a', 64), Header(handler, "session-id"));

        handler = Frames(TerminalFrame);
        await Collect(Model(handler), Request(new("sess_child-7", longId)));
        var subagentKey = Header(handler, "session-id");
        Assert.Equal(64, subagentKey!.Length);
        Assert.StartsWith("subagent:", subagentKey, StringComparison.Ordinal);
        Assert.Equal(new string('a', 70), Header(handler, "thread-id"));
    }

    [Fact]
    public async Task ClampCountsCodePoints_NotUtf16Units()
    {
        var emoji = "\U0001F600";
        var astralId = "sess_" + string.Concat(Enumerable.Repeat(emoji, 70));
        var handler = Frames(TerminalFrame);
        await Collect(Model(handler), Request(new(astralId)));
        Assert.Equal(string.Concat(Enumerable.Repeat(emoji, 64)), Header(handler, "session-id"));
    }

    [Fact]
    public async Task StaticKeyRequests_KeepAffinityFieldsOutOfBodyAndHeaders()
    {
        var handler = Frames(TerminalFrame);
        var model = new OpenAiResponsesChatModel(new HttpClient(handler), new("test", "static-key", "https://test/v1"),
            new ModelProperties());
        await Collect(model, Request(new("sess_main-session-1")));
        Assert.Null(Header(handler, "session-id"));
        Assert.Null(CacheKey(handler));
    }

    private static string? Header(RecordingHandler handler, string name) =>
        handler.Requests[0].Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private static string? CacheKey(RecordingHandler handler)
    {
        var body = JsonDocument.Parse(handler.Bodies[0]!);
        return body.RootElement.TryGetProperty("prompt_cache_key", out var key) ? key.GetString() : null;
    }

    private static OpenAiResponsesChatModel Model(RecordingHandler handler) =>
        new(new HttpClient(handler), new("test", "", "https://test/v1"),
            new ModelProperties(), accessTokenSource: _ => ValueTask.FromResult("oauth-token"));

    private static ModelRequest Request(ModelCacheAffinity affinity) =>
        new([new(MessageRole.User, [new InputTextBlock("hi")])], [], new ModelCallOptions(), affinity);

    private static async Task<List<ModelStreamEvent>> Collect(IChatModel model, ModelRequest request)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var e in model.StreamAsync(request, CancellationToken.None)) events.Add(e);
        return events;
    }

    private const string TerminalFrame =
        """{"type":"response.completed","response":{"output":[]}}""";

    private static RecordingHandler Frames(params string[] frames) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join("", frames.Select(f => "data: " + f + "\n\n")), Encoding.UTF8, "text/event-stream"),
    }));
}
