using System.Collections.Frozen;
using System.Text.Json;
using Alcoler.Browser;

namespace Alcoler.Browser.Chromium;

public sealed class ChromiumBrowserPage : IBrowserPage
{
    // Methods any ProtocolEvent subscriber acts on: the union of
    // CdpBrowserAutomation.OnProtocolEvent and BrowserController.OnProtocolEvent.
    private static readonly FrozenSet<string> ConsumedProtocolMethods = new string[]
    {
        "Page.frameNavigated", "Page.navigatedWithinDocument", "DOM.documentUpdated",
        "Runtime.executionContextsCleared", "Page.frameDetached", "Target.attachedToTarget",
        "Target.detachedFromTarget",
        "Page.lifecycleEvent", "Host.titleChanged", "Host.loadingChanged",
        "Page.javascriptDialogOpening", "Page.javascriptDialogClosed",
        "Browser.downloadWillBegin", "Page.downloadWillBegin",
        "Browser.downloadProgress", "Page.downloadProgress",
        "Page.frameStartedLoading", "Page.frameStoppedLoading",
        "Host.closed", "Inspector.detached", "Inspector.targetCrashed"
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly CdpConnection _connection;
    private readonly string _ownSessionId;
    private readonly Lock _sync = new();
    private readonly HashSet<string> _childSessions = new(StringComparer.Ordinal);
    private readonly object _protocolEventGate = new();
    private EventHandler<BrowserProtocolEvent>? _protocolEvent;
    private EventHandler<BrowserProtocolEvent>[]? _protocolEventHandlers;
    private bool _inputActivated;
    private bool _closed;
    private bool _disposed;

    internal ChromiumBrowserPage(CdpConnection connection, string tabId, string targetId, string ownSessionId)
    {
        _connection = connection;
        _ownSessionId = ownSessionId;
        Id = tabId;
        TargetId = targetId;
        connection.RegisterSession(ownSessionId, OnConnectionEvent);
    }

    public string Id { get; }
    public string TargetId { get; }

    public event EventHandler<BrowserProtocolEvent>? ProtocolEvent
    {
        add
        {
            lock (_protocolEventGate)
            {
                _protocolEvent += value;
                _protocolEventHandlers = null;
            }
        }
        remove
        {
            lock (_protocolEventGate)
            {
                _protocolEvent -= value;
                _protocolEventHandlers = null;
            }
        }
    }

    public Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken, string? sessionId = null)
    {
        // Synthetic input into a tab that was never activated is silently dropped by Chromium once the page
        // navigated hidden, so the first input on a page activates its tab first.
        if (!_inputActivated && method.StartsWith("Input.", StringComparison.Ordinal))
        {
            _inputActivated = true;
            return ActivateThenSendAsync(method, parameters, cancellationToken, sessionId);
        }
        return SendCoreAsync(method, parameters, cancellationToken, sessionId);
    }

    private async Task<JsonElement> ActivateThenSendAsync(string method, object? parameters, CancellationToken cancellationToken, string? sessionId)
    {
        try
        {
            await _connection.SendAsync("Page.bringToFront", null, cancellationToken, _ownSessionId).ConfigureAwait(false);
            // Activation completes asynchronously; input dispatched before the page reports visible is still dropped.
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(2));
            await _connection.SendAsync("Runtime.evaluate", new Dictionary<string, object?>
            {
                ["expression"] = "new Promise(r=>document.visibilityState==='visible'?r():document.addEventListener('visibilitychange',()=>r(),{once:true}))",
                ["awaitPromise"] = true,
            }, bound.Token, _ownSessionId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return await SendCoreAsync(method, parameters, cancellationToken, sessionId).ConfigureAwait(false);
    }

    private Task<JsonElement> SendCoreAsync(string method, object? parameters, CancellationToken cancellationToken, string? sessionId) =>
        method == "Page.captureScreenshot" && parameters is Dictionary<string, object?> capture
            ? CaptureScreenshotAsync(capture, cancellationToken, sessionId)
            : _connection.SendAsync(method, parameters, cancellationToken, sessionId ?? _ownSessionId);

    // A minimized window stops producing compositor frames: viewport captures then only answer with
    // captureBeyondViewport, while clipped captures only answer without it. Normal windows answer either way well
    // inside the fallback bound, so the requested parameters are always attempted first.
    private async Task<JsonElement> CaptureScreenshotAsync(Dictionary<string, object?> parameters, CancellationToken cancellationToken, string? sessionId)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await _connection.SendAsync("Page.captureScreenshot", parameters, bounded.Token, sessionId ?? _ownSessionId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        var hasClip = parameters.ContainsKey("clip");
        var widened = new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
        {
            ["captureBeyondViewport"] = !hasClip,
        };
        return await _connection.SendAsync("Page.captureScreenshot", widened, cancellationToken, sessionId ?? _ownSessionId);
    }

    internal void OnConnectionEvent(BrowserProtocolEvent protocolEvent)
    {
        lock (_sync)
        {
            if (_disposed) return;
            // Events for the page's own session are re-raised sessionless (automation treats sessionless events as
            // main-frame, mirroring WebView2); child OOPIF session events keep the child session id.
            switch (protocolEvent.Method)
            {
                case "Target.attachedToTarget":
                    if (IsOwnOrChildRoute(protocolEvent.SessionId) && AttachedChildSession(protocolEvent.Parameters) is { } child)
                    {
                        _childSessions.Add(child);
                        _connection.RegisterSession(child, OnConnectionEvent);
                    }
                    break;
                case "Target.detachedFromTarget":
                    if (String(protocolEvent.Parameters, "sessionId") is { } retired && _childSessions.Remove(retired))
                    {
                        _connection.UnregisterSession(retired);
                    }
                    break;
            }
        }
        if (!ConsumedProtocolMethods.Contains(protocolEvent.Method)) return;
        var raisedSessionId = protocolEvent.SessionId == _ownSessionId ? null : protocolEvent.SessionId;
        Raise(new BrowserProtocolEvent(protocolEvent.Method, protocolEvent.Parameters, raisedSessionId));
    }

    private bool IsOwnOrChildRoute(string? sessionId) =>
        sessionId == _ownSessionId || sessionId is not null && _childSessions.Contains(sessionId);

    private static string? AttachedChildSession(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("targetInfo", out var info)
            && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "iframe"
            && parameters.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String
            ? session.GetString() : null;

    private static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal void OnTargetInfoChanged(string? title, string? url)
    {
        lock (_sync)
        {
            if (_disposed) return;
        }
        Raise(new BrowserProtocolEvent("Host.titleChanged",
            BrowserJson.Element(new Dictionary<string, object?> { ["title"] = title, ["url"] = url })));
    }

    internal void Close()
    {
        lock (_sync)
        {
            if (_closed || _disposed) return;
            _closed = true;
        }
        Raise(new BrowserProtocolEvent("Host.closed",
            BrowserJson.Element(new Dictionary<string, object?> { ["reason"] = "The browser tab was closed." })));
    }

    private void Raise(BrowserProtocolEvent protocolEvent)
    {
        EventHandler<BrowserProtocolEvent>[] handlers;
        lock (_protocolEventGate)
        {
            if (_protocolEvent is null) return;
            if (_protocolEventHandlers is null)
            {
                var delegates = _protocolEvent.GetInvocationList();
                var rebuilt = new EventHandler<BrowserProtocolEvent>[delegates.Length];
                for (var index = 0; index < delegates.Length; index++)
                {
                    rebuilt[index] = (EventHandler<BrowserProtocolEvent>)delegates[index];
                }
                _protocolEventHandlers = rebuilt;
            }
            handlers = _protocolEventHandlers;
        }
        foreach (var handler in handlers)
        {
            try
            {
                handler(this, protocolEvent);
            }
            catch (Exception)
            {
                // Managed subscriber failures cannot unwind the CDP receive loop.
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.UnregisterSession(_ownSessionId);
            foreach (var child in _childSessions) _connection.UnregisterSession(child);
            _childSessions.Clear();
        }
    }
}
