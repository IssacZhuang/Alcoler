using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Alcoler.Workbench.Auth;

/// <summary>
/// Loopback OAuth callback server on 127.0.0.1 with an ephemeral port. The
/// redirect host and path are fixed by the SIWC spec; only the port may vary.
/// Built on a raw TcpListener so no platform HTTP listener ACLs are required.
/// </summary>
internal sealed class LoopbackCallbackListener : IAsyncDisposable
{
    private const string SuccessText = "Authorization successful. You may close this window and return to Alcoler.";
    private const string FailureText = "Authorization failed. You may close this window and return to Alcoler.";

    private readonly TcpListener _listener;
    private readonly string _callbackPath;
    private readonly string _expectedState;
    private readonly TaskCompletionSource<string> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private bool _settled;

    public string CallbackUrl { get; }

    private LoopbackCallbackListener(TcpListener listener, string callbackPath, string expectedState, int port)
    {
        _listener = listener;
        _callbackPath = callbackPath;
        _expectedState = expectedState;
        CallbackUrl = $"http://{SiwcProtocol.CallbackHost}:{port}{callbackPath}";
    }

    public static async Task<LoopbackCallbackListener> StartAsync(string callbackPath, string expectedState, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Parse(SiwcProtocol.CallbackHost), 0);
        listener.Start(backlog: 4);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var instance = new LoopbackCallbackListener(listener, callbackPath, expectedState, port);
        _ = Task.Run(() => instance.AcceptLoopAsync(), ct);
        return instance;
    }

    /// <summary>
    /// Resolves with the full callback URL of the authorization response. A
    /// mismatched state only answers 400 and keeps waiting: stale browser tabs
    /// and prefetches must not kill the transaction.
    /// </summary>
    public Task<string> WaitForCallbackAsync(CancellationToken ct) => _callback.Task.WaitAsync(ct);

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested && !_settled)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => HandleConnectionAsync(client), _stopping.Token);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var requestTarget = await ReadRequestTargetAsync(client.GetStream(), timeout.Token);
                if (requestTarget is null)
                {
                    await RespondAsync(client, 400, FailureText);
                    return;
                }
                var url = new Uri($"http://{SiwcProtocol.CallbackHost}{requestTarget}");
                if (url.AbsolutePath != _callbackPath)
                {
                    await RespondAsync(client, 404, FailureText);
                    return;
                }
                var query = SiwcProtocol.ParseQuery(url);
                if (query.GetValueOrDefault("state") != _expectedState)
                {
                    await RespondAsync(client, 400, FailureText);
                    return;
                }
                if (query.GetValueOrDefault("error") is { Length: > 0 } oauthError)
                {
                    await RespondAsync(client, 400, FailureText);
                    Settle(new SiwcCallbackDeniedException(oauthError));
                    return;
                }
                if (!query.ContainsKey("code") && !query.ContainsKey("authCode"))
                {
                    // A state match without code or error can never produce a
                    // token; fail the transaction instead of burning the window.
                    await RespondAsync(client, 400, FailureText);
                    Settle(new SiwcProtocolException("missing-code", "OAuth callback is missing an authorization code."));
                    return;
                }
                await RespondAsync(client, 200, SuccessText);
                Settle(null, url.ToString());
            }
            catch (Exception)
            {
                // A malformed connection never settles the transaction.
            }
        }
    }

    private void Settle(Exception? failure, string? url = null)
    {
        if (_settled) return;
        _settled = true;
        if (failure is not null)
            _callback.TrySetException(failure);
        else
            _callback.TrySetResult(url!);
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[2048];
        var head = new StringBuilder();
        while (head.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var requestLine = head.ToString().Split("\r\n", 2)[0].Split(' ');
        return requestLine.Length == 3 && requestLine[0] == "GET" ? requestLine[1] : null;
    }

    private static async Task RespondAsync(TcpClient client, int status, string body)
    {
        try
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            var stream = client.GetStream();
            await stream.WriteAsync(head, CancellationToken.None);
            await stream.WriteAsync(payload, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // The browser may have gone away; the decision has already been made.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested) return ValueTask.CompletedTask;
        _settled = true;
        _callback.TrySetCanceled();
        _stopping.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
