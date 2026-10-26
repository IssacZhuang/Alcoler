using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcoler.Browser;
using Alcoler.Browser.IntegrationTests;
using Alcoler.Browser.Chromium;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Identifiers;
using Microsoft.Extensions.Logging;

namespace Alcoler.Browser.Chromium.IntegrationTests;

internal sealed class ChromiumSuite : IDisposable
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] EbmlSignature = [0x1a, 0x45, 0xdf, 0xa3];
    private readonly string _dataRoot;
    private readonly LoopbackFixture _fixture;
    private readonly bool _headless;
    private readonly CancellationToken _ct;
    private readonly SessionId _session = StrongId.NewSessionId();
    private readonly SessionId _otherSession = StrongId.NewSessionId();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ChromiumBrowserHost _host;
    private BrowserController _controller;
    private TaskCompletionSource<(BrowserNewTabRequestedEventArgs Request, BrowserActionResult Result)>? _popup;
    private string _tab = "";
    private string _otherTab = "";

    public ChromiumSuite(string dataRoot, LoopbackFixture fixture, bool headless, CancellationToken ct)
    {
        _dataRoot = dataRoot;
        _fixture = fixture;
        _headless = headless;
        _ct = ct;
        _host = new ChromiumBrowserHost(dataRoot, new ChromiumBrowserOptions { Headless = headless }, new ConsoleLogger<ChromiumBrowserHost>());
        _controller = new BrowserController(_host, backendName: "chromium");
        _host.NewTabRequested += OnNewTabRequested;
    }

    private sealed class ConsoleLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Console.WriteLine($"HOSTLOG {formatter(state, exception)}{(exception is null ? "" : " :: " + exception.GetType().Name + ": " + exception.Message)}");
    }

    internal sealed record Scenario(string Name, bool RequiresWindow, Func<Task> Body);

    public IReadOnlyList<Scenario> Scenarios() =>
    [
        new("capabilities advertise the chromium backend", false, CapabilitiesAsync),
        new("first action auto-creates a background tab and navigation tracks url and title", false, AutoCreateAsync),
        new("accessibility snapshot yields refs and ref clicks mutate the DOM", false, AccessibilityAsync),
        new("locators, fill versus type, keyboard press and shadow DOM", false, LocatorsAsync),
        new("cross-origin iframe interaction through OOPIF flat sessions", false, CrossOriginFrameAsync),
        new("OOPIF swap rebinds frame interaction to the replacement host", false, OopifSwapAsync),
        new("evaluate values, exception text and canceled-command drain", false, EvaluateAsync),
        new("viewport, clipped and full-page PNG screenshots", false, ScreenshotsAsync),
        new("minimized window keeps rendering changing content", true, HiddenRenderingAsync),
        new("JavaScript prompt acceptance and confirm dismissal", false, DialogsAsync),
        new("browser download completes into the session downloads directory", false, DownloadsAsync),
        new("download_media returns exact fixture bytes", false, DownloadMediaAsync),
        new("bounded WebM recording completes with EBML media", false, RecordingAsync),
        new("target=_blank popup is adopted as a new session tab", true, PopupAsync),
        new("takeover blocks model input until user resume", true, TakeoverAsync),
        new("per-session profile isolation for cookies and localStorage", false, SessionIsolationAsync),
        new("visibility show restores the window and screenshots keep working", true, VisibilityShowAsync),
        new("close_tab auto-creates the next tab and finalize keeps listed tabs", false, CloseAndFinalizeAsync),
        new("history back and forward return expected URLs", false, HistoryAsync),
        new("wait_for visible and hidden on a toggled element", false, WaitForToggleAsync),
        new("graceful restart preserves session cookies", false, RestartPersistenceAsync),
        new("dispose exits every browser process holding the data root", false, ProcessHygieneAsync),
    ];

    private async Task CapabilitiesAsync()
    {
        var capabilities = Structured(await CommandAsync("capabilities"));
        Equal("chromium", capabilities.GetProperty("backend").GetString(), "Backend name");
        var expectedPlatform = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other";
        Equal(expectedPlatform, capabilities.GetProperty("platform").GetString(), "Platform");
        Equal(32, capabilities.GetProperty("maxTabs").GetInt32(), "Global tab limit");
        foreach (var group in new[] { "observations", "interactions", "lifecycle", "events", "media" })
        {
            Check(capabilities.TryGetProperty(group, out var actions) && actions.ValueKind == JsonValueKind.Array
                && actions.GetArrayLength() > 0, $"Capabilities omitted action group '{group}'.");
        }
        Check(capabilities.GetProperty("unsupported").EnumerateArray()
            .Any(action => action.GetString() == "networkidle"), "networkidle is not listed as unsupported.");
        Check(capabilities.GetProperty("interactions").EnumerateArray()
            .Any(action => action.GetString() == "select_option"), "Capabilities omitted select_option.");
    }

    private async Task AutoCreateAsync()
    {
        Equal(0, Tabs(await CommandAsync("tabs")).Length, "Fresh session tab count");
        var created = await CommandAsync("evaluate", D(("expression", "document.readyState")));
        _tab = created.TabId ?? throw new InvalidOperationException("The first action omitted the auto-created tab ID.");
        var list = Tabs(await CommandAsync("tabs"));
        Equal(1, list.Length, "Session tab count after auto-creation");
        Equal(_tab, list[0].GetProperty("id").GetString(), "Auto-created tab id");
        Check(list[0].GetProperty("active").GetBoolean(), "Auto-created tab is not active.");
        Equal("about:blank", list[0].GetProperty("url").GetString(), "Auto-created tab URL");
        if (!_headless)
        {
            await CommandAsync("visibility", D(("visible", false)), _tab);
            await CommandAsync("visibility", D(("visible", true)), _tab);
        }
        var navigated = await CommandAsync("navigate", D(("url", _fixture.Url())), _tab);
        Equal(_fixture.Url(), navigated.Url, "Navigated URL");
        Equal("Alcoler native fixture", navigated.Title, "Loaded page title");
        await AssertJsAsync("document.body.dataset.marker === 'fixture-ready' && document.querySelector('#marker').textContent === 'Real native browser marker'");
        var tracked = Tabs(await CommandAsync("tabs")).Single(tab => tab.GetProperty("id").GetString() == _tab);
        Equal(_fixture.Url(), tracked.GetProperty("url").GetString(), "Tracked tab URL");
        Equal("Alcoler native fixture", tracked.GetProperty("title").GetString(), "Tracked tab title");
        Check((await CommandAsync("read_text", tab: _tab)).Text?.Contains("Native integration fixture", StringComparison.Ordinal) == true,
            "read_text did not return rendered fixture content.");
    }

    private async Task AccessibilityAsync()
    {
        BrowserActionResult snapshot;
        JsonElement[] nodes;
        var clock = Stopwatch.StartNew();
        do
        {
            snapshot = await CommandAsync("snapshot", D(("maxChars", 24000), ("maxDepth", 30)), _tab);
            nodes = Structured(snapshot).GetProperty("nodes").EnumerateArray().ToArray();
            if (nodes.Any(node => node.GetProperty("role").GetString() == "button"
                && node.GetProperty("name").GetString() == "Reference action")) break;
            await Task.Delay(100, _ct);
        } while (clock.Elapsed < TimeSpan.FromSeconds(10));
        var buttons = nodes.Where(node => node.GetProperty("role").GetString() == "button"
            && node.GetProperty("name").GetString() == "Reference action").ToArray();
        Check(buttons.Length == 1, "Accessibility snapshot omitted the fixture button after its readiness deadline: "
            + snapshot.Text + Environment.NewLine + snapshot.StructuredOutput);
        var reference = buttons[0].GetProperty("ref").GetString();
        Check(!string.IsNullOrWhiteSpace(reference), "Accessibility button did not expose a reusable ref.");
        Check(snapshot.Text?.Contains($"[ref={reference}]", StringComparison.Ordinal) == true, "Snapshot text omitted the structured ref.");
        await CommandAsync("click", D(("ref", reference)), _tab);
        await AssertJsAsync("document.body.dataset.refClicks === '1'");
        var dom = Structured(await CommandAsync("dom_snapshot", tab: _tab));
        Check(dom.ValueKind == JsonValueKind.Array && dom.GetArrayLength() > 5, "DOM snapshot did not return actual nodes.");
    }

    private async Task LocatorsAsync()
    {
        await CommandAsync("click", Target(D(("css", "#css-click"))), _tab);
        await CommandAsync("click", Target(D(("role", "button"), ("name", "Role action"), ("exact", true))), _tab);
        await CommandAsync("click", Target(D(("text", "Text action"), ("exact", true))), _tab);
        await CommandAsync("fill", Target(D(("css", "#account")), ("text", "chromium account")), _tab);
        await AssertJsAsync("document.body.dataset.cssClicks === '1' && document.body.dataset.roleClicks === '1' && document.body.dataset.textClicks === '1' && document.querySelector('#account').value === 'chromium account'");
        await CommandAsync("type", Target(D(("css", "#account")), ("text", "!")), _tab);
        await AssertJsAsync("document.querySelector('#account').value === 'chromium account!'");
        await CommandAsync("fill", Target(D(("placeholder", "Optional alias")), ("text", "chromium alias")), _tab);
        await CommandAsync("fill", Target(D(("testId", "account-input")), ("text", "test id account")), _tab);
        await AssertJsAsync("document.querySelector('#account').value === 'test id account' && document.querySelector('#placeholder').value === 'chromium alias'");
        await CommandAsync("fill", Target(D(("css", "#keyboard")), ("text", "keyboard data")), _tab);
        await CommandAsync("press", Target(D(("css", "#keyboard")), ("key", "Control+A")), _tab);
        await CommandAsync("press", Target(D(("css", "#keyboard")), ("key", "Backspace")), _tab);
        await AssertJsAsync("document.querySelector('#keyboard').value === ''");
        await CommandAsync("press", Target(D(("css", "#keyboard")), ("key", "Enter")), _tab);
        await AssertJsAsync("document.body.dataset.keyboardEnter === 'yes'");
        await CommandAsync("click", Target(D(("role", "button"), ("name", "Shadow action"))), _tab);
        await CommandAsync("fill", Target(D(("label", "Shadow name")), ("text", "shadow value")), _tab);
        await AssertJsAsync("document.body.dataset.shadowClicked === 'yes' && document.querySelector('#shadow-host').shadowRoot.querySelector('input').value === 'shadow value'");
    }

    private string ForeignFrameUrl() => _fixture.Url("/frame").Replace("127.0.0.1", "localhost", StringComparison.Ordinal);

    private async Task CrossOriginFrameAsync()
    {
        await CommandAsync("evaluate", D(("expression", "url => {const frame=document.createElement('iframe');frame.id='cross-origin-frame';frame.title='Cross origin frame';frame.src=url;document.body.append(frame);return true;}"), ("arg", ForeignFrameUrl())), _tab);
        var clock = Stopwatch.StartNew();
        var ready = false;
        while (clock.Elapsed < TimeSpan.FromSeconds(12))
        {
            var count = await RawAsync("count", Target(D(("frame", "#cross-origin-frame"), ("css", "#frame-click")), ("timeoutMs", 1000)), _tab);
            if (count.Ok && Structured(count).GetInt32() == 1) { ready = true; break; }
            await Task.Delay(100, _ct);
        }
        Check(ready, "Cross-origin localhost frame did not become available through its CDP execution context.");
        await CommandAsync("click", Target(D(("frame", "#cross-origin-frame"), ("role", "button"), ("name", "Frame action")), ("timeoutMs", 10000)), _tab);
        await CommandAsync("fill", Target(D(("frame", "#cross-origin-frame"), ("label", "Frame name")), ("text", "cross-origin value"), ("timeoutMs", 10000)), _tab);
        Equal("yes", Structured(await CommandAsync("get_attribute", Target(D(("frame", "#cross-origin-frame"), ("css", "body")), ("name", "data-clicked")), _tab)).GetString(), "Cross-origin click effect");
        Equal("cross-origin value", Structured(await CommandAsync("get_attribute", Target(D(("frame", "#cross-origin-frame"), ("css", "body")), ("name", "data-value")), _tab)).GetString(), "Cross-origin fill effect");
        await AssertJsAsync("document.querySelector('#cross-origin-frame').contentDocument === null");
        await CommandAsync("evaluate", D(("expression", "document.querySelector('#cross-origin-frame').remove()")), _tab);
    }

    private async Task OopifSwapAsync()
    {
        await AssertJsAsync("document.querySelector('#fixture-frame').contentDocument !== null");
        await CommandAsync("click", Target(D(("frame", "#fixture-frame"), ("role", "button"), ("name", "Frame action"))), _tab);
        await AssertJsAsync("document.querySelector('#fixture-frame').contentDocument.body.dataset.clicked === 'yes'");
        await CommandAsync("evaluate", D(("expression", "url => {document.querySelector('#fixture-frame').src=url;return true;}"), ("arg", ForeignFrameUrl())), _tab);
        await CommandAsync("wait_for", Target(D(("frame", "#fixture-frame"), ("css", "#frame-click")), ("state", "visible"), ("timeoutMs", 15000)), _tab);
        await CommandAsync("click", Target(D(("frame", "#fixture-frame"), ("role", "button"), ("name", "Frame action")), ("timeoutMs", 10000)), _tab);
        await CommandAsync("fill", Target(D(("frame", "#fixture-frame"), ("label", "Frame name")), ("text", "swapped value"), ("timeoutMs", 10000)), _tab);
        Equal("swapped value", Structured(await CommandAsync("get_attribute", Target(D(("frame", "#fixture-frame"), ("css", "body")), ("name", "data-value")), _tab)).GetString(), "Swapped frame fill effect");
        await AssertJsAsync("document.querySelector('#fixture-frame').contentDocument === null");
    }

    private async Task EvaluateAsync()
    {
        var result = Structured(await CommandAsync("evaluate", D(("expression", "arg => ({answer: arg.number + 2, text: arg.text, title: document.title})"), ("arg", D(("number", 40), ("text", "chromium argument")))), _tab));
        Equal(42, result.GetProperty("answer").GetInt32(), "Evaluate JSON number argument");
        Equal("chromium argument", result.GetProperty("text").GetString(), "Evaluate JSON text argument");
        Equal("Alcoler native fixture", result.GetProperty("title").GetString(), "Evaluate actual DOM");
        var failed = await RawAsync("evaluate", D(("expression", "(() => { throw Error('chromium suite deliberate failure'); })()")), _tab);
        Check(!failed.Ok, "A thrown JavaScript error did not fail the evaluate result.");
        Check(failed.Error?.Contains("chromium suite deliberate failure", StringComparison.Ordinal) == true,
            "Evaluate exception text was not propagated: " + failed.Error);
        var timedOut = await RawAsync("evaluate", D(("expression", "new Promise(resolve => setTimeout(() => resolve('late completion'), 1500))"), ("timeoutMs", 300)), _tab);
        Check(!timedOut.Ok, "Bounded pending evaluate did not time out.");
        Check(timedOut.Error?.Contains("timed out", StringComparison.Ordinal) == true, "Unexpected timeout error: " + timedOut.Error);
        var next = Structured(await CommandAsync("evaluate", D(("expression", "21 * 2"), ("timeoutMs", 5000)), _tab));
        Equal(42, next.GetInt32(), "Command following a timed-out evaluate");
        await Task.Delay(1600, _ct);
        await AssertJsAsync("document.body.dataset.marker === 'fixture-ready'");
    }

    private async Task ScreenshotsAsync()
    {
        await CommandAsync("viewport", D(("width", 960), ("height", 640)), _tab);
        await AssertJsAsync("innerWidth === 960 && innerHeight === 640");
        await CommandAsync("evaluate", D(("expression", "scrollTo(0,0)")), _tab);
        var viewport = PngSize(await CommandAsync("screenshot", tab: _tab));
        Equal(960, viewport.Width, "Viewport PNG width");
        Equal(640, viewport.Height, "Viewport PNG height");
        var clip = PngSize(await CommandAsync("screenshot", D(("clip", D(("x", 10), ("y", 10), ("width", 240), ("height", 130)))), _tab));
        Equal(240, clip.Width, "Clip PNG width");
        Equal(130, clip.Height, "Clip PNG height");
        var full = PngSize(await CommandAsync("screenshot", D(("fullPage", true)), _tab));
        Equal(960, full.Width, "Full-page PNG width");
        Check(full.Height >= 1900 && full.Height > viewport.Height, $"Full-page PNG height {full.Height} did not include content below the viewport.");
    }

    private async Task HiddenRenderingAsync()
    {
        await CommandAsync("visibility", D(("visible", false)), _tab);
        await CommandAsync("evaluate", D(("expression", "const marker=document.createElement('div');marker.id='render-marker';marker.style.cssText='position:fixed;inset:0;background:rgb(255,0,0);z-index:2147483647';document.body.append(marker);true")), _tab);
        try
        {
            await AssertRenderedColorAsync(0xffff0000);
            await CommandAsync("evaluate", D(("expression", "document.querySelector('#render-marker').style.background='rgb(0,0,255)';true")), _tab);
            await AssertRenderedColorAsync(0xff0000ff);
        }
        finally
        {
            await CommandAsync("evaluate", D(("expression", "document.querySelector('#render-marker')?.remove();true")), _tab);
            await CommandAsync("visibility", D(("visible", true)), _tab);
        }
    }

    private async Task AssertRenderedColorAsync(uint expected)
    {
        var clock = Stopwatch.StartNew();
        uint actual = 0;
        do
        {
            var result = await CommandAsync("screenshot", D(("clip", D(("x", 100), ("y", 100), ("width", 16), ("height", 16)))), _tab);
            var bytes = result.ScreenshotPng ?? throw new InvalidOperationException("Screenshot omitted PNG bytes.");
            actual = DecodePng(bytes).Pixels[0];
            if (actual == expected) return;
            await Task.Delay(100, _ct);
        } while (clock.Elapsed < TimeSpan.FromSeconds(10));
        throw new InvalidOperationException($"The hidden browser window did not render changing content: expected {expected:X8}, actual {actual:X8}.");
    }

    private async Task DialogsAsync()
    {
        // The trigger must return before the modal opens; an awaited prompt would hold the command gate.
        await CommandAsync("evaluate", D(("expression", "(() => {setTimeout(() => {document.body.dataset.promptResult=prompt('Integration prompt','default')},250);return 'scheduled';})()")), _tab);
        var prompt = await WaitDialogAsync();
        Equal("prompt", prompt.GetProperty("dialog").GetProperty("type").GetString(), "Prompt dialog type");
        await CommandAsync("dialog", D(("operation", "accept"), ("text", "chromium prompt answer")), _tab);
        await AssertJsAsync("document.body.dataset.promptResult === 'chromium prompt answer'");
        await CommandAsync("evaluate", D(("expression", "(() => {setTimeout(() => {document.body.dataset.confirmResult=String(confirm('Integration confirm'))},250);return 'scheduled';})()")), _tab);
        var confirm = await WaitDialogAsync();
        Equal("confirm", confirm.GetProperty("dialog").GetProperty("type").GetString(), "Confirm dialog type");
        await CommandAsync("dialog", D(("operation", "dismiss")), _tab);
        await AssertJsAsync("document.body.dataset.confirmResult === 'false'");
        Check(!Structured(await CommandAsync("dialog", D(("operation", "status")), _tab)).GetProperty("open").GetBoolean(), "Dismissed dialog remained pending.");
    }

    private async Task<JsonElement> WaitDialogAsync()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            var status = Structured(await CommandAsync("dialog", D(("operation", "status")), _tab));
            if (status.GetProperty("open").GetBoolean()) return status;
            await Task.Delay(50, _ct);
        }
        throw new TimeoutException("The JavaScript dialog did not become observable.");
    }

    private string SessionDownloadsDirectory(SessionId session) => Path.Combine(_dataRoot, "chromium-profiles",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session.Value))), "downloads");

    private async Task DownloadsAsync()
    {
        await CommandAsync("click", Target(D(("css", "#download"))), _tab);
        JsonElement? completed = null;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            var downloads = Structured(await CommandAsync("downloads", tab: _tab));
            Check(downloads.ValueKind == JsonValueKind.Array, "Download observation did not return a structured list.");
            foreach (var download in downloads.EnumerateArray())
            {
                if (download.TryGetProperty("url", out var url) && url.GetString() == _fixture.Url("/download")
                    && download.TryGetProperty("state", out var state) && state.GetString() == "completed")
                    completed = download.Clone();
            }
            if (completed is not null) break;
            await Task.Delay(100, _ct);
        }
        var entry = completed ?? throw new InvalidOperationException("The fixture download did not reach the completed state.");
        Check(!string.IsNullOrWhiteSpace(entry.GetProperty("guid").GetString()), "Completed download omitted its guid.");
        Equal("fixture-download.bin", entry.GetProperty("suggestedFilename").GetString(), "Download suggested filename");
        var downloaded = Path.Combine(SessionDownloadsDirectory(_session), "fixture-download.bin");
        var fileClock = Stopwatch.StartNew();
        while (fileClock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(downloaded))
            {
                try
                {
                    if ((await File.ReadAllBytesAsync(downloaded, _ct)).AsSpan().SequenceEqual(LoopbackFixture.MediaBytes)) break;
                }
                catch (IOException) { }
            }
            await Task.Delay(100, _ct);
        }
        Check(File.Exists(downloaded) && (await File.ReadAllBytesAsync(downloaded, _ct)).AsSpan().SequenceEqual(LoopbackFixture.MediaBytes),
            $"The downloaded file does not carry the fixture bytes at {downloaded}.");
    }

    private async Task DownloadMediaAsync()
    {
        var media = await CommandAsync("download_media", D(("url", _fixture.Url("/media")), ("fileName", "fixture-media.bin")), _tab);
        Check(media.Media is not null && media.Media.AsSpan().SequenceEqual(LoopbackFixture.MediaBytes), "Downloaded media bytes differ from the local HTTP response.");
        Equal("application/octet-stream", media.MediaType, "Downloaded media MIME");
        Equal("fixture-media.bin", media.MediaFileName, "Downloaded media name");
    }

    private async Task RecordingAsync()
    {
        await CommandAsync("record_start", D(("maxDurationMs", 1500), ("fps", 4)), _tab);
        BrowserActionResult? completed = null;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            var status = await CommandAsync("record_status", tab: _tab);
            var state = Structured(status).GetProperty("state").GetString();
            Check(state is "recording" or "completed", "Unexpected recording state: " + state);
            if (state == "completed") { completed = status; break; }
            await Task.Delay(100, _ct);
        }
        Check(completed is not null, "Bounded recording did not finish.");
        Equal("video/webm", completed!.MediaType, "Recording MIME");
        var bytes = completed.Media ?? throw new InvalidOperationException("Completed recording omitted media bytes.");
        Check(bytes.Length > 0, "Recording media was empty.");
        Check(bytes.AsSpan(0, 4).SequenceEqual(EbmlSignature), "Recording did not start with a WebM EBML signature.");
    }

    private async Task PopupAsync()
    {
        _popup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Chrome only opens target=_blank windows for input in a focused window.
            await CommandAsync("visibility", D(("visible", true)), _tab);
            await CommandAsync("click", Target(D(("css", "#popup"))), _tab);
            var opened = await _popup.Task.WaitAsync(TimeSpan.FromSeconds(30), _ct);
            Check(opened.Result.Ok, "Popup creation failed: " + opened.Result.Error);
            Equal(_session, opened.Request.SessionId, "Popup session ownership");
            Equal(_tab, opened.Request.OpenerTabId, "Popup opener tab");
            Equal(_fixture.Url("/popup"), opened.Request.Url, "Popup target URL");
            var popupId = opened.Result.TabId ?? throw new InvalidOperationException("Popup omitted its tab ID.");
            try
            {
                var tabs = Tabs(await CommandAsync("tabs"));
                Check(tabs.Any(tab => tab.GetProperty("id").GetString() == popupId), "Popup missing from the opener session tab list.");
                // target=_blank is implicitly noopener; the adopted target committed /popup directly, so its
                // history holds a single entry, unlike a fallback tab created at about:blank and then navigated.
                Equal(1, Structured(await CommandAsync("evaluate", D(("expression", "history.length")), popupId)).GetInt32(),
                    "Popup tab session history length");
                Equal("Popup fixture", Structured(await CommandAsync("evaluate", D(("expression", "document.title")), popupId)).GetString(), "Popup page title");
            }
            finally
            {
                await RawAsync("close_tab", tab: popupId);
                await RawAsync("select_tab", tab: _tab);
            }
        }
        finally { _popup = null; }
    }

    private async void OnNewTabRequested(object? sender, BrowserNewTabRequestedEventArgs e)
    {
        var completion = _popup;
        try
        {
            var result = await _controller.OpenPopupAsync(e.SessionId, e.OpenerTabId, e.Url, _ct, e.IsUserInitiated, e.TargetId);
            completion?.TrySetResult((e, result));
        }
        catch (Exception ex)
        {
            if (completion is null) Console.WriteLine($"WARN unexpected popup callback{Environment.NewLine}{ex}");
            else completion.TrySetException(ex);
        }
    }

    private async Task TakeoverAsync()
    {
        await CommandAsync("record_start", D(("maxDurationMs", 3000), ("fps", 4)), _tab);
        await CommandAsync("takeover", tab: _tab);
        var recording = await CommandAsync("record_status", tab: _tab);
        Equal("cancelled", Structured(recording).GetProperty("state").GetString(), "Takeover recording cancellation");
        Check(recording.Media is null, "Takeover exposed recording media after yielding control.");
        Check(_controller.GetStatus(_session)?.IsUserControlled == true, "Takeover did not publish human ownership.");
        var blocked = await RawAsync("fill", Target(D(("css", "#account")), ("text", "must not be written")), _tab);
        Check(!blocked.Ok && blocked.Error?.Contains("human control", StringComparison.Ordinal) == true,
            "Automated input was not blocked during human control: " + blocked.Error);
        Check(!(await RawAsync("evaluate", D(("expression", "document.body.dataset.illegal='yes'")), _tab)).Ok, "Evaluate bypassed human ownership.");
        Check((await CommandAsync("tabs")).Ok, "Observation was blocked during human control.");
        Check(!(await RawAsync("resume", tab: _tab)).Ok, "Model resume bypassed human ownership.");
        Check(_controller.GetStatus(_session)?.IsUserControlled == true, "Rejected model resume changed ownership.");
        await CommandAsync("fill", Target(D(("css", "#account")), ("text", "human content")), _tab, userInitiated: true);
        await CommandAsync("resume", tab: _tab, userInitiated: true);
        Check(_controller.GetStatus(_session)?.IsUserControlled == false, "User resume did not release human ownership.");
        await CommandAsync("fill", Target(D(("css", "#account")), ("text", "resumed account")), _tab);
        await AssertJsAsync("document.querySelector('#account').value === 'resumed account' && !document.body.dataset.illegal");
    }

    private async Task SessionIsolationAsync()
    {
        await CommandAsync("evaluate", D(("expression", "localStorage.setItem('chromiumSuite','owner');document.cookie='chromiumSuite=owner;path=/';true")), _tab);
        var other = await CommandAsync("new_tab", D(("url", _fixture.Url())), session: _otherSession);
        _otherTab = other.TabId!;
        Equal(1, Tabs(await CommandAsync("tabs", session: _otherSession)).Length, "Other session tab count");
        Check(!Tabs(await CommandAsync("tabs")).Any(tab => tab.GetProperty("id").GetString() == _otherTab), "Session listing leaked another session's tab.");
        var isolated = Structured(await CommandAsync("evaluate", D(("expression", "({storage: localStorage.getItem('chromiumSuite'), cookie: document.cookie})")), _otherTab, _otherSession));
        Check(isolated.GetProperty("storage").ValueKind == JsonValueKind.Null, "Session profiles leaked localStorage.");
        Check(!isolated.GetProperty("cookie").GetString()!.Contains("chromiumSuite=owner", StringComparison.Ordinal), "Session profiles leaked cookies.");
        await AssertJsAsync("localStorage.getItem('chromiumSuite') === 'owner'");
        foreach (var verb in new[] { "select_tab", "evaluate" })
        {
            var denied = await RawAsync(verb, verb == "evaluate" ? D(("expression", "document.title")) : null, _tab, _otherSession);
            Check(!denied.Ok, $"Cross-session {verb} was not rejected.");
        }
    }

    private async Task VisibilityShowAsync()
    {
        await CommandAsync("visibility", D(("visible", false)), _tab);
        await CommandAsync("visibility", D(("visible", true)), _tab);
        var shown = PngSize(await CommandAsync("screenshot", tab: _tab));
        Check(shown.Width > 0 && shown.Height > 0, "Screenshot after restoring the window produced no pixels.");
    }

    private async Task CloseAndFinalizeAsync()
    {
        var open = Tabs(await CommandAsync("tabs"));
        foreach (var tab in open.Select(tab => tab.GetProperty("id").GetString()).Where(id => id != null).SkipLast(1).ToArray())
            await CommandAsync("close_tab", tab: tab!);
        await CommandAsync("close_tab", tab: _tab);
        Equal(0, Tabs(await CommandAsync("tabs")).Length, "Tab count after closing the last tab");
        var recreated = await CommandAsync("evaluate", D(("expression", "document.readyState")));
        _tab = recreated.TabId ?? throw new InvalidOperationException("The action after close_tab omitted the auto-created tab ID.");
        Equal(1, Tabs(await CommandAsync("tabs")).Length, "Tab count after auto-recreation");
        var retained = await CommandAsync("finalize", D(("keep", new[] { _tab })), _tab);
        Equal("Browser deliverables retained. Unlisted tabs were not closed.", retained.Text, "Finalize contract text");
        var kept = Tabs(retained).Single(tab => tab.GetProperty("id").GetString() == _tab);
        Check(kept.GetProperty("keep").GetBoolean(), "Finalize did not retain the listed tab.");
    }

    private async Task HistoryAsync()
    {
        var first = await CommandAsync("navigate", D(("url", _fixture.Url())), _tab);
        Equal("Alcoler native fixture", first.Title, "First history entry title");
        var second = await CommandAsync("navigate", D(("url", _fixture.Url("/second"))), _tab);
        Equal("Second fixture page", second.Title, "Second navigation title");
        var back = await CommandAsync("back", tab: _tab);
        Equal(_fixture.Url(), back.Url, "Back action URL");
        await AssertJsAsync("location.pathname === '/'");
        var forward = await CommandAsync("forward", tab: _tab);
        Equal(_fixture.Url("/second"), forward.Url, "Forward action URL");
        await AssertJsAsync("location.pathname === '/second'");
    }

    private async Task WaitForToggleAsync()
    {
        await CommandAsync("navigate", D(("url", _fixture.Url())), _tab);
        await CommandAsync("evaluate", D(("expression", "document.querySelector('#wait-marker')?.remove();true")), _tab);
        await CommandAsync("click", Target(D(("css", "#delayed"))), _tab);
        await CommandAsync("wait_for", Target(D(("css", "#wait-marker")), ("state", "visible"), ("timeoutMs", 5000)), _tab);
        Equal("Wait completed", Structured(await CommandAsync("inner_text", Target(D(("css", "#wait-marker"))), _tab)).GetString(), "Explicit locator wait");
        await CommandAsync("evaluate", D(("expression", "document.querySelector('#wait-marker').remove();true")), _tab);
        await CommandAsync("wait_for", Target(D(("css", "#wait-marker")), ("state", "hidden"), ("timeoutMs", 5000)), _tab);
    }

    private async Task RestartPersistenceAsync()
    {
        await CommandAsync("navigate", D(("url", _fixture.Url())), _tab);
        // Session cookies die with the process by design; profile-persisted state proves the data root was reused.
        await CommandAsync("evaluate", D(("expression",
            "document.cookie='restartPersisted=yes;path=/;max-age=3600';localStorage.setItem('restartPersisted','yes');true")), _tab);
        await AssertJsAsync("document.cookie.includes('restartPersisted=yes')");
        TearDownControllers();
        await WaitForDataRootReleasedAsync(TimeSpan.FromSeconds(40));
        _host = new ChromiumBrowserHost(_dataRoot, new ChromiumBrowserOptions { Headless = _headless }, new ConsoleLogger<ChromiumBrowserHost>());
        _controller = new BrowserController(_host, backendName: "chromium");
        _host.NewTabRequested += OnNewTabRequested;
        var navigated = await CommandAsync("navigate", D(("url", _fixture.Url())));
        _tab = navigated.TabId ?? throw new InvalidOperationException("Restart navigation omitted its tab ID.");
        await AssertJsAsync("document.cookie.includes('restartPersisted=yes') && localStorage.getItem('restartPersisted') === 'yes'");
    }

    private async Task ProcessHygieneAsync()
    {
        var holding = await ProcessesHoldingAsync(_dataRoot);
        Check(holding.Count > 0, "No live browser process was observed holding the data root before disposal; the hygiene check cannot prove anything.");
        TearDownControllers();
        await WaitForDataRootReleasedAsync(TimeSpan.FromSeconds(40));
    }

    private async Task WaitForDataRootReleasedAsync(TimeSpan bound)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < bound)
        {
            var holding = await ProcessesHoldingAsync(_dataRoot);
            if (holding.Count == 0)
            {
                Console.WriteLine($"STEP data root released by all browser processes after {clock.Elapsed.TotalSeconds:F1}s");
                return;
            }
            await Task.Delay(600, _ct);
        }
        var lingering = string.Join("; ", await ProcessesHoldingAsync(_dataRoot));
        throw new TimeoutException($"A browser process still holds the suite data root after controller disposal: {lingering}");
    }

    private static async Task<List<string>> ProcessesHoldingAsync(string path)
    {
        var holding = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            var escaped = path.Replace("'", "''", StringComparison.Ordinal);
            // The querying shell's own command line contains the path, so it must exclude itself.
            info.ArgumentList.Add($"Get-CimInstance Win32_Process -Filter \"CommandLine IS NOT NULL\" | Where-Object {{ $_.ProcessId -ne $PID -and $_.CommandLine -like '*{escaped}*' }} | ForEach-Object {{ '{{0}}|{{1}}' -f $_.ProcessId, $_.Name }}");
            using var query = Process.Start(info)!;
            var output = await query.StandardOutput.ReadToEndAsync(CancellationToken.None);
            await query.WaitForExitAsync(CancellationToken.None);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                holding.Add(line);
            return holding;
        }
        if (OperatingSystem.IsLinux())
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                try
                {
                    var command = await File.ReadAllTextAsync(Path.Combine(directory, "cmdline"), CancellationToken.None);
                    if (command.Contains(path, StringComparison.Ordinal))
                        holding.Add(directory.Replace("/proc/", "", StringComparison.Ordinal) + "|" + command.Replace('\0', ' '));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return holding;
        }
        using var ps = Process.Start(new ProcessStartInfo("ps", "-axo pid=,command=")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        var lines = await ps.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await ps.WaitForExitAsync(CancellationToken.None);
        foreach (var line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.Contains(path, StringComparison.Ordinal)) holding.Add(line.Trim());
        return holding;
    }

    private void TearDownControllers()
    {
        var controller = _controller;
        var host = _host;
        _controller = null!;
        _host = null!;
        if (controller is null) return;
        if (host is not null) host.NewTabRequested -= OnNewTabRequested;
        controller.Dispose();
    }

    private async Task<BrowserActionResult> RawAsync(string verb, Dictionary<string, object?>? input = null,
        string? tab = null, SessionId? session = null, bool userInitiated = false)
    {
        var action = new BrowserCommandAction(verb, BrowserJson.Element(input ?? D())) { TabId = tab, UserInitiated = userInitiated };
        return await _controller.ExecuteAsync(session ?? _session, action, _ct).WaitAsync(TimeSpan.FromSeconds(65), _ct);
    }

    private async Task<BrowserActionResult> CommandAsync(string verb, Dictionary<string, object?>? input = null,
        string? tab = null, SessionId? session = null, bool userInitiated = false)
    {
        var result = await RawAsync(verb, input, tab, session, userInitiated);
        Check(result.Ok, $"Browser '{verb}' failed on {tab ?? "active tab"}: {result.Error}; uncertain={result.SideEffectUncertain}");
        return result;
    }

    private async Task AssertJsAsync(string expression) =>
        Check(Structured(await CommandAsync("evaluate", D(("expression", expression)), _tab)).ValueKind == JsonValueKind.True,
            "JavaScript assertion was false: " + expression);

    private static JsonElement Structured(BrowserActionResult result)
    {
        using var doc = JsonDocument.Parse(result.StructuredOutput ?? throw new InvalidOperationException("Browser result omitted structured output."));
        return doc.RootElement.Clone();
    }

    private static JsonElement[] Tabs(BrowserActionResult result) =>
        Structured(result).GetProperty("tabs").EnumerateArray().Select(tab => tab.Clone()).ToArray();

    private static (int Width, int Height) PngSize(BrowserActionResult result) => PngSize(result.ScreenshotPng
        ?? throw new InvalidOperationException("Screenshot omitted PNG bytes."));

    private static (int Width, int Height) PngSize(byte[] png)
    {
        Check(png.Length > 32 && png.AsSpan(0, 8).SequenceEqual(PngSignature), "Screenshot is not a PNG file.");
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    private sealed record DecodedPng(int Width, int Height, uint[] Pixels);

    // Screenshots originate from Chrome's RGBA8 non-interlaced PNG encoder; anything else is a decode failure, not a fallback case.
    private static DecodedPng DecodePng(byte[] png)
    {
        var (width, height) = PngSize(png);
        Check(width > 0 && height > 0 && width * height <= 16_000_000, "Decoded PNG has no usable pixels.");
        int bitDepth = 0, colorType = 0, interlace = -1;
        using var idat = new MemoryStream();
        var position = 8;
        while (position + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position, 4));
            var type = Encoding.ASCII.GetString(png, position + 4, 4);
            var data = png.AsSpan(position + 8, length);
            if (type == "IHDR")
            {
                bitDepth = data[8];
                colorType = data[9];
                interlace = data[12];
            }
            else if (type == "IDAT") idat.Write(data);
            position += 12 + length;
            if (type == "IEND") break;
        }
        Check(bitDepth == 8 && interlace == 0, $"Unsupported PNG encoding (bit depth {bitDepth}, interlace {interlace}).");
        var channels = colorType switch { 6 => 4, 2 => 3, _ => -1 };
        Check(channels > 0, $"Unsupported PNG color type {colorType}.");
        idat.Position = 2;
        byte[] raw;
        using (var deflate = new DeflateStream(idat, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            deflate.CopyTo(output);
            raw = output.ToArray();
        }
        var stride = width * channels;
        Check(raw.Length >= (stride + 1) * height, "PNG pixel data is truncated.");
        var pixels = new uint[width * height];
        Span<byte> previous = new byte[stride];
        Span<byte> current = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            raw.AsSpan(y * (stride + 1) + 1, stride).CopyTo(current);
            for (var i = 0; i < stride; i++)
            {
                byte left = i >= channels ? current[i - channels] : (byte)0, up = previous[i],
                    upLeft = i >= channels ? previous[i - channels] : (byte)0;
                switch (filter)
                {
                    case 1: current[i] += left; break;
                    case 2: current[i] += up; break;
                    case 3: current[i] += (byte)((left + up) / 2); break;
                    case 4: current[i] += Paeth(left, up, upLeft); break;
                }
            }
            for (var x = 0; x < width; x++)
            {
                var offset = x * channels;
                var r = current[offset];
                var g = current[offset + 1];
                var b = current[offset + 2];
                var a = channels == 4 ? current[offset + 3] : (byte)255;
                pixels[y * width + x] = (uint)(a << 24 | r << 16 | g << 8 | b);
            }
            var exchanged = previous;
            previous = current;
            current = exchanged;
        }
        return new(width, height, pixels);
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static Dictionary<string, object?> D(params (string Name, object? Value)[] values) =>
        values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

    private static Dictionary<string, object?> Target(Dictionary<string, object?> locator, params (string Name, object? Value)[] values)
    {
        var input = D(values);
        input.Add("locator", locator);
        return input;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string label) => Check(EqualityComparer<T>.Default.Equals(expected, actual),
        $"{label}: expected '{expected}', actual '{actual}'.");

    public void Dispose()
    {
        _controller?.Dispose();
        _controller = null!;
        _host = null!;
    }
}
