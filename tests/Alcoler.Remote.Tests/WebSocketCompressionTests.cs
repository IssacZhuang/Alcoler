using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("stream-gateway")]
public sealed class WebSocketCompressionTests
{
    private static readonly TimeSpan FrameBudget = TimeSpan.FromSeconds(15);

    private readonly GatewayHarness _harness;

    public WebSocketCompressionTests(GatewayHarness harness) => _harness = harness;

    [Fact]
    public async Task StreamHandshake_NegotiatesPermessageDeflateOnlyWhenOffered()
    {
        var (_, token) = await _harness.PairAsync();
        var path = $"api/sessions/sess_deflate_probe/stream?token={Uri.EscapeDataString(token)}";

        var offered = await UpgradeHeadersAsync(path,
            "Sec-WebSocket-Extensions: permessage-deflate; client_max_window_bits");
        Assert.StartsWith("HTTP/1.1 101", offered, StringComparison.Ordinal);
        Assert.Contains(
            "sec-websocket-extensions: permessage-deflate",
            offered,
            StringComparison.OrdinalIgnoreCase);

        var plain = await UpgradeHeadersAsync(path, extensionHeader: null);
        Assert.StartsWith("HTTP/1.1 101", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("sec-websocket-extensions", plain, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompressedStream_DeliversSnapshotAndDeltas()
    {
        var (sessionId, token) = await _harness.NewSessionAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var socket = new ClientWebSocket();
        socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        await socket.ConnectAsync(
            WebSocketFrames.ToWebSocketUri(new Uri(
                $"{_harness.BaseAddress}api/sessions/{sessionId.Value}/stream?token={Uri.EscapeDataString(token)}")),
            cancellation.Token);
        try
        {
            var snapshotFrame = await WebSocketFrames.ReceiveUntilAsync(
                socket,
                document => document.RootElement.GetProperty("type").GetString() == "snapshot",
                FrameBudget);
            Assert.NotNull(snapshotFrame);

            using var ack = await _harness.PostCommandAsync(
                sessionId, new SendTextCommand(StrongId.NewCommandId(), sessionId, BaseRevision: 0, Text: "deflate me"), token);
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

            var appendedFrame = await WebSocketFrames.ReceiveUntilAsync(
                socket, IsUserRowAppendedWithText("deflate me"), FrameBudget);
            Assert.NotNull(appendedFrame);
        }
        finally
        {
            socket.Abort();
            socket.Dispose();
        }
    }

    private static Func<JsonDocument, bool> IsUserRowAppendedWithText(string text) =>
        document =>
        {
            if (document.RootElement.GetProperty("type").GetString() != "delta")
                return false;
            foreach (var op in document.RootElement.GetProperty("delta").GetProperty("ops").EnumerateArray())
            {
                if (op.GetProperty("$type").GetString() != "row_appended")
                    continue;
                var row = op.GetProperty("row");
                if (row.GetProperty("$type").GetString() == "user_message" &&
                    row.GetProperty("text").GetString() == text)
                    return true;
            }
            return false;
        };

    private async Task<string> UpgradeHeadersAsync(string pathAndQuery, string? extensionHeader)
    {
        var baseUri = new Uri(_harness.BaseAddress);
        using var connection = new TcpClient();
        await connection.ConnectAsync(baseUri.Host, baseUri.Port);
        using var stream = connection.GetStream();
        var request = new StringBuilder()
            .Append("GET /").Append(pathAndQuery).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(baseUri.Host).Append(':').Append(baseUri.Port).Append("\r\n")
            .Append("Upgrade: websocket\r\n")
            .Append("Connection: Upgrade\r\n")
            .Append("Sec-WebSocket-Key: ").Append(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))).Append("\r\n")
            .Append("Sec-WebSocket-Version: 13\r\n");
        if (extensionHeader is not null)
            request.Append(extensionHeader).Append("\r\n");
        request.Append("\r\n");
        var payload = Encoding.ASCII.GetBytes(request.ToString());
        await stream.WriteAsync(payload);

        var buffer = new byte[8192];
        var head = new MemoryStream();
        while (Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                break;
            head.Write(buffer, 0, read);
        }
        return Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length);
    }
}
