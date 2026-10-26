using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Alcoler.Browser;

namespace Alcoler.Browser.Chromium;

public interface ICdpWire : IDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Returns a whole text message, or null when the peer closed the wire.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

public sealed class WebSocketCdpWire : ICdpWire
{
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _receiveBuffer = new byte[16 * 1024];

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => _socket.ConnectAsync(uri, cancellationToken);

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(_receiveBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                // A transport reset between open and close handshake is an ordinary termination for CDP.
                throw new IOException("The browser WebSocket connection reset.", ex);
            }
            if (received.MessageType == WebSocketMessageType.Close) return null;
            if (received.Count > 0) message.Write(_receiveBuffer, 0, received.Count);
            if (received.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by Alcoler.", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void Dispose() => _socket.Dispose();
}

public sealed class CdpConnection : IDisposable
{
    private static readonly HashSet<string> BroadcastMethods = new(StringComparer.Ordinal)
    {
        "Browser.downloadWillBegin",
        "Browser.downloadProgress",
    };

    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _drainWindow;
    private readonly Lock _sync = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly Dictionary<string, Action<BrowserProtocolEvent>> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ICdpWire? _wire;
    private Task? _receiveLoop;
    private long _nextId;
    private bool _closed;

    public CdpConnection(TimeSpan? commandTimeout = null, TimeSpan? drainWindow = null)
    {
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(45);
        _drainWindow = drainWindow ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Connection-level handler for events delivered without a session id (browser target).</summary>
    public Action<BrowserProtocolEvent>? BrowserEvent { get; set; }

    public async Task ConnectAsync(ICdpWire wire, Uri uri, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _wire = wire;
        await wire.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        _receiveLoop = Task.Run(ReceiveLoopAsync, CancellationToken.None);
    }

    public void RegisterSession(string sessionId, Action<BrowserProtocolEvent> handler)
    {
        lock (_sync) _sessions[sessionId] = handler;
    }

    public void UnregisterSession(string sessionId)
    {
        lock (_sync) _sessions.Remove(sessionId);
    }

    public Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken, string? sessionId = null) =>
        SendAsync(method, parameters, cancellationToken, sessionId, drainOnFailure: true);

    private async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken,
        string? sessionId, bool drainOnFailure)
    {
        var wire = _wire ?? throw new InvalidOperationException("The CDP connection is not connected.");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_closed) throw new InvalidOperationException("The CDP connection is closed.");
            _pending[id] = completion;
        }
        var frame = Encode(id, method, parameters, sessionId);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await wire.SendTextAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
        try
        {
            return await completion.Task.WaitAsync(_commandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            if (drainOnFailure) await DrainCanceledCommandAsync(id, method, sessionId, completion).ConfigureAwait(false);
            else
            {
                lock (_sync)
                {
                    _pending.Remove(id);
                    completion.TrySetCanceled();
                }
            }
            throw;
        }
    }

    private async Task DrainCanceledCommandAsync(long id, string method, string? sessionId,
        TaskCompletionSource<JsonElement> completion)
    {
        if (completion.Task.IsCompleted)
        {
            lock (_sync) _pending.Remove(id);
            return;
        }
        // A canceled command may still be executing in the renderer; terminating execution lets its response arrive,
        // which frees the browser-side command slot before the caller retries on the same session.
        if (sessionId is not null && method != "Runtime.terminateExecution")
        {
            try
            {
                using var bound = new CancellationTokenSource(_drainWindow);
                await SendAsync("Runtime.terminateExecution", null, bound.Token, sessionId, drainOnFailure: false)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort only: the drain window below still bounds the pending entry.
            }
        }
        try
        {
            await completion.Task.WaitAsync(_drainWindow).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The late response is dropped; the entry must not leak past the window.
        }
        lock (_sync)
        {
            _pending.Remove(id);
            completion.TrySetCanceled();
        }
    }

    private static string Encode(long id, string method, object? parameters, string? sessionId)
    {
        var envelope = new Dictionary<string, object?>(4)
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters is null ? BrowserJson.Element(new Dictionary<string, object?>()) : BrowserJson.Element(parameters),
        };
        if (sessionId is not null) envelope["sessionId"] = sessionId;
        return BrowserJson.Serialize(envelope);
    }

    private async Task ReceiveLoopAsync()
    {
        var wire = _wire!;
        while (true)
        {
            string? text;
            try
            {
                text = await wire.ReceiveTextAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                FailPending(new InvalidOperationException($"The Chromium CDP connection failed: {ex.Message}", ex));
                break;
            }
            if (text is null)
            {
                FailPending(new InvalidOperationException("The Chromium CDP connection closed before the command completed."));
                break;
            }
            try
            {
                ProcessMessage(text);
            }
            catch (JsonException)
            {
                // A malformed frame must not kill the loop.
            }
        }
    }

    private void ProcessMessage(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
        {
            CompletePending(id, root);
            return;
        }
        if (root.TryGetProperty("method", out var methodElement) && methodElement.GetString() is { } method)
        {
            DispatchEvent(method, root);
        }
    }

    private void CompletePending(long id, JsonElement root)
    {
        TaskCompletionSource<JsonElement>? completion;
        lock (_sync)
        {
            _pending.Remove(id, out completion);
        }
        if (completion is null) return;
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text)
                && text.GetString() is { Length: > 0 } details ? details : "The CDP command failed.";
            completion.TrySetException(new InvalidOperationException(message));
            return;
        }
        var result = root.TryGetProperty("result", out var value) ? value.Clone() : BrowserJson.Element(new Dictionary<string, object?>());
        completion.TrySetResult(result);
    }

    private void DispatchEvent(string method, JsonElement root)
    {
        var parameters = root.TryGetProperty("params", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Clone() : BrowserJson.Element(new Dictionary<string, object?>());
        var sessionId = root.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String
            ? session.GetString() : null;
        var protocolEvent = new BrowserProtocolEvent(method, parameters, sessionId);
        // Download events are broadcast to every page session: they carry the owning session in params, and each
        // page's automation deduplicates by download guid.
        if (BroadcastMethods.Contains(method))
        {
            Action<BrowserProtocolEvent>[] handlers;
            lock (_sync) handlers = [.. _sessions.Values];
            foreach (var handler in handlers) TryInvoke(handler, protocolEvent);
            return;
        }
        if (sessionId is not null)
        {
            Action<BrowserProtocolEvent>? handler;
            lock (_sync) _sessions.TryGetValue(sessionId, out handler);
            if (handler is not null) TryInvoke(handler, protocolEvent);
            return;
        }
        if (BrowserEvent is { } browser) TryInvoke(browser, protocolEvent);
    }

    private static void TryInvoke(Action<BrowserProtocolEvent> handler, BrowserProtocolEvent protocolEvent)
    {
        try
        {
            handler(protocolEvent);
        }
        catch (Exception)
        {
            // A subscriber failure must not unwind the receive loop.
        }
    }

    private void FailPending(Exception failure)
    {
        TaskCompletionSource<JsonElement>[] pending;
        lock (_sync)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var completion in pending) completion.TrySetException(failure);
    }

    public async Task CloseAsync()
    {
        Task? loop;
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            loop = _receiveLoop;
        }
        var wire = _wire;
        if (wire is not null)
        {
            try
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await wire.CloseAsync(close.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Closing a dead wire is expected during process teardown.
            }
        }
        _lifetime.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (Exception) { }
        }
        FailPending(new InvalidOperationException("The Chromium CDP connection closed before the command completed."));
        wire?.Dispose();
    }

    public void Dispose()
    {
        if (_closed)
        {
            _lifetime.Dispose();
            return;
        }
        CloseAsync().GetAwaiter().GetResult();
        _lifetime.Dispose();
    }
}
