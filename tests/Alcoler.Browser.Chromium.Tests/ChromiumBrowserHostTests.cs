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
            Assert.Equal("POP1", request.TargetId);

            var before = wire.Snapshot().Count;
            await host.CreatePageAsync(_session, "tab_2", CancellationToken.None, adoptTargetId: "POP1");
            var after = Parse(wire.Snapshot().Skip(before));
            Assert.DoesNotContain(after, f => f.Method == "Target.createTarget");
            Assert.Contains(after, f => f.Method == "Target.attachToTarget" && f.Parameters.GetProperty("targetId").GetString() == "POP1");
        }
    }

    [Fact]
    public async Task TargetWithoutOpenerRaisesNewTabRequested()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            var raised = new TaskCompletionSource<BrowserNewTabRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.NewTabRequested += (_, e) => raised.TrySetResult(e);
            wire.Push("{\"method\":\"Target.targetCreated\",\"params\":{\"targetInfo\":{\"targetId\":\"USER1\",\"type\":\"page\",\"url\":\"https://example.test/manual\"}}}");
            var request = await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstInputWaitsForVisibleRenderedFrameBeforeDispatch(bool headless)
    {
        var (host, wire, _) = CreateHost(headless);
        using (host)
        {
            var page = await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            wire.HoldEvaluations = true;
            var pending = page.SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object?> { ["type"] = "mousePressed" }, CancellationToken.None);
            var readiness = await WaitForFrameAsync(wire, frame => frame.Method == "Runtime.evaluate");
            try
            {
                var expression = readiness.Parameters.GetProperty("expression").GetString()!;
                Assert.Contains("while(document.visibilityState!=='visible')", expression, StringComparison.Ordinal);
                Assert.Contains("requestAnimationFrame(()=>requestAnimationFrame(r))", expression, StringComparison.Ordinal);
                Assert.True(readiness.Parameters.GetProperty("awaitPromise").GetBoolean());
                Assert.Equal("S1", readiness.SessionId);
                Assert.DoesNotContain(Parse(wire.Snapshot()), frame => frame.Method.StartsWith("Input.", StringComparison.Ordinal));
            }
            finally
            {
                wire.Push("{\"id\":" + readiness.Id + ",\"result\":{}}");
            }
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(Parse(wire.Snapshot()), frame => frame.Method == "Input.dispatchMouseEvent");
        }
    }

    [Fact]
    public async Task CallerCancellationDuringFirstInputReadinessDoesNotDispatchInput()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        using (var cancellation = new CancellationTokenSource())
        {
            var page = await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            wire.HoldEvaluations = true;
            var pending = page.SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object?> { ["type"] = "mousePressed" }, cancellation.Token);
            var readiness = await WaitForFrameAsync(wire, frame => frame.Method == "Runtime.evaluate");
            cancellation.Cancel();
            var terminate = await WaitForFrameAsync(wire, frame => frame.Method == "Runtime.terminateExecution");
            Assert.Equal("S1", terminate.SessionId);
            wire.Push("{\"id\":" + readiness.Id + ",\"result\":{}}");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.DoesNotContain(Parse(wire.Snapshot()), frame => frame.Method.StartsWith("Input.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task FirstInputReadinessTimeoutFallsBackAfterDrainingTheBoundedEvaluation()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            var page = await host.CreatePageAsync(_session, "tab_1", CancellationToken.None);
            wire.HoldEvaluations = true;
            var pending = page.SendAsync("Input.dispatchMouseEvent", new Dictionary<string, object?> { ["type"] = "mousePressed" }, CancellationToken.None);
            var readiness = await WaitForFrameAsync(wire, frame => frame.Method == "Runtime.evaluate");
            await WaitForFrameAsync(wire, frame => frame.Method == "Runtime.terminateExecution");
            wire.Push("{\"id\":" + readiness.Id + ",\"result\":{}}");
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(Parse(wire.Snapshot()), frame => frame.Method == "Input.dispatchMouseEvent");
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

    [Fact]
    public async Task StartupCancellationCleansTheOwnedAttemptWithoutRetrying()
    {
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProcess { OnStart = ct => { cancellation.Cancel(); return Task.FromCanceled(ct); } };
        var wire = new ScriptedWire();
        using var host = CreateHost([process], [wire]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.CreatePageAsync(_session, "tab_1", cancellation.Token));
        Assert.Equal(1, process.StartCalls);
        Assert.Equal(1, process.ShutdownCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.False(process.ShutdownToken.IsCancellationRequested);
        Assert.Null(wire.ConnectedUri);
        host.Dispose();
        Assert.Equal(1, process.DisposeCalls);
    }

    [Fact]
    public async Task FailedStartupRetryCleansTheAttemptAndIgnoresItsQueuedAndStaleExitCallbacks()
    {
        Action? staleExit = null;
        var failed = new FakeProcess();
        failed.OnStart = _ =>
        {
            staleExit = failed.CaptureExit();
            failed.RaiseExited();
            throw new InvalidOperationException("failed startup");
        };
        var successful = new FakeProcess();
        using var host = CreateHost([failed, successful], [new ScriptedWire()]);
        var page = await host.CreatePageAsync(_session, "tab_1", default);
        var closed = false;
        page.ProtocolEvent += (_, e) => { if (e.Method == "Host.closed") closed = true; };
        Assert.Equal(1, failed.ShutdownCalls);
        Assert.Equal(1, failed.DisposeCalls);
        Assert.Equal(0, successful.ShutdownCalls);
        Assert.Equal(0, successful.DisposeCalls);
        staleExit!();
        await Task.Delay(150);
        Assert.False(closed);
        Assert.Same(page, await host.CreatePageAsync(_session, "tab_1", default));
        await host.ClosePageAsync("tab_1", default);
        Assert.Equal(1, successful.ShutdownCalls);
        Assert.Equal(1, successful.DisposeCalls);
    }

    [Fact]
    public async Task ExhaustedStartupRetriesPreserveTheStartupFailureWhenCleanupFails()
    {
        var original = new InvalidOperationException("original startup failure");
        var attempts = Enumerable.Range(0, 5).Select(_ => new FakeProcess
        {
            OnStart = _ => Task.FromException(original),
            OnShutdown = _ => Task.FromException(new OperationCanceledException("cleanup canceled")),
        }).ToArray();
        using var host = CreateHost(attempts, []);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.CreatePageAsync(_session, "tab_1", default));
        Assert.Same(original, failure);
        Assert.All(attempts, process =>
        {
            Assert.Equal(1, process.StartCalls);
            Assert.Equal(1, process.ShutdownCalls);
            Assert.Equal(1, process.DisposeCalls);
        });
        host.Dispose();
        Assert.All(attempts, process => Assert.Equal(1, process.DisposeCalls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionAndDiscoveryFailuresDisposeBothAttemptResourcesBeforeRetry(bool discovery)
    {
        var failed = new FakeProcess();
        var successful = new FakeProcess();
        var failedWire = new ScriptedWire
        {
            OnConnect = discovery ? null : _ => Task.FromException(new IOException("connection failed")),
            FailMethod = discovery ? "Target.setDiscoverTargets" : null,
        };
        var wire = new ScriptedWire();
        using var host = CreateHost([failed, successful], [failedWire, wire]);
        await host.CreatePageAsync(_session, "tab_1", default);
        Assert.Equal(1, failed.ShutdownCalls);
        Assert.Equal(1, failed.DisposeCalls);
        Assert.Equal(1, failedWire.DisposeCalls);
        Assert.Equal(0, successful.DisposeCalls);
        Assert.Equal(0, wire.DisposeCalls);
        host.Dispose();
        Assert.Equal(1, successful.DisposeCalls);
        Assert.Equal(1, wire.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionCancellationDisposesBothAttemptResourcesWithoutRetrying(bool discovery)
    {
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProcess();
        var wire = new ScriptedWire
        {
            OnConnect = discovery ? null : ct => { cancellation.Cancel(); return Task.FromCanceled(ct); },
            OnReply = (method, ct) =>
            {
                if (discovery && method == "Target.setDiscoverTargets") { cancellation.Cancel(); throw new OperationCanceledException(ct); }
                return Task.CompletedTask;
            },
        };
        using var host = CreateHost([process], [wire]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.CreatePageAsync(_session, "tab_1", cancellation.Token));
        Assert.Equal(1, process.StartCalls);
        Assert.Equal(1, process.ShutdownCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.Equal(1, wire.DisposeCalls);
    }

    [Fact]
    public async Task ShutdownCancellationStillDisposesTheSuccessfulProcessAndConnection()
    {
        var process = new FakeProcess { OnShutdown = _ => Task.FromException(new OperationCanceledException("shutdown canceled")) };
        var wire = new ScriptedWire();
        using var host = CreateHost([process], [wire]);
        await host.CreatePageAsync(_session, "tab_1", default);
        await host.ClosePageAsync("tab_1", default);
        Assert.Equal(1, process.ShutdownCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.Equal(1, wire.DisposeCalls);
    }

    [Fact]
    public async Task RejectionClosesOnlyStillPendingTargetsInTheOwningSession()
    {
        var (host, wire, _) = CreateHost();
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", default);
            var raised = new TaskCompletionSource<BrowserNewTabRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.NewTabRequested += (_, e) => raised.TrySetResult(e);
            PushPopup(wire, "POP", "https://example.test/popup");
            await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.RejectPopupAsync(new SessionId("wrong-owner"), "POP", default);
            Assert.DoesNotContain(Parse(wire.Snapshot()), frame => frame.Method == "Target.closeTarget");
            await host.RejectPopupAsync(_session, "POP", default);
            await host.RejectPopupAsync(_session, "POP", default);
            Assert.Single(Parse(wire.Snapshot()), frame => frame.Method == "Target.closeTarget"
                && frame.Parameters.GetProperty("targetId").GetString() == "POP");
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.CreatePageAsync(_session, "rejected", default, "POP"));
        }
    }

    [Fact]
    public async Task PendingBlankPopupExpiresWithoutNewBrowserActivityAndTimerStopsOnDispose()
    {
        var time = new ManualTimeProvider();
        var (host, wire, _) = CreateHost(timeProvider: time);
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", default);
            PushPopup(wire, "BLANK", "about:blank");
            Assert.True(await WaitForAsync(() => time.ActiveTimers == 1));
            time.Advance(TimeSpan.FromMinutes(2));
            await WaitForFrameAsync(wire, frame => frame.Method == "Target.closeTarget"
                && frame.Parameters.GetProperty("targetId").GetString() == "BLANK");
            wire.Push("{\"method\":\"Target.targetDestroyed\",\"params\":{\"targetId\":\"BLANK\"}}");
            PushPopup(wire, "LATER", "");
            Assert.True(await WaitForAsync(() => time.ActiveTimers == 1));
            host.Dispose();
            Assert.Equal(0, time.ActiveTimers);
            time.Advance(TimeSpan.FromMinutes(10));
        }
    }

    [Fact]
    public async Task DelayedNavigationCanAdoptBeforeTheLeaseAndExpiryCannotCloseTheAdoptedTarget()
    {
        var time = new ManualTimeProvider();
        var (host, wire, _) = CreateHost(timeProvider: time);
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", default);
            var raised = new TaskCompletionSource<BrowserNewTabRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.NewTabRequested += (_, e) => raised.TrySetResult(e);
            PushPopup(wire, "DELAYED", "about:blank");
            Assert.True(await WaitForAsync(() => time.ActiveTimers == 1));
            time.Advance(TimeSpan.FromSeconds(119));
            wire.Push("{\"method\":\"Target.targetInfoChanged\",\"params\":{\"targetInfo\":{\"targetId\":\"DELAYED\",\"type\":\"page\",\"url\":\"https://example.test/late\"}}}");
            await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.CreatePageAsync(_session, "tab_2", default, "DELAYED");
            await host.RejectPopupAsync(_session, "DELAYED", default);
            time.Advance(TimeSpan.FromMinutes(10));
            Assert.DoesNotContain(Parse(wire.Snapshot()), frame => frame.Method == "Target.closeTarget"
                && frame.Parameters.GetProperty("targetId").GetString() == "DELAYED");
        }
    }

    [Fact]
    public async Task ExpiryWinningTheAdoptionRaceDoesNotCreateAnUnrelatedReplacementTab()
    {
        var time = new ManualTimeProvider();
        var (host, wire, _) = CreateHost(timeProvider: time);
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", default);
            PushPopup(wire, "EXPIRED", "about:blank");
            Assert.True(await WaitForAsync(() => time.ActiveTimers == 1));
            time.Advance(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.CreatePageAsync(_session, "late", default, "EXPIRED"));
            await WaitForFrameAsync(wire, frame => frame.Method == "Target.closeTarget"
                && frame.Parameters.GetProperty("targetId").GetString() == "EXPIRED");
            Assert.Single(Parse(wire.Snapshot()), frame => frame.Method == "Target.createTarget");
        }
    }

    [Fact]
    public async Task PendingPopupCapClosesOverflowWithoutEvictingLegitimateDelayedTargets()
    {
        var time = new ManualTimeProvider();
        var (host, wire, _) = CreateHost(timeProvider: time);
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", default);
            for (var i = 0; i <= 32; i++) PushPopup(wire, "BLANK" + i, "about:blank");
            await WaitForFrameAsync(wire, frame => frame.Method == "Target.closeTarget"
                && frame.Parameters.GetProperty("targetId").GetString() == "BLANK32");
            Assert.Single(Parse(wire.Snapshot()), frame => frame.Method == "Target.closeTarget");
            await host.CreatePageAsync(_session, "tab_2", default, "BLANK0");
            time.Advance(TimeSpan.FromMinutes(2));
            Assert.True(await WaitForAsync(() => Parse(wire.Snapshot()).Count(frame => frame.Method == "Target.closeTarget") == 32));
            Assert.DoesNotContain(Parse(wire.Snapshot()), frame => frame.Method == "Target.closeTarget"
                && frame.Parameters.GetProperty("targetId").GetString() == "BLANK0");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPopupAttachmentClosesItsRemovedPendingNativeTarget(bool canceled)
    {
        using var cancellation = new CancellationTokenSource();
        var (host, wire, _) = CreateHost();
        using (host)
        {
            await host.CreatePageAsync(_session, "tab_1", default);
            var raised = new TaskCompletionSource<BrowserNewTabRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.NewTabRequested += (_, e) => raised.TrySetResult(e);
            PushPopup(wire, "FAIL", "https://example.test/popup");
            await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
            wire.FailMethod = canceled ? null : "Target.attachToTarget";
            wire.OnReply = (method, ct) =>
            {
                if (canceled && method == "Target.attachToTarget") { cancellation.Cancel(); throw new OperationCanceledException(ct); }
                return Task.CompletedTask;
            };
            if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.CreatePageAsync(_session, "failed", cancellation.Token, "FAIL"));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => host.CreatePageAsync(_session, "failed", default, "FAIL"));
            Assert.Contains(Parse(wire.Snapshot()), frame => frame.Method == "Target.closeTarget"
                && frame.Parameters.GetProperty("targetId").GetString() == "FAIL");
        }
    }

    private static void PushPopup(ScriptedWire wire, string targetId, string url) =>
        wire.Push("{\"method\":\"Target.targetCreated\",\"params\":{\"targetInfo\":{\"targetId\":\"" + targetId
            + "\",\"type\":\"page\",\"url\":\"" + url + "\",\"openerId\":\"T1\"}}}");

    private static ChromiumBrowserHost CreateHost(FakeProcess[] processes, ScriptedWire[] wires)
    {
        var attempts = new Queue<FakeProcess>(processes);
        var connections = new Queue<ScriptedWire>(wires);
        return new ChromiumBrowserHost(Path.Combine(Path.GetTempPath(), "alcoler-chromium-host-" + Guid.NewGuid().ToString("N")),
            binaryResolver: _ => Task.FromResult("/fake/chrome"),
            processFactory: (_, _, _) => attempts.Dequeue(), wireFactory: () => connections.Dequeue());
    }

    private static (ChromiumBrowserHost Host, ScriptedWire Wire, FakeProcess Process) CreateHost(bool headless = false, TimeProvider? timeProvider = null)
    {
        var wire = new ScriptedWire();
        var process = new FakeProcess();
        var host = new ChromiumBrowserHost(
            Path.Combine(Path.GetTempPath(), "alcoler-chromium-host-" + Guid.NewGuid().ToString("N")),
            new ChromiumBrowserOptions { Headless = headless },
            binaryResolver: _ => Task.FromResult("/fake/chrome"),
            processFactory: (_, _, _) => process,
            wireFactory: () => wire, timeProvider: timeProvider);
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
        public bool HoldEvaluations { get; set; }
        public string? FailMethod { get; set; }
        public Func<CancellationToken, Task>? OnConnect { get; init; }
        public Func<string, CancellationToken, Task>? OnReply { get; set; }
        public int DisposeCalls;

        public List<string> Snapshot()
        {
            lock (_gate) return [.. Frames];
        }

        public Uri? ConnectedUri { get; private set; }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            ConnectedUri = uri;
            return OnConnect?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            lock (_gate) Frames.Add(text);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method) return Task.CompletedTask;
            var parameters = root.TryGetProperty("params", out var parameterElement) ? parameterElement.Clone() : default;
            if (method == FailMethod)
            {
                Push("{\"id\":" + root.GetProperty("id").GetInt64() + ",\"error\":{\"message\":\"deliberate protocol failure\"}}");
                return Task.CompletedTask;
            }
            if (method == "Runtime.evaluate" && HoldEvaluations) return Task.CompletedTask;
            var response = Respond(method, parameters);
            Push("{\"id\":" + root.GetProperty("id").GetInt64() + ",\"result\":" + BrowserJson.Serialize(response) + "}");
            return OnReply?.Invoke(method, cancellationToken) ?? Task.CompletedTask;
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

        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public int ActiveTimers { get { lock (_sync) return _timers.Count(timer => timer.Due is not null); } }
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_sync) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            ManualTimer[] due;
            lock (_sync)
            {
                _now += amount;
                due = _timers.Where(timer => timer.Due <= _now).ToArray();
                foreach (var timer in due) timer.Due = null;
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (owner._sync) { Due = null; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeProcess : IChromiumProcess
    {
        public int ShutdownCalls;
        public int StartCalls;
        public int DisposeCalls;
        public CancellationToken ShutdownToken;
        public Func<CancellationToken, Task>? OnStart { get; set; }
        public Func<CancellationToken, Task>? OnShutdown { get; init; }
        private EventHandler? _exited;

        public Uri WsUri { get; } = new("ws://127.0.0.1:9222/devtools/browser/fake");

        public event EventHandler? Exited
        {
            add => _exited += value;
            remove => _exited -= value;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StartCalls);
            return OnStart?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ShutdownCalls);
            ShutdownToken = cancellationToken;
            RaiseExited();
            return OnShutdown?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Action CaptureExit()
        {
            var handlers = _exited;
            return () => handlers?.Invoke(this, EventArgs.Empty);
        }
        public void RaiseExited() => _exited?.Invoke(this, EventArgs.Empty);

        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }
}
