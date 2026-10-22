using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;
using Alcoler.Workbench.Browser;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Alcoler.Workbench.Views;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Alcoler.Browser.IntegrationTests;

internal sealed class NativeBrowserSuite
{
    private readonly Window _window;
    private readonly LoopbackFixture _fixture;
    private readonly WebViewBrowserHost _host;
    private readonly WebViewBrowserController _nativeController;
    private readonly IBrowserController _controller;
    private readonly CancellationToken _ct;
    private readonly string _profileDirectory;
    private readonly SessionId _session = StrongId.NewSessionId();
    private readonly SessionId _otherSession = StrongId.NewSessionId();
    private readonly MemoryArtifactWorkspace _workspace = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TaskCompletionSource<(BrowserNewTabRequestedEventArgs Request, BrowserActionResult Result)>? _popup;
    private string _tab = "";
    private string _otherTab = "";
    private int _passed;

    private NativeBrowserSuite(Window window, LoopbackFixture fixture, WebViewBrowserHost host,
        WebViewBrowserController controller, string profileDirectory, CancellationToken ct)
    {
        _window = window;
        _fixture = fixture;
        _host = host;
        _nativeController = controller;
        _controller = controller;
        _ct = ct;
        _profileDirectory = profileDirectory;
        _host.NewTabRequested += OnNewTabRequested;
    }

    public static async Task RunAsync(Window window, CancellationToken ct)
    {
        Dispatcher.UIThread.VerifyAccess();
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Native dispatcher is not STA.");
        var directory = Directory.CreateTempSubdirectory("Alcoler-Browser-Integration-").FullName;
        Console.WriteLine($"Native WebView2 profile: {directory}");
        try
        {
            await using var fixture = new LoopbackFixture();
            var profileDirectory = Path.Combine(directory, "profile");
            var host = new WebViewBrowserHost(profileDirectory);
            var controller = new WebViewBrowserController(host);
            var suite = new NativeBrowserSuite(window, fixture, host, controller, profileDirectory, ct);
            try
            {
                await suite.RunCasesAsync();
                fixture.AssertHealthy();
                Console.WriteLine($"PASS All {suite._passed} native browser scenarios ({suite._clock.Elapsed.TotalSeconds:F1}s)");
            }
            finally
            {
                suite._host.NewTabRequested -= suite.OnNewTabRequested;
                window.Content = null;
                controller.Dispose();
                host.Dispose();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(200);
            }
        }
        finally
        {
            await DeleteOwnedDirectoryAsync(directory);
        }
    }

    private async Task RunCasesAsync()
    {
        await CaseAsync("Capabilities and parked native tab creation", ParkedCreateAsync);
        await CaseAsync("Local navigation, JavaScript title and marker", NavigationAsync);
        await CaseAsync("AX snapshot roles, discoverable refs and ref click", AccessibilityAsync);
        await CaseAsync("CSS, role, text, label, placeholder and testId locators", LocatorsAsync);
        await CaseAsync("Open shadow root and iframe click/fill", ShadowAndFrameAsync);
        await CaseAsync("Checkbox, select, count, text, attributes and explicit waits", QueriesAsync);
        await CaseAsync("Native keyboard, scroll and mouse drag", InputAsync);
        await CaseAsync("Evaluate expression with JSON argument", EvaluateAsync);
        await CaseAsync("Timed-out JavaScript drains pending native command", CancellationAsync);
        await CaseAsync("Viewport, clipped and full-page decoded PNG screenshots", ScreenshotsAsync);
        await CaseAsync("BrowserTool screenshot emits model-visible artifact media", ToolScreenshotAsync);
        await RecordMemoryAsync();
        await CaseAsync("Tab lifecycle, session access and profile isolation", SessionsAsync);
        await CaseAsync("Human takeover blocks model input and model resume", TakeoverAsync);
        await CaseAsync("Real target=_blank popup remains in opener session", PopupAsync);
        await CaseAsync("Prompt acceptance and confirm dismissal without modal deadlock", DialogsAsync);
        await CaseAsync("Bounded local media artifact and download observation", DownloadsAsync);
        await CaseAsync("Short real WebM recording completion and cancellation", RecordingAsync);
        await CaseAsync("Embedded presenter and pane detach preserve native adapter and JS state", PresenterAsync);
        await CaseAsync("History navigation and stale snapshot reference rejection", HistoryAsync);
        await CaseAsync("Cross-origin iframe CDP context click and fill", CrossOriginFrameAsync);
        await CaseAsync("Close all tabs releases native pages", CloseAsync);
    }

    private async Task CaseAsync(string title, Func<Task> test)
    {
        Console.WriteLine($"RUN  {title}");
        try
        {
            _ct.ThrowIfCancellationRequested();
            await test();
            _fixture.AssertHealthy();
            _passed++;
            Console.WriteLine($"PASS {title}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {title}{Environment.NewLine}{ex}");
            throw;
        }
    }

    private async Task ParkedCreateAsync()
    {
        var capabilities = Structured(await CommandAsync("capabilities"));
        Equal("webview2", capabilities.GetProperty("backend").GetString(), "Native backend");
        Equal(32, capabilities.GetProperty("maxTabs").GetInt32(), "Global tab limit");
        var created = await CommandAsync("new_tab");
        _tab = created.TabId ?? throw new InvalidOperationException("new_tab omitted its tab ID.");
        Check(_host.GetPageControl(_tab) is NativeWebView, "Parked creation did not create a NativeWebView.");
        Check(_host.GetPageControl(_tab)!.Parent is Panel, "New tab was not parked before presenter attachment.");
        Equal(1, Tabs(await CommandAsync("tabs")).Length, "Session tab count after parked creation");
    }

    private async Task NavigationAsync()
    {
        var navigated = await CommandAsync("navigate", D(("url", _fixture.Url())), _tab);
        Equal("Alcoler native fixture", navigated.Title, "Loaded page title");
        await AssertJsAsync("document.body.dataset.marker === 'fixture-ready' && document.querySelector('#marker').textContent === 'Real native browser marker'");
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
            if (nodes.Any(n => n.GetProperty("role").GetString() == "button" && n.GetProperty("name").GetString() == "Reference action")) break;
            await Task.Delay(100, _ct);
        } while (clock.Elapsed < TimeSpan.FromSeconds(5));
        var buttons = nodes.Where(n => n.GetProperty("role").GetString() == "button" && n.GetProperty("name").GetString() == "Reference action").ToArray();
        Check(buttons.Length == 1, "AX snapshot omitted the fixture button after readiness deadline: " + snapshot.Text + "\n" + snapshot.StructuredOutput);
        var reference = buttons[0].GetProperty("ref").GetString();
        Check(!string.IsNullOrWhiteSpace(reference), "AX button did not expose a reusable ref.");
        Check(snapshot.Text?.Contains($"[ref={reference}]", StringComparison.Ordinal) == true, "AX text omitted structured ref.");
        Check(nodes.Any(n => n.GetProperty("role").GetString() == "textbox"), "AX tree omitted textbox role.");
        await CommandAsync("click", D(("ref", reference)), _tab);
        await AssertJsAsync("document.body.dataset.refClicks === '1'");
        var scoped = Structured(await CommandAsync("snapshot", D(("ref", reference)), _tab));
        var scopedButtons = scoped.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("role").GetString() == "button").ToArray();
        Check(scopedButtons.Length == 1 && scopedButtons[0].GetProperty("name").GetString() == "Reference action",
            "Snapshot scoped to an existing AX ref included unrelated buttons: " + scoped.GetRawText());
        var dom = Structured(await CommandAsync("dom_snapshot", tab: _tab));
        Check(dom.ValueKind == JsonValueKind.Array && dom.GetArrayLength() > 5, "DOM snapshot did not return actual nodes.");
    }

    private async Task LocatorsAsync()
    {
        await CommandAsync("click", Target(D(("css", "#css-click"))), _tab);
        await CommandAsync("click", Target(D(("role", "button"), ("name", "Role action"), ("exact", true))), _tab);
        await CommandAsync("click", Target(D(("text", "Text action"), ("exact", true))), _tab);
        await CommandAsync("fill", Target(D(("label", "Account name"), ("exact", true)), ("text", "native account")), _tab);
        await AssertJsAsync("document.body.dataset.cssClicks === '1' && document.body.dataset.roleClicks === '1' && document.body.dataset.textClicks === '1' && document.querySelector('#account').value === 'native account'");
        await CommandAsync("fill", Target(D(("placeholder", "Optional alias")), ("text", "native alias")), _tab);
        await CommandAsync("fill", Target(D(("testId", "account-input")), ("text", "test id account")), _tab);
        await AssertJsAsync("document.querySelector('#account').value === 'test id account' && document.querySelector('#placeholder').value === 'native alias'");
    }

    private async Task ShadowAndFrameAsync()
    {
        await CommandAsync("click", Target(D(("role", "button"), ("name", "Shadow action"))), _tab);
        await CommandAsync("fill", Target(D(("label", "Shadow name")), ("text", "shadow value")), _tab);
        await AssertJsAsync("document.body.dataset.shadowClicked === 'yes' && document.querySelector('#shadow-host').shadowRoot.querySelector('input').value === 'shadow value'");
        await CommandAsync("click", Target(D(("frame", "#fixture-frame"), ("role", "button"), ("name", "Frame action"))), _tab);
        await CommandAsync("fill", Target(D(("frame", "#fixture-frame"), ("label", "Frame name")), ("text", "frame value")), _tab);
        await AssertJsAsync("document.querySelector('#fixture-frame').contentDocument.body.dataset.clicked === 'yes' && document.querySelector('#fixture-frame').contentDocument.querySelector('input').value === 'frame value'");
        await CommandAsync("evaluate", D(("expression", "(() => {document.querySelector('#fixture-frame').contentDocument.body.dataset.clicked='blocked';const e=document.createElement('div');e.id='iframe-overlay';e.style.cssText='position:fixed;inset:0;z-index:999999;background:rgba(0,0,0,.01)';document.body.append(e);return true;})()")), _tab);
        try
        {
            var blocked = await RawAsync("click", Target(D(("frame", "#fixture-frame"), ("role", "button"), ("name", "Frame action")), ("timeoutMs", 700)), _tab);
            Check(!blocked.Ok, "Iframe input passed through a covering parent-document overlay.");
            await AssertJsAsync("document.querySelector('#fixture-frame').contentDocument.body.dataset.clicked === 'blocked'");
        }
        finally
        {
            await CommandAsync("evaluate", D(("expression", "document.querySelector('#iframe-overlay')?.remove()")), _tab);
        }
    }

    private async Task CrossOriginFrameAsync()
    {
        var foreignUrl = _fixture.Url("/frame").Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        await CommandAsync("evaluate", D(("expression", "url => {const frame=document.createElement('iframe');frame.id='cross-origin-frame';frame.title='Cross origin frame';frame.src=url;document.body.append(frame);return true;}"), ("arg", foreignUrl)), _tab);
        var clock = Stopwatch.StartNew();
        var ready = false;
        while (clock.Elapsed < TimeSpan.FromSeconds(8))
        {
            var count = await RawAsync("count", Target(D(("frame", "#cross-origin-frame"), ("css", "#frame-click")), ("timeoutMs", 1000)), _tab);
            if (count.Ok && Structured(count).GetInt32() == 1) { ready = true; break; }
            await Task.Delay(100, _ct);
        }
        Check(ready, "Cross-origin localhost frame did not become available through its CDP execution context.");
        await CommandAsync("click", Target(D(("frame", "#cross-origin-frame"), ("role", "button"), ("name", "Frame action")), ("timeoutMs", 5000)), _tab);
        await CommandAsync("fill", Target(D(("frame", "#cross-origin-frame"), ("label", "Frame name")), ("text", "cross-origin value"), ("timeoutMs", 5000)), _tab);
        Equal("yes", Structured(await CommandAsync("get_attribute", Target(D(("frame", "#cross-origin-frame"), ("css", "body")), ("name", "data-clicked")), _tab)).GetString(), "Cross-origin native click effect");
        Equal("cross-origin value", Structured(await CommandAsync("get_attribute", Target(D(("frame", "#cross-origin-frame"), ("css", "body")), ("name", "data-value")), _tab)).GetString(), "Cross-origin native fill effect");
        await AssertJsAsync("document.querySelector('#cross-origin-frame').contentDocument === null");
        await CommandAsync("evaluate", D(("expression", "document.querySelector('#cross-origin-frame').remove()")), _tab);
    }

    private async Task QueriesAsync()
    {
        await CommandAsync("check", Target(D(("role", "checkbox"), ("name", "Enable feature"))), _tab);
        await AssertJsAsync("document.querySelector('#enabled').checked === true");
        await CommandAsync("uncheck", Target(D(("css", "#enabled"))), _tab);
        await AssertJsAsync("document.querySelector('#enabled').checked === false");
        var selected = Structured(await CommandAsync("select_option", Target(D(("label", "Choice")), ("values", new[] { "b" })), _tab));
        Equal("b", selected[0].GetString(), "Selected option result");
        await AssertJsAsync("document.querySelector('#choice').value === 'b'");
        Equal(3, Structured(await CommandAsync("count", Target(D(("css", "#items li"))), _tab)).GetInt32(), "Locator count");
        Equal("Second item", Structured(await CommandAsync("inner_text", Target(D(("css", "#items li"), ("hasText", "Second"), ("nth", 0))), _tab)).GetString(), "Filtered locator text");
        Equal("Observed text", Structured(await CommandAsync("text_content", Target(D(("css", "#attribute"))), _tab)).GetString(), "Text content");
        Equal("forty-two", Structured(await CommandAsync("get_attribute", Target(D(("css", "#attribute")), ("name", "data-answer")), _tab)).GetString(), "DOM attribute");
        Check(Structured(await CommandAsync("is_visible", Target(D(("css", "#attribute"))), _tab)).GetBoolean(), "Visible locator query was false.");
        Check(Structured(await CommandAsync("is_enabled", Target(D(("css", "#account"))), _tab)).GetBoolean(), "Enabled locator query was false.");
        await CommandAsync("click", Target(D(("css", "#delayed"))), _tab);
        await CommandAsync("wait_for", Target(D(("css", "#wait-marker")), ("state", "visible"), ("timeoutMs", 5000)), _tab);
        Equal("Wait completed", Structured(await CommandAsync("inner_text", Target(D(("css", "#wait-marker"))), _tab)).GetString(), "Explicit locator wait");
    }

    private async Task InputAsync()
    {
        await CommandAsync("fill", Target(D(("css", "#keyboard")), ("text", "keyboard data")), _tab);
        await CommandAsync("press", Target(D(("css", "#keyboard")), ("key", "Control+A")), _tab);
        await CommandAsync("press", Target(D(("css", "#keyboard")), ("key", "Backspace")), _tab);
        await AssertJsAsync("document.querySelector('#keyboard').value === ''");
        await CommandAsync("press", Target(D(("css", "#keyboard")), ("key", "Enter")), _tab);
        await AssertJsAsync("document.body.dataset.keyboardEnter === 'yes'");
        await CommandAsync("evaluate", D(("expression", "scrollTo(0,0)")), _tab);
        await CommandAsync("scroll", D(("x", 800), ("y", 300), ("deltaX", 0), ("deltaY", 450)), _tab);
        await EventuallyJsAsync("scrollY > 100", "Mouse wheel did not scroll the document.");
        await CommandAsync("evaluate", D(("expression", "scrollTo(0,0)")), _tab);
        await CommandAsync("drag", D(("from", Target(D(("css", "#drag-source")))),
            ("to", Target(D(("css", "#drag-target")))), ("steps", 10)), _tab);
        await AssertJsAsync("document.body.dataset.dragged === 'true'");
    }

    private async Task EvaluateAsync()
    {
        var result = Structured(await CommandAsync("evaluate", D(("expression", "arg => ({answer:arg.number + 2, text:arg.text, title:document.title})"),
            ("arg", D(("number", 40), ("text", "native argument")))), _tab));
        Equal(42, result.GetProperty("answer").GetInt32(), "Evaluate JSON number argument");
        Equal("native argument", result.GetProperty("text").GetString(), "Evaluate JSON text argument");
        Equal("Alcoler native fixture", result.GetProperty("title").GetString(), "Evaluate actual DOM");
    }

    private async Task CancellationAsync()
    {
        var timedOut = await RawAsync("evaluate", D(("expression", "new Promise(resolve => setTimeout(() => resolve('late completion'), 1500))"), ("timeoutMs", 300)), _tab);
        Check(!timedOut.Ok, "Bounded pending evaluate did not time out.");
        var next = Structured(await CommandAsync("evaluate", D(("expression", "21 * 2"), ("timeoutMs", 3000)), _tab));
        Equal(42, next.GetInt32(), "Native command following timed-out evaluate");
        await Task.Delay(1600, _ct);
        await AssertJsAsync("document.body.dataset.marker === 'fixture-ready'");
    }

    private async Task ScreenshotsAsync()
    {
        await CommandAsync("viewport", D(("width", 960), ("height", 640)), _tab);
        await AssertJsAsync("innerWidth === 960 && innerHeight === 640");
        await CommandAsync("evaluate", D(("expression", "scrollTo(0,0)")), _tab);
        var viewport = Png(await CommandAsync("screenshot", tab: _tab));
        Equal(960, viewport.Width, "Viewport PNG width");
        Equal(640, viewport.Height, "Viewport PNG height");
        var clip = Png(await CommandAsync("screenshot", D(("clip", D(("x", 10), ("y", 10), ("width", 240), ("height", 130)))), _tab));
        Equal(240, clip.Width, "Clip PNG width");
        Equal(130, clip.Height, "Clip PNG height");
        var full = Png(await CommandAsync("screenshot", D(("fullPage", true)), _tab));
        Equal(960, full.Width, "Full-page PNG width");
        Check(full.Height >= 1900 && full.Height > viewport.Height, "Full-page PNG did not include content below viewport.");
    }

    private async Task ToolScreenshotAsync()
    {
        var tool = new BrowserTool(_controller);
        var input = BrowserJson.Element(D(("action", "screenshot"), ("tabId", _tab)));
        Check(await tool.ValidateInputAsync(input, _ct) is null, "BrowserTool rejected its documented screenshot input.");
        var gate = new IntegrationPermissionGate();
        var notifications = new List<ToolExecutionNotification>();
        var executor = new ToolCallExecutor(new ToolRegistry([tool]), gate);
        var outcome = await executor.ExecuteAsync(new ToolCall(StrongId.NewToolCallId(), "Browser", input),
            (call, ct) => ValueTask.FromResult(new ToolExecutionContext(_session, _workspace.Root, call.Input, _workspace, TimeProvider.System)),
            notification => { notifications.Add(notification); return ValueTask.CompletedTask; }, _ct);
        var result = outcome.Result;
        Equal(1, gate.Requests.Count, "Kernel browser permission checks");
        Equal(_session, gate.Requests[0].SessionId, "Kernel permission session");
        Equal("Browser", gate.Requests[0].ToolName, "Kernel permission tool");
        Equal(1, notifications.OfType<ToolExecutionStarted>().Count(), "Kernel started notifications");
        Equal(1, notifications.OfType<ToolExecutionPermissionAsked>().Count(), "Kernel permission notifications");
        Equal(1, notifications.OfType<ToolExecutionResultNotification>().Count(), "Kernel completed notifications");
        Check(!result.IsError, "BrowserTool screenshot failed: " + string.Join("\n", result.Content.OfType<ToolResultText>().Select(t => t.Text)));
        var media = result.Content.OfType<ToolResultMedia>().Single();
        var saved = _workspace.Artifacts.Single(a => a.Uri == media.ArtifactUri);
        Equal(_session, saved.SessionId, "Screenshot artifact session");
        Equal("image/png", media.MimeType, "Model-visible screenshot MIME");
        Equal("image/png", saved.MimeType, "Stored screenshot MIME");
        Png(saved.Data);
        Check(!result.Content.OfType<ToolResultText>().Any(t => t.Text.Contains("base64", StringComparison.OrdinalIgnoreCase)), "Screenshot leaked inline media encoding.");
    }

    private async Task PresenterAsync()
    {
        var native = (NativeWebView)_host.GetPageControl(_tab)!;
        var adapter = NativeAdapter(native);
        var destroyed = 0;
        native.AdapterDestroyed += OnDestroyed;
        var sentinel = Guid.NewGuid().ToString("N");
        await CommandAsync("evaluate", D(("expression", "arg => { globalThis.__integrationSentinel = arg; return true; }"), ("arg", sentinel)), _tab);
        try
        {
            var first = _host.CreatePagePresenter(_tab);
            _window.Content = first;
            await UiTurnAsync();
            Check(ReferenceEquals(native.Parent, first), "Presenter did not adopt the existing native page.");
            await VerifyAsync();
            _window.Content = new TextBlock { Text = "Native browser is parked between presenters." };
            await UiTurnAsync();
            Check(native.Parent is Panel, "Detached presenter did not return page to parking deck.");
            await VerifyAsync();
            (first as IDisposable)?.Dispose();
            var second = _host.CreatePagePresenter(_tab);
            var container = new Grid();
            container.Children.Add(second);
            _window.Content = container;
            await UiTurnAsync();
            Check(ReferenceEquals(native.Parent, second), "Second presenter did not adopt native page.");
            await VerifyAsync();
            container.IsVisible = false;
            await UiTurnAsync();
            Check(!ReferenceEquals(native.Parent, second), "Hidden presenter ancestor did not park native page.");
            await VerifyAsync();
            container.IsVisible = true;
            await UiTurnAsync();
            Check(ReferenceEquals(native.Parent, second), "Visible presenter ancestor did not restore native page.");
            await VerifyAsync();
            _window.Content = null;
            (second as IDisposable)?.Dispose();
            await UiTurnAsync();

            using var vm = new BrowserPaneViewModel(_controller, AvaloniaUiScheduler.Instance, pagePresenterFactory: _host.CreatePagePresenter);
            vm.Bind(_session);
            var pane = new BrowserPaneView { DataContext = vm };
            _window.Content = pane;
            await UiTurnAsync();
            Check(vm.HasPagePresenter && !vm.ShowScreenshot, "BrowserPaneView did not embed the live page presenter.");
            Check(ReferenceEquals(native.Parent, vm.PagePresenter), "Pane presenter did not own existing native page.");
            await VerifyAsync();
            _window.Content = null;
            await UiTurnAsync();
            Check(!vm.HasPagePresenter, "Detached pane retained its presenter.");
            await VerifyAsync();
            Console.WriteLine("STEP Reattach previously detached BrowserPaneView to native main window");
            _window.Content = pane;
            await UiTurnAsync();
            Check(vm.HasPagePresenter, "Reattached pane did not recreate its presenter.");
            await VerifyAsync();
            _window.Content = null;
            await UiTurnAsync();
        }
        finally
        {
            native.AdapterDestroyed -= OnDestroyed;
            _window.Content = null;
        }

        void OnDestroyed(object? sender, WebViewAdapterEventArgs args) => destroyed++;
        async Task VerifyAsync()
        {
            Equal(0, destroyed, "Native adapter destruction count while reparenting");
            Check(ReferenceEquals(adapter, NativeAdapter(native)), "Reparenting replaced the native adapter instance.");
            Check(ReferenceEquals(native, _host.GetPageControl(_tab)), "Reparenting replaced the native control.");
            Equal(sentinel, Structured(await CommandAsync("evaluate", D(("expression", "globalThis.__integrationSentinel")), _tab)).GetString(), "Reparented JavaScript sentinel");
        }
    }

    private async Task RecordMemoryAsync()
    {
        Equal(1, Tabs(await CommandAsync("tabs")).Length, "Memory sample active tab count");
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var managed = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"MEM  One active native tab; host PID={process.Id}, workingSetMiB={process.WorkingSet64 / 1048576d:F1}, managedMiB={managed / 1048576d:F1}");
        using var query = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        query.StartInfo.ArgumentList.Add("-NoProfile");
        query.StartInfo.ArgumentList.Add("-NonInteractive");
        query.StartInfo.ArgumentList.Add("-Command");
        query.StartInfo.ArgumentList.Add("$all=Get-CimInstance Win32_Process -Filter \"name = 'msedgewebview2.exe'\";" +
            "$ids=[System.Collections.Generic.HashSet[int]]::new();" +
            $"[void]$ids.Add({process.Id});" +
            "do{$added=$false;foreach($p in $all){if($ids.Contains([int]$p.ParentProcessId) -and $ids.Add([int]$p.ProcessId)){$added=$true}}}while($added);" +
            "$children=@($all|Where-Object{$ids.Contains([int]$_.ProcessId)});" +
            "$total=($children|Measure-Object -Property WorkingSetSize -Sum).Sum;" +
            "Write-Output ('MEM  WebView2 descendants count={0}, totalWorkingSetMiB={1:N1} (separate from host)' -f $children.Count,($total/1MB))");
        query.Start();
        var output = query.StandardOutput.ReadToEndAsync(_ct);
        var error = query.StandardError.ReadToEndAsync(_ct);
        await query.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(15), _ct);
        Check(query.ExitCode == 0, "Could not measure WebView2 child memory: " + await error);
        Console.Write((await output).TrimEnd() + Environment.NewLine);
    }

    private async Task SessionsAsync()
    {
        await CommandAsync("evaluate", D(("expression", "localStorage.setItem('integration-session','owner');document.cookie='integrationSession=owner;path=/';true")), _tab);
        var second = await CommandAsync("new_tab", D(("url", _fixture.Url())), session: _session);
        var secondId = second.TabId!;
        Equal(2, Tabs(await CommandAsync("tabs")).Length, "Same-session new tab count");
        Equal("owner", Structured(await CommandAsync("evaluate", D(("expression", "localStorage.getItem('integration-session')")), secondId)).GetString(), "Same-session browser storage");
        await CommandAsync("select_tab", tab: _tab);
        Equal(_tab, Structured(await CommandAsync("tabs")).GetProperty("activeTabId").GetString(), "Active tab selection");
        await CommandAsync("close_tab", tab: secondId);
        Check(_host.GetPageControl(secondId) is null, "Closed tab retained its native page.");
        var other = await CommandAsync("new_tab", D(("url", _fixture.Url())), session: _otherSession);
        _otherTab = other.TabId!;
        Equal(1, Tabs(await CommandAsync("tabs", session: _otherSession)).Length, "Other session tab count");
        Check(!Tabs(await CommandAsync("tabs")).Any(t => t.GetProperty("id").GetString() == _otherTab), "Session listing leaked another session's tab.");
        var isolated = Structured(await CommandAsync("evaluate", D(("expression", "({storage:localStorage.getItem('integration-session'),cookie:document.cookie})")), _otherTab, _otherSession));
        Check(isolated.GetProperty("storage").ValueKind == JsonValueKind.Null, "Session profiles leaked localStorage.");
        Check(!isolated.GetProperty("cookie").GetString()!.Contains("integrationSession=owner", StringComparison.Ordinal), "Session profiles leaked cookies.");
        foreach (var verb in new[] { "select_tab", "close_tab", "evaluate" })
        {
            var denied = await RawAsync(verb, verb == "evaluate" ? D(("expression", "document.title")) : null, _tab, _otherSession);
            Check(!denied.Ok, $"Cross-session {verb} was not rejected.");
        }
        await AssertJsAsync("localStorage.getItem('integration-session') === 'owner'");
        var retained = Structured(await CommandAsync("finalize", D(("keep", new[] { _tab })), _tab));
        Check(retained.GetProperty("tabs").EnumerateArray().Single().GetProperty("keep").GetBoolean(), "Finalize did not retain requested tab.");
    }

    private async Task TakeoverAsync()
    {
        await CommandAsync("record_start", D(("maxDurationMs", 3000), ("fps", 4)), _tab);
        await CommandAsync("takeover", tab: _tab);
        var recording = await CommandAsync("record_status", tab: _tab);
        Equal("cancelled", Structured(recording).GetProperty("state").GetString(), "Takeover recording cancellation");
        Check(recording.Media is null, "Takeover exposed recording media after yielding to the human.");
        Check(_controller.GetStatus(_session)?.IsUserControlled == true, "Takeover did not publish human ownership.");
        Check((await RawAsync("fill", Target(D(("css", "#account")), ("text", "must not be written")), _tab)).Ok == false,
            "Automated input was not blocked during human control.");
        Check(!(await RawAsync("evaluate", D(("expression", "document.body.dataset.illegal='yes'")), _tab)).Ok, "Evaluate bypassed human ownership.");
        Check(!(await RawAsync("resume", tab: _tab)).Ok, "Model resume bypassed human ownership.");
        Check(_controller.GetStatus(_session)?.IsUserControlled == true, "Rejected model resume changed ownership.");
        var observed = Structured(await CommandAsync("element_info", Target(D(("css", "#account"))), _tab));
        Equal("test id account", observed.GetProperty("value").GetString(), "Blocked fill did not mutate page");
        await CommandAsync("snapshot", tab: _tab);
        await CommandAsync("fill", Target(D(("css", "#account")), ("text", "manual content")), _tab, userInitiated: true);
        await Task.Delay(3500, _ct);
        var afterHumanInput = await CommandAsync("record_status", tab: _tab);
        Equal("cancelled", Structured(afterHumanInput).GetProperty("state").GetString(), "Recording stayed canceled during manual input");
        Check(afterHumanInput.Media is null, "Recording exposed media containing post-takeover manual input.");
        await CommandAsync("resume", tab: _tab, userInitiated: true);
        Check(_controller.GetStatus(_session)?.IsUserControlled == false, "Manual resume did not release human ownership.");
        await CommandAsync("fill", Target(D(("css", "#account")), ("text", "resumed account")), _tab);
        await AssertJsAsync("document.querySelector('#account').value === 'resumed account' && !document.body.dataset.illegal");
        await CommandAsync("visibility", D(("visible", false)), _tab);
    }

    private async Task PopupAsync()
    {
        _popup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await CommandAsync("click", Target(D(("css", "#popup"))), _tab);
            var opened = await _popup.Task.WaitAsync(TimeSpan.FromSeconds(45), _ct);
            Check(opened.Result.Ok, "Popup creation failed: " + opened.Result.Error);
            Equal(_session, opened.Request.SessionId, "Popup session ownership");
            Equal(_tab, opened.Request.OpenerTabId, "Popup opener");
            Equal(_fixture.Url("/popup"), opened.Request.Url, "Popup target URL");
            var popupId = opened.Result.TabId ?? throw new InvalidOperationException("Popup omitted its tab ID.");
            Equal("Popup fixture", opened.Result.Title, "Popup real navigation title");
            Check(Tabs(await CommandAsync("tabs")).Any(t => t.GetProperty("id").GetString() == popupId), "Popup missing from opener session.");
            Check(!Tabs(await CommandAsync("tabs", session: _otherSession)).Any(t => t.GetProperty("id").GetString() == popupId), "Popup leaked to another session.");
            await CommandAsync("close_tab", tab: popupId);
            await CommandAsync("select_tab", tab: _tab);
        }
        finally { _popup = null; }
    }

    private async void OnNewTabRequested(object? sender, BrowserNewTabRequestedEventArgs e)
    {
        var completion = _popup;
        try
        {
            var result = await _nativeController.OpenPopupAsync(e.SessionId, e.OpenerTabId, e.Url, _ct, e.IsUserInitiated);
            completion?.TrySetResult((e, result));
        }
        catch (Exception ex)
        {
            if (completion is null) Console.Error.WriteLine($"FAIL Unexpected popup callback{Environment.NewLine}{ex}");
            else completion.TrySetException(ex);
        }
    }

    private async Task DialogsAsync()
    {
        // The trigger returns before opening the modal; awaited prompt evaluation holds the command gate.
        await CommandAsync("evaluate", D(("expression", "(() => {setTimeout(() => {document.body.dataset.promptResult=prompt('Integration prompt','default')},250);return 'scheduled';})()")), _tab);
        var prompt = await WaitDialogAsync();
        Equal("prompt", prompt.GetProperty("dialog").GetProperty("type").GetString(), "Prompt dialog type");
        await CommandAsync("dialog", D(("operation", "accept"), ("text", "native prompt answer")), _tab);
        await AssertJsAsync("document.body.dataset.promptResult === 'native prompt answer'");
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
        while (clock.Elapsed < TimeSpan.FromSeconds(8))
        {
            var status = Structured(await CommandAsync("dialog", D(("operation", "status")), _tab));
            if (status.GetProperty("open").GetBoolean()) return status;
            await Task.Delay(50, _ct);
        }
        throw new TimeoutException("JavaScript dialog did not become observable.");
    }

    private async Task DownloadsAsync()
    {
        var media = await CommandAsync("download_media", D(("url", _fixture.Url("/media")), ("fileName", "fixture-media.bin")), _tab);
        Check(media.Media is not null && media.Media.AsSpan().SequenceEqual(LoopbackFixture.MediaBytes), "Downloaded media bytes differ from local HTTP response.");
        Equal("application/octet-stream", media.MediaType, "Downloaded media MIME");
        Equal("fixture-media.bin", media.MediaFileName, "Downloaded media name");
        var tool = new BrowserTool(_controller);
        var result = await tool.ExecuteAsync(new(_session, _workspace.Root,
            BrowserJson.Element(D(("action", "download_media"), ("url", _fixture.Url("/media")), ("fileName", "fixture-artifact.bin"), ("tabId", _tab))),
            _workspace, TimeProvider.System), _ct);
        Check(!result.IsError, "BrowserTool download_media failed.");
        var artifact = result.Content.OfType<ToolResultMedia>().Single();
        Check(_workspace.Artifacts.Single(a => a.Uri == artifact.ArtifactUri).Data.AsSpan().SequenceEqual(LoopbackFixture.MediaBytes), "Tool media artifact lost downloaded bytes.");
        await CommandAsync("click", Target(D(("css", "#download"))), _tab);
        var clock = Stopwatch.StartNew();
        JsonElement? completed = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(12))
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
            await Task.Delay(50, _ct);
        }
        var completedDownload = completed ?? throw new InvalidOperationException("Native HTTP download did not emit completed metadata.");
        Equal("fixture-download.bin", completedDownload.GetProperty("suggestedFilename").GetString(), "Native download filename");
        var path = completedDownload.GetProperty("filePath").GetString() ?? throw new InvalidOperationException("Completed download omitted file path.");
        var downloadedPath = Path.GetFullPath(path);
        Check(downloadedPath.StartsWith(Path.GetFullPath(_profileDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Download escaped integration-owned profile.");
        Check((await File.ReadAllBytesAsync(path, _ct)).AsSpan().SequenceEqual(LoopbackFixture.MediaBytes), "Native downloaded file contents differ from fixture response.");
    }

    private async Task RecordingAsync()
    {
        await CommandAsync("record_start", D(("maxDurationMs", 1200), ("fps", 4)), _tab);
        BrowserActionResult? completed = null;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(25))
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
        Check(bytes.Length > 128 && bytes.AsSpan().StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }), "Recording did not return a real WebM EBML container.");
        await CommandAsync("record_start", D(("maxDurationMs", 3000), ("fps", 4)), _tab);
        var active = Structured(await CommandAsync("record_status", tab: _tab));
        Equal("recording", active.GetProperty("state").GetString(), "Active recording status");
        var cancelled = await CommandAsync("record_cancel", tab: _tab);
        Equal("cancelled", Structured(cancelled).GetProperty("state").GetString(), "Recording cancellation status");
        Check(cancelled.Media is null, "Cancelled recording retained media.");
        await EventuallyJsAsync("!globalThis.__alcolerRecording", "Cancelled recording retained browser capture resources.");
        var after = await CommandAsync("record_status", tab: _tab);
        Equal("cancelled", Structured(after).GetProperty("state").GetString(), "Persistent recording cancellation");
        Check(after.Media is null, "Cancelled recording status exposed stale media.");
    }

    private async Task HistoryAsync()
    {
        var snapshot = Structured(await CommandAsync("snapshot", tab: _tab));
        var reference = snapshot.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("role").GetString() == "button").GetProperty("ref").GetString();
        var second = await CommandAsync("navigate", D(("url", _fixture.Url("/second"))), _tab);
        Equal("Second fixture page", second.Title, "Second navigation title");
        Check(!(await RawAsync("click", D(("ref", reference)), _tab)).Ok, "Navigation did not invalidate old snapshot reference.");
        await CommandAsync("back", tab: _tab);
        await AssertJsAsync("document.title === 'Alcoler native fixture'");
        await CommandAsync("forward", tab: _tab);
        await AssertJsAsync("document.title === 'Second fixture page'");
        await CommandAsync("reload", tab: _tab);
        await AssertJsAsync("document.title === 'Second fixture page'");
    }

    private async Task CloseAsync()
    {
        await CommandAsync("close_tab", tab: _tab);
        await CommandAsync("close_tab", tab: _otherTab, session: _otherSession);
        Equal(0, Tabs(await CommandAsync("tabs")).Length, "Closed primary session tab count");
        Equal(0, Tabs(await CommandAsync("tabs", session: _otherSession)).Length, "Closed secondary session tab count");
        Check(_host.GetPageControl(_tab) is null && _host.GetPageControl(_otherTab) is null, "Closed tabs retained native controls.");
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

    private async Task EventuallyJsAsync(string expression, string error)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (Structured(await CommandAsync("evaluate", D(("expression", expression)), _tab)).ValueKind == JsonValueKind.True) return;
            await Task.Delay(50, _ct);
        }
        throw new InvalidOperationException(error);
    }

    private static JsonElement Structured(BrowserActionResult result)
    {
        using var doc = JsonDocument.Parse(result.StructuredOutput ?? throw new InvalidOperationException("Browser result omitted structured output."));
        return doc.RootElement.Clone();
    }

    private static JsonElement[] Tabs(BrowserActionResult result) => Structured(result).GetProperty("tabs").EnumerateArray().Select(t => t.Clone()).ToArray();

    private static object NativeAdapter(NativeWebView native) => native.TryGetPlatformHandle()
        ?? throw new InvalidOperationException("Native WebView has no live adapter.");

    private static async Task UiTurnAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(100);
    }

    private static (int Width, int Height) Png(BrowserActionResult result) => Png(result.ScreenshotPng
        ?? throw new InvalidOperationException("Screenshot omitted PNG bytes."));

    private static (int Width, int Height) Png(byte[] png)
    {
        Check(png.Length > 32 && png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Screenshot is not a PNG file.");
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        using var stream = new MemoryStream(png, writable: false);
        using var decoded = new Bitmap(stream);
        Equal(width, decoded.PixelSize.Width, "Decoded PNG width");
        Equal(height, decoded.PixelSize.Height, "Decoded PNG height");
        Check(width > 0 && height > 0, "Decoded PNG has no pixels.");
        return (width, height);
    }

    private sealed class IntegrationPermissionGate : IPermissionGate
    {
        public List<PermissionCheckRequest> Requests { get; } = [];

        public ValueTask<PermissionDecision> CheckAsync(PermissionCheckRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(PermissionDecision.Allow);
        }
    }

    private static Dictionary<string, object?> D(params (string Name, object? Value)[] values) =>
        values.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

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

    private static async Task DeleteOwnedDirectoryAsync(string directory)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                Console.WriteLine("CLEAN Temporary native browser profile deleted.");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        Console.Error.WriteLine($"CLEAN Temporary profile retained after WebView2 release retries: {directory}{Environment.NewLine}{last}");
        throw new IOException("The native runtime retained the integration-owned temporary profile after disposal.", last);
    }
}
