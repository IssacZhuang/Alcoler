using System.Net.WebSockets;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;

namespace Alcoler.Remote;

internal static class SessionStream
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] PingFrame = GatewayJson.Frame("ping");
    private static readonly byte[] EndedFrame = GatewayJson.Frame("ended");
    private static readonly byte[] SessionNotFoundFrame = GatewayJson.Frame("error", "session_not_found");

    public static async Task RunAsync(
        GatewayState state, SessionId sessionId, WebSocket socket, CancellationToken requestAborted,
        TimeSpan? pingInterval = null, TimeSpan? sendTimeout = null, bool appendText = false, bool compact = false)
    {
        var writeTimeout = sendTimeout ?? SendTimeout;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        using var sendGate = new SemaphoreSlim(1, 1);
        Task? receive = null;
        Task? ping = null;
        IConversationProjection? projection = null;
        IDisposable? registration = null;
        try
        {
            projection = await ResolveProjectionAsync(state.Options.Runtime(), sessionId, cancellation.Token);
            if (projection is null)
            {
                await SendAsync(socket, sendGate, SessionNotFoundFrame, cancellation.Token, writeTimeout);
                await CloseAsync(socket, sendGate, (WebSocketCloseStatus)4404, "session_not_found");
                return;
            }
            registration = state.Streams.Register(sessionId, projection);
            receive = ReceiveLoopAsync(socket, sendGate, cancellation);
            var snapshot = projection.Snapshot();
            var compactRows = compact ? new CompactTranscript(snapshot) : null;
            var legacyRows = appendText || compact ? null : new LegacyRows(snapshot);
            await SendAsync(socket, sendGate,
                compactRows?.SnapshotFrame(snapshot.Revision, snapshot.Sequence) ?? GatewayJson.SnapshotFrame(snapshot),
                cancellation.Token, writeTimeout);
            ping = PingLoopAsync(socket, sendGate, cancellation, pingInterval ?? PingInterval, writeTimeout);
            var lastSequence = snapshot.Sequence;
            await foreach (var delta in projection.Deltas(cancellation.Token).ConfigureAwait(false))
            {
                if (delta.Sequence <= lastSequence)
                    continue;
                await SendAsync(socket, sendGate,
                    compactRows?.Apply(delta, appendText) ?? GatewayJson.DeltaFrame(legacyRows?.Apply(delta) ?? delta),
                    cancellation.Token, writeTimeout);
                lastSequence = delta.Sequence;
            }
            await SendAsync(socket, sendGate, EndedFrame, cancellation.Token, writeTimeout);
            await CloseAsync(socket, sendGate, WebSocketCloseStatus.NormalClosure, "ended");
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
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                cancellation.Cancel();
                await Task.WhenAll(receive ?? Task.CompletedTask, ping ?? Task.CompletedTask);
            }
            finally
            {
                try
                {
                    registration?.Dispose();
                    projection?.Dispose();
                }
                finally
                {
                    socket.Dispose();
                }
            }
        }
    }

    private sealed class LegacyRows(ConversationSnapshot snapshot)
    {
        private readonly Dictionary<string, AssistantMessageRow> _rows = snapshot.Rows
            .OfType<AssistantMessageRow>()
            .Where(row => row.IsStreaming)
            .ToDictionary(row => row.RowId, StringComparer.Ordinal);

        public ConversationDelta Apply(ConversationDelta delta)
        {
            List<RowOp>? translated = null;
            for (var index = 0; index < delta.Ops.Count; index++)
            {
                var op = delta.Ops[index];
                switch (op)
                {
                    case RowAppended { Row: AssistantMessageRow appendedRow }:
                        Remember(appendedRow);
                        break;
                    case RowUpserted { Row: AssistantMessageRow upsertedRow }:
                        Remember(upsertedRow);
                        break;
                    case RowRemoved removed:
                        _rows.Remove(removed.RowId);
                        break;
                    case RowTextAppended appended:
                        if (!_rows.TryGetValue(appended.RowId, out var current)
                            || current.Parts is not [TextPart text])
                            throw new InvalidOperationException("An appended text operation requires its streaming row baseline.");
                        var updated = current with { Parts = [text with { Text = text.Text + appended.Delta }], At = appended.At };
                        _rows[updated.RowId] = updated;
                        if (translated is null)
                        {
                            translated = new List<RowOp>(delta.Ops.Count);
                            for (var previous = 0; previous < index; previous++) translated.Add(delta.Ops[previous]);
                        }
                        op = new RowUpserted(updated);
                        break;
                }
                translated?.Add(op);
            }
            return translated is null ? delta : delta with { Ops = translated };
        }

        private void Remember(AssistantMessageRow row)
        {
            if (row.IsStreaming) _rows[row.RowId] = row;
            else _rows.Remove(row.RowId);
        }
    }

    internal static async Task<IConversationProjection?> ResolveProjectionAsync(
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

    private static async Task SendAsync(
        WebSocket socket, SemaphoreSlim gate, byte[] payload, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var entered = false;
        try
        {
            await gate.WaitAsync(deadline.Token);
            entered = true;
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Abort();
            throw;
        }
        finally
        {
            if (entered)
                gate.Release();
        }
    }

    private static async Task CloseAsync(WebSocket socket, SemaphoreSlim gate, WebSocketCloseStatus status, string reason)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await gate.WaitAsync(timeout.Token);
            try
            {
                // CloseAsync would start a second receive alongside ReceiveLoopAsync.
                await socket.CloseOutputAsync(status, reason, timeout.Token);
            }
            finally
            {
                gate.Release();
            }
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
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket, SemaphoreSlim gate, CancellationTokenSource cancellation)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, cancellation.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    cancellation.Cancel();
                    await CloseAsync(socket, gate, WebSocketCloseStatus.NormalClosure, "closed");
                    return;
                }
                // This socket is a one-way projection feed; non-empty client frames end it.
                if (received.Count > 0)
                    return;
            }
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
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private static async Task PingLoopAsync(
        WebSocket socket, SemaphoreSlim gate, CancellationTokenSource cancellation, TimeSpan interval, TimeSpan sendTimeout)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                await SendAsync(socket, gate, PingFrame, cancellation.Token, sendTimeout);
            }
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
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation.Cancel();
        }
    }
}
