using System.Net;
using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

[Collection("api-gateway")]
public sealed class CommandEndpointTests
{
    private static readonly TimeSpan StreamBudget = TimeSpan.FromSeconds(15);

    private readonly GatewayHarness _harness;

    public CommandEndpointTests(GatewayHarness harness) => _harness = harness;

    [Fact]
    public async Task SendText_IsAccepted_AndBumpsRevision()
    {
        var (sessionId, token) = await _harness.NewSessionAsync();

        using var response = await _harness.PostCommandAsync(
            sessionId, new SendTextCommand(StrongId.NewCommandId(), sessionId, BaseRevision: 0, Text: "hello gateway"), token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"accepted\":true", body, StringComparison.Ordinal);
        var ack = await ReadAckAsync(response);
        Assert.True(ack.Accepted);
        Assert.Equal(1, ack.Revision);
    }

    [Fact]
    public async Task SendText_SessionIdMismatchingRoute_IsRejected()
    {
        var (_, token) = await _harness.PairAsync();
        var routedSession = await _harness.CreateSessionAsync(token);
        var otherSession = await _harness.CreateSessionAsync(token);

        using var response = await _harness.PostCommandAsync(
            routedSession, new SendTextCommand(StrongId.NewCommandId(), otherSession, BaseRevision: 0, Text: "wrong route"), token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.Equal("session_mismatch", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SendText_WithoutDiscriminator_IsRejected()
    {
        var (sessionId, token) = await _harness.NewSessionAsync();
        var body = GatewayHarness.Serialize(new
        {
            commandId = StrongId.NewCommandId().Value,
            sessionId = sessionId.Value,
            baseRevision = 0,
            text = "missing type discriminator",
        });

        using var response = await _harness.SendWithTokenAsync(
            HttpMethod.Post, $"api/sessions/{sessionId.Value}/commands", token, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await response.ReadJsonAsync();
        Assert.Equal("bad_request", document.RootElement.GetProperty("error").GetString());
        Assert.Equal("command", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SendText_WithStaleBaseRevision_IsAcknowledgedAsRejected()
    {
        var (sessionId, token) = await _harness.NewSessionAsync();
        using var accepted = await _harness.PostCommandAsync(
            sessionId, new SendTextCommand(StrongId.NewCommandId(), sessionId, BaseRevision: 0, Text: "first"), token);
        var firstAck = await ReadAckAsync(accepted);
        Assert.True(firstAck.Accepted);

        using var stale = await _harness.PostCommandAsync(
            sessionId, new SendTextCommand(StrongId.NewCommandId(), sessionId, BaseRevision: 0, Text: "second"), token);

        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        var staleAck = await ReadAckAsync(stale);
        Assert.False(staleAck.Accepted);
        Assert.False(string.IsNullOrEmpty(staleAck.RejectionReason));
        Assert.Equal(firstAck.Revision, staleAck.Revision);
    }

    [Fact]
    public async Task ResolveInteraction_ClearsPendingPermissionInView()
    {
        var (sessionId, token) = await _harness.NewSessionAsync();
        var interactionId = _harness.Runtime.SeedPendingPermission(sessionId);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var socket = await WebSocketFrames.ConnectAsync(
            new Uri($"{_harness.BaseAddress}api/sessions/{sessionId.Value}/stream?token={Uri.EscapeDataString(token)}"),
            cancellation.Token);
        try
        {
            var snapshotFrame = await WebSocketFrames.ReceiveUntilAsync(
                socket,
                document => document.RootElement.GetProperty("type").GetString() == "snapshot",
                StreamBudget);
            Assert.NotNull(snapshotFrame);
            using var snapshot = JsonDocument.Parse(snapshotFrame!);
            var state = snapshot.RootElement.GetProperty("snapshot").GetProperty("state");
            Assert.Equal("shell", state.GetProperty("pendingPermission").GetProperty("toolName").GetString());

            using var resolved = await _harness.PostCommandAsync(
                sessionId,
                new ResolveInteractionCommand(
                    StrongId.NewCommandId(),
                    sessionId,
                    BaseRevision: 0,
                    interactionId,
                    PermissionDecision: PermissionDecision.Allow),
                token);
            var ack = await ReadAckAsync(resolved);
            Assert.True(ack.Accepted);

            var clearedFrame = await WebSocketFrames.ReceiveUntilAsync(
                socket,
                document =>
                {
                    if (document.RootElement.GetProperty("type").GetString() != "delta")
                        return false;
                    foreach (var op in document.RootElement.GetProperty("delta").GetProperty("ops").EnumerateArray())
                    {
                        if (op.GetProperty("$type").GetString() != "state_patch")
                            continue;
                        var patched = op.GetProperty("state");
                        return !patched.TryGetProperty("pendingPermission", out var pending) ||
                            pending.ValueKind == JsonValueKind.Null;
                    }
                    return false;
                },
                StreamBudget);
            Assert.NotNull(clearedFrame);
        }
        finally
        {
            socket.Abort();
            socket.Dispose();
        }
    }

    private static async Task<CommandAck> ReadAckAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<CommandAck>(body, GatewayJson.Options)
            ?? throw new InvalidOperationException("The response carried no command ack.");
    }
}
