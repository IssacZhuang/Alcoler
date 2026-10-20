using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcoler.Adapters.Mcp;

internal sealed class McpServerConnection : IAsyncDisposable
{
    private const int MaxListPages = 1000;

    private readonly IMcpTransport _transport;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _receiveLoopEndedCts = new();
    private readonly CancellationTokenSource _disposedCts = new();
    private Task _receiveLoop = Task.CompletedTask;
    private int _receiveLoopStarted;
    private int _disposed;
    private long _nextId;
    private volatile bool _initialized;

    public McpServerConnection(string serverName, IMcpTransport transport, ILogger? logger = null)
    {
        ServerName = serverName ?? throw new ArgumentNullException(nameof(serverName));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logger = logger ?? NullLogger.Instance;
    }

    public string ServerName { get; }

    public string? ProtocolVersion { get; private set; }

    public McpImplementation? ServerInfo { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_initialized)
            return;
        var parameters = new McpInitializeParams(
            McpProtocol.LatestVersion,
            new McpClientCapabilities(),
            new McpImplementation(McpProtocol.ClientName, McpProtocol.ClientVersion));
        var response = await SendRequestAsync(McpProtocol.InitializeMethod, parameters, cancellationToken).ConfigureAwait(false);
        var initialize = response.Deserialize(McpJsonContext.Default.McpInitializeResult)
            ?? throw new McpProtocolException($"MCP server '{ServerName}' returned an unreadable initialize result.");
        ProtocolVersion = NegotiateProtocolVersion(initialize.ProtocolVersion);
        ServerInfo = initialize.ServerInfo;
        _initialized = true;
        await SendNotificationAsync(McpProtocol.InitializedNotification, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<McpToolDefinition>();
        string? cursor = null;
        for (var page = 0; page < MaxListPages; page++)
        {
            var response = await SendRequestAsync(
                McpProtocol.ListToolsMethod,
                new McpToolListParams(cursor),
                cancellationToken).ConfigureAwait(false);
            var listed = response.Deserialize(McpJsonContext.Default.McpToolListResult)
                ?? throw new McpProtocolException($"MCP server '{ServerName}' returned an unreadable tools/list result.");
            if (listed.Tools is not null)
            {
                foreach (var tool in listed.Tools)
                {
                    if (string.IsNullOrWhiteSpace(tool.Name))
                    {
                        _logger.LogWarning("Skipping a tool from MCP server '{ServerName}' because it has no name.", ServerName);
                        continue;
                    }
                    tools.Add(tool);
                }
            }
            cursor = listed.NextCursor;
            if (cursor is null)
                return tools;
        }
        throw new McpProtocolException($"MCP server '{ServerName}' exceeded {MaxListPages} tools/list pages.");
    }

    public async Task<McpToolCallOutcome> CallToolAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            McpProtocol.CallToolMethod,
            new McpToolCallParams(name, arguments),
            cancellationToken).ConfigureAwait(false);
        var called = response.Deserialize(McpJsonContext.Default.McpToolCallResult)
            ?? throw new McpProtocolException($"MCP server '{ServerName}' returned an unreadable tools/call result.");
        var textParts = new List<string>();
        if (called.Content is not null)
        {
            foreach (var part in called.Content)
            {
                if (part is { Type: "text", Text: not null })
                    textParts.Add(part.Text);
                else
                    _logger.LogDebug(
                        "Ignoring a content part of type '{Type}' from MCP server '{ServerName}'; only text is supported.",
                        part.Type,
                        ServerName);
            }
        }
        return new McpToolCallOutcome(textParts, called.IsError == true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _disposedCts.Cancel();
        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing the transport of MCP server '{ServerName}' failed.", ServerName);
        }
        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The receive loop of MCP server '{ServerName}' faulted.", ServerName);
        }
        FailPending(new McpTransportException($"MCP server '{ServerName}' connection was disposed."));
        _receiveLoopEndedCts.Cancel();
        _receiveLoopEndedCts.Dispose();
        _disposedCts.Dispose();
    }

    private string NegotiateProtocolVersion(string? serverVersion)
    {
        if (serverVersion is McpProtocol.LatestVersion or McpProtocol.FallbackVersion)
            return serverVersion;
        if (!string.IsNullOrEmpty(serverVersion))
            _logger.LogWarning(
                "MCP server '{ServerName}' reported unsupported protocol version '{ServerVersion}'; falling back to '{FallbackVersion}'.",
                ServerName,
                serverVersion,
                McpProtocol.FallbackVersion);
        return McpProtocol.FallbackVersion;
    }

    private async Task<JsonElement> SendRequestAsync<T>(string method, T parameters, CancellationToken cancellationToken)
        where T : class
    {
        ThrowIfDisposed();
        var id = Interlocked.Increment(ref _nextId);
        var key = id.ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, completion))
            throw new McpProtocolException($"MCP server '{ServerName}' reused request id {key}.");
        // The receive loop must only start after the entry is tracked, so an immediately failing
        // transport faults this request with the underlying cause instead of a generic closure.
        StartReceiveLoop();
        var paramsElement = JsonSerializer.SerializeToElement(parameters, typeof(T), McpJsonContext.Default);
        var payload = JsonSerializer.Serialize(
            new JsonRpcRequest(McpProtocol.JsonRpcVersion, id, method, paramsElement),
            McpJsonContext.Default.JsonRpcRequest);
        try
        {
            await _transport.SendAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(key, out _);
            throw;
        }
        catch (Exception ex)
        {
            _pending.TryRemove(key, out _);
            throw new McpTransportException($"Could not send '{method}' to MCP server '{ServerName}': {ex.Message}", ex);
        }
        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _receiveLoopEndedCts.Token,
                _disposedCts.Token);
            var response = await completion.Task.WaitAsync(waitCts.Token).ConfigureAwait(false);
            if (response.TryGetProperty("error", out var error))
                throw CreateRemoteException(method, error);
            if (!response.TryGetProperty("result", out var result))
                throw new McpProtocolException($"MCP server '{ServerName}' returned neither result nor error for '{method}'.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && completion.Task.IsFaulted)
        {
            // The transport failure raced the loop-ended cancellation; surface the root cause.
            throw completion.Task.Exception!.GetBaseException();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpTransportException($"MCP server '{ServerName}' closed the connection before '{method}' completed.");
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private async Task SendNotificationAsync(string method, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        StartReceiveLoop();
        var payload = JsonSerializer.Serialize(
            new JsonRpcNotification(McpProtocol.JsonRpcVersion, method),
            McpJsonContext.Default.JsonRpcNotification);
        try
        {
            await _transport.SendAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpTransportException($"Could not send '{method}' to MCP server '{ServerName}': {ex.Message}", ex);
        }
    }

    private McpRemoteException CreateRemoteException(string method, JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed) ? parsed : 0;
        var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : null;
        return new McpRemoteException(
            $"MCP server '{ServerName}' rejected '{method}' with error {code}: {message ?? "no message"}.",
            code);
    }

    private void StartReceiveLoop()
    {
        if (Interlocked.Exchange(ref _receiveLoopStarted, 1) == 0)
            _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await _transport.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FailPending(new McpTransportException($"MCP server '{ServerName}' transport failed: {ex.Message}", ex));
                    break;
                }
                if (line is null)
                {
                    FailPending(new McpTransportException($"MCP server '{ServerName}' closed the connection."));
                    break;
                }
                line = line.TrimEnd('\r');
                if (line.Length == 0)
                    continue;
                Dispatch(line);
            }
        }
        finally
        {
            _receiveLoopEndedCts.Cancel();
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring a malformed message from MCP server '{ServerName}'.", ServerName);
            return;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                _logger.LogWarning("Ignoring a non-object message from MCP server '{ServerName}'.", ServerName);
                return;
            }
            if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind is JsonValueKind.String)
            {
                if (root.TryGetProperty("id", out _))
                    _logger.LogWarning(
                        "Ignoring server-initiated request '{Method}' from MCP server '{ServerName}'; not supported in this phase.",
                        methodElement.GetString(),
                        ServerName);
                else
                    _logger.LogDebug(
                        "Ignoring notification '{Method}' from MCP server '{ServerName}'.",
                        methodElement.GetString(),
                        ServerName);
                return;
            }
            if (root.TryGetProperty("id", out var idElement))
            {
                var key = ResponseKey(idElement);
                if (_pending.TryGetValue(key, out var completion))
                    completion.TrySetResult(root.Clone());
                else
                    _logger.LogDebug(
                        "Ignoring a response from MCP server '{ServerName}' with unknown id '{Id}'.",
                        ServerName,
                        key);
            }
        }
    }

    private static string ResponseKey(JsonElement id) =>
        id.ValueKind is JsonValueKind.String ? id.GetString()! : id.GetRawText();

    private void FailPending(McpTransportException exception)
    {
        foreach (var entry in _pending)
            entry.Value.TrySetException(exception);
        _pending.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
            throw new ObjectDisposedException(nameof(McpServerConnection), $"MCP server '{ServerName}' connection is disposed.");
    }
}
