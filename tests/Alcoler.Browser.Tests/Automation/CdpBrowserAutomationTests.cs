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
        var result = await Execute(engine, "click", "{\"locator\":{\"frame\":\"iframe\",\"role\":\"button\",\"name\":\"Frame action\"},\"timeoutMs\":200}");
        Assert.Equal(!blocked, result.Ok);
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
        var result = await Execute(engine, "click", "{\"locator\":{\"frame\":\"iframe\",\"role\":\"button\",\"name\":\"Frame action\"},\"timeoutMs\":200}");
        Assert.True(result.Ok, result.Error);
        var world = Assert.Single(page.Calls, c => c.Method == "Page.createIsolatedWorld" && c.Parameters.GetProperty("frameId").GetString() == "child");
        Assert.Equal("swap-session", world.Session);
    }

    [Fact]
    public async Task TargetDetachedFromTargetRetiresFrameSessionRoute()
    {
        using var page = new FakePage { FrameTransforms = [Transform(1, 1)] };
        using var engine = new CdpBrowserAutomation(page);
        page.Emit("Target.attachedToTarget", "{\"sessionId\":\"doomed-session\",\"targetInfo\":{\"type\":\"iframe\",\"targetId\":\"child\"}}");
        page.Emit("Target.detachedFromTarget", "{\"sessionId\":\"doomed-session\"}");
        var result = await Execute(engine, "click", "{\"locator\":{\"frame\":\"iframe\",\"role\":\"button\",\"name\":\"Frame action\"},\"timeoutMs\":200}");
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
        var result = await Execute(engine, "click", "{\"locator\":{\"frame\":\"iframe\",\"role\":\"button\",\"name\":\"Frame action\"},\"timeoutMs\":200}");
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
    public async Task SuspendDiscardsCompletedRecordingAndDrainsCancelledRecording()
    {
        using var page = new FakePage();
        using var engine = new CdpBrowserAutomation(page);
        var start = await Execute(engine, "record_start", "{\"maxDurationMs\":100,\"fps\":10}");
        Assert.True(start.Ok, start.Error);
        await Task.Delay(200);
        await engine.SuspendAsync(default);
        var status = await Execute(engine, "record_status", "{}");
        Assert.Null(status.Media);
        Assert.Contains("cancelled", status.StructuredOutput);
        await Execute(engine, "record_start", "{\"maxDurationMs\":5000}");
        await engine.SuspendAsync(default);
        Assert.Null((await Execute(engine, "record_status", "{}")).Media);
        engine.Dispose();
        await engine.SuspendAsync(default);
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
        public JsonElement[]? FrameTransforms { get; init; }
        public bool OmitChildFrame { get; init; }
        public bool BlockFrame { get; init; }
        public Action<string>? AfterSend { get; set; }
        private string _url = "https://example.test/";

        public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken, string? sessionId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = BrowserJson.Element(parameters);
            Calls.Add(new(method, p, sessionId));
            AfterSend?.Invoke(method);
            if (HangMouse && method == "Input.dispatchMouseEvent") await Task.Delay(Timeout.Infinite, cancellationToken);
            if (FailAccessibility && method.StartsWith("Accessibility.")) throw new NotSupportedException("Accessibility unsupported");
            if (method == "Page.getFrameTree") return FrameTransforms is null || OmitChildFrame
                ? Parse("{\"frameTree\":{\"frame\":{\"id\":\"root\",\"url\":\"https://example.test/\"}}}")
                : Parse("{\"frameTree\":{\"frame\":{\"id\":\"root\",\"url\":\"https://example.test/\"},\"childFrames\":[{\"frame\":{\"id\":\"child\",\"parentId\":\"root\",\"url\":\"https://example.test/frame\"}}]}}");
            if (method == "Page.createIsolatedWorld") return Parse("{\"executionContextId\":7}");
            if (method == "Page.navigate")
            {
                _url = p.GetProperty("url").GetString()!;
                Emit("Page.frameNavigated", "{\"frame\":{\"id\":\"root\",\"url\":\"" + _url + "\"}}");
                Emit("Page.lifecycleEvent", "{\"frameId\":\"root\",\"name\":\"load\"}");
                return Parse("{\"frameId\":\"root\",\"loaderId\":\"load1\"}");
            }
            if (method == "Page.getLayoutMetrics") return Parse("{\"cssContentSize\":{\"width\":800,\"height\":1200}}");
            if (method == "Page.captureScreenshot") return Parse("{\"data\":\"iVBORw==\"}");
            if (method == "DOM.resolveNode") return Parse("{\"object\":{\"objectId\":\"element1\"}}");
            if (method == "DOM.getFrameOwner") return Parse("{\"backendNodeId\":42}");
            if (method == "DOM.describeNode") return FrameTransforms is null ? Parse("{\"node\":{\"backendNodeId\":42}}") : Parse("{\"node\":{\"backendNodeId\":42,\"frameId\":\"child\"}}");
            if (method == "Accessibility.getFullAXTree") return Parse("""
                {"nodes":[{"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Test"},"childIds":["2"]},{"nodeId":"2","role":{"value":"button"},"name":{"value":"Save"},"backendDOMNodeId":42,"properties":[{"name":"disabled","value":{"value":false}}]}]}
                """);
            if (method == "Runtime.evaluate")
            {
                var expression = p.GetProperty("expression").GetString()!;
                if (expression.StartsWith("(async()=>") && FailEvaluation) return Parse("{\"exceptionDetails\":{\"exception\":{\"description\":\"Error: broken\"}}}");
                if (expression.Contains("__alcolerAutomation.select(") && !expression.Contains("Object.defineProperty"))
                    return StrictFailure ? Parse("{\"exceptionDetails\":{\"text\":\"Strict locator matched 2 elements\"}}") : Parse("{\"result\":{\"objectId\":\"element1\"}}");
                if (expression.Contains("url:location.href")) return BrowserJson.Element(new Dictionary<string, object?> { ["result"] = new Dictionary<string, object?> { ["value"] = new Dictionary<string, object?> { ["url"] = _url, ["title"] = "Test" } } });
                if (expression == "document.readyState") return Value("complete");
                return Value(true);
            }
            if (method == "Runtime.callFunctionOn")
            {
                var function = p.GetProperty("functionDeclaration").GetString()!;
                if (function.Contains("DOMMatrixReadOnly")) return Value(new Dictionary<string, object?> { ["x"] = 100, ["y"] = 200, ["transforms"] = FrameTransforms });
                if (function.Contains("elementFromPoint(x,y)")) return Value(!BlockFrame);
                if (function.Contains("inspect.call")) return Parse("{\"result\":{\"value\":{\"connected\":true,\"visible\":true,\"enabled\":true,\"editable\":true,\"hit\":true,\"x\":50,\"y\":20,\"left\":10,\"top\":10,\"width\":80,\"height\":20}}}");
                if (function.Contains("credentials:'include'")) return Parse("{\"result\":{\"value\":{\"type\":\"image/png\",\"data\":\"AQID\"}}}");
                if (function.Contains("No WebM")) return Parse("{\"result\":{\"value\":{\"mimeType\":\"video/webm\",\"width\":800,\"height\":600}}}");
                if (function.Contains("return data.slice")) return Value("AQID");
                return Value(true);
            }
            return Parse("{}");
        }

        private static JsonElement Value(object value) => BrowserJson.Element(new Dictionary<string, object?> { ["result"] = new Dictionary<string, object?> { ["value"] = value } });
        public void Emit(string method, string parameters) => ProtocolEvent?.Invoke(this, new(method, Parse(parameters)));
        public void Dispose() { }
    }

    private sealed record Call(string Method, JsonElement Parameters, string? Session);
}
