using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alcoler.Contracts.Browser;

namespace Alcoler.Browser.Automation;

public sealed partial class CdpBrowserAutomation : IDisposable
{
    private const int MaxText = 24000;
    private const int MaxMedia = 32 * 1024 * 1024;
    private readonly IBrowserPage _page;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, FrameContext> _contexts = new();
    private readonly ConcurrentDictionary<string, string> _frameSessions = new();
    private readonly ConcurrentDictionary<string, FrameOwner> _frameOwners = new();
    private readonly ConcurrentDictionary<string, ElementReference> _references = new();
    private readonly ConcurrentDictionary<string, JsonElement> _downloads = new();
    private readonly ConcurrentDictionary<string, string> _loadStates = new();
    private JsonElement? _dialog;
    private string? _mainFrame;
    private int _generation;
    private int _snapshotSequence;
    private bool _initialized;
    private bool _disposed;
    private bool _sideEffect;
    private readonly Dictionary<string, Dictionary<string, object?>> _pressedKeys = new();
    private Dictionary<string, object?>? _pressedMouse;
    private int _viewportWidth = 1280;
    private int _viewportHeight = 720;

    public CdpBrowserAutomation(IBrowserPage page)
    {
        _page = page;
        _page.ProtocolEvent += OnProtocolEvent;
    }

    public string? Url { get; private set; }
    public string? Title { get; private set; }
    public event EventHandler? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try { await InitializeCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await SendAsync("Page.enable", null, ct);
        await SendAsync("Runtime.enable", null, ct);
        await SendAsync("DOM.enable", null, ct);
        await OptionalAsync("Accessibility.enable", null, ct);
        await OptionalAsync("Page.setLifecycleEventsEnabled", D(("enabled", true)), ct);
        await OptionalAsync("Target.setAutoAttach", D(("autoAttach", true), ("waitForDebuggerOnStart", false), ("flatten", true)), ct);
        var tree = await SendAsync("Page.getFrameTree", null, ct);
        if (tree.TryGetProperty("frameTree", out var frameTree))
        {
            var frame = frameTree.GetProperty("frame");
            _mainFrame = String(frame, "id");
            Url = String(frame, "url");
        }
        _initialized = true;
    }

    public async Task<BrowserActionResult> ExecuteAsync(BrowserAction action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (command, input) = Normalize(action);
        var defaultTimeout = command == "wait" ? Math.Min(60000, Integer(input, "milliseconds", 0, 0, 60000) + 1000) : 30000;
        var timeout = Integer(input, "timeoutMs", defaultTimeout, 1, 60000);
        if (command == "wait" && !input.TryGetProperty("timeoutMs", out _)) timeout += 1000;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        bounded.CancelAfter(timeout);
        var token = bounded.Token;
        var entered = false;
        try
        {
            await _gate.WaitAsync(token);
            entered = true;
            _sideEffect = false;
            await InitializeCoreAsync(token);
            var result = await ExecuteCoreAsync(command, input, token);
            if (command is not ("dialog" or "record_start" or "record_status" or "record_cancel") && _dialog is null)
                await UpdateMetadataAsync(token);
            return result with { Url = result.Url ?? Url, Title = result.Title ?? Title, TabId = _page.Id };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            return new(false, Url, Title, Error: $"Browser action '{command}' timed out after {timeout} ms.", TabId: _page.Id, SideEffectUncertain: entered && _sideEffect);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(false, Url, Title, Error: ex.Message, TabId: _page.Id, SideEffectUncertain: entered && _sideEffect);
        }
        finally
        {
            if (entered)
            {
                await ReleasePressedInputAsync();
                _gate.Release();
            }
        }
    }

    private async Task<BrowserActionResult> ExecuteCoreAsync(string command, JsonElement input, CancellationToken ct)
    {
        switch (command)
        {
            case "navigate": return await NavigateAsync(input, ct);
            case "back": case "forward": case "reload": return await HistoryAsync(command, input, ct);
            case "snapshot": return await SnapshotAsync(input, false, ct);
            case "dom_snapshot": return await SnapshotAsync(input, true, ct);
            case "evaluate":
            {
                var expression = RequiredString(input, "expression");
                var arg = input.TryGetProperty("arg", out var value) ? value : BrowserJson.Element(null);
                _sideEffect = true;
                var script = $"(async()=>{{const v=(0,eval)({BrowserJson.Serialize(expression)});return typeof v==='function'?await v({arg.GetRawText()}):await v;}})()";
                var result = await SendAsync("Runtime.evaluate", D(("expression", script), ("awaitPromise", true), ("returnByValue", true)), ct);
                CheckException(result);
                var output = RemoteValue(result);
                return Structured(output);
            }
            case "click": case "dblclick": case "hover": case "fill": case "type": case "press":
            case "scroll": case "drag": case "check": case "uncheck": case "select_option":
                return await InteractAsync(command, input, ct);
            case "count": case "inner_text": case "text_content": case "get_attribute": case "is_visible": case "is_enabled": case "element_info":
                return await QueryAsync(command, input, ct);
            case "read_text":
            {
                var context = await ContextForInputAsync(input, ct);
                var locator = Locator(input, false);
                var value = locator is null
                    ? await EvaluateValueAsync(context, "document.body?.innerText || ''", ct)
                    : RemoteValue(await WithElementAsync(input, "function(){return this.innerText ?? this.textContent ?? '';}", [], ct, true));
                return new(true, Text: Truncate(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString(), Integer(input, "maxChars", MaxText, 1, MaxText)));
            }
            case "wait":
                await Task.Delay(Integer(input, "milliseconds", 0, 0, 60000), ct);
                return new(true);
            case "wait_for": return await WaitForAsync(input, ct);
            case "screenshot": return await ScreenshotAsync(input, ct);
            case "viewport": case "set_viewport":
                _viewportWidth = Integer(input, "width", 1280, 1, 8192);
                _viewportHeight = Integer(input, "height", 720, 1, 8192);
                if ((long)_viewportWidth * _viewportHeight > 16000000) throw new InvalidOperationException("Viewport exceeds 16 million pixels.");
                _sideEffect = true;
                await SendAsync("Emulation.setDeviceMetricsOverride", D(("width", _viewportWidth), ("height", _viewportHeight), ("deviceScaleFactor", 1), ("mobile", false)), ct);
                return new(true);
            case "dialog": return await DialogAsync(input, ct);
            case "downloads": return Structured(BrowserJson.Element(_downloads.Values.ToArray()));
            case "download_media": return await DownloadMediaAsync(input, ct);
            case "record_start": case "record_status": case "record_cancel": case "record_stop": return await RecordingAsync(command, input, ct);
            default: throw new NotSupportedException($"Unsupported browser page action '{command}'.");
        }
    }

    private async Task<BrowserActionResult> NavigateAsync(JsonElement input, CancellationToken ct)
    {
        var url = RequiredString(input, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme is not ("http" or "https") && url != "about:blank"))
            throw new ArgumentException("Navigation requires an http(s) URL or about:blank.");
        var wait = String(input, "waitUntil") ?? "load";
        ValidateLoadState(wait);
        InvalidateDocument();
        _sideEffect = true;
        var result = await SendAsync("Page.navigate", D(("url", url)), ct);
        if (String(result, "errorText") is { Length: > 0 } error) throw new InvalidOperationException(error);
        var frameId = String(result, "frameId") ?? _mainFrame;
        Url = url;
        if (wait != "commit") await WaitLoadAsync(frameId, wait, ct);
        return new(true);
    }

    private async Task<BrowserActionResult> HistoryAsync(string command, JsonElement input, CancellationToken ct)
    {
        var wait = String(input, "waitUntil") ?? "load";
        ValidateLoadState(wait);
        if (command == "reload")
        {
            InvalidateDocument();
            _sideEffect = true;
            await SendAsync("Page.reload", D(("ignoreCache", Boolean(input, "ignoreCache"))), ct);
        }
        else
        {
            var history = await SendAsync("Page.getNavigationHistory", null, ct);
            var index = history.GetProperty("currentIndex").GetInt32() + (command == "back" ? -1 : 1);
            var entries = history.GetProperty("entries");
            if (index < 0 || index >= entries.GetArrayLength()) return new(true, Text: "No history entry.");
            InvalidateDocument();
            _sideEffect = true;
            await SendAsync("Page.navigateToHistoryEntry", D(("entryId", entries[index].GetProperty("id").GetInt32())), ct);
        }
        if (wait != "commit") await WaitLoadAsync(_mainFrame, wait, ct);
        return new(true);
    }

    private static void ValidateLoadState(string state)
    {
        if (state == "networkidle") throw new NotSupportedException("networkidle is not supported; use load, domcontentloaded, or an explicit locator wait.");
        if (state is not ("load" or "domcontentloaded" or "commit")) throw new ArgumentException("Invalid page load state.");
    }

    private async Task WaitLoadAsync(string? frameId, string state, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (frameId is not null && _loadStates.TryGetValue(frameId, out var loaded) && (loaded == "load" || loaded == state)) return;
            try
            {
                var context = await ContextAsync(frameId, ct);
                var ready = await EvaluateValueAsync(context, "document.readyState", ct);
                if (ready.ValueKind == JsonValueKind.String && (ready.GetString() == "complete" || state == "domcontentloaded" && ready.GetString() == "interactive")) return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && IsContextLost(ex)) { _contexts.Clear(); }
            await Task.Delay(50, ct);
        }
    }

    private async Task<BrowserActionResult> WaitForAsync(JsonElement input, CancellationToken ct)
    {
        var state = String(input, "state") ?? "visible";
        if (state == "networkidle") ValidateLoadState(state);
        if (Locator(input, false) is null && String(input, "url") is null)
        {
            ValidateLoadState(state);
            await WaitLoadAsync(_mainFrame, state, ct);
            return new(true);
        }
        if (String(input, "url") is null && state is not ("attached" or "detached" or "visible" or "hidden")) throw new ArgumentException("Invalid locator wait state.");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (String(input, "url") is { } target)
            {
                await UpdateMetadataAsync(ct);
                if (Url == target) return new(true);
            }
            else
            {
                try
                {
                    var context = await ContextForInputAsync(input, ct);
                    var locator = await RuntimeLocatorAsync(input, context, ct);
                    var value = await EvaluateValueAsync(context, $"(() => {{const es=__alcolerAutomation.resolve({locator});return {{count:es.length,visible:es.some(__alcolerAutomation.visible)}};}})()", ct);
                    var count = value.GetProperty("count").GetInt32();
                    var visible = value.GetProperty("visible").GetBoolean();
                    if (state switch { "attached" => count > 0, "detached" => count == 0, "hidden" => !visible, _ => visible }) return new(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && IsContextLost(ex)) { _contexts.Clear(); }
            }
            await Task.Delay(50, ct);
        }
    }

    private async Task UpdateMetadataAsync(CancellationToken ct)
    {
        var context = await ContextAsync(null, ct);
        var value = await EvaluateValueAsync(context, "({url:location.href,title:document.title})", ct);
        Url = String(value, "url") ?? Url;
        Title = String(value, "title") ?? Title;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<FrameContext> ContextAsync(string? frameId, CancellationToken ct)
    {
        frameId ??= _mainFrame;
        if (frameId is null)
        {
            var tree = await SendAsync("Page.getFrameTree", null, ct);
            frameId = tree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
            _mainFrame = frameId;
        }
        if (_contexts.TryGetValue(frameId, out var existing)) return existing;
        _frameSessions.TryGetValue(frameId, out var session);
        var world = await SendAsync("Page.createIsolatedWorld", D(("frameId", frameId), ("worldName", "alcoler.automation"), ("grantUniveralAccess", false)), ct, session);
        var context = new FrameContext(frameId, world.GetProperty("executionContextId").GetInt32(), session);
        await SendAsync("Runtime.evaluate", D(("expression", BrowserRuntime.Install), ("contextId", context.Id), ("returnByValue", true)), ct, session);
        _contexts[frameId] = context;
        return context;
    }

    private async Task<FrameContext> ContextForInputAsync(JsonElement input, CancellationToken ct)
    {
        var locator = Locator(input, false);
        if (locator is { } l && l.ValueKind == JsonValueKind.Object && (l.TryGetProperty("frame", out var frameSpec) || l.TryGetProperty("frameLocator", out frameSpec)))
        {
            var frames = frameSpec.ValueKind == JsonValueKind.Array ? frameSpec.EnumerateArray().Select(v => v.Clone()).ToArray() : [frameSpec];
            var context = await ContextAsync(null, ct);
            foreach (var frameSelector in frames)
            {
                var ownerContext = context;
                var owner = await ResolveObjectAsync(BrowserJson.Element(D(("selector", frameSelector))), ownerContext, ct);
                try
                {
                    var description = await SendAsync("DOM.describeNode", D(("objectId", owner)), ct, ownerContext.Session);
                    var node = description.GetProperty("node");
                    var frame = String(node, "frameId") ?? throw new InvalidOperationException("Frame locator did not select an iframe.");
                    _frameOwners[frame] = new(ownerContext.FrameId, node.GetProperty("backendNodeId").GetInt32());
                    context = await ContextAsync(frame, ct);
                }
                finally { await ReleaseAsync(owner, ownerContext.Session, ct); }
            }
            return context;
        }
        if (GetReference(input) is { } reference && _references.TryGetValue(reference, out var stored)) return await ContextAsync(stored.FrameId, ct);
        return await ContextAsync(null, ct);
    }

    private async Task<string> RuntimeLocatorAsync(JsonElement input, FrameContext context, CancellationToken ct)
    {
        var locator = Locator(input, true)!.Value;
        var node = JsonNode.Parse(locator.GetRawText());
        if (node is JsonObject obj) { obj.Remove("frame"); obj.Remove("frameLocator"); }
        if (GetReference(input) is { } reference)
        {
            if (!_references.TryGetValue(reference, out var stored) || stored.Generation != _generation)
                throw new InvalidOperationException("Stale element reference; take a new snapshot.");
            if (stored.BackendId is { } backend)
            {
                var result = await SendAsync("DOM.resolveNode", D(("backendNodeId", backend), ("executionContextId", context.Id)), ct, context.Session);
                var objectId = String(result.GetProperty("object"), "objectId") ?? throw new InvalidOperationException("Referenced node is no longer available.");
                try { await CallObjectAsync(context, objectId, "function(ref){globalThis.__alcolerAutomation.refs.set(ref,this);return true;}", [D(("value", reference))], ct, true); }
                finally { await ReleaseAsync(objectId, context.Session, ct); }
            }
        }
        return node?.ToJsonString() ?? locator.GetRawText();
    }

    private async Task<string> ResolveObjectAsync(JsonElement input, FrameContext context, CancellationToken ct)
    {
        var locator = await RuntimeLocatorAsync(input, context, ct);
        while (true)
        {
            var result = await SendAsync("Runtime.evaluate", D(("expression", $"__alcolerAutomation.select({locator})"), ("contextId", context.Id), ("returnByValue", false), ("objectGroup", "alcoler")), ct, context.Session);
            try
            {
                CheckException(result);
                var remote = result.GetProperty("result");
                return String(remote, "objectId") ?? throw new InvalidOperationException("Locator did not resolve to a DOM element.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("matched 0 elements", StringComparison.Ordinal) && GetReference(input) is null)
            {
                await Task.Delay(50, ct);
            }
        }
    }

    private async Task<JsonElement> WithElementAsync(JsonElement input, string function, object[] arguments, CancellationToken ct, bool byValue)
    {
        var context = await ContextForInputAsync(input, ct);
        var id = await ResolveObjectAsync(input, context, ct);
        try { return await CallObjectAsync(context, id, function, arguments, ct, byValue); }
        finally { await ReleaseAsync(id, context.Session, ct); }
    }

    private async Task<JsonElement> EvaluateValueAsync(FrameContext context, string expression, CancellationToken ct)
    {
        var result = await SendAsync("Runtime.evaluate", D(("expression", expression), ("contextId", context.Id), ("awaitPromise", true), ("returnByValue", true)), ct, context.Session);
        CheckException(result);
        return RemoteValue(result);
    }

    private async Task<JsonElement> CallAsync(FrameContext context, string function, object[] args, CancellationToken ct, bool byValue)
    {
        var result = await SendAsync("Runtime.callFunctionOn", D(("functionDeclaration", function), ("executionContextId", context.Id), ("arguments", args), ("awaitPromise", true), ("returnByValue", byValue)), ct, context.Session);
        CheckException(result);
        return result;
    }

    private async Task<JsonElement> CallObjectAsync(FrameContext context, string objectId, string function, object[] args, CancellationToken ct, bool byValue)
    {
        var result = await SendAsync("Runtime.callFunctionOn", D(("functionDeclaration", function), ("objectId", objectId), ("arguments", args), ("awaitPromise", true), ("returnByValue", byValue)), ct, context.Session);
        CheckException(result);
        return result;
    }

    private async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct, string? session = null)
    {
        ct.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        if (parameters is Dictionary<string, object?> p)
        {
            if (method == "Input.dispatchMouseEvent" && p.TryGetValue("type", out var mouseType) && mouseType is "mousePressed")
                _pressedMouse = new(p) { ["type"] = "mouseReleased", ["buttons"] = 0 };
            if (method == "Input.dispatchKeyEvent" && p.TryGetValue("type", out var keyType) && keyType is "keyDown" or "rawKeyDown" && p.TryGetValue("key", out var key) && key is string keyName)
            {
                var release = new Dictionary<string, object?>(p) { ["type"] = "keyUp" };
                release.Remove("text");
                _pressedKeys[keyName] = release;
            }
        }
        var result = await _page.SendAsync(method, parameters, ct, session);
        if (parameters is Dictionary<string, object?> released)
        {
            if (method == "Input.dispatchMouseEvent" && released.TryGetValue("type", out var mouseType) && mouseType is "mouseReleased") _pressedMouse = null;
            if (method == "Input.dispatchKeyEvent" && released.TryGetValue("type", out var keyType) && keyType is "keyUp" && released.TryGetValue("key", out var key) && key is string keyName) _pressedKeys.Remove(keyName);
        }
        return result;
    }

    private async Task ReleasePressedInputAsync()
    {
        if (_pressedMouse is null && _pressedKeys.Count == 0) return;
        using var cleanup = new CancellationTokenSource(1000);
        try
        {
            if (_pressedMouse is { } mouse) await _page.SendAsync("Input.dispatchMouseEvent", mouse, cleanup.Token);
            foreach (var key in _pressedKeys.Values.Reverse()) await _page.SendAsync("Input.dispatchKeyEvent", key, cleanup.Token);
        }
        catch (Exception) { }
        finally { _pressedMouse = null; _pressedKeys.Clear(); }
    }

    private async Task OptionalAsync(string method, object? parameters, CancellationToken ct)
    {
        try { await SendAsync(method, parameters, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private async Task ReleaseAsync(string id, string? session, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _lifetime.IsCancellationRequested) return;
        try { await SendAsync("Runtime.releaseObject", D(("objectId", id)), ct, session); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private static void CheckException(JsonElement result)
    {
        if (!result.TryGetProperty("exceptionDetails", out var details)) return;
        var message = details.TryGetProperty("exception", out var exception) ? String(exception, "description") : null;
        throw new InvalidOperationException(message ?? String(details, "text") ?? "JavaScript execution failed.");
    }

    private static JsonElement RemoteValue(JsonElement result)
    {
        CheckException(result);
        if (!result.TryGetProperty("result", out var remote)) return result.Clone();
        if (remote.TryGetProperty("value", out var value)) return value.Clone();
        if (String(remote, "unserializableValue") is { } special) return BrowserJson.Element(special);
        return BrowserJson.Element(null);
    }

    private void OnProtocolEvent(object? sender, BrowserProtocolEvent e)
    {
        var p = e.Parameters;
        switch (e.Method)
        {
            case "Page.frameNavigated":
                if (!p.TryGetProperty("frame", out var frame)) break;
                var id = String(frame, "id");
                if (String(frame, "parentId") is null && e.SessionId is null)
                {
                    _mainFrame = id;
                    Url = String(frame, "url");
                    InvalidateDocument();
                }
                else if (id is not null) { _contexts.TryRemove(id, out _); _references.Clear(); }
                break;
            case "Page.navigatedWithinDocument":
                if (String(p, "frameId") == _mainFrame) Url = String(p, "url");
                break;
            case "DOM.documentUpdated":
            case "Runtime.executionContextsCleared": InvalidateDocument(); break;
            case "Page.frameDetached":
                if (String(p, "frameId") is { } detached)
                {
                    _contexts.TryRemove(detached, out _);
                    _frameSessions.TryRemove(detached, out _);
                    if (String(p, "reason") != "swap") _frameOwners.TryRemove(detached, out _);
                }
                _references.Clear();
                break;
            case "Target.attachedToTarget":
                if (p.TryGetProperty("targetInfo", out var info) && String(info, "type") == "iframe" && String(info, "targetId") is { } target && String(p, "sessionId") is { } session)
                    _frameSessions[target] = session;
                break;
            case "Page.lifecycleEvent":
                if (String(p, "frameId") is { } loadFrame && String(p, "name") is { } name)
                    if (name is "load" or "DOMContentLoaded") _loadStates[loadFrame] = name == "load" ? "load" : "domcontentloaded";
                break;
            case "Host.titleChanged":
                Title = String(p, "title") ?? Title;
                Url = String(p, "url") ?? Url;
                break;
            case "Host.loadingChanged": Url = String(p, "url") ?? Url; break;
            case "Page.javascriptDialogOpening": _dialog = p.Clone(); break;
            case "Page.javascriptDialogClosed": _dialog = null; break;
            case "Browser.downloadWillBegin": case "Page.downloadWillBegin": case "Browser.downloadProgress": case "Page.downloadProgress":
                if (String(p, "guid") is { } guid)
                {
                    _downloads.AddOrUpdate(guid, p.Clone(), (_, previous) =>
                    {
                        var merged = JsonNode.Parse(previous.GetRawText())!.AsObject();
                        foreach (var property in p.EnumerateObject()) merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                        return JsonDocument.Parse(merged.ToJsonString()).RootElement.Clone();
                    });
                    while (_downloads.Count > 100 && _downloads.Keys.FirstOrDefault() is { } oldest) _downloads.TryRemove(oldest, out _);
                }
                break;
        }
        if (e.Method is "Page.frameNavigated" or "Page.navigatedWithinDocument" or "Page.frameDetached" or "Host.titleChanged" or "Host.loadingChanged" or "Page.javascriptDialogOpening" or "Page.javascriptDialogClosed" || e.Method.Contains("download", StringComparison.Ordinal) || e.Method == "Page.lifecycleEvent" && String(p, "name") is "load" or "DOMContentLoaded")
            StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateDocument()
    {
        Interlocked.Increment(ref _generation);
        _contexts.Clear();
        _references.Clear();
        _loadStates.Clear();
    }

    private static (string Command, JsonElement Input) Normalize(BrowserAction action) => action switch
    {
        BrowserCommandAction c => (c.Command.ToLowerInvariant(), c.Input),
        BrowserNavigateAction n => ("navigate", BrowserJson.Element(D(("url", n.Url)))),
        BrowserClickAction c => ("click", BrowserJson.Element(D(("selector", c.Selector)))),
        BrowserTypeAction t => ("type", BrowserJson.Element(D(("selector", t.Selector), ("text", t.Text), ("submit", t.Submit)))),
        BrowserScreenshotAction => ("screenshot", BrowserJson.Element(D())),
        BrowserPressAction p => ("press", BrowserJson.Element(D(("key", p.Key)))),
        BrowserScrollAction s => ("scroll", BrowserJson.Element(D(("deltaX", s.DeltaX), ("deltaY", s.DeltaY)))),
        BrowserWaitAction w => ("wait", BrowserJson.Element(D(("milliseconds", w.Milliseconds)))),
        BrowserReadTextAction => ("read_text", BrowserJson.Element(D())),
        _ => throw new NotSupportedException($"Unsupported browser action '{action.Verb}'.")
    };

    private static JsonElement? Locator(JsonElement input, bool required)
    {
        if (input.ValueKind == JsonValueKind.Object)
        {
            if (input.TryGetProperty("locator", out var locator)) return locator;
            if (input.TryGetProperty("ref", out var reference)) return BrowserJson.Element(D(("ref", reference.GetString())));
            if (input.TryGetProperty("selector", out var selector)) return selector;
        }
        if (required) throw new ArgumentException("A selector, ref, or locator is required.");
        return null;
    }

    private static string? GetReference(JsonElement input)
    {
        var locator = Locator(input, false);
        if (locator is not { } l) return null;
        if (l.ValueKind == JsonValueKind.String && l.GetString() is { } s && s.StartsWith("ref=", StringComparison.Ordinal)) return s[4..];
        return String(l, "ref");
    }

    private static string? String(JsonElement input, string key) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string RequiredString(JsonElement input, string key) => String(input, key) ?? throw new ArgumentException($"'{key}' is required.");
    private static bool Boolean(JsonElement input, string key, bool fallback = false) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var value) ? value.GetBoolean() : fallback;
    private static int Integer(JsonElement input, string key, int fallback, int min, int max)
    {
        var value = input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var property) ? property.GetInt32() : fallback;
        return value >= min && value <= max ? value : throw new ArgumentException($"'{key}' must be between {min} and {max}.");
    }
    private static double Number(JsonElement input, string key, double fallback = 0)
    {
        var value = input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var property) ? property.GetDouble() : fallback;
        return double.IsFinite(value) ? value : throw new ArgumentException($"'{key}' must be finite.");
    }
    private static Dictionary<string, object?> D(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);
    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
    private static BrowserActionResult Structured(JsonElement value)
    {
        var json = value.GetRawText();
        if (json.Length > MaxText) throw new InvalidOperationException($"Structured browser result exceeds {MaxText} characters; narrow the locator or evaluate a smaller result.");
        return new(true, Text: value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : json, StructuredOutput: json);
    }
    private static bool IsContextLost(Exception ex) => ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("navigat", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("frame", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _page.ProtocolEvent -= OnProtocolEvent;
        _lifetime.Cancel();
        _recordingCancellation?.Cancel();
        _references.Clear();
        _contexts.Clear();
        _frameOwners.Clear();
        _frameSessions.Clear();
        _downloads.Clear();
    }

    private sealed record FrameContext(string FrameId, int Id, string? Session);
    private sealed record FrameOwner(string ParentId, int? BackendId);
    private sealed record ElementReference(int Generation, string FrameId, int? BackendId);
}
