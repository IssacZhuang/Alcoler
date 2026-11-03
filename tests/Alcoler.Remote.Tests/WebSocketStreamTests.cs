using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Kernel.Runtime;
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
    public void DirectFrames_AreByteIdenticalToLegacyWrappingWithDeclaredPolymorphicContracts()
    {
        var at = new DateTimeOffset(2026, 11, 3, 12, 34, 56, TimeSpan.FromHours(-7));
        var callId = new ToolCallId("call_exact");
        var input = JsonSerializer.SerializeToElement(new { text = "中文 <>&\"\\\r\n", nullable = (string?)null });
        ToolResultBlock[] output = [new ToolResultText("text <>&"), new ToolResultMedia("alcoler-artifact://s/file", "video/webm", null)];
        ConversationRow[] rows =
        [
            new UserMessageRow("user", "Unicode 中文 <>&\r\n", at, [new FilePart(new PartId("image"), "alcoler-artifact://s/image", "image/png")], true),
            new AssistantMessageRow("assistant", new MessageId("message"),
            [
                new TextPart(new PartId("text"), new string('x', 131072) + " <>&中文"),
                new ReasoningPart(new PartId("reasoning"), "thinking", ProviderSignature: "signature"),
                new ToolCallPart(new PartId("call"), callId, "Browser", input),
                new ToolResultPart(new PartId("result"), callId, output, StructuredOutput: "{\"exact\":true}"),
                new FilePart(new PartId("file"), "alcoler-artifact://s/file", "video/webm", "original.webm"),
                new AttachmentPart(new PartId("attachment"), AttachmentKind.SystemReminder, "reminder"),
            ], false, at, true),
            new ToolCallRow("tool", callId, "Browser", input, ToolCallStatus.Succeeded, output, false, TimeSpan.FromMilliseconds(1234), at),
            new FileChangeSummaryRow("changes", [new FileChangeSummaryItem("中文.cs", 3, 2)], 3, 2),
            new SystemNoticeRow("notice", "info", "notice", null),
        ];
        var state = ConversationState.Initial("title <>&中文", PermissionMode.Build, null, 200000);
        var snapshot = new ConversationSnapshot(123, 456, rows, state);
        var delta = new ConversationDelta(457,
        [
            new RowAppended(rows[0]), new RowUpserted(rows[1]),
            new RowTextAppended("assistant", " appended\r\n中文", at.AddMilliseconds(1)),
            new RowRemoved("removed"), new StatePatched(state),
        ]);
        Assert.Equal(LegacyFrame("snapshot", snapshot), GatewayJson.SnapshotFrame(snapshot));
        Assert.Equal(LegacyFrame("delta", delta), GatewayJson.DeltaFrame(delta));
        using var frame = JsonDocument.Parse(GatewayJson.DeltaFrame(delta));
        var append = frame.RootElement.GetProperty("delta").GetProperty("ops")[2];
        Assert.Equal("row_text_appended", append.GetProperty("$type").GetString());
        Assert.Equal(" appended\r\n中文", append.GetProperty("delta").GetString());
    }

    [Fact]
    public void DirectFrames_AvoidPayloadAndWrappingAllocationCopies()
    {
        var snapshot = new ConversationSnapshot(0, 1,
            [new SystemNoticeRow("large", "info", new string('x', 131072))],
            ConversationState.Initial("allocation", PermissionMode.Yolo, null, 200000));
        _ = GatewayJson.SnapshotFrame(snapshot);
        _ = LegacyFrame("snapshot", snapshot);
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 16; index++) _ = GatewayJson.SnapshotFrame(snapshot);
        var direct = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 16; index++) _ = LegacyFrame("snapshot", snapshot);
        var legacy = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(direct < legacy / 2, $"Direct frames allocated {direct} bytes versus {legacy} legacy bytes.");
    }

    private static byte[] LegacyFrame<T>(string type, T payload) => GatewayJson.Object(writer =>
    {
        writer.WriteString("type", type);
        writer.WritePropertyName(type);
        writer.WriteRawValue(GatewayJson.Serialize(payload));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_AppendCapability_PreservesNewClientChunksAndLegacyFullRowUpdates(bool appendText)
    {
        var (sessionId, token) = await _harness.NewSessionAsync();
        var store = new SessionEventStore(sessionId, TimeProvider.System);
        var turn = StrongId.NewTurnId();
        store.Append(new ModelStreamingTextEvent(turn, "snapshot baseline"));
        using var projection = new TrackedProjection(new LiveConversationProjection(store, 0, () => 0));
        _harness.Runtime.UseProjection(sessionId, () => projection);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = await WebSocketFrames.ConnectAsync(
            new Uri($"{_harness.BaseAddress}api/sessions/{sessionId.Value}/stream?token={Uri.EscapeDataString(token)}{(appendText ? "&appendText=1" : "")}"),
            cancellation.Token);
        try
        {
            using var snapshot = await ReceiveDocumentAsync(socket);
            var baseline = Assert.Single(snapshot.RootElement.GetProperty("snapshot").GetProperty("rows").EnumerateArray());
            var appended = store.Append(new ModelStreamingTextEvent(turn, " next chunk"));
            using var frame = await ReceiveDocumentAsync(socket);
            var delta = frame.RootElement.GetProperty("delta");
            Assert.Equal(appended.Sequence, delta.GetProperty("sequence").GetInt64());
            var op = Assert.Single(delta.GetProperty("ops").EnumerateArray(), value => value.GetProperty("$type").GetString() != "state_patch");
            if (appendText)
            {
                Assert.Equal("row_text_appended", op.GetProperty("$type").GetString());
                Assert.Equal(baseline.GetProperty("rowId").GetString(), op.GetProperty("rowId").GetString());
                Assert.Equal(" next chunk", op.GetProperty("delta").GetString());
                Assert.False(op.TryGetProperty("row", out _));
            }
            else
            {
                Assert.Equal("row_upserted", op.GetProperty("$type").GetString());
                var updated = op.GetProperty("row");
                Assert.Equal("snapshot baseline next chunk", Assert.Single(updated.GetProperty("parts").EnumerateArray()).GetProperty("text").GetString());
                Assert.Equal(baseline.GetProperty("id").GetString(), updated.GetProperty("id").GetString());
                Assert.Equal(baseline.GetProperty("parts")[0].GetProperty("id").GetString(), updated.GetProperty("parts")[0].GetProperty("id").GetString());
            }
        }
        finally
        {
            socket.Abort();
            await projection.Disposed.WaitAsync(FrameBudget);
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

            var ping = await WebSocketFrames.ReceiveUntilAsync(
                socket,
                document => document.RootElement.GetProperty("type").GetString() == "ping",
                TimeSpan.FromSeconds(24));
            Assert.NotNull(ping);
            using var document = JsonDocument.Parse(ping);
            Assert.Equal("ping", document.RootElement.GetProperty("type").GetString());
        }
        finally
        {
            socket.Abort();
            socket.Dispose();
        }
    }

    [Fact]
    public async Task Stream_AlreadyRunningKernelProjection_DeliversLaterChunksAfterSnapshotWatermark()
    {
        var (sessionId, token) = await _harness.NewSessionAsync();
        var store = new SessionEventStore(sessionId, TimeProvider.System);
        var turn = StrongId.NewTurnId();
        store.Append(new TurnStartedEvent(turn, StrongId.NewCommandId()));
        store.Append(new ModelStreamingTextEvent(turn, "already running"));
        using var projection = new TrackedProjection(new LiveConversationProjection(store, 0, () => 0));
        var queued = store.Append(new ModelStreamingTextEvent(turn, ", queued before snapshot"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await projection.WaitUntilSequenceAsync(queued.Sequence, cancellation.Token);
        _harness.Runtime.UseProjection(sessionId, () => projection);
        using var socket = await ConnectAsync(sessionId, token, cancellation.Token);
        try
        {
            using var snapshot = await ReceiveDocumentAsync(socket);
            Assert.Equal("snapshot", snapshot.RootElement.GetProperty("type").GetString());
            var payload = snapshot.RootElement.GetProperty("snapshot");
            Assert.Equal(queued.Sequence, payload.GetProperty("sequence").GetInt64());
            AssertStreamingRows(payload.GetProperty("rows"), "already running, queued before snapshot");

            var later = store.Append(new ModelStreamingTextEvent(turn, ", delayed chunk"));
            using var delta = await ReceiveDocumentAsync(socket);
            AssertStreamingDelta(delta, later.Sequence, "already running, queued before snapshot, delayed chunk");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellation.Token);
            await projection.Disposed.WaitAsync(FrameBudget);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        }
        finally
        {
            socket.Abort();
        }
    }

    [Fact]
    public async Task Stream_ReconnectAndSimultaneousKernelObservers_KeepReceivingSubsequentChunks()
    {
        var (sessionId, token) = await _harness.NewSessionAsync();
        var store = new SessionEventStore(sessionId, TimeProvider.System);
        var turn = StrongId.NewTurnId();
        store.Append(new ModelStreamingTextEvent(turn, "one"));
        var observers = new ConcurrentQueue<TrackedProjection>();
        _harness.Runtime.UseProjection(sessionId, () =>
        {
            var projection = new TrackedProjection(new LiveConversationProjection(store, 0, () => 0));
            observers.Enqueue(projection);
            return projection;
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var first = await ConnectAsync(sessionId, token, cancellation.Token);
        using var second = await ConnectAsync(sessionId, token, cancellation.Token);
        ClientWebSocket? reconnected = null;
        try
        {
            using var firstSnapshot = await ReceiveDocumentAsync(first);
            using var secondSnapshot = await ReceiveDocumentAsync(second);
            AssertStreamingRows(firstSnapshot.RootElement.GetProperty("snapshot").GetProperty("rows"), "one");
            AssertStreamingRows(secondSnapshot.RootElement.GetProperty("snapshot").GetProperty("rows"), "one");
            var middle = store.Append(new ModelStreamingTextEvent(turn, " two"));
            using var firstDelta = await ReceiveDocumentAsync(first);
            using var secondDelta = await ReceiveDocumentAsync(second);
            AssertStreamingDelta(firstDelta, middle.Sequence, "one two");
            AssertStreamingDelta(secondDelta, middle.Sequence, "one two");

            first.Abort();
            await observers.First().Disposed.WaitAsync(FrameBudget);
            var whileDisconnected = store.Append(new ModelStreamingTextEvent(turn, " while disconnected"));
            using var stillLive = await ReceiveDocumentAsync(second);
            AssertStreamingDelta(stillLive, whileDisconnected.Sequence, "one two while disconnected");
            reconnected = await ConnectAsync(sessionId, token, cancellation.Token);
            using var reconnectedSnapshot = await ReceiveDocumentAsync(reconnected);
            var payload = reconnectedSnapshot.RootElement.GetProperty("snapshot");
            Assert.Equal(whileDisconnected.Sequence, payload.GetProperty("sequence").GetInt64());
            AssertStreamingRows(payload.GetProperty("rows"), "one two while disconnected");

            var later = store.Append(new ModelStreamingTextEvent(turn, " three"));
            using var secondLater = await ReceiveDocumentAsync(second);
            using var reconnectedLater = await ReceiveDocumentAsync(reconnected);
            AssertStreamingDelta(secondLater, later.Sequence, "one two while disconnected three");
            AssertStreamingDelta(reconnectedLater, later.Sequence, "one two while disconnected three");
            Assert.Equal(3, observers.Count);
        }
        finally
        {
            first.Abort();
            second.Abort();
            reconnected?.Abort();
            reconnected?.Dispose();
            await Task.WhenAll(observers.Select(observer => observer.Disposed)).WaitAsync(FrameBudget);
        }
    }

    [Theory]
    [InlineData("close")]
    [InlineData("abort")]
    [InlineData("message")]
    public async Task Stream_IdleDisconnect_ReleasesKernelObserver(string disconnect)
    {
        var (sessionId, token) = await _harness.NewSessionAsync();
        var store = new SessionEventStore(sessionId, TimeProvider.System);
        using var projection = new TrackedProjection(new LiveConversationProjection(store, 0, () => 0));
        _harness.Runtime.UseProjection(sessionId, () => projection);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = await ConnectAsync(sessionId, token, cancellation.Token);
        try
        {
            using var snapshot = await ReceiveDocumentAsync(socket);
            if (disconnect == "close")
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellation.Token);
            else if (disconnect == "abort")
                socket.Abort();
            else
                await socket.SendAsync(new byte[] { 1 }, WebSocketMessageType.Text, true, cancellation.Token);
            await projection.Disposed.WaitAsync(FrameBudget);
        }
        finally
        {
            socket.Abort();
        }
    }

    [Theory]
    [InlineData("receive", "websocket")]
    [InlineData("receive", "disposed")]
    [InlineData("receive", "io")]
    [InlineData("receive", "unexpected")]
    [InlineData("ping", "websocket")]
    [InlineData("ping", "disposed")]
    [InlineData("ping", "io")]
    [InlineData("ping", "unexpected")]
    public async Task Stream_BackgroundTransportFailure_CancelsIdleFeedAndAlwaysDisposes(string loop, string error)
    {
        var (sessionId, _) = await _harness.NewSessionAsync();
        var store = new SessionEventStore(sessionId, TimeProvider.System);
        using var projection = new TrackedProjection(new LiveConversationProjection(store, 0, () => 0));
        _harness.Runtime.UseProjection(sessionId, () => projection);
        using var socket = new TestWebSocket();
        using var request = new CancellationTokenSource();
        var failure = TransportFailure(error);
        socket.OnSend = (type, _) => type == "ping" && loop == "ping"
            ? Task.FromException(failure)
            : Task.CompletedTask;
        var run = SessionStream.RunAsync(CreateState(), sessionId, socket, request.Token,
            pingInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            await socket.SnapshotSent.Task.WaitAsync(FrameBudget);
            if (loop == "receive")
                socket.FailReceive(failure);
            if (error == "unexpected")
                await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(FrameBudget));
            else
                await run.WaitAsync(FrameBudget);
            Assert.True(projection.Disposed.IsCompletedSuccessfully);
            Assert.True(socket.IsDisposed);
        }
        finally
        {
            request.Cancel();
            try { await run.WaitAsync(FrameBudget); } catch (InvalidOperationException) when (error == "unexpected") { }
        }
    }

    [Fact]
    public async Task Stream_StalledSend_DeadlineAbortsAndReleasesObserver()
    {
        var (sessionId, _) = await _harness.NewSessionAsync();
        var store = new SessionEventStore(sessionId, TimeProvider.System);
        using var projection = new TrackedProjection(new LiveConversationProjection(store, 0, () => 0));
        _harness.Runtime.UseProjection(sessionId, () => projection);
        using var socket = new TestWebSocket
        {
            OnSend = (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
        };
        await SessionStream.RunAsync(CreateState(), sessionId, socket, CancellationToken.None,
            sendTimeout: TimeSpan.FromMilliseconds(50)).WaitAsync(FrameBudget);
        Assert.True(socket.WasAborted);
        Assert.True(socket.IsDisposed);
        Assert.True(projection.Disposed.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Stream_StalledHistorySend_DeadlineAbortsAndReleasesObserver()
    {
        var (sessionId, _) = await _harness.NewSessionAsync();
        using var projection = new TrackedProjection(_harness.Runtime.Subscribe(sessionId));
        _harness.Runtime.UseProjection(sessionId, () => projection);
        using var socket = new TestWebSocket
        {
            OnSend = (type, cancellationToken) => type == "history"
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : Task.CompletedTask,
        };
        var run = SessionStream.RunAsync(CreateState(), sessionId, socket, CancellationToken.None,
            sendTimeout: TimeSpan.FromMilliseconds(50), compact: true, history: true);
        await socket.SnapshotSent.Task.WaitAsync(FrameBudget);
        socket.QueueMessage("{\"type\":\"history\",\"requestId\":1,\"latest\":true}");
        await run.WaitAsync(FrameBudget);
        Assert.True(socket.WasAborted);
        Assert.True(socket.IsDisposed);
        Assert.True(projection.Disposed.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_PingDeltaAndCloseOutput_NeverSendConcurrently(bool history)
    {
        var (sessionId, _) = await _harness.NewSessionAsync();
        var store = new SessionEventStore(sessionId, TimeProvider.System);
        using var projection = new TrackedProjection(new LiveConversationProjection(store, 0, () => 0));
        _harness.Runtime.UseProjection(sessionId, () => projection);
        using var socket = new TestWebSocket();
        using var request = new CancellationTokenSource();
        var pingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.OnSend = async (type, cancellationToken) =>
        {
            if (type == "ping")
            {
                pingStarted.TrySetResult();
                await releasePing.Task.WaitAsync(cancellationToken);
            }
            if (type == "history") historySent.TrySetResult();
        };
        var run = SessionStream.RunAsync(CreateState(), sessionId, socket, request.Token,
            pingInterval: TimeSpan.FromMilliseconds(10), compact: history, history: history);
        try
        {
            await pingStarted.Task.WaitAsync(FrameBudget);
            if (history) socket.QueueMessage("{\"type\":\"history\",\"requestId\":1,\"latest\":true}");
            store.Append(new SessionTitleChangedEvent("queued while ping sends"));
            releasePing.TrySetResult();
            if (history) await historySent.Task.WaitAsync(FrameBudget);
            projection.Dispose();
            await run.WaitAsync(FrameBudget);
            Assert.Equal(1, socket.MaxConcurrentSends);
            Assert.Equal(1, socket.CloseOutputCount);
            Assert.Equal(0, socket.CloseCount);
            Assert.Equal("snapshot", socket.Frames.First());
            Assert.Contains("ended", socket.Frames);
        }
        finally
        {
            request.Cancel();
            releasePing.TrySetResult();
            await run.WaitAsync(FrameBudget);
        }
    }

    [Fact]
    public async Task Stream_SlowSnapshotSend_BoundedProjectionEndsAndCanResnapshot()
    {
        var (sessionId, _) = await _harness.NewSessionAsync();
        var store = new SessionEventStore(sessionId, TimeProvider.System);
        var turn = StrongId.NewTurnId();
        using var projection = new TrackedProjection(new LiveConversationProjection(store, 0, () => 0));
        _harness.Runtime.UseProjection(sessionId, () => projection);
        using var socket = new TestWebSocket();
        using var request = new CancellationTokenSource();
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.OnSend = (type, cancellationToken) => type == "snapshot"
            ? releaseSnapshot.Task.WaitAsync(cancellationToken)
            : Task.CompletedTask;
        var run = SessionStream.RunAsync(CreateState(), sessionId, socket, request.Token);
        try
        {
            await socket.SnapshotSent.Task.WaitAsync(FrameBudget);
            for (var index = 0; index < 1024; index++)
                store.Append(new ModelStreamingTextEvent(turn, "x"));
            await projection.WaitUntilSequenceAsync(1024).WaitAsync(FrameBudget);
            Assert.Equal(1024, projection.Snapshot().Sequence);
            releaseSnapshot.TrySetResult();
            await run.WaitAsync(FrameBudget);
            Assert.InRange(socket.Frames.Count(type => type == "delta"), 1, 1023);
            Assert.Equal("ended", socket.Frames.Last());
            Assert.True(projection.Disposed.IsCompletedSuccessfully);
            using var reconnected = new LiveConversationProjection(store, 0, () => 0);
            Assert.Equal(store.LastSequence, reconnected.Snapshot().Sequence);
            var later = store.Append(new ModelStreamingTextEvent(turn, " later"));
            using var timeout = new CancellationTokenSource(FrameBudget);
            await using var deltas = reconnected.Deltas(timeout.Token).GetAsyncEnumerator();
            Assert.True(await deltas.MoveNextAsync());
            Assert.Equal(later.Sequence, deltas.Current.Sequence);
        }
        finally
        {
            request.Cancel();
            releaseSnapshot.TrySetResult();
            await run.WaitAsync(FrameBudget);
        }
    }

    private Task<ClientWebSocket> ConnectAsync(SessionId sessionId, string token, CancellationToken cancellationToken) =>
        WebSocketFrames.ConnectAsync(
            new Uri($"{_harness.BaseAddress}api/sessions/{sessionId.Value}/stream?token={Uri.EscapeDataString(token)}"),
            cancellationToken);

    private GatewayState CreateState() => GatewayState.Create(new RemoteGatewayOptions
    {
        Runtime = () => _harness.Runtime,
        DataDirectory = _harness.DataDirectory,
    });

    private static async Task<JsonDocument> ReceiveDocumentAsync(ClientWebSocket socket)
    {
        var frame = await WebSocketFrames.ReceiveAsync(socket, FrameBudget);
        Assert.False(frame.IsClose);
        Assert.NotNull(frame.Text);
        return JsonDocument.Parse(frame.Text);
    }

    private static void AssertStreamingRows(JsonElement rows, string text)
    {
        var row = Assert.Single(rows.EnumerateArray());
        Assert.Equal("assistant_message", row.GetProperty("$type").GetString());
        Assert.True(row.GetProperty("isStreaming").GetBoolean());
        Assert.Equal(text, Assert.Single(row.GetProperty("parts").EnumerateArray()).GetProperty("text").GetString());
    }

    private static void AssertStreamingDelta(JsonDocument frame, long sequence, string text)
    {
        Assert.Equal("delta", frame.RootElement.GetProperty("type").GetString());
        var delta = frame.RootElement.GetProperty("delta");
        Assert.Equal(sequence, delta.GetProperty("sequence").GetInt64());
        var op = Assert.Single(delta.GetProperty("ops").EnumerateArray(),
            op => op.GetProperty("$type").GetString() is "row_upserted" or "row_appended");
        AssertStreamingRows(JsonSerializer.SerializeToElement(new[] { op.GetProperty("row") }), text);
    }

    private static Exception TransportFailure(string error) => error switch
    {
        "websocket" => new WebSocketException(WebSocketError.ConnectionClosedPrematurely),
        "disposed" => new ObjectDisposedException("socket"),
        "io" => new IOException("transport failed"),
        _ => new InvalidOperationException("unexpected transport failure"),
    };

    private sealed class TrackedProjection(IConversationProjection inner) : IConversationProjection
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Disposed => _disposed.Task;
        public ConversationSnapshot Snapshot() => inner.Snapshot();
        public IAsyncEnumerable<ConversationDelta> Deltas(CancellationToken cancellationToken) => inner.Deltas(cancellationToken);
        public Task WaitUntilSequenceAsync(long sequence, CancellationToken cancellationToken = default) =>
            inner.WaitUntilSequenceAsync(sequence, cancellationToken);
        public void Dispose()
        {
            inner.Dispose();
            _disposed.TrySetResult();
        }
    }

    private sealed class TestWebSocket : WebSocket
    {
        private readonly Channel<(Exception? Error, string? Text)> _receives = Channel.CreateUnbounded<(Exception?, string?)>();
        private WebSocketState _state = WebSocketState.Open;
        private int _activeSends;
        private int _maxConcurrentSends;
        public Func<string, CancellationToken, Task>? OnSend { get; set; }
        public TaskCompletionSource SnapshotSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Frames { get; } = new();
        public bool IsDisposed { get; private set; }
        public bool WasAborted { get; private set; }
        public int MaxConcurrentSends => _maxConcurrentSends;
        public int CloseOutputCount { get; private set; }
        public int CloseCount { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void FailReceive(Exception failure) => _receives.Writer.TryWrite((failure, null));
        public void QueueMessage(string text) => _receives.Writer.TryWrite((null, text));

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var received = await _receives.Reader.ReadAsync(cancellationToken);
            if (received.Error is { } failure) throw failure;
            var payload = Encoding.UTF8.GetBytes(received.Text!);
            payload.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true);
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            EnterSend();
            try
            {
                using var frame = JsonDocument.Parse(Encoding.UTF8.GetString(buffer));
                var type = frame.RootElement.GetProperty("type").GetString()!;
                if (type == "snapshot")
                    SnapshotSent.TrySetResult();
                if (OnSend is not null)
                    await OnSend(type, cancellationToken);
                Frames.Enqueue(type);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSends);
            }
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            EnterSend();
            try
            {
                CloseOutputCount++;
                _state = WebSocketState.CloseSent;
                return Task.CompletedTask;
            }
            finally
            {
                Interlocked.Decrement(ref _activeSends);
            }
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseCount++;
            throw new InvalidOperationException("CloseAsync would compete with the receive loop.");
        }

        private void EnterSend()
        {
            var active = Interlocked.Increment(ref _activeSends);
            int previous;
            do
            {
                previous = _maxConcurrentSends;
                if (previous >= active)
                    break;
            } while (Interlocked.CompareExchange(ref _maxConcurrentSends, active, previous) != previous);
        }

        public override void Abort()
        {
            WasAborted = true;
            _state = WebSocketState.Aborted;
        }

        public override void Dispose()
        {
            IsDisposed = true;
            _state = WebSocketState.Closed;
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
