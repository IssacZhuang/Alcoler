using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Alcoler.Browser;
using Alcoler.Browser.Chromium;
using Alcoler.Contracts.Identifiers;
using Xunit;

namespace Alcoler.Browser.Chromium.Tests;

public sealed class ChromiumBrowserHostTests
{
    private readonly SessionId _session = new("sess_host_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreatePageIssuesCreateTargetBackgroundAttachFlattenAndSingleDownloadBehavior()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            var first = await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var second = await host.CreatePageAsync(_session, "tab_2", CancellationToken.None);
            Assert.NotSame(first, second);
            var frames = Parse(wire.Snapshot());

            var discover = Assert.Single(frames, f => f.Method == "Target.setDiscoverTargets");
            Assert.True(discover.Parameters.GetProperty("discover").GetBoolean());

            var created = frames.Where(f => f.Method == "Target.createTarget").ToArray();
            Assert.Equal(2, created.Length);
            Assert.All(created, f =>
            {
                Assert.Equal("about:blank", f.Parameters.GetProperty("url").GetString());
                Assert.True(f.Parameters.GetProperty("background").GetBoolean());
            });

            var attached = frames.Where(f => f.Method == "Target.attachToTarget").ToArray();
            Assert.Equal(2, attached.Length);
            Assert.All(attached, f => Assert.True(f.Parameters.GetProperty("flatten").GetBoolean()));
            Assert.Equal("T1", attached[0].Parameters.GetProperty("targetId").GetString());
            Assert.Equal("T2", attached[1].Parameters.GetProperty("targetId").GetString());

            var downloads = Assert.Single(frames, f => f.Method == "Browser.setDownloadBehavior");
            Assert.Equal("allow", downloads.Parameters.GetProperty("behavior").GetString());
            Assert.True(downloads.Parameters.GetProperty("eventsEnabled").GetBoolean());
            Assert.EndsWith("downloads", downloads.Parameters.GetProperty("downloadPath").GetString()!.Replace('\\', '/'));
        }
    }

    [Fact]
    public async Task PopupTargetCreatedRaisesNewTabRequestedAndAdoptionSkipsCreateTarget()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var raised = new TaskCompletionSource<BrowserNewTabRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.NewTabRequested += (_, e) => raised.TrySetResult(e);
            wire.Push("{\"method\":\"Target.targetCreated\",\"params\":{\"targetInfo\":{\"targetId\":\"POP1\",\"type\":\"page\",\"url\":\"https://example.test/popup\",\"openerId\":\"T1\"}}}");
            var request = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(_session, request.SessionId);
            Assert.Equal("tab_1", request.OpenerTabId);
            Assert.Equal("https://example.test/popup", request.Url);
            Assert.False(request.IsUserInitiated);
            Assert.Equal("POP1", request.TargetId);

            var before = wire.Snapshot().Count;
            await host.CreatePageAsync(_session, "tab_2", CancellationToken.None, adoptTargetId: "POP1");
            var after = Parse(wire.Snapshot().Skip(before));
            Assert.DoesNotContain(after, f => f.Method == "Target.createTarget");
            Assert.Contains(after, f => f.Method == "Target.attachToTarget" && f.Parameters.GetProperty("targetId").GetString() == "POP1");
        }
    }

    [Fact]
    public async Task UserOpenedTargetWithoutOpenerRaisesUserInitiatedNewTabRequested()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var raised = new TaskCompletionSource<BrowserNewTabRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.NewTabRequested += (_, e) => raised.TrySetResult(e);
            wire.Push("{\"method\":\"Target.targetCreated\",\"params\":{\"targetInfo\":{\"targetId\":\"USER1\",\"type\":\"page\",\"url\":\"https://example.test/manual\"}}}");
            var request = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(request.IsUserInitiated);
            Assert.Equal("tab_1", request.OpenerTabId);
            Assert.Equal("https://example.test/manual", request.Url);
            Assert.Equal("USER1", request.TargetId);
        }
    }

    [Fact]
    public async Task PopupCreatedWithEmptyUrlRaisesOnlyAfterTheNavigationCommits()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var raised = new TaskCompletionSource<BrowserNewTabRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.NewTabRequested += (_, e) => raised.TrySetResult(e);
            wire.Push("{\"method\":\"Target.targetCreated\",\"params\":{\"targetInfo\":{\"targetId\":\"POP2\",\"type\":\"page\",\"url\":\"\",\"openerId\":\"T1\"}}}");
            wire.Push("{\"method\":\"Target.targetInfoChanged\",\"params\":{\"targetInfo\":{\"targetId\":\"POP2\",\"type\":\"page\",\"url\":\"about:blank\",\"openerId\":\"T1\"}}}");
            await Task.Delay(150);
            Assert.False(raised.Task.IsCompleted);
            wire.Push("{\"method\":\"Target.targetInfoChanged\",\"params\":{\"targetInfo\":{\"targetId\":\"POP2\",\"type\":\"page\",\"url\":\"https://example.test/late\",\"openerId\":\"T1\"}}}");
            var request = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("https://example.test/late", request.Url);
            Assert.Equal("tab_1", request.OpenerTabId);
            Assert.Equal("POP2", request.TargetId);

            var before = wire.Snapshot().Count;
            await host.CreatePageAsync(_session, "tab_2", CancellationToken.None, adoptTargetId: "POP2");
            var after = Parse(wire.Snapshot().Skip(before));
            Assert.DoesNotContain(after, f => f.Method == "Target.createTarget");
            Assert.Contains(after, f => f.Method == "Target.attachToTarget" && f.Parameters.GetProperty("targetId").GetString() == "POP2");
        }
    }

    [Fact]
    public async Task UnanchorableTargetIsClosed()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            wire.Push("{\"method\":\"Target.targetCreated\",\"params\":{\"targetInfo\":{\"targetId\":\"ORPHAN\",\"type\":\"page\",\"url\":\"https://example.test/orphan\",\"openerId\":\"UNKNOWN\"}}}");
            var closed = await WaitForFrameAsync(wire, f => f.Method == "Target.closeTarget"
                && f.Parameters.GetProperty("targetId").GetString() == "ORPHAN");
            Assert.Equal("ORPHAN", closed.Parameters.GetProperty("targetId").GetString());
        }
    }

    [Fact]
    public async Task TargetDestroyedRaisesHostClosedOnThePage()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            var page = await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var closed = new TaskCompletionSource<BrowserProtocolEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            page.ProtocolEvent += (_, e) =>
            {
                if (e.Method == "Host.closed") closed.TrySetResult(e);
            };
            wire.Push("{\"method\":\"Target.targetDestroyed\",\"params\":{\"targetId\":\"T1\"}}");
            var raised = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(raised.SessionId);
        }
    }

    [Fact]
    public async Task LastTabCloseDisposesTheSessionAndFurtherClosesAreIdempotent()
    {
        var (host, wire, process) = CreateHost();
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            await host.CreatePageAsync(_session, "tab_2", CancellationToken.None);

            await host.ClosePageAsync("tab_1", CancellationToken.None);
            Assert.DoesNotContain(Parse(wire.Snapshot()), f => f.Method == "Browser.close");
            Assert.Equal(0, process.ShutdownCalls);

            await host.ClosePageAsync("tab_2", CancellationToken.None);
            Assert.Contains(Parse(wire.Snapshot()), f => f.Method == "Browser.close");
            Assert.Contains(Parse(wire.Snapshot()), f => f.Method == "Target.closeTarget"
                && f.Parameters.GetProperty("targetId").GetString() == "T2");
            await WaitForAsync(() => process.ShutdownCalls == 1);

            await host.ClosePageAsync("tab_2", CancellationToken.None);
            Assert.Equal(1, process.ShutdownCalls);
        }
    }

    [Fact]
    public async Task FirstInputActivatesTheTabOnceAndLaterInputSkipsActivation()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            var page = await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var before = wire.Snapshot().Count;
            await page.SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object?> { ["type"] = "mousePressed" }, CancellationToken.None);
            await page.SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object?> { ["type"] = "mouseReleased" }, CancellationToken.None);
            await page.SendAsync("Runtime.evaluate", new Dictionary<string, object?> { ["expression"] = "1" }, CancellationToken.None);
            var frames = Parse(wire.Snapshot().Skip(before)).ToArray();
            Assert.Equal(["Page.bringToFront", "Runtime.evaluate", "Input.dispatchMouseEvent", "Input.dispatchMouseEvent", "Runtime.evaluate"],
                frames.Select(f => f.Method).ToArray());
            Assert.Equal("S1", frames[0].SessionId);
        }
    }

    [Fact]
    public async Task ShowAndHideIssueBringToFrontGetWindowAndSetWindowBoundsInOrder()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var before = wire.Snapshot().Count;
            await host.ShowPageAsync("tab_1", CancellationToken.None);
            var shown = Parse(wire.Snapshot().Skip(before));
            Assert.Equal(["Page.bringToFront", "Browser.getWindowForTarget", "Browser.setWindowBounds"],
                shown.Select(f => f.Method).ToArray());
            Assert.Equal("S1", shown[0].SessionId);
            Assert.Equal("T1", shown[1].Parameters.GetProperty("targetId").GetString());
            Assert.Equal(7L, shown[2].Parameters.GetProperty("windowId").GetInt64());
            Assert.Equal("normal", shown[2].Parameters.GetProperty("bounds").GetProperty("windowState").GetString());

            before = wire.Snapshot().Count;
            await host.HidePageAsync("tab_1", CancellationToken.None);
            var hidden = Parse(wire.Snapshot().Skip(before));
            Assert.Equal(["Browser.getWindowForTarget", "Browser.setWindowBounds"],
                hidden.Select(f => f.Method).ToArray());
            Assert.Equal("minimized", hidden[1].Parameters.GetProperty("bounds").GetProperty("windowState").GetString());
        }
    }

    [Fact]
    public async Task OwnSessionEventsAreRaisedSessionlessAndChildSessionEventsKeepTheChildId()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            var page = await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var events = new ConcurrentQueue<BrowserProtocolEvent>();
            page.ProtocolEvent += (_, e) => events.Enqueue(e);

            wire.Push("{\"method\":\"Page.frameNavigated\",\"params\":{\"frame\":{\"id\":\"f1\"}},\"sessionId\":\"S1\"}");
            var own = await WaitForAsync(() => events.FirstOrDefault(e => e.Method == "Page.frameNavigated"));
            Assert.Null(own.SessionId);

            wire.Push("{\"method\":\"Target.attachedToTarget\",\"params\":{\"sessionId\":\"CHILD1\",\"targetInfo\":{\"type\":\"iframe\",\"targetId\":\"F1\"}},\"sessionId\":\"S1\"}");
            await WaitForAsync(() => events.Any(e => e.Method == "Target.attachedToTarget"));

            wire.Push("{\"method\":\"Page.navigatedWithinDocument\",\"params\":{\"url\":\"https://example.test/inside\"},\"sessionId\":\"CHILD1\"}");
            var child = await WaitForAsync(() => events.FirstOrDefault(e => e.Method == "Page.navigatedWithinDocument"));
            Assert.Equal("CHILD1", child.SessionId);

            wire.Push("{\"method\":\"Network.requestWillBeSent\",\"params\":{},\"sessionId\":\"S1\"}");
            wire.Push("{\"method\":\"Target.detachedFromTarget\",\"params\":{\"sessionId\":\"CHILD1\"},\"sessionId\":\"S1\"}");
            await WaitForAsync(() => events.Any(e => e.Method == "Target.detachedFromTarget"));
            var count = events.Count;
            wire.Push("{\"method\":\"Page.navigatedWithinDocument\",\"params\":{\"url\":\"https://example.test/late\"},\"sessionId\":\"CHILD1\"}");
            await Task.Delay(150);
            Assert.Equal(count, events.Count);
            Assert.DoesNotContain(events, e => e.Method == "Network.requestWillBeSent");
        }
    }

    [Fact]
    public async Task HeadlessModeRejectsShowAndHide()
    {
        var (host, _, _) = CreateHost(headless: true);
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var shown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ShowPageAsync("tab_1", CancellationToken.None));
            Assert.Contains("headless", shown.Message, StringComparison.Ordinal);
            var hidden = await Assert.ThrowsAsync<InvalidOperationException>(() => host.HidePageAsync("tab_1", CancellationToken.None));
            Assert.Contains("headless", hidden.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ProcessExitClosesEveryPageOfTheSession()
    {
        var (host, wire, process) = CreateHost();
        using (host)
        {
            var first = await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var second = await host.CreatePageAsync(_session, "tab_2", CancellationToken.None);
            var closedFirst = new TaskCompletionSource<BrowserProtocolEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closedSecond = new TaskCompletionSource<BrowserProtocolEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.ProtocolEvent += (_, e) => { if (e.Method == "Host.closed") closedFirst.TrySetResult(e); };
            second.ProtocolEvent += (_, e) => { if (e.Method == "Host.closed") closedSecond.TrySetResult(e); };

            process.RaiseExited();
            await Task.WhenAll(closedFirst.Task.WaitAsync(TimeSpan.FromSeconds(5)), closedSecond.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void HeadlessIsTheDefaultUnlessExplicitlyDisabled()
    {
        Assert.True(new ChromiumBrowserOptions().EffectiveHeadless);
        Assert.True(new ChromiumBrowserOptions { Headless = true }.EffectiveHeadless);
        Assert.False(new ChromiumBrowserOptions { Headless = false }.EffectiveHeadless);
    }

    private static (ChromiumBrowserHost Host, ScriptedWire Wire, FakeProcess Process) CreateHost(bool headless = false)
    {
        var wire = new ScriptedWire();
        var process = new FakeProcess();
        var host = new ChromiumBrowserHost(
            Path.Combine(Path.GetTempPath(), "alcoler-chromium-host-" + Guid.NewGuid().ToString("N")),
            new ChromiumBrowserOptions { Headless = headless },
            binaryResolver: _ => Task.FromResult("/fake/chrome"),
            processFactory: (_, _, _) => process,
            wireFactory: () => wire);
        return (host, wire, process);
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> condition, int timeoutMs = 5000) where T : class
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition() is { } value) return value;
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException("The expected condition was not reached within the timeout.");
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private static Task<Frame> WaitForFrameAsync(ScriptedWire wire, Func<Frame, bool> predicate) =>
        WaitForAsync<Frame>(() => Parse(wire.Snapshot()).FirstOrDefault(predicate));

    private static List<Frame> Parse(IEnumerable<string> frames)
    {
        var parsed = new List<Frame>();
        foreach (var text in frames)
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            parsed.Add(new Frame(
                root.GetProperty("id").GetInt64(),
                root.GetProperty("method").GetString()!,
                root.GetProperty("params").Clone(),
                root.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String
                    ? session.GetString() : null));
        }
        return parsed;
    }

    private sealed record Frame(long Id, string Method, JsonElement Parameters, string? SessionId);

    private sealed class ScriptedWire : ICdpWire
    {
        private readonly Channel<string?> _inbound = Channel.CreateUnbounded<string?>();
        private readonly Lock _gate = new();
        private int _targets;
        private int _sessions;

        public List<string> Frames { get; } = [];

        public List<string> Snapshot()
        {
            lock (_gate) return [.. Frames];
        }

        public Uri? ConnectedUri { get; private set; }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            ConnectedUri = uri;
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            lock (_gate) Frames.Add(text);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method) return Task.CompletedTask;
            var parameters = root.TryGetProperty("params", out var parameterElement) ? parameterElement.Clone() : default;
            var response = Respond(method, parameters);
            Push("{\"id\":" + root.GetProperty("id").GetInt64() + ",\"result\":" + BrowserJson.Serialize(response) + "}");
            return Task.CompletedTask;
        }

        private object? Respond(string method, JsonElement parameters) => method switch
        {
            "Target.createTarget" => new Dictionary<string, object?> { ["targetId"] = "T" + Interlocked.Increment(ref _targets) },
            "Target.attachToTarget" => new Dictionary<string, object?> { ["sessionId"] = "S" + Interlocked.Increment(ref _sessions) },
            "Browser.getWindowForTarget" => new Dictionary<string, object?> { ["windowId"] = 7 },
            _ => new Dictionary<string, object?>(),
        };

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken) => _inbound.Reader.ReadAsync(cancellationToken).AsTask();

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            _inbound.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public void Push(string message) => _inbound.Writer.TryWrite(message);

        public void Dispose() { }
    }

    private sealed class FakeProcess : IChromiumProcess
    {
        public int ShutdownCalls;
        private EventHandler? _exited;

        public Uri WsUri { get; } = new("ws://127.0.0.1:9222/devtools/browser/fake");

        public event EventHandler? Exited
        {
            add => _exited += value;
            remove => _exited -= value;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ShutdownCalls);
            RaiseExited();
            return Task.CompletedTask;
        }

        public void RaiseExited() => _exited?.Invoke(this, EventArgs.Empty);

        public void Dispose() { }
    }
}
