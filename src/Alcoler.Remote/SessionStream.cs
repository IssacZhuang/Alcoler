using System.Net.WebSockets;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Microsoft.AspNetCore.Http;

namespace Alcoler.Remote;

internal static class SessionStream
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] PingFrame = GatewayJson.Frame("ping");
    private static readonly byte[] EndedFrame = GatewayJson.Frame("ended");
    private static readonly byte[] SessionNotFoundFrame = GatewayJson.Frame("error", "session_not_found");

    public static async Task RunAsync(GatewayState state, SessionId sessionId, WebSocket socket, CancellationToken requestAborted)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        using var sendGate = new SemaphoreSlim(1, 1);
        Task? receive = null;
        Task? ping = null;
        IConversationProjection? projection = null;
        try
        {
            projection = await ResolveProjectionAsync(state.Options.Runtime(), sessionId, cancellation.Token);
            if (projection is null)
            {
                await SendAsync(socket, sendGate, SessionNotFoundFrame, CancellationToken.None);
                await CloseAsync(socket, (WebSocketCloseStatus)4404, "session_not_found");
                return;
            }
            receive = ReceiveLoopAsync(socket, cancellation);
            ping = PingLoopAsync(socket, sendGate, cancellation);
            await SendAsync(socket, sendGate,
                GatewayJson.Wrap("snapshot", "snapshot", GatewayJson.Serialize(projection.Snapshot())),
                cancellation.Token);
            await foreach (var delta in projection.Deltas(cancellation.Token).ConfigureAwait(false))
            {
                await SendAsync(socket, sendGate,
                    GatewayJson.Wrap("delta", "delta", GatewayJson.Serialize(delta)),
                    cancellation.Token);
            }
            await SendAsync(socket, sendGate, EndedFrame, cancellation.Token);
            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "ended");
        }
        catch (OperationCanceledException)
        {
            // Client disconnect or transport abort; teardown happens in finally.
        }
        catch (WebSocketException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            cancellation.Cancel();
            if (receive is not null)
                await receive;
            if (ping is not null)
                await ping;
            projection?.Dispose();
            socket.Dispose();
        }
    }

    private static async Task<IConversationProjection?> ResolveProjectionAsync(
        IAgentRuntime runtime, SessionId sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var resident = runtime.TryObserve(sessionId);
            if (resident is not null)
                return resident;
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            await runtime.ResumeSessionAsync(sessionId, cancellationToken);
            return runtime.Subscribe(sessionId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim gate, byte[] payload, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await socket.CloseAsync(status, reason, timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, CancellationTokenSource cancellation)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, cancellation.Token);
                // Client frames are ignored, except that any non-empty message ends
                // the stream: this socket is a one-way projection feed.
                if (received.MessageType == WebSocketMessageType.Close || received.Count > 0)
                {
                    cancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task PingLoopAsync(WebSocket socket, SemaphoreSlim gate, CancellationTokenSource cancellation)
    {
        using var timer = new PeriodicTimer(PingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                await SendAsync(socket, gate, PingFrame, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }
}
