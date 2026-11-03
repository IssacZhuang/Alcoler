using System.Text.Json;
using Alcoler.Browser.Automation;
using Alcoler.Contracts.Browser;
using Xunit;

namespace Alcoler.Browser.Tests.Automation;

public sealed class CdpBrowserAutomationTests
{
    [Fact]
    public async Task InitializesOnceWithOnlyAotSafeProtocolParameters()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        await engine.InitializeAsync(default);
        await engine.InitializeAsync(default);
        Assert.Single(page.Calls, c => c.Method == "Page.enable");
        Assert.Contains(page.Calls, c => c.Method == "Runtime.enable");
        Assert.Contains(page.Calls, c => c.Method == "DOM.enable");
        Assert.All(page.Calls, c => Assert.True(c.Parameters.ValueKind is JsonValueKind.Object or JsonValueKind.Null));
    }

    [Fact]
    public async Task OptionalAccessibilityFailureDoesNotBreakInitializationButSnapshotReportsIt()
    {
        using var page = new FakePage { FailAccessibility = true };
        using var engine = new CdpBrowserAutomation(page);
        await engine.InitializeAsync(default);
        var result = await Execute(engine, "snapshot", "{}");
        Assert.False(result.Ok);
        Assert.Contains("Accessibility unsupported", result.Error);
    }

    [Fact]
    public async Task SnapshotIncludesMeaningfulStatesAndRejectsRefsAfterNavigation()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var snapshot = await Execute(engine, "snapshot", "{}");
        Assert.True(snapshot.Ok, snapshot.Error);
        Assert.Contains("button \"Save\"", snapshot.Text);
        Assert.Contains("disabled=False", snapshot.Text);
        using var document = JsonDocument.Parse(snapshot.StructuredOutput!);
        var reference = document.RootElement.GetProperty("nodes")[1].GetProperty("ref").GetString();
        var query = await Execute(engine, "element_info", "{\"ref\":\"" + reference + "\"}");
        Assert.True(query.Ok, query.Error);
        Assert.Contains(page.Calls, c => c.Method == "DOM.resolveNode" && c.Parameters.GetProperty("backendNodeId").GetInt32() == 42);
        page.Emit("Page.frameNavigated", "{\"frame\":{\"id\":\"root\",\"url\":\"https://example.test/next\"}}");
        var stale = await Execute(engine, "click", "{\"ref\":\"" + reference + "\"}");
        Assert.False(stale.Ok);
        Assert.Contains("Stale", stale.Error);
    }

    [Fact]
    public async Task ClickUsesStableHitTestAndNativeMouseEvents()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var result = await Execute(engine, "dblclick", "{\"locator\":{\"role\":\"button\",\"name\":\"Save\"},\"modifiers\":[\"Shift\"]}");
        Assert.True(result.Ok, result.Error);
        var mouse = page.Calls.Where(c => c.Method == "Input.dispatchMouseEvent").ToArray();
        Assert.Equal(5, mouse.Length);
        Assert.Equal("mouseMoved", mouse[0].Parameters.GetProperty("type").GetString());
        Assert.Equal(2, mouse[^1].Parameters.GetProperty("clickCount").GetInt32());
        Assert.All(mouse, c => Assert.Equal(8, c.Parameters.GetProperty("modifiers").GetInt32()));
        Assert.True(page.Calls.Count(c => c.Method == "Runtime.callFunctionOn" && c.Parameters.GetProperty("functionDeclaration").GetString()!.Contains("inspect.call")) >= 2);
    }

    [Theory]
    [InlineData(-1, 1, false)]
    [InlineData(-1, -1, false)]
    [InlineData(-1, 1, true)]
    [InlineData(-1, -1, true)]
    public async Task MirroredOrRotatedFrameAncestorsNeverDispatchInput(double scaleX, double scaleY, bool ancestor)
    {
        using var page = new FakePage
        {
            FrameTransforms = ancestor ? [Transform(1, 1), Transform(scaleX, scaleY)] : [Transform(scaleX, scaleY)]
        };
        using var engine = new CdpBrowserAutomation(page);
        var result = await Execute(engine, "click", "{\"locator\":{\"frame\":\"iframe\",\"css\":\"button\"},\"force\":true}");
        Assert.False(result.Ok);
        Assert.Contains("Transformed frame input is not supported", result.Error);
        Assert.DoesNotContain(page.Calls, c => c.Method.StartsWith("Input."));
        var probe = Assert.Single(page.Calls, c => c.Method == "Runtime.callFunctionOn" && c.Parameters.GetProperty("functionDeclaration").GetString()!.Contains("DOMMatrixReadOnly"));
        Assert.Contains("e.assignedSlot || e.parentElement || e.getRootNode().host", probe.Parameters.GetProperty("functionDeclaration").GetString());
    }

    [Fact]
    public async Task TranslatedFrameAncestorsPreserveNativeTargetCoordinates()
    {
        using var page = new FakePage { FrameTransforms = [Transform(1, 1, 30, 40), Transform(1, 1, -5, 10)] };
        using var engine = new CdpBrowserAutomation(page);
        var result = await Execute(engine, "click", "{\"locator\":{\"frame\":\"iframe\",\"css\":\"button\"},\"force\":true}");
        Assert.True(result.Ok, result.Error);
        var down = Assert.Single(page.Calls, c => c.Method == "Input.dispatchMouseEvent" && c.Parameters.GetProperty("type").GetString() == "mousePressed");
        Assert.Equal(150, down.Parameters.GetProperty("x").GetDouble());
        Assert.Equal(220, down.Parameters.GetProperty("y").GetDouble());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OopifMissingFromRootFrameTreeUsesDiscoveredOwnerAndChecksParentOverlay(bool blocked)
    {
        using var page = new FakePage { FrameTransforms = [Transform(1, 1)], OmitChildFrame = true, BlockFrame = blocked };
        using var engine = new CdpBrowserAutomation(page);
        page.Emit("Target.attachedToTarget", "{\"sessionId\":\"oopif-session\",\"targetInfo\":{\"type\":\"iframe\",\"targetId\":\"child\"}}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var hitTests = 0;
        // The first blocked hit test must prevent input before cancellation ends the retry loop.
        page.AfterSend = method =>
        {
            if (blocked && method == "Runtime.callFunctionOn"
                && page.Calls[^1].Parameters.GetProperty("functionDeclaration").GetString()!.Contains("elementFromPoint(x,y)")
                && ++hitTests == 2)
                cancellation.Cancel();
        };
        var action = new BrowserCommandAction("click", Parse("{\"locator\":{\"frame\":\"iframe\",\"role\":\"button\",\"name\":\"Frame action\"}}"));
        if (blocked)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(action, cancellation.Token));
            Assert.Equal(2, hitTests);
        }
        else
        {
            var result = await engine.ExecuteAsync(action, cancellation.Token);
            Assert.True(result.Ok, result.Error);
        }
        Assert.Contains(page.Calls, c => c.Method == "Page.createIsolatedWorld" && c.Session == "oopif-session");
        Assert.Contains(page.Calls, c => c.Method == "Runtime.callFunctionOn" && c.Session is null && c.Parameters.GetProperty("functionDeclaration").GetString()!.Contains("DOMMatrixReadOnly"));
        Assert.DoesNotContain(page.Calls, c => c.Method == "DOM.getFrameOwner");
        if (blocked) Assert.DoesNotContain(page.Calls, c => c.Method.StartsWith("Input."));
        else
        {
            var down = Assert.Single(page.Calls, c => c.Method == "Input.dispatchMouseEvent" && c.Parameters.GetProperty("type").GetString() == "mousePressed");
            Assert.Equal(150, down.Parameters.GetProperty("x").GetDouble());
            Assert.Equal(220, down.Parameters.GetProperty("y").GetDouble());
        }
    }

    [Fact]
    public async Task OopifSwapDetachKeepsFreshlyAttachedFrameSession()
    {
        using var page = new FakePage { FrameTransforms = [Transform(1, 1)] };
        using var engine = new CdpBrowserAutomation(page);
        page.Emit("Target.attachedToTarget", "{\"sessionId\":\"swap-session\",\"targetInfo\":{\"type\":\"iframe\",\"targetId\":\"child\"}}");
        page.Emit("Page.frameDetached", "{\"frameId\":\"child\",\"reason\":\"swap\"}");
        var result = await Execute(engine, "click", "{\"locator\":{\"frame\":\"iframe\",\"role\":\"button\",\"name\":\"Frame action\"}}");
        Assert.True(result.Ok, result.Error);
        var world = Assert.Single(page.Calls, c => c.Method == "Page.createIsolatedWorld" && c.Parameters.GetProperty("frameId").GetString() == "child");
        Assert.Equal("swap-session", world.Session);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    public async Task TargetDetachedFromTargetRetiresFrameSessionRoute(int initializationDelayMs)
    {
        using var page = new FakePage { FrameTransforms = [Transform(1, 1)], InitializationDelayMs = initializationDelayMs };
        using var engine = new CdpBrowserAutomation(page);
        page.Emit("Target.attachedToTarget", "{\"sessionId\":\"doomed-session\",\"targetInfo\":{\"type\":\"iframe\",\"targetId\":\"child\"}}");
        page.Emit("Target.detachedFromTarget", "{\"sessionId\":\"doomed-session\"}");
        var result = await Execute(engine, "click", "{\"locator\":{\"frame\":\"iframe\",\"role\":\"button\",\"name\":\"Frame action\"}}");
        Assert.True(result.Ok, result.Error);
        var world = Assert.Single(page.Calls, c => c.Method == "Page.createIsolatedWorld" && c.Parameters.GetProperty("frameId").GetString() == "child");
        Assert.Null(world.Session);
    }

    [Fact]
    public async Task NonSwapFrameDetachedRemovesFrameSession()
    {
        using var page = new FakePage { FrameTransforms = [Transform(1, 1)] };
        using var engine = new CdpBrowserAutomation(page);
        page.Emit("Target.attachedToTarget", "{\"sessionId\":\"old-session\",\"targetInfo\":{\"type\":\"iframe\",\"targetId\":\"child\"}}");
        page.Emit("Page.frameDetached", "{\"frameId\":\"child\",\"reason\":\"replace\"}");
        var result = await Execute(engine, "click", "{\"locator\":{\"frame\":\"iframe\",\"role\":\"button\",\"name\":\"Frame action\"}}");
        Assert.True(result.Ok, result.Error);
        var world = Assert.Single(page.Calls, c => c.Method == "Page.createIsolatedWorld" && c.Parameters.GetProperty("frameId").GetString() == "child");
        Assert.Null(world.Session);
    }

    private static JsonElement Transform(double scaleX, double scaleY, double x = 0, double y = 0) => BrowserJson.Element(new Dictionary<string, object?>
    {
        ["matrix"] = new double[] { scaleX, 0, 0, 0, 0, scaleY, 0, 0, 0, 0, 1, 0, x, y, 0, 1 },
        ["rotate"] = "none", ["scale"] = "none", ["perspective"] = "none"
    });

    [Fact]
    public async Task TypeAlwaysAppendsAndFillReplaces()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var legacy = await engine.ExecuteAsync(new BrowserTypeAction("#name", "first"), default);
        Assert.True(legacy.Ok, legacy.Error);
        Assert.DoesNotContain(page.Calls, c => c.Method == "Input.dispatchKeyEvent");
        page.Calls.Clear();
        var filled = await Execute(engine, "fill", "{\"selector\":\"#name\",\"text\":\"replace\"}");
        Assert.True(filled.Ok, filled.Error);
        Assert.Contains(page.Calls, c => c.Method == "Input.dispatchKeyEvent" && c.Parameters.GetProperty("key").GetString() == "A");
        Assert.Single(page.Calls, c => c.Method == "Input.insertText");
    }

    [Fact]
    public async Task EvaluateUsesPageWorldAwaitsPromiseAndPropagatesExceptions()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var success = await Execute(engine, "evaluate", "{\"expression\":\"async arg => arg.value\",\"arg\":{\"value\":17}}");
        Assert.True(success.Ok, success.Error);
        var evaluation = page.Calls.Single(c => c.Method == "Runtime.evaluate" && c.Parameters.GetProperty("expression").GetString()!.StartsWith("(async()=>"));
        Assert.True(evaluation.Parameters.GetProperty("awaitPromise").GetBoolean());
        Assert.False(evaluation.Parameters.TryGetProperty("contextId", out _));
        page.FailEvaluation = true;
        var failed = await Execute(engine, "evaluate", "{\"expression\":\"throw Error('broken')\"}");
        Assert.False(failed.Ok);
        Assert.Contains("broken", failed.Error);
        Assert.True(failed.SideEffectUncertain);
    }

    [Fact]
    public async Task NavigateRejectsUnsafeUrlsAndNetworkIdleWithoutSendingNavigation()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var unsafeUrl = await Execute(engine, "navigate", "{\"url\":\"file:///C:/secret\"}");
        var unsupported = await Execute(engine, "navigate", "{\"url\":\"https://example.test\",\"waitUntil\":\"networkidle\"}");
        Assert.False(unsafeUrl.Ok);
        Assert.False(unsupported.Ok);
        Assert.Contains("networkidle", unsupported.Error);
        Assert.DoesNotContain(page.Calls, c => c.Method == "Page.navigate");
    }

    [Fact]
    public async Task NavigationUsesLoadStateAndMetadata()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var result = await Execute(engine, "navigate", "{\"url\":\"https://example.test/page\",\"waitUntil\":\"load\"}");
        Assert.True(result.Ok, result.Error);
        Assert.Equal("https://example.test/page", result.Url);
        Assert.Equal("Test", result.Title);
    }

    [Fact]
    public async Task ScreenshotCapsAreaBeforeCaptureAndReturnsPng()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var tooLarge = await Execute(engine, "screenshot", "{\"clip\":{\"x\":0,\"y\":0,\"width\":8000,\"height\":8000}}");
        Assert.False(tooLarge.Ok);
        Assert.DoesNotContain(page.Calls, c => c.Method == "Page.captureScreenshot");
        var result = await Execute(engine, "screenshot", "{\"fullPage\":true}");
        Assert.True(result.Ok, result.Error);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, result.ScreenshotPng);
        var clip = page.Calls.Single(c => c.Method == "Page.captureScreenshot").Parameters.GetProperty("clip");
        Assert.Equal(1, clip.GetProperty("scale").GetInt32());
    }

    [Fact]
    public async Task DialogAndDownloadsConsumeRealProtocolEvents()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        page.Emit("Page.javascriptDialogOpening", "{\"type\":\"prompt\",\"message\":\"Name?\"}");
        var status = await Execute(engine, "dialog", "{}");
        Assert.Contains("Name?", status.StructuredOutput);
        var accepted = await Execute(engine, "dialog", "{\"operation\":\"accept\",\"text\":\"Alcoler\"}");
        Assert.True(accepted.Ok, accepted.Error);
        Assert.Contains(page.Calls, c => c.Method == "Page.handleJavaScriptDialog" && c.Parameters.GetProperty("promptText").GetString() == "Alcoler");
        page.Emit("Browser.downloadWillBegin", "{\"guid\":\"one\",\"url\":\"https://example.test/a\",\"suggestedFilename\":\"a.bin\"}");
        page.Emit("Browser.downloadProgress", "{\"guid\":\"one\",\"state\":\"completed\",\"receivedBytes\":15}");
        var downloads = await Execute(engine, "downloads", "{}");
        Assert.Contains("completed", downloads.StructuredOutput);
        Assert.Contains("a.bin", downloads.StructuredOutput);
    }

    [Fact]
    public async Task CancellationPreventsFurtherProtocolSends()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        using var cancellation = new CancellationTokenSource();
        page.AfterSend = method => { if (method == "Input.dispatchMouseEvent") cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(new BrowserCommandAction("click", Parse("{\"x\":5,\"y\":10}")), cancellation.Token));
        Assert.Single(page.Calls, c => c.Method == "Input.dispatchMouseEvent");
        Assert.Equal("Input.dispatchMouseEvent", page.Calls[^1].Method);
    }

    [Fact]
    public async Task CancellationAfterMouseDownOnlySendsCompensatingRelease()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        using var cancellation = new CancellationTokenSource();
        page.AfterSend = method =>
        {
            if (method == "Input.dispatchMouseEvent" && page.Calls[^1].Parameters.GetProperty("type").GetString() == "mousePressed") cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(new BrowserCommandAction("click", Parse("{\"x\":5,\"y\":10}")), cancellation.Token));
        var mouse = page.Calls.Where(c => c.Method == "Input.dispatchMouseEvent").ToArray();
        Assert.Equal(3, mouse.Length);
        Assert.Equal("mouseReleased", mouse[^1].Parameters.GetProperty("type").GetString());
    }

    [Fact]
    public async Task HostMetadataUpdatesDoNotPublishDomMutationStorms()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var count = 0;
        engine.StateChanged += (_, _) => count++;
        for (var i = 0; i < 100; i++) page.Emit("DOM.attributeModified", "{}");
        Assert.Equal(0, count);
        page.Emit("Host.titleChanged", "{\"title\":\"Human edit\",\"url\":\"https://example.test/new\"}");
        Assert.Equal("Human edit", engine.Title);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TimeoutReportsUncertainNativeSideEffectAndStopsSending()
    {
        using var page = new FakePage { HangMouse = true };
        using var engine = new CdpBrowserAutomation(page);
        var result = await Execute(engine, "click", "{\"x\":5,\"y\":10,\"timeoutMs\":50}");
        Assert.False(result.Ok);
        Assert.True(result.SideEffectUncertain);
        Assert.Contains("timed out", result.Error);
        Assert.Single(page.Calls, c => c.Method == "Input.dispatchMouseEvent");
    }

    [Fact]
    public async Task RecordingCompletionReturnsWebMAndCancelIsAsynchronous()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var start = await Execute(engine, "record_start", "{\"maxDurationMs\":100,\"fps\":10}");
        Assert.True(start.Ok, start.Error);
        BrowserActionResult? status = null;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(10);
            status = await Execute(engine, "record_status", "{}");
            if (status.Media is not null) break;
        }
        Assert.NotNull(status?.Media);
        Assert.Equal("video/webm", status.MediaType);
        var again = await Execute(engine, "record_start", "{\"maxDurationMs\":5000}");
        Assert.True(again.Ok, again.Error);
        var cancel = await Execute(engine, "record_cancel", "{}");
        Assert.Contains("cancelled", cancel.StructuredOutput);
    }

    [Fact]
    public async Task RecordingRejectsEmptyMediaInsteadOfCompleting()
    {
        using var page = new FakePage { RecordingMedia = "" };
        using var engine = new CdpBrowserAutomation(page);
        var start = await Execute(engine, "record_start", "{\"maxDurationMs\":100,\"fps\":10}");
        Assert.True(start.Ok, start.Error);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        BrowserActionResult status;
        do
        {
            await Task.Delay(10, deadline.Token);
            status = await engine.ExecuteAsync(new BrowserCommandAction("record_status", Parse("{}")), deadline.Token);
        } while (status.Ok && status.Media is null);
        Assert.False(status.Ok);
        Assert.Contains("Recording did not produce media", status.Error);
        Assert.Null(status.Media);
        Assert.Equal("failed", Parse(status.StructuredOutput!).GetProperty("state").GetString());
    }

    [Fact]
    public async Task SnapshotScopeCanReuseExistingReferenceBeforeInvalidation()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var first = await Execute(engine, "snapshot", "{}");
        using var document = JsonDocument.Parse(first.StructuredOutput!);
        var reference = document.RootElement.GetProperty("nodes")[1].GetProperty("ref").GetString();
        var scoped = await Execute(engine, "snapshot", "{\"ref\":\"" + reference + "\"}");
        Assert.True(scoped.Ok, scoped.Error);
        using var scope = JsonDocument.Parse(scoped.StructuredOutput!);
        Assert.Single(scope.RootElement.GetProperty("nodes").EnumerateArray());
        Assert.DoesNotContain("RootWebArea", scoped.Text);
    }

    [Fact]
    public async Task DownloadMediaIsBoundedAndDoesNotReadFiles()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var result = await Execute(engine, "download_media", "{\"url\":\"blob:https://example.test/id\",\"fileName\":\"image.png\"}");
        Assert.True(result.Ok, result.Error);
        Assert.Equal("image/png", result.MediaType);
        Assert.Equal("image.png", result.MediaFileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Media);
        Assert.Contains(page.Calls, c => c.Method == "Runtime.callFunctionOn" && c.Parameters.GetProperty("functionDeclaration").GetString()!.Contains("credentials:'include'"));
    }

    [Fact]
    public async Task StrictLocatorErrorDoesNotDispatchNativeInput()
    {
        using var page = new FakePage { StrictFailure = true };
        using var engine = new CdpBrowserAutomation(page);
        var result = await Execute(engine, "click", "{\"selector\":\"button\"}");
        Assert.False(result.Ok);
        Assert.Contains("Strict locator", result.Error);
        Assert.DoesNotContain(page.Calls, c => c.Method.StartsWith("Input."));
    }

    [Fact]
    public async Task FreshAccessibilitySnapshotsRetireAllPreviouslyBoundRendererRefs()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        string? previous = null;
        for (var i = 0; i < 5; i++)
        {
            var snapshot = await Execute(engine, "snapshot", "{}");
            Assert.True(snapshot.Ok, snapshot.Error);
            Assert.All(page.RendererRefs.Values, refs => Assert.Empty(refs));
            if (previous is not null) Assert.False((await Execute(engine, "element_info", "{\"ref\":\"" + previous + "\"}")).Ok);
            previous = Reference(snapshot);
            Assert.True((await Execute(engine, "element_info", "{\"ref\":\"" + previous + "\"}")).Ok);
            Assert.Single(Assert.Single(page.RendererRefs).Value);
        }
    }

    [Theory]
    [InlineData("snapshot", "snapshot")]
    [InlineData("snapshot", "dom_snapshot")]
    [InlineData("dom_snapshot", "snapshot")]
    [InlineData("dom_snapshot", "dom_snapshot")]
    public async Task ScopedSnapshotResolvesThePreviousEpochBeforeRetiringRendererRefs(string firstKind, string secondKind)
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var first = await Execute(engine, firstKind, "{}");
        Assert.True(first.Ok, first.Error);
        var reference = Reference(first);
        var before = page.Calls.Count;
        var scoped = await Execute(engine, secondKind, "{\"ref\":\"" + reference + "\"}");
        Assert.True(scoped.Ok, scoped.Error);
        Assert.DoesNotContain(page.RendererRefs.Values, refs => refs.Contains(reference));
        var calls = page.Calls.Skip(before).ToArray();
        var selected = Array.FindIndex(calls, call => call.Method == "Runtime.evaluate"
            && call.Parameters.GetProperty("expression").GetString()!.StartsWith("__alcolerAutomation.select(", StringComparison.Ordinal));
        var retired = Array.FindIndex(calls, call => call.Method == "Runtime.evaluate"
            && call.Parameters.GetProperty("expression").GetString()!.StartsWith("globalThis.__alcolerAutomation?.retire(", StringComparison.Ordinal));
        Assert.True(selected >= 0 && retired > selected);
        Assert.True((await Execute(engine, "element_info", "{\"ref\":\"" + Reference(scoped) + "\"}")).Ok);
    }

    [Fact]
    public async Task ScopedAccessibilitySnapshotRetiresMapsInBothMainAndOopifContexts()
    {
        using var page = new FakePage { FrameTransforms = [Transform(1, 1)] };
        using var engine = new CdpBrowserAutomation(page);
        page.Emit("Target.attachedToTarget", "{\"sessionId\":\"oopif-session\",\"targetInfo\":{\"type\":\"iframe\",\"targetId\":\"child\"}}");
        var snapshot = await Execute(engine, "snapshot", "{}");
        Assert.True(snapshot.Ok, snapshot.Error);
        using var document = JsonDocument.Parse(snapshot.StructuredOutput!);
        var references = document.RootElement.GetProperty("nodes").EnumerateArray()
            .Where(node => node.GetProperty("role").GetString() == "button").Select(node => node.GetProperty("ref").GetString()!).ToArray();
        Assert.Equal(2, references.Length);
        foreach (var reference in references) Assert.True((await Execute(engine, "element_info", "{\"ref\":\"" + reference + "\"}")).Ok);
        Assert.Equal(2, page.RendererRefs.Count);
        Assert.All(page.RendererRefs.Values, refs => Assert.Single(refs));
        var scoped = await Execute(engine, "snapshot", "{\"ref\":\"" + references[1] + "\"}");
        Assert.True(scoped.Ok, scoped.Error);
        Assert.All(page.RendererRefs.Values, refs => Assert.Empty(refs));
        var retire = page.Calls.Where(call => call.Method == "Runtime.evaluate"
            && call.Parameters.GetProperty("expression").GetString()!.StartsWith("globalThis.__alcolerAutomation?.retire(", StringComparison.Ordinal)).ToArray();
        Assert.Contains(retire, call => call.Session is null);
        Assert.Contains(retire, call => call.Session == "oopif-session");
    }

    [Theory]
    [InlineData("acquired")]
    [InlineData("bound")]
    [InlineData("frame-owner")]
    [InlineData("actionability")]
    [InlineData("frame-offset")]
    [InlineData("frame-hit")]
    [InlineData("scoped-snapshot")]
    public async Task CancellationAfterAcquiringRemoteObjectsStillReleasesThem(string phase)
    {
        using var page = new FakePage { FrameTransforms = phase.StartsWith("frame", StringComparison.Ordinal) ? [Transform(1, 1)] : null };
        using var engine = new CdpBrowserAutomation(page);
        using var cancellation = new CancellationTokenSource();
        var reference = phase is "bound" or "scoped-snapshot" ? Reference(await Execute(engine, "snapshot", "{}")) : null;
        var canceled = false;
        page.AfterSend = method =>
        {
            var call = page.Calls[^1];
            var function = method == "Runtime.callFunctionOn" ? call.Parameters.GetProperty("functionDeclaration").GetString()! : "";
            if (phase switch
            {
                "acquired" => method == "Runtime.evaluate" && call.Parameters.GetProperty("expression").GetString()!.StartsWith("__alcolerAutomation.select(", StringComparison.Ordinal),
                "bound" => method == "DOM.resolveNode",
                "frame-owner" or "scoped-snapshot" => method == "DOM.describeNode",
                "actionability" => function.Contains("scrollIntoView", StringComparison.Ordinal),
                "frame-offset" => function.Contains("DOMMatrixReadOnly", StringComparison.Ordinal),
                "frame-hit" => function.Contains("elementFromPoint(x,y)", StringComparison.Ordinal),
                _ => false,
            }) { canceled = true; cancellation.Cancel(); }
        };
        var input = phase.StartsWith("frame", StringComparison.Ordinal) ? "{\"locator\":{\"frame\":\"iframe\",\"css\":\"button\"}}"
            : reference is null ? "{\"selector\":\"button\"}" : "{\"ref\":\"" + reference + "\"}";
        var action = phase == "scoped-snapshot" ? "snapshot" : phase == "acquired" || phase == "bound" ? "element_info" : "click";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(new BrowserCommandAction(action, Parse(input)), cancellation.Token));
        Assert.True(canceled);
        Assert.Contains(page.Calls, call => call.Method == "Runtime.releaseObject");
        Assert.All(page.ReleaseTokensWereCanceled, canceledToken => Assert.False(canceledToken));
        Assert.All(page.AcquiredObjects, id => Assert.Contains(page.Calls, call => call.Method == "Runtime.releaseObject" && call.Parameters.GetProperty("objectId").GetString() == id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseFailureOrNoncooperativeReleaseCannotMaskCancellationOrHoldTheAction(bool hang)
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Release = _ => hang ? release.Task : Task.FromException<JsonElement>(new OperationCanceledException("release failed"));
        page.AfterSend = method =>
        {
            if (method == "Runtime.evaluate" && page.Calls[^1].Parameters.GetProperty("expression").GetString()!.StartsWith("__alcolerAutomation.select(", StringComparison.Ordinal)) cancellation.Cancel();
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ExecuteAsync(
                new BrowserCommandAction("element_info", Parse("{\"selector\":\"button\"}")), cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Single(page.ReleaseTokensWereCanceled);
            Assert.False(page.ReleaseTokensWereCanceled[0]);
        }
        finally { release.TrySetResult(Parse("{}")); }
    }

    [Fact]
    public async Task DisposalAfterAcquiringAHandleStillRunsIndependentRelease()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        page.AfterSend = method =>
        {
            if (method == "Runtime.evaluate" && page.Calls[^1].Parameters.GetProperty("expression").GetString()!.StartsWith("__alcolerAutomation.select(", StringComparison.Ordinal)) engine.Dispose();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(engine, "element_info", "{\"selector\":\"button\"}"));
        Assert.Single(page.ReleaseTokensWereCanceled);
        Assert.False(page.ReleaseTokensWereCanceled[0]);
    }

    [Fact]
    public async Task JavaScriptFailureReleasesItsExceptionHandleAndOriginalElementWithoutReplacingTheError()
    {
        using var page = new FakePage { FailFunction = "inspect.call", Release = _ => Task.FromException<JsonElement>(new IOException("cleanup failed")) };
        using var engine = new CdpBrowserAutomation(page);
        var result = await Execute(engine, "element_info", "{\"selector\":\"button\"}");
        Assert.False(result.Ok);
        Assert.Contains("original JavaScript failure", result.Error);
        Assert.Contains(page.Calls, call => call.Method == "Runtime.releaseObject" && call.Parameters.GetProperty("objectId").GetString() == "exception1");
        Assert.Contains(page.Calls, call => call.Method == "Runtime.releaseObject" && call.Parameters.GetProperty("objectId").GetString() == "element1");
    }

    [Theory]
    [InlineData("navigation")]
    [InlineData("destroyed")]
    [InlineData("cleared")]
    [InlineData("frame-detached")]
    [InlineData("session-detached")]
    public async Task DeadExecutionContextsDoNotAccumulateCleanupMetadata(string reason)
    {
        var child = reason is "frame-detached" or "session-detached";
        using var page = new FakePage { FrameTransforms = child ? [Transform(1, 1)] : null, FailRetirement = true };
        using var engine = new CdpBrowserAutomation(page);
        for (var i = 0; i < 5; i++)
        {
            if (child) page.Emit("Target.attachedToTarget", "{\"sessionId\":\"child-session\",\"targetInfo\":{\"type\":\"iframe\",\"targetId\":\"child\"}}");
            var snapshot = await Execute(engine, "snapshot", child ? "{\"locator\":{\"frame\":\"iframe\",\"css\":\"button\"}}" : "{}");
            Assert.True(snapshot.Ok, snapshot.Error);
            var queried = await Execute(engine, "element_info", "{\"ref\":\"" + Reference(snapshot) + "\"}");
            Assert.True(queried.Ok, queried.Error);
            Assert.Equal(1, TrackedContextCount(engine));
            switch (reason)
            {
                case "navigation": page.Emit("Page.frameNavigated", "{\"frame\":{\"id\":\"root\",\"url\":\"https://example.test/next\"}}"); break;
                case "destroyed": page.Emit("Runtime.executionContextDestroyed", "{\"executionContextId\":7}"); break;
                case "cleared": page.Emit("Runtime.executionContextsCleared", "{}"); break;
                case "frame-detached": page.Emit("Page.frameDetached", "{\"frameId\":\"child\",\"reason\":\"remove\"}"); break;
                case "session-detached": page.Emit("Target.detachedFromTarget", "{\"sessionId\":\"child-session\"}"); break;
            }
            Assert.Equal(0, TrackedContextCount(engine));
        }
    }

    [Fact]
    public async Task FailedCleanupOfALiveContextIsRetriedAfterDocumentOnlyInvalidation()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var snapshot = await Execute(engine, "snapshot", "{}");
        await Execute(engine, "element_info", "{\"ref\":\"" + Reference(snapshot) + "\"}");
        page.Emit("DOM.documentUpdated", "{}");
        page.FailRetirement = true;
        Assert.True((await Execute(engine, "snapshot", "{}")).Ok);
        Assert.Equal(1, TrackedContextCount(engine));
        Assert.Single(Assert.Single(page.RendererRefs).Value);
        page.FailRetirement = false;
        Assert.True((await Execute(engine, "snapshot", "{}")).Ok);
        Assert.Equal(0, TrackedContextCount(engine));
        Assert.Empty(Assert.Single(page.RendererRefs).Value);
    }

    private static int TrackedContextCount(CdpBrowserAutomation engine)
    {
        var contexts = typeof(CdpBrowserAutomation).GetField("_referenceContexts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
        return (int)contexts.GetType().GetProperty("Count")!.GetValue(contexts)!;
    }

    private static string Reference(BrowserActionResult snapshot)
    {
        using var document = JsonDocument.Parse(snapshot.StructuredOutput!);
        var root = document.RootElement;
        return (root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : root.GetProperty("nodes").EnumerateArray())
            .First(node => node.GetProperty("role").GetString() == "button").GetProperty("ref").GetString()!;
    }

    private static Task<BrowserActionResult> Execute(CdpBrowserAutomation engine, string command, string input) => engine.ExecuteAsync(new BrowserCommandAction(command, Parse(input)), default);
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }

    private sealed class FakePage : IBrowserPage
    {
        public string Id => "tab-test";
        public event EventHandler<BrowserProtocolEvent>? ProtocolEvent;
        public List<Call> Calls { get; } = [];
        public bool FailAccessibility { get; init; }
        public bool FailEvaluation { get; set; }
        public bool StrictFailure { get; init; }
        public bool HangMouse { get; init; }
        public int InitializationDelayMs { get; init; }
        public JsonElement[]? FrameTransforms { get; init; }
        public bool OmitChildFrame { get; init; }
        public bool BlockFrame { get; init; }
        public Action<string>? AfterSend { get; set; }
        public string? FailFunction { get; init; }
        public bool FailRetirement { get; set; }
        public string RecordingMedia { get; init; } = "AQID";
        public Func<CancellationToken, Task<JsonElement>>? Release { get; set; }
        public List<bool> ReleaseTokensWereCanceled { get; } = [];
        public HashSet<string> AcquiredObjects { get; } = [];
        public Dictionary<(int Context, string? Session), HashSet<string>> RendererRefs { get; } = [];
        private readonly Dictionary<(int Context, string? Session), long> _epochs = [];
        private string _url = "https://example.test/";

        public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken, string? sessionId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = BrowserJson.Element(parameters);
            Calls.Add(new(method, p, sessionId));
            AfterSend?.Invoke(method);
            if (method == "Page.enable" && InitializationDelayMs > 0) await Task.Delay(InitializationDelayMs, cancellationToken);
            if (HangMouse && method == "Input.dispatchMouseEvent") await Task.Delay(Timeout.Infinite, cancellationToken);
            if (FailAccessibility && method.StartsWith("Accessibility.")) throw new NotSupportedException("Accessibility unsupported");
            if (method == "Page.getFrameTree") return FrameTransforms is null || OmitChildFrame
                ? Parse("{\"frameTree\":{\"frame\":{\"id\":\"root\",\"url\":\"https://example.test/\"}}}")
                : Parse("{\"frameTree\":{\"frame\":{\"id\":\"root\",\"url\":\"https://example.test/\"},\"childFrames\":[{\"frame\":{\"id\":\"child\",\"parentId\":\"root\",\"url\":\"https://example.test/frame\"}}]}}");
            if (method == "Page.createIsolatedWorld") return Parse("{\"executionContextId\":" + (p.GetProperty("frameId").GetString() == "child" ? 8 : 7) + "}");
            if (method == "Page.navigate")
            {
                _url = p.GetProperty("url").GetString()!;
                Emit("Page.frameNavigated", "{\"frame\":{\"id\":\"root\",\"url\":\"" + _url + "\"}}");
                Emit("Page.lifecycleEvent", "{\"frameId\":\"root\",\"name\":\"load\"}");
                return Parse("{\"frameId\":\"root\",\"loaderId\":\"load1\"}");
            }
            if (method == "Page.getLayoutMetrics") return Parse("{\"cssContentSize\":{\"width\":800,\"height\":1200}}");
            if (method == "Page.captureScreenshot") return Parse("{\"data\":\"iVBORw==\"}");
            if (method == "Runtime.releaseObject")
            {
                ReleaseTokensWereCanceled.Add(cancellationToken.IsCancellationRequested);
                return Release is null ? Parse("{}") : await Release(cancellationToken);
            }
            if (method == "DOM.resolveNode")
            {
                var objectId = p.GetProperty("backendNodeId").GetInt32() == 43 ? "child-element" : "element1";
                AcquiredObjects.Add(objectId);
                return Parse("{\"object\":{\"objectId\":\"" + objectId + "\"}}");
            }
            if (method == "DOM.getFrameOwner") return Parse("{\"backendNodeId\":42}");
            if (method == "DOM.describeNode") return p.GetProperty("objectId").GetString() == "child-element"
                ? Parse("{\"node\":{\"backendNodeId\":43}}")
                : FrameTransforms is null ? Parse("{\"node\":{\"backendNodeId\":42}}") : Parse("{\"node\":{\"backendNodeId\":42,\"frameId\":\"child\"}}");
            if (method == "Accessibility.getFullAXTree")
            {
                var frame = p.GetProperty("frameId").GetString()!;
                return Parse("{\"nodes\":[{\"nodeId\":\"1\",\"role\":{\"value\":\"RootWebArea\"},\"name\":{\"value\":\"Test\"},\"childIds\":[\"2\"]},{\"nodeId\":\"2\",\"frameId\":\"" + frame
                    + "\",\"role\":{\"value\":\"button\"},\"name\":{\"value\":\"Save\"},\"backendDOMNodeId\":" + (frame == "child" ? 43 : 42) + ",\"properties\":[{\"name\":\"disabled\",\"value\":{\"value\":false}}]}]}");
            }
            if (method == "Runtime.evaluate")
            {
                var expression = p.GetProperty("expression").GetString()!;
                if (expression.StartsWith("(async()=>") && FailEvaluation) return Parse("{\"exceptionDetails\":{\"exception\":{\"description\":\"Error: broken\"}}}");
                if (expression.StartsWith("globalThis.__alcolerAutomation?.retire(", StringComparison.Ordinal))
                {
                    if (FailRetirement) throw new InvalidOperationException("retirement failed");
                    Retire((p.GetProperty("contextId").GetInt32(), sessionId), long.Parse(expression[(expression.LastIndexOf('(') + 1)..^1], System.Globalization.CultureInfo.InvariantCulture));
                    return Value(true);
                }
                if (expression.StartsWith("__alcolerAutomation.snapshot(", StringComparison.Ordinal))
                    return Snapshot((p.GetProperty("contextId").GetInt32(), sessionId), long.Parse(expression.Split('(')[1].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture));
                if (expression.Contains("__alcolerAutomation.select(") && !expression.Contains("Object.defineProperty"))
                {
                    if (StrictFailure) return Parse("{\"exceptionDetails\":{\"text\":\"Strict locator matched 2 elements\"}}");
                    var objectId = p.GetProperty("contextId").GetInt32() == 8 ? "child-element" : "element1";
                    AcquiredObjects.Add(objectId);
                    return Parse("{\"result\":{\"objectId\":\"" + objectId + "\"}}");
                }
                if (expression.Contains("url:location.href")) return BrowserJson.Element(new Dictionary<string, object?> { ["result"] = new Dictionary<string, object?> { ["value"] = new Dictionary<string, object?> { ["url"] = _url, ["title"] = "Test" } } });
                if (expression == "document.readyState") return Value("complete");
                return Value(true);
            }
            if (method == "Runtime.callFunctionOn")
            {
                var function = p.GetProperty("functionDeclaration").GetString()!;
                var context = (p.TryGetProperty("executionContextId", out var contextId) ? contextId.GetInt32()
                    : p.TryGetProperty("objectId", out var objectId) && objectId.GetString() == "child-element" ? 8 : 7, sessionId);
                if (FailFunction is not null && function.Contains(FailFunction, StringComparison.Ordinal))
                    return Parse("{\"exceptionDetails\":{\"exception\":{\"description\":\"original JavaScript failure\",\"objectId\":\"exception1\"}},\"result\":{\"objectId\":\"exception1\"}}");
                if (function.Contains("refs.set", StringComparison.Ordinal))
                {
                    Retire(context, p.GetProperty("arguments")[1].GetProperty("value").GetInt64());
                    RendererRefs[context].Add(p.GetProperty("arguments")[0].GetProperty("value").GetString()!);
                    return Value(true);
                }
                if (function.Contains("__alcolerAutomation.snapshot(", StringComparison.Ordinal))
                    return Snapshot(context, p.GetProperty("arguments")[0].GetProperty("value").GetInt64());
                if (function.Contains("DOMMatrixReadOnly")) return Value(new Dictionary<string, object?> { ["x"] = 100, ["y"] = 200, ["transforms"] = FrameTransforms });
                if (function.Contains("elementFromPoint(x,y)")) return Value(!BlockFrame);
                if (function.Contains("inspect.call")) return Parse("{\"result\":{\"value\":{\"connected\":true,\"visible\":true,\"enabled\":true,\"editable\":true,\"hit\":true,\"x\":50,\"y\":20,\"left\":10,\"top\":10,\"width\":80,\"height\":20}}}");
                if (function.Contains("credentials:'include'")) return Parse("{\"result\":{\"value\":{\"type\":\"image/png\",\"data\":\"AQID\"}}}");
                if (function.Contains("No WebM")) return Parse("{\"result\":{\"value\":{\"mimeType\":\"video/webm\",\"width\":800,\"height\":600}}}");
                if (function.Contains("return data.slice")) return Value(RecordingMedia);
                return Value(true);
            }
            return Parse("{}");
        }

        private void Retire((int Context, string? Session) context, long epoch)
        {
            if (!RendererRefs.TryGetValue(context, out var refs)) RendererRefs[context] = refs = [];
            if (!_epochs.TryGetValue(context, out var previous) || previous != epoch) refs.Clear();
            _epochs[context] = epoch;
        }
        private JsonElement Snapshot((int Context, string? Session) context, long epoch)
        {
            Retire(context, epoch);
            var reference = "d" + epoch + "_1";
            RendererRefs[context].Add(reference);
            return Parse("{\"result\":{\"value\":[{\"ref\":\"" + reference + "\",\"role\":\"button\",\"name\":\"Save\"}]}}");
        }
        private static JsonElement Value(object value) => BrowserJson.Element(new Dictionary<string, object?> { ["result"] = new Dictionary<string, object?> { ["value"] = value } });
        public void Emit(string method, string parameters, string? session = null) => ProtocolEvent?.Invoke(this, new(method, Parse(parameters), session));
        public void Dispose() { }
    }

    private sealed record Call(string Method, JsonElement Parameters, string? Session);
}
