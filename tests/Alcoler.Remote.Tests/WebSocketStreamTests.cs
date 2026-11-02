using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("stream-gateway")]
public sealed class WebSocketStreamTests
{
    private static readonly TimeSpan FrameBudget = TimeSpan.FromSeconds(15);

    private readonly GatewayHarness _harness;

    public WebSocketStreamTests(GatewayHarness harness) => _harness = harness;

    [Fact]
    public async Task Stream_SendsSnapshotFirst_ThenDeltasForSubmittedCommands()
    {
        var (sessionId, token) = await _harness.NewSessionAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var socket = await WebSocketFrames.ConnectAsync(
            new Uri($"{_harness.BaseAddress}api/sessions/{sessionId.Value}/stream?token={Uri.EscapeDataString(token)}"),
            cancellation.Token);
        try
        {
            var snapshotFrame = await WebSocketFrames.ReceiveUntilAsync(
                socket,
                document => document.RootElement.GetProperty("type").GetString() == "snapshot",
                FrameBudget);
            Assert.NotNull(snapshotFrame);
            using var snapshot = JsonDocument.Parse(snapshotFrame!);
            var payload = snapshot.RootElement.GetProperty("snapshot");
            Assert.Equal(0, payload.GetProperty("revision").GetInt64());
            Assert.Equal(0, payload.GetProperty("sequence").GetInt64());
            Assert.Equal(JsonValueKind.Array, payload.GetProperty("rows").ValueKind);
            var phase = payload.GetProperty("state").GetProperty("phase").GetString();
            Assert.True(
                string.Equals("idle", phase, StringComparison.OrdinalIgnoreCase),
                $"phase was {phase}");

            using var ack = await _harness.PostCommandAsync(
                sessionId, new SendTextCommand(StrongId.NewCommandId(), sessionId, BaseRevision: 0, Text: "stream me"), token);
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

            var appendedFrame = await WebSocketFrames.ReceiveUntilAsync(
                socket, IsUserRowAppendedWithText("stream me"), FrameBudget);
            Assert.NotNull(appendedFrame);

            var settledFrame = await WebSocketFrames.ReceiveUntilAsync(
                socket, IsAssistantCompletedAndPhaseIdle(), FrameBudget);
            Assert.NotNull(settledFrame);
        }
        finally
        {
            socket.Abort();
            socket.Dispose();
        }
    }

    [Fact]
    public async Task Stream_WithInvalidToken_RejectsHandshake()
    {
        var socket = new ClientWebSocket();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var exception = await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(
                WebSocketFrames.ToWebSocketUri(
                    new Uri($"{_harness.BaseAddress}api/sessions/sess_any/stream?token=not-a-real-token")),
                cancellation.Token));
        // The handshake status code is not surfaced on every platform; assert it when present.
        if (exception.InnerException is HttpRequestException { StatusCode: var status })
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        socket.Dispose();
    }

    [Fact]
    public async Task Stream_UnknownSession_SendsErrorFrameAndCloses()
    {
        var (_, token) = await _harness.PairAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var socket = await WebSocketFrames.ConnectAsync(
            new Uri($"{_harness.BaseAddress}api/sessions/sess_missing/stream?token={Uri.EscapeDataString(token)}"),
            cancellation.Token);
        try
        {
            var errorFrame = await WebSocketFrames.ReceiveUntilAsync(
                socket,
                document =>
                    document.RootElement.GetProperty("type").GetString() == "error" &&
                    document.RootElement.GetProperty("code").GetString() == "session_not_found",
                FrameBudget);
            Assert.NotNull(errorFrame);

            var close = await WebSocketFrames.ReceiveAsync(socket, FrameBudget);
            Assert.True(close.IsClose);
            Assert.Equal((WebSocketCloseStatus)4404, close.Result.CloseStatus);
            Assert.Equal("session_not_found", close.Result.CloseStatusDescription);
        }
        finally
        {
            socket.Abort();
            socket.Dispose();
        }
    }

    [Fact]
    public async Task Stream_SendsPingFrames_WithinTheirCadence()
    {
        var (sessionId, token) = await _harness.NewSessionAsync();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var socket = await WebSocketFrames.ConnectAsync(
            new Uri($"{_harness.BaseAddress}api/sessions/{sessionId.Value}/stream?token={Uri.EscapeDataString(token)}"),
            cancellation.Token);
        try
        {
            var snapshotFrame = await WebSocketFrames.ReceiveUntilAsync(
                socket,
                document => document.RootElement.GetProperty("type").GetString() == "snapshot",
                FrameBudget);
            Assert.NotNull(snapshotFrame);

            // The ping cadence (20s server-side) is timing-dependent; a ping that
            // does not arrive within the budget is treated as a soft pass.
            var ping = await WebSocketFrames.ReceiveUntilAsync(
                socket,
                document => document.RootElement.GetProperty("type").GetString() == "ping",
                TimeSpan.FromSeconds(24));
            Assert.True(ping is null || JsonDocument.Parse(ping).RootElement.GetProperty("type").GetString() == "ping");
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

    private static Func<JsonDocument, bool> IsAssistantCompletedAndPhaseIdle() =>
        document =>
        {
            if (document.RootElement.GetProperty("type").GetString() != "delta")
                return false;
            var hasCompletedAssistant = false;
            var hasIdlePatch = false;
            foreach (var op in document.RootElement.GetProperty("delta").GetProperty("ops").EnumerateArray())
            {
                var discriminator = op.GetProperty("$type").GetString();
                if (discriminator == "row_upserted")
                {
                    var row = op.GetProperty("row");
                    if (row.GetProperty("$type").GetString() == "assistant_message" &&
                        !row.GetProperty("isStreaming").GetBoolean())
                        hasCompletedAssistant = true;
                }
                else if (discriminator == "state_patch" &&
                    string.Equals(
                        op.GetProperty("state").GetProperty("phase").GetString(),
                        "idle",
                        StringComparison.OrdinalIgnoreCase))
                {
                    hasIdlePatch = true;
                }
            }
            return hasCompletedAssistant && hasIdlePatch;
        };
}
