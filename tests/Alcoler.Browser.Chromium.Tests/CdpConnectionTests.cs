using System.Text.Json;
using System.Threading.Channels;
using Alcoler.Browser;
using Xunit;

namespace Alcoler.Browser.Chromium.Tests;

public sealed class CdpConnectionTests
{
    private static readonly Uri TestUri = new("ws://127.0.0.1:9222/devtools/browser/test");

    [Fact]
    public async Task CorrelatesResponsesRegardlessOfArrivalOrder()
    {
        var wire = new FakeWire();
        using var connection = new CdpConnection();
        await connection.ConnectAsync(wire, TestUri, CancellationToken.None);

        var first = connection.SendAsync("Target.createTarget", null, CancellationToken.None);
        var second = connection.SendAsync("Runtime.evaluate",
            new Dictionary<string, object?> { ["expression"] = "1" }, CancellationToken.None);
        await WaitForAsync(() => wire.Snapshot().Count >= 2);

        var frames = Parse(wire.Snapshot());
        Assert.Equal(1, frames[0].Id);
        Assert.Equal("Target.createTarget", frames[0].Method);
        Assert.Equal(JsonValueKind.Object, frames[0].Parameters.ValueKind);
        Assert.Equal(2, frames[1].Id);
        Assert.Equal("Runtime.evaluate", frames[1].Method);
        Assert.Equal("1", frames[1].Parameters.GetProperty("expression").GetString());

        wire.Push("{\"id\":2,\"result\":{\"value\":true}}");
        wire.Push("{\"id\":1,\"result\":{\"targetId\":\"T1\"}}");

        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondResult.GetProperty("value").GetBoolean());
        Assert.Equal("T1", firstResult.GetProperty("targetId").GetString());
    }

    [Fact]
    public async Task PropagatesCdpErrorTextVerbatim()
    {
        var wire = new FakeWire();
        using var connection = new CdpConnection();
        await connection.ConnectAsync(wire, TestUri, CancellationToken.None);
        var pending = connection.SendAsync("Runtime.evaluate", null, CancellationToken.None, "S1");
        await WaitForAsync(() => wire.Snapshot().Count >= 1);
        var id = Parse(wire.Snapshot())[0].Id;
        Assert.Equal("S1", Parse(wire.Snapshot())[0].SessionId);
        wire.Push("{\"id\":" + id + ",\"error\":{\"code\":-32000,\"message\":\"No frame for given id found\"}}");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("No frame for given id found", failure.Message);
    }

    [Fact]
    public async Task RoutesEventsBySessionIdAndSessionlessEventsToConnectionHandler()
    {
        var wire = new FakeWire();
        using var connection = new CdpConnection();
        await connection.ConnectAsync(wire, TestUri, CancellationToken.None);
        var sessionOne = new List<BrowserProtocolEvent>();
        var sessionTwo = new List<BrowserProtocolEvent>();
        var browser = new List<BrowserProtocolEvent>();
        connection.RegisterSession("S1", sessionOne.Add);
        connection.RegisterSession("S2", sessionTwo.Add);
        connection.BrowserEvent = browser.Add;

        wire.Push("{\"method\":\"Page.frameNavigated\",\"params\":{\"frame\":{\"id\":\"f1\"}},\"sessionId\":\"S1\"}");
        await WaitForAsync(() => sessionOne.Count == 1);
        wire.Push("{\"method\":\"Target.targetCreated\",\"params\":{\"targetInfo\":{\"targetId\":\"B1\",\"type\":\"browser\"}}}");
        await WaitForAsync(() => browser.Count == 1);
        await Task.Delay(100);

        Assert.Equal("Page.frameNavigated", sessionOne[0].Method);
        Assert.Equal("S1", sessionOne[0].SessionId);
        Assert.Empty(sessionTwo);
        Assert.Equal("Target.targetCreated", browser[0].Method);
        Assert.Null(browser[0].SessionId);
    }

    [Fact]
    public async Task BroadcastsDownloadEventsToAllRegisteredSessions()
    {
        var wire = new FakeWire();
        using var connection = new CdpConnection();
        await connection.ConnectAsync(wire, TestUri, CancellationToken.None);
        var sessionOne = new List<BrowserProtocolEvent>();
        var sessionTwo = new List<BrowserProtocolEvent>();
        var browser = new List<BrowserProtocolEvent>();
        connection.RegisterSession("S1", sessionOne.Add);
        connection.RegisterSession("S2", sessionTwo.Add);
        connection.BrowserEvent = browser.Add;

        wire.Push("{\"method\":\"Browser.downloadWillBegin\",\"params\":{\"guid\":\"g1\"},\"sessionId\":\"S1\"}");
        await WaitForAsync(() => sessionOne.Count == 1 && sessionTwo.Count == 1);
        wire.Push("{\"method\":\"Browser.downloadProgress\",\"params\":{\"guid\":\"g1\",\"state\":\"completed\"}}");
        await WaitForAsync(() => sessionOne.Count == 2 && sessionTwo.Count == 2);
        await Task.Delay(100);

        Assert.All(sessionOne, e => Assert.StartsWith("Browser.download", e.Method, StringComparison.Ordinal));
        Assert.Equal(2, sessionTwo.Count);
        Assert.Empty(browser);
    }

    [Fact]
    public async Task InjectableCommandTimeoutFiresWithoutResponse()
    {
        var wire = new FakeWire();
        using var connection = new CdpConnection(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(50));
        await connection.ConnectAsync(wire, TestUri, CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(
            () => connection.SendAsync("Page.navigate", null, CancellationToken.None, "S1"));
    }

    [Fact]
    public async Task CancellationDrainsWithTerminateExecutionOnTheSameSession()
    {
        var wire = new FakeWire();
        using var connection = new CdpConnection(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        await connection.ConnectAsync(wire, TestUri, CancellationToken.None);
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var pending = connection.SendAsync("Runtime.evaluate", null, canceled.Token, "S7");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        await WaitForAsync(() => Parse(wire.Snapshot()).Any(f => f.Method == "Runtime.terminateExecution"));
        var terminate = Parse(wire.Snapshot()).Single(f => f.Method == "Runtime.terminateExecution");
        Assert.Equal("S7", terminate.SessionId);
        var original = Parse(wire.Snapshot()).First(f => f.Method == "Runtime.evaluate");
        wire.Push("{\"id\":" + original.Id + ",\"result\":{\"value\":false}}");
        await Task.Delay(50);
    }

    [Fact]
    public async Task WireCloseFailsPendingCommands()
    {
        var wire = new FakeWire();
        using var connection = new CdpConnection();
        await connection.ConnectAsync(wire, TestUri, CancellationToken.None);
        var pending = connection.SendAsync("Page.navigate", null, CancellationToken.None, "S1");
        await WaitForAsync(() => wire.Snapshot().Count >= 1);
        wire.End();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Contains("closed", failure.Message, StringComparison.Ordinal);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "The expected condition was not reached within the timeout.");
    }

    private static List<(long Id, string Method, JsonElement Parameters, string? SessionId)> Parse(IEnumerable<string> frames)
    {
        var parsed = new List<(long, string, JsonElement, string?)>();
        foreach (var frame in frames)
        {
            using var document = JsonDocument.Parse(frame);
            var root = document.RootElement;
            parsed.Add((root.GetProperty("id").GetInt64(), root.GetProperty("method").GetString()!,
                root.GetProperty("params").Clone(),
                root.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String
                    ? session.GetString() : null));
        }
        return parsed;
    }

    private sealed class FakeWire : ICdpWire
    {
        private readonly Channel<string?> _inbound = Channel.CreateUnbounded<string?>();
        private readonly Lock _gate = new();

        public List<string> Frames { get; } = [];

        public List<string> Snapshot()
        {
            lock (_gate) return [.. Frames];
        }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            lock (_gate) Frames.Add(text);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken) => _inbound.Reader.ReadAsync(cancellationToken).AsTask();

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            _inbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public void Push(string message) => _inbound.Writer.TryWrite(message);

        public void End() => _inbound.Writer.TryWrite(null);

        public void Dispose() { }
    }
}
