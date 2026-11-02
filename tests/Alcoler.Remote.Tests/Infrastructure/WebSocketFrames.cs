using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Alcoler.Remote.Tests.Infrastructure;

internal readonly record struct WebSocketFrame(string? Text, WebSocketReceiveResult Result)
{
    public bool IsClose => Result.MessageType == WebSocketMessageType.Close;
}

internal static class WebSocketFrames
{
    public static async Task<ClientWebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(ToWebSocketUri(uri), cancellationToken);
        return socket;
    }

    // ClientWebSocket refuses http(s) schemes; gateway base addresses are http.
    public static Uri ToWebSocketUri(Uri uri) =>
        uri.Scheme is "http" or "https"
            ? new UriBuilder(uri) { Scheme = uri.Scheme == "http" ? "ws" : "wss" }.Uri
            : uri;

    public static async Task<WebSocketFrame> ReceiveAsync(ClientWebSocket socket, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        return await ReceiveAsync(socket, cancellation.Token);
    }

    public static async Task<WebSocketFrame> ReceiveAsync(
        ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return new WebSocketFrame(null, received);
            message.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
                return new WebSocketFrame(Encoding.UTF8.GetString(message.ToArray()), received);
        }
    }

    // Returns the first frame matching the predicate within the budget, or null when
    // the budget expires or the socket closes. Receiving is never cancelled while a
    // frame is pending; the caller aborts the socket afterwards.
    public static async Task<string?> ReceiveUntilAsync(
        ClientWebSocket socket, Func<JsonDocument, bool> predicate, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return null;
            var frame = await ReceiveAsync(socket, remaining);
            if (frame.Text is null)
                return null;
            using var document = JsonDocument.Parse(frame.Text);
            if (predicate(document))
                return frame.Text;
        }
    }
}
