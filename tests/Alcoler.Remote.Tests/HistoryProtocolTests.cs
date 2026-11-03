using System.Collections;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("stream-gateway")]
public sealed class HistoryProtocolTests(GatewayHarness harness)
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private const string Paged = "compact=1&history=1&appendText=1";

    [Fact]
    public async Task FirstFrame_IsBounded_AndOlderPagesCoverHistoryWithoutOverlap()
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session, Turns(0, 125), State());
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        Assert.True(Encoding.UTF8.GetByteCount(initial.RootElement.GetRawText()) < 128 * 1024);
        var client = new Window(initial.RootElement.GetProperty("snapshot"));
        Assert.Equal(Ids(105, 20), client.Ids);
        Assert.Equal("u-105", client.Before);
        Assert.Null(client.After);
        var requests = 0;
        while (client.Before is { } before)
        {
            await Send(socket, new { type = "history", requestId = ++requests, before });
            using var frame = await Receive(socket);
            Assert.Equal(requests, frame.RootElement.GetProperty("requestId").GetInt32());
            var page = frame.RootElement.GetProperty("page");
            Assert.InRange(page.GetProperty("rows").GetArrayLength(), 1, 20);
            Assert.Equal("before", page.GetProperty("direction").GetString());
            Assert.True(Encoding.UTF8.GetByteCount(frame.RootElement.GetRawText()) < 128 * 1024);
            client.Page(page);
        }
        Assert.Equal(Ids(0, 125), client.Ids);
        Assert.Equal(1, harness.Runtime.ProjectionCount(session));
        await Send(socket, new { type = "history", requestId = ++requests, latest = true });
        using var latest = await Receive(socket);
        client.Page(latest.RootElement.GetProperty("page"));
        Assert.Equal(Ids(105, 20), client.Ids);
        Assert.Equal("u-105", client.Before);
        Assert.Null(client.After);
        socket.Abort();
    }

    [Theory]
    [InlineData("u-65")]
    [InlineData("t-65")]
    [InlineData("a-65")]
    public async Task Around_RestoresContainingTurn_AndPagesBothDirectionsAndReconnects(string anchor)
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session, Turns(0, 120), State());
        var query = Paged + "&around=" + Uri.EscapeDataString(anchor);
        using var socket = await Connect(session, token, query);
        using var initial = await Receive(socket);
        var client = new Window(initial.RootElement.GetProperty("snapshot"));
        Assert.Equal(Ids(56, 20), client.Ids);
        Assert.Equal("u-56", client.Before);
        Assert.Equal("u-75", client.After);
        Assert.Contains(client.Rows, row => row.GetProperty("answer").GetProperty("rowId").GetString() == "a-65");
        using var reconnected = await Connect(session, token, query);
        using var restored = await Receive(reconnected);
        Assert.Equal(initial.RootElement.GetProperty("snapshot").GetRawText(), restored.RootElement.GetProperty("snapshot").GetRawText());
        var requestId = 1;
        await Send(socket, new { type = "history", requestId, before = client.Before });
        using (var older = await Receive(socket)) client.Page(older.RootElement.GetProperty("page"));
        Assert.Equal(Ids(36, 40), client.Ids);
        Assert.Equal("u-36", client.Before);
        Assert.Equal("u-75", client.After);
        while (client.After is { } after)
        {
            await Send(socket, new { type = "history", requestId = ++requestId, after });
            using var next = await Receive(socket);
            Assert.Equal("after", next.RootElement.GetProperty("page").GetProperty("direction").GetString());
            client.Page(next.RootElement.GetProperty("page"));
        }
        Assert.Equal(Ids(36, 84), client.Ids);
        await Send(socket, new { type = "history", requestId = ++requestId, latest = true });
        using (var latest = await Receive(socket)) client.Page(latest.RootElement.GetProperty("page"));
        Assert.Equal(Ids(100, 20), client.Ids);
        socket.Abort();
        reconnected.Abort();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WireBudget_LimitsLargeAndEscapedAnswers_WithoutPartialTurns(bool escaped)
    {
        var (session, token) = await harness.NewSessionAsync();
        var answer = escaped ? string.Concat(Enumerable.Repeat("中文<&\"😀", 2000)) : new string('a', 20_000);
        harness.Runtime.SetTranscript(session, Turns(0, 60, answer), State());
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        var client = new Window(initial.RootElement.GetProperty("snapshot"));
        Assert.InRange(client.Rows.Count, 1, 19);
        Assert.True(Encoding.UTF8.GetByteCount(initial.RootElement.GetRawText()) <= 128 * 1024);
        Assert.Equal(Ids(60 - client.Rows.Count, client.Rows.Count), client.Ids);
        Assert.All(client.Rows, row => Assert.Equal(answer, AnswerText(row)));
        await Send(socket, new { type = "history", requestId = 1, before = client.Before });
        using var next = await Receive(socket);
        Assert.True(Encoding.UTF8.GetByteCount(next.RootElement.GetRawText()) <= 128 * 1024);
        client.Page(next.RootElement.GetProperty("page"));
        Assert.Equal(Ids(60 - client.Rows.Count, client.Rows.Count), client.Ids);
        socket.Abort();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedTurn_IsKeptWhole_IncludingActiveRows(bool active)
    {
        var (session, token) = await harness.NewSessionAsync();
        var rows = Turns(0, 60);
        var answer = Assistant(59, new string('x', 180_000)) with { IsStreaming = active };
        rows[^1] = answer;
        var state = active ? State() with { Phase = TurnPhase.Streaming, ActiveTurn = new TurnId("active") } : State();
        harness.Runtime.SetTranscript(session, rows, state);
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        var snapshot = initial.RootElement.GetProperty("snapshot");
        var client = new Window(snapshot);
        Assert.True(Encoding.UTF8.GetByteCount(initial.RootElement.GetRawText()) > 128 * 1024);
        Assert.Equal(active ? new[] { "u-59", "t-59", "a-59" } : new[] { "u-59" }, client.Ids);
        Assert.Equal("u-59", client.Before);
        Assert.Null(client.After);
        if (active)
        {
            Assert.True(client.Rows[^1].GetProperty("isStreaming").GetBoolean());
            Assert.Equal(180_000, client.Rows[^1].GetProperty("parts")[0].GetProperty("text").GetString()!.Length);
        }
        socket.Abort();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WireBudget_IncludesUnboundedVisibleMetadataAndUserFiles(bool toolMetadata)
    {
        var (session, token) = await harness.NewSessionAsync();
        var rows = Turns(0, 60);
        for (var index = 0; index < 60; index++)
        {
            if (toolMetadata)
                rows[index * 3 + 1] = Tool(index) with
                {
                    ToolName = new string('n', 50_000), Status = ToolCallStatus.Failed,
                    FileChange = new FileChangeStats(int.MaxValue, int.MinValue),
                };
            else
                rows[index * 3] = User(index) with
                {
                    Images = [new FilePart(new PartId(new string('i', 2000)), "alcoler-artifact://" + new string('u', 20_000),
                        new string('m', 5000), new string('f', 10_000), ProviderReady: false)],
                };
        }
        harness.Runtime.SetTranscript(session, rows, State());
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        var snapshot = initial.RootElement.GetProperty("snapshot");
        Assert.True(Encoding.UTF8.GetByteCount(initial.RootElement.GetRawText()) <= 128 * 1024);
        var visible = snapshot.GetProperty("rows").EnumerateArray().ToList();
        if (toolMetadata)
        {
            Assert.InRange(visible.Count, 3, 6);
            var tool = visible.Single(row => Id(row) == "t-59");
            Assert.Equal(50_000, tool.GetProperty("toolName").GetString()!.Length);
            Assert.Equal(int.MaxValue, tool.GetProperty("fileChange").GetProperty("added").GetInt32());
        }
        else
        {
            Assert.InRange(visible.Count, 1, 3);
            var image = visible[^1].GetProperty("user").GetProperty("images")[0];
            Assert.Equal(10_000, image.GetProperty("fileName").GetString()!.Length);
            Assert.False(image.GetProperty("providerReady").GetBoolean());
        }
        socket.Abort();
    }

    [Fact]
    public void InitialWindow_DoesNotHashOrReadHiddenToolPayloads()
    {
        var rows = Turns(0, 80);
        var payloads = Enumerable.Range(0, 80).Select(_ => new CountingOutput()).ToArray();
        for (var index = 0; index < payloads.Length; index++)
            rows[index * 3 + 1] = Tool(index) with { Output = payloads[index] };
        var transcript = new CompactTranscript(new ConversationSnapshot(0, 0, rows, State()), paged: true);
        _ = transcript.SnapshotFrame(0, 0);
        Assert.All(payloads.Take(60), payload => Assert.Equal(0, payload.Reads));
        Assert.All(payloads.Skip(60), payload => Assert.True(payload.Reads > 0));
        _ = transcript.Page(new HistoryRequest(1, "before", "u-60"), 0);
        Assert.All(payloads.Take(40), payload => Assert.Equal(0, payload.Reads));
        Assert.All(payloads.Skip(40), payload => Assert.True(payload.Reads > 0));
    }

    [Fact]
    public async Task SnapshotApproval_IsImmediatelyActionable_AndLiveTurnFoldsAfterOlderPages()
    {
        var (session, token) = await harness.NewSessionAsync();
        var rows = Turns(0, 60);
        var tool = Tool(59) with { Status = ToolCallStatus.AwaitingPermission };
        rows[^2] = tool;
        rows[^1] = Assistant(59, "baseline") with { IsStreaming = true };
        var interaction = new InteractionId("approval");
        var permission = new PermissionRequest(interaction, session, "Bash",
            JsonSerializer.SerializeToElement(new { command = "consent must remain available" }), RiskLevel.Medium, "approval required", ToolCallId: tool.Id);
        var active = State() with { Phase = TurnPhase.AwaitingPermission, ActiveTurn = new TurnId("active"), PendingPermission = permission };
        harness.Runtime.SetTranscript(session, rows, active);
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        var snapshot = initial.RootElement.GetProperty("snapshot");
        var client = new Window(snapshot);
        Assert.Equal("approval required", snapshot.GetProperty("state").GetProperty("pendingPermission").GetProperty("reason").GetString());
        using var ack = await harness.PostCommandAsync(session,
            new ResolveInteractionCommand(StrongId.NewCommandId(), session, snapshot.GetProperty("revision").GetInt64(), interaction, PermissionDecision.Allow), token);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        using (var resolved = await Receive(socket))
        {
            client.Delta(resolved.RootElement.GetProperty("delta"));
            Assert.Equal(JsonValueKind.Null, client.State.GetProperty("pendingPermission").ValueKind);
        }
        await Send(socket, new { type = "history", requestId = 1, before = client.Before });
        using (var older = await Receive(socket)) client.Page(older.RootElement.GetProperty("page"));
        Assert.Equal("u-20", client.Before);
        var streaming = active with { Phase = TurnPhase.Streaming, PendingPermission = null };
        var chunk = harness.Runtime.Publish(session, new RowTextAppended("a-59", " chunk", At.AddSeconds(4)), new StatePatched(streaming));
        using (var live = await Receive(socket))
        {
            var delta = live.RootElement.GetProperty("delta");
            Assert.Equal(chunk.Sequence, delta.GetProperty("sequence").GetInt64());
            Assert.Contains(delta.GetProperty("ops").EnumerateArray(), op => Type(op) == "row_text_appended");
            client.Delta(delta);
        }
        Assert.Equal("baseline chunk", client.Rows.Single(row => Id(row) == "a-59").GetProperty("parts")[0].GetProperty("text").GetString());
        var completed = harness.Runtime.Publish(session, new RowUpserted(tool with { Status = ToolCallStatus.Succeeded }),
            new RowUpserted(Assistant(59, "finished")), new StatePatched(State()));
        using (var folded = await Receive(socket))
        {
            var delta = folded.RootElement.GetProperty("delta");
            Assert.Equal(completed.Sequence, delta.GetProperty("sequence").GetInt64());
            client.Delta(delta);
        }
        Assert.Equal(Ids(20, 40), client.Ids);
        Assert.Equal("finished", AnswerText(client.Rows[^1]));
        Assert.Equal("u-20", client.Before);
        Assert.Null(client.After);
        var appended = harness.Runtime.Publish(session, new RowAppended(User(60)), new RowAppended(Assistant(60, "new live") with { IsStreaming = true }),
            new StatePatched(streaming));
        using (var live = await Receive(socket)) client.Delta(live.RootElement.GetProperty("delta"));
        Assert.Equal(appended.Sequence, client.Sequence);
        Assert.Equal(new[] { "u-60", "a-60" }, client.Ids.TakeLast(2));
        Assert.Null(client.After);
        socket.Abort();
    }

    [Fact]
    public async Task HiddenHistoryChanges_StayHidden_WhileStateAlwaysFlows_AndPagesUseUpdatedSource()
    {
        var (session, token) = await harness.NewSessionAsync();
        var rows = Turns(0, 100);
        rows[^1] = Assistant(99, "baseline") with { IsStreaming = true };
        var active = State() with { Phase = TurnPhase.Streaming, ActiveTurn = new TurnId("active") };
        harness.Runtime.SetTranscript(session, rows, active);
        using var socket = await Connect(session, token, Paged + "&around=a-30");
        using var initial = await Receive(socket);
        var client = new Window(initial.RootElement.GetProperty("snapshot"));
        Assert.Equal(Ids(21, 20), client.Ids);
        var permission = new PermissionRequest(new InteractionId("hidden-approval"), session, "Read",
            JsonSerializer.SerializeToElement(new { path = "consent.txt" }), RiskLevel.Low, "still visible");
        var awaiting = active with { Phase = TurnPhase.AwaitingPermission, PendingPermission = permission };
        var first = harness.Runtime.Publish(session, new RowTextAppended("a-99", " hidden chunk", At),
            new RowUpserted(Tool(5) with { Output = [new ToolResultText("changed hidden output")] }), new StatePatched(awaiting));
        using (var frame = await Receive(socket))
        {
            var delta = frame.RootElement.GetProperty("delta");
            Assert.Equal(first.Sequence, delta.GetProperty("sequence").GetInt64());
            Assert.Equal("state_patch", Type(Assert.Single(delta.GetProperty("ops").EnumerateArray())));
            client.Delta(delta);
        }
        Assert.Equal("still visible", client.State.GetProperty("pendingPermission").GetProperty("reason").GetString());
        var next = harness.Runtime.Publish(session, new RowAppended(User(100)), new RowAppended(Tool(100) with { Status = ToolCallStatus.Running }),
            new RowAppended(Assistant(100, "new hidden tail") with { IsStreaming = true }), new StatePatched(active));
        using (var frame = await Receive(socket))
        {
            var delta = frame.RootElement.GetProperty("delta");
            Assert.Equal(next.Sequence, delta.GetProperty("sequence").GetInt64());
            Assert.Equal("state_patch", Type(Assert.Single(delta.GetProperty("ops").EnumerateArray())));
            client.Delta(delta);
        }
        Assert.Equal(Ids(21, 20), client.Ids);
        Assert.Equal("u-40", client.After);
        var requestId = 0;
        while (client.After is { } after)
        {
            await Send(socket, new { type = "history", requestId = ++requestId, after });
            using var frame = await Receive(socket);
            client.Page(frame.RootElement.GetProperty("page"));
        }
        Assert.Equal("baseline hidden chunk", client.Rows.Single(row => Id(row) == "a-99").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("new hidden tail", client.Rows.Single(row => Id(row) == "a-100").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(next.Sequence, client.Sequence);
        Assert.Null(client.After);
        var delivered = harness.Runtime.Publish(session, new RowTextAppended("a-100", " delivered", At));
        using (var frame = await Receive(socket)) client.Delta(frame.RootElement.GetProperty("delta"));
        Assert.Equal(delivered.Sequence, client.Sequence);
        Assert.Equal("new hidden tail delivered", client.Rows.Single(row => Id(row) == "a-100").GetProperty("parts")[0].GetProperty("text").GetString());
        socket.Abort();
    }

    [Fact]
    public async Task Rewind_RemovedLoadedRangeFallsBackToLatest_AndRemovedAnchorNeverReturns()
    {
        var (session, token) = await harness.NewSessionAsync();
        var source = Turns(0, 100);
        harness.Runtime.SetTranscript(session, source, State());
        using var socket = await Connect(session, token, Paged + "&around=a-50");
        using var initial = await Receive(socket);
        var client = new Window(initial.RootElement.GetProperty("snapshot"));
        var stale = client.After;
        var rewind = harness.Runtime.Publish(session, source.Skip(40 * 3).Select(row => (RowOp)new RowRemoved(row.RowId))
            .Append(new StatePatched(State() with { Title = "rewound" })).ToArray());
        using (var frame = await Receive(socket)) client.Delta(frame.RootElement.GetProperty("delta"));
        Assert.Equal(rewind.Sequence, client.Sequence);
        Assert.Equal(Ids(20, 20), client.Ids);
        Assert.Equal("rewound", client.State.GetProperty("title").GetString());
        Assert.Equal("u-20", client.Before);
        Assert.Null(client.After);
        await Send(socket, new { type = "history", requestId = 1, after = stale });
        using (var error = await Receive(socket))
        {
            Assert.Equal("stale_history", error.RootElement.GetProperty("error").GetString());
            Assert.Equal(rewind.Sequence, error.RootElement.GetProperty("sequence").GetInt64());
            Assert.False(error.RootElement.TryGetProperty("page", out _));
        }
        using var reconnected = await Connect(session, token, Paged + "&around=a-50");
        using (var restored = await Receive(reconnected))
            Assert.Equal(client.Ids, new Window(restored.RootElement.GetProperty("snapshot")).Ids);
        harness.Runtime.Publish(session, source.Take(40 * 3).Select(row => (RowOp)new RowRemoved(row.RowId)).Append(new StatePatched(State())).ToArray());
        using (var frame = await Receive(socket)) client.Delta(frame.RootElement.GetProperty("delta"));
        Assert.Empty(client.Rows);
        Assert.Null(client.Before);
        Assert.Null(client.After);
        await Send(socket, new { type = "history", requestId = 2, latest = true });
        using (var empty = await Receive(socket)) client.Page(empty.RootElement.GetProperty("page"));
        Assert.Empty(client.Rows);
        harness.Runtime.Publish(session, Turns(0, 1).Select(row => (RowOp)new RowAppended(row)).ToArray());
        using (var frame = await Receive(socket)) client.Delta(frame.RootElement.GetProperty("delta"));
        Assert.Equal(new[] { "u-0" }, client.Ids);
        socket.Abort();
        reconnected.Abort();
    }

    [Fact]
    public async Task RemovingBoundaryUsers_RegroupsOnlyContiguousLoadedTurns_AndInvalidatesCursor()
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session, Turns(0, 80), State());
        using var socket = await Connect(session, token, Paged + "&around=a-40");
        using var initial = await Receive(socket);
        var client = new Window(initial.RootElement.GetProperty("snapshot"));
        Assert.Equal(Ids(31, 20), client.Ids);
        harness.Runtime.Publish(session, new RowRemoved("u-31"));
        using (var frame = await Receive(socket)) client.Delta(frame.RootElement.GetProperty("delta"));
        Assert.Equal(new[] { "u-30" }.Concat(Ids(32, 19)), client.Ids);
        Assert.Equal("a-31", client.Rows[0].GetProperty("answer").GetProperty("rowId").GetString());
        Assert.Equal("u-30", client.Before);
        var oldAfter = client.After;
        harness.Runtime.Publish(session, new RowRemoved("u-10"), new RowRemoved("u-50"));
        using (var frame = await Receive(socket)) client.Delta(frame.RootElement.GetProperty("delta"));
        Assert.Equal(new[] { "u-30" }.Concat(Ids(32, 18)), client.Ids);
        Assert.Equal("u-49", client.After);
        Assert.DoesNotContain("u-10", client.Ids);
        Assert.DoesNotContain("u-31", client.Ids);
        Assert.DoesNotContain("u-50", client.Ids);
        await Send(socket, new { type = "history", requestId = 1, after = oldAfter });
        using (var error = await Receive(socket)) Assert.Equal("stale_history", error.RootElement.GetProperty("error").GetString());
        await Send(socket, new { type = "history", requestId = 2, after = client.After });
        using (var next = await Receive(socket)) client.Page(next.RootElement.GetProperty("page"));
        Assert.Equal("u-70", client.After);
        Assert.DoesNotContain("u-50", client.Ids);
        socket.Abort();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageAndDeltas_HaveOneOrderedAuthority_EvenWhenProjectionIsAhead(bool removed)
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session, Turns(0, 55), State());
        using var projection = new PausedProjection(harness.Runtime.Subscribe(session));
        harness.Runtime.UseProjection(session, () => projection);
        using var socket = await Connect(session, token);
        try
        {
            using var initial = await Receive(socket);
            var client = new Window(initial.RootElement.GetProperty("snapshot"));
            var changed = removed
                ? harness.Runtime.Publish(session, new RowRemoved("u-20"), new RowRemoved("t-20"), new RowRemoved("a-20"))
                : harness.Runtime.Publish(session, new RowUpserted(Assistant(20, "newer authoritative answer")));
            await projection.Paused.Task.WaitAsync(Budget);
            await Send(socket, new { type = "history", requestId = 1, before = client.Before });
            using (var page = await Receive(socket)) client.Page(page.RootElement.GetProperty("page"));
            Assert.Equal(0, client.Sequence);
            Assert.Equal("answer 20", AnswerText(client.Rows.Single(row => Id(row) == "u-20")));
            Assert.Equal(1, projection.Snapshots);
            projection.Release.TrySetResult();
            using (var frame = await Receive(socket)) client.Delta(frame.RootElement.GetProperty("delta"));
            Assert.Equal(changed.Sequence, client.Sequence);
            if (removed) Assert.DoesNotContain("u-20", client.Ids);
            else Assert.Equal("newer authoritative answer", AnswerText(client.Rows.Single(row => Id(row) == "u-20")));
            var hidden = harness.Runtime.Publish(session, new RowUpserted(Assistant(5, "updated before page")));
            using (var frame = await Receive(socket))
            {
                var delta = frame.RootElement.GetProperty("delta");
                Assert.Empty(delta.GetProperty("ops").EnumerateArray());
                client.Delta(delta);
            }
            await Send(socket, new { type = "history", requestId = 2, before = client.Before });
            using (var page = await Receive(socket)) client.Page(page.RootElement.GetProperty("page"));
            Assert.Equal(hidden.Sequence, client.Sequence);
            Assert.Equal("updated before page", AnswerText(client.Rows.Single(row => Id(row) == "u-5")));
            if (removed) Assert.DoesNotContain("u-20", client.Ids);
            else Assert.Equal("newer authoritative answer", AnswerText(client.Rows.Single(row => Id(row) == "u-20")));
            Assert.Equal(removed ? Ids(0, 55).Where(id => id != "u-20") : Ids(0, 55), client.Ids);
            Assert.Equal(1, projection.Snapshots);
        }
        finally
        {
            projection.Release.TrySetResult();
            harness.Runtime.UseProjection(session, null);
            socket.Abort();
        }
    }

    [Fact]
    public async Task Requests_AreOrdered_AndRepeatedOrUnknownCursorsAreStale()
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session, Turns(0, 75), State());
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        var client = new Window(initial.RootElement.GetProperty("snapshot"));
        var before = client.Before;
        await Send(socket, new { type = "history", requestId = 1, before });
        await Send(socket, new { type = "history", requestId = 2, before });
        await Send(socket, new { type = "history", requestId = 3, after = "not-a-cursor" });
        using (var first = await Receive(socket))
        {
            Assert.Equal(1, first.RootElement.GetProperty("requestId").GetInt32());
            client.Page(first.RootElement.GetProperty("page"));
        }
        foreach (var requestId in new[] { 2, 3 })
        {
            using var error = await Receive(socket);
            Assert.Equal(requestId, error.RootElement.GetProperty("requestId").GetInt32());
            Assert.Equal("stale_history", error.RootElement.GetProperty("error").GetString());
            Assert.Equal(client.Before, error.RootElement.GetProperty("history").GetProperty("before").GetString());
        }
        for (var requestId = 4; requestId < 28; requestId++)
            await Send(socket, new { type = "history", requestId, latest = true });
        for (var requestId = 4; requestId < 28; requestId++)
        {
            using var response = await Receive(socket);
            Assert.Equal(requestId, response.RootElement.GetProperty("requestId").GetInt32());
            client.Page(response.RootElement.GetProperty("page"));
            Assert.Equal(Ids(55, 20), client.Ids);
        }
        socket.Abort();
        await WaitForObservers(session, 0);
    }

    [Fact]
    public async Task Abort_WithQueuedHistoryRequests_ReleasesObserver()
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session, Turns(0, 100), State());
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        for (var requestId = 1; requestId <= 64; requestId++)
            await Send(socket, new { type = "history", requestId, latest = true });
        socket.Abort();
        await WaitForObservers(session, 0);
    }

    [Fact]
    public async Task FragmentedHistoryRequest_IsReassembledWithinTheLimit_AndCloseDisposesObserver()
    {
        var (session, token) = await harness.NewSessionAsync();
        harness.Runtime.SetTranscript(session, Turns(0, 60), State());
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        var client = new Window(initial.RootElement.GetProperty("snapshot"));
        var payload = Encoding.UTF8.GetBytes(GatewayHarness.Serialize(new { type = "history", requestId = 1, before = client.Before }));
        using var timeout = new CancellationTokenSource(Budget);
        for (var index = 0; index < payload.Length; index++)
            await socket.SendAsync(new ArraySegment<byte>(payload, index, 1), WebSocketMessageType.Text, index == payload.Length - 1, timeout.Token);
        using (var response = await Receive(socket)) client.Page(response.RootElement.GetProperty("page"));
        Assert.Equal(Ids(20, 40), client.Ids);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        await WaitForObservers(session, 0);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"history\",\"requestId\":\"bad\",\"latest\":true}")]
    [InlineData("{\"type\":\"history\",\"requestId\":0,\"latest\":true}")]
    [InlineData("{\"type\":\"history\",\"requestId\":2147483648,\"latest\":true}")]
    [InlineData("{\"type\":\"history\",\"requestId\":1,\"latest\":true,\"after\":\"u-1\"}")]
    [InlineData("{\"type\":\"history\",\"requestId\":1,\"latest\":true,\"latest\":true}")]
    [InlineData("{\"type\":\"history\",\"requestId\":1,\"before\":null}")]
    [InlineData("{\"type\":\"other\",\"requestId\":1,\"latest\":true}")]
    public async Task InvalidHistoryMessages_CloseWithoutLeakingObservers(string text)
    {
        var (session, token) = await harness.NewSessionAsync();
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        await SendText(socket, text);
        var close = await WebSocketFrames.ReceiveAsync(socket, Budget);
        Assert.True(close.IsClose);
        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, close.Result.CloseStatus);
        socket.Abort();
        await WaitForObservers(session, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedOrBinaryMessages_CloseWithBoundedBuffer(bool binary)
    {
        var (session, token) = await harness.NewSessionAsync();
        using var socket = await Connect(session, token);
        using var initial = await Receive(socket);
        using var timeout = new CancellationTokenSource(Budget);
        await socket.SendAsync(new ArraySegment<byte>(new byte[HistoryRequest.MaxBytes + 1]),
            binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, timeout.Token);
        var close = await WebSocketFrames.ReceiveAsync(socket, Budget);
        Assert.True(close.IsClose);
        Assert.Equal(binary ? WebSocketCloseStatus.InvalidMessageType : WebSocketCloseStatus.MessageTooBig, close.Result.CloseStatus);
        socket.Abort();
        await WaitForObservers(session, 0);
    }

    [Theory]
    [InlineData("history=1", false)]
    [InlineData("compact=0&history=1", false)]
    [InlineData("compact=1", true)]
    [InlineData("compact=1&history=0", true)]
    public async Task PagingRequiresBothCapabilities_AndLegacyFramesAreUnchanged(string query, bool compact)
    {
        var (session, token) = await harness.NewSessionAsync();
        var rows = Turns(0, 60);
        var state = State();
        harness.Runtime.SetTranscript(session, rows, state);
        using var socket = await Connect(session, token, query + "&around=a-5");
        using var initial = await Receive(socket);
        var snapshot = initial.RootElement.GetProperty("snapshot");
        Assert.False(snapshot.TryGetProperty("history", out _));
        Assert.Equal(compact ? 60 : 180, snapshot.GetProperty("rows").GetArrayLength());
        if (!compact)
            Assert.Equal(Encoding.UTF8.GetString(GatewayJson.SnapshotFrame(new ConversationSnapshot(0, 0, rows, state))), initial.RootElement.GetRawText());
        var delta = harness.Runtime.Publish(session, new RowUpserted(Assistant(5, "legacy changed")), new StatePatched(state));
        using var frame = await Receive(socket);
        Assert.False(frame.RootElement.GetProperty("delta").TryGetProperty("history", out _));
        if (!compact) Assert.Equal(Encoding.UTF8.GetString(GatewayJson.DeltaFrame(delta)), frame.RootElement.GetRawText());
        socket.Abort();
    }

    private Task<ClientWebSocket> Connect(SessionId session, string token, string query = Paged) =>
        WebSocketFrames.ConnectAsync(new Uri($"{harness.BaseAddress}api/sessions/{session.Value}/stream?token={Uri.EscapeDataString(token)}&{query}"), CancellationToken.None);

    private static Task Send(ClientWebSocket socket, object payload) => SendText(socket, GatewayHarness.Serialize(payload));

    private static async Task SendText(ClientWebSocket socket, string text)
    {
        using var timeout = new CancellationTokenSource(Budget);
        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, timeout.Token);
    }

    private static async Task<JsonDocument> Receive(ClientWebSocket socket)
    {
        while (true)
        {
            var frame = await WebSocketFrames.ReceiveAsync(socket, Budget);
            Assert.False(frame.IsClose);
            var document = JsonDocument.Parse(Assert.IsType<string>(frame.Text));
            if (document.RootElement.GetProperty("type").GetString() != "ping") return document;
            document.Dispose();
        }
    }

    private async Task WaitForObservers(SessionId session, int count)
    {
        using var timeout = new CancellationTokenSource(Budget);
        while (harness.Runtime.ObserverCount(session) != count) await Task.Delay(10, timeout.Token);
    }

    private static List<ConversationRow> Turns(int first, int count, string? answer = null)
    {
        List<ConversationRow> rows = [];
        for (var index = first; index < first + count; index++) rows.AddRange([User(index), Tool(index), Assistant(index, answer ?? $"answer {index}")]);
        return rows;
    }

    private static ConversationState State() => ConversationState.Initial("paged history", PermissionMode.Build, null, 200_000);
    private static UserMessageRow User(int index) => new($"u-{index}", $"request {index}", At, CanRewind: true);
    private static AssistantMessageRow Assistant(int index, string text) => new($"a-{index}", new MessageId($"m-{index}"),
        [new TextPart(new PartId($"p-{index}"), text)], false, At.AddSeconds(2));
    private static ToolCallRow Tool(int index) => new($"t-{index}", new ToolCallId($"c-{index}"), "Read",
        JsonSerializer.SerializeToElement(new { path = "file.cs" }), ToolCallStatus.Succeeded, [new ToolResultText("hidden tool output")], false, TimeSpan.FromSeconds(1), At);
    private static IEnumerable<string> Ids(int first, int count) => Enumerable.Range(first, count).Select(index => $"u-{index}");
    private static string Id(JsonElement row) => row.GetProperty("rowId").GetString()!;
    private static string? Type(JsonElement value) => value.GetProperty("$type").GetString();
    private static string? AnswerText(JsonElement row) => row.GetProperty("answer").GetProperty("parts")[0].GetProperty("text").GetString();

    private sealed class Window
    {
        public Window(JsonElement snapshot)
        {
            Rows.AddRange(snapshot.GetProperty("rows").EnumerateArray().Select(row => row.Clone()));
            Sequence = snapshot.GetProperty("sequence").GetInt64();
            State = snapshot.GetProperty("state").Clone();
            Bounds(snapshot);
        }

        public List<JsonElement> Rows { get; } = [];
        public IEnumerable<string> Ids => Rows.Select(Id);
        public long Sequence { get; private set; }
        public JsonElement State { get; private set; }
        public string? Before { get; private set; }
        public string? After { get; private set; }

        public void Page(JsonElement page)
        {
            Assert.Equal(Sequence, page.GetProperty("sequence").GetInt64());
            var direction = page.GetProperty("direction").GetString();
            if (direction == "latest") Rows.Clear();
            var rows = page.GetProperty("rows").EnumerateArray().Select(row => row.Clone()).ToList();
            Assert.Empty(Ids.Intersect(rows.Select(Id)));
            if (direction == "before") Rows.InsertRange(0, rows);
            else Rows.AddRange(rows);
            Bounds(page);
        }

        public void Delta(JsonElement delta)
        {
            var sequence = delta.GetProperty("sequence").GetInt64();
            Assert.True(sequence > Sequence);
            Sequence = sequence;
            foreach (var operation in delta.GetProperty("ops").EnumerateArray())
            {
                switch (Type(operation))
                {
                    case "row_appended":
                    case "row_upserted":
                        var row = operation.GetProperty("row");
                        var index = Rows.FindIndex(current => Id(current) == Id(row));
                        if (index < 0) Rows.Add(row.Clone());
                        else Rows[index] = row.Clone();
                        break;
                    case "row_removed":
                        Rows.RemoveAll(current => Id(current) == operation.GetProperty("rowId").GetString());
                        break;
                    case "row_text_appended":
                        var position = Rows.FindIndex(current => Id(current) == operation.GetProperty("rowId").GetString());
                        Assert.True(position >= 0);
                        var node = JsonNode.Parse(Rows[position].GetRawText())!;
                        var text = node["parts"]![0]!;
                        text["text"] = text["text"]!.GetValue<string>() + operation.GetProperty("delta").GetString();
                        Rows[position] = JsonSerializer.SerializeToElement(node);
                        break;
                    case "state_patch":
                        State = operation.GetProperty("state").Clone();
                        break;
                }
            }
            Bounds(delta);
            Assert.Equal(Rows.Count, Ids.Distinct().Count());
        }

        private void Bounds(JsonElement payload)
        {
            var history = payload.GetProperty("history");
            Before = history.GetProperty("before").GetString();
            After = history.GetProperty("after").GetString();
        }
    }

    private sealed class CountingOutput : IReadOnlyList<ToolResultBlock>
    {
        public int Reads { get; private set; }
        public int Count { get { Reads++; return 1; } }
        public ToolResultBlock this[int index] { get { Reads++; return new ToolResultText("deferred payload"); } }
        public IEnumerator<ToolResultBlock> GetEnumerator()
        {
            Reads++;
            yield return this[0];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class PausedProjection(IConversationProjection inner) : IConversationProjection
    {
        public int Snapshots { get; private set; }
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConversationSnapshot Snapshot() { Snapshots++; return inner.Snapshot(); }
        public async IAsyncEnumerable<ConversationDelta> Deltas([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var delta in inner.Deltas(cancellationToken))
            {
                Paused.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                yield return delta;
            }
        }
        public Task WaitUntilSequenceAsync(long sequence, CancellationToken cancellationToken = default) => inner.WaitUntilSequenceAsync(sequence, cancellationToken);
        public void Dispose() => inner.Dispose();
    }
}
