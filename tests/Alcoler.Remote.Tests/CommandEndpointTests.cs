using System.Net;
using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
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
    public async Task SendText_WithExplicitModelSelection_PassesReasoningOptionsToRuntime()
    {
        await using var runtime = new CommandRecordingRuntime();
        var harness = new CommandRecordingGatewayHarness(runtime);
        await harness.InitializeAsync();
        try
        {
            var (_, token) = await harness.PairAsync();
            const string body = """{"$type":"send_text","commandId":"cmd_model_selection","sessionId":"sess_model_selection","baseRevision":7,"text":"selected model","modelSelection":{"providerId":"provider-custom","modelId":"model-custom","options":{"reasoningLevel":"high","maxOutputTokens":8192}}}""";

            using var response = await harness.SendWithTokenAsync(
                HttpMethod.Post, "api/sessions/sess_model_selection/commands", token, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var ack = await ReadAckAsync(response);
            Assert.True(ack.Accepted);
            var command = Assert.IsType<SendTextCommand>(runtime.Command);
            Assert.Equal(new CommandId("cmd_model_selection"), command.CommandId);
            Assert.Equal(new SessionId("sess_model_selection"), command.SessionId);
            Assert.Equal(7, command.BaseRevision);
            Assert.Equal("selected model", command.Text);
            Assert.Equal(
                new ModelSelection("provider-custom", "model-custom", new ModelCallOptions("high", 8192)),
                command.ModelSelection);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("send_queued_now")]
    [InlineData("remove_queued_input")]
    public async Task QueueCommand_PassesTargetAndRevisionToRuntime(string type)
    {
        await using var runtime = new CommandRecordingRuntime();
        var harness = new CommandRecordingGatewayHarness(runtime);
        await harness.InitializeAsync();
        try
        {
            var (_, token) = await harness.PairAsync();
            var body = $$"""{"$type":"{{type}}","commandId":"cmd_queue_action","sessionId":"sess_queue_action","baseRevision":7,"targetCommandId":"cmd_queued"}""";

            using var response = await harness.SendWithTokenAsync(
                HttpMethod.Post, "api/sessions/sess_queue_action/commands", token, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await ReadAckAsync(response)).Accepted);
            var command = Assert.IsAssignableFrom<ConversationCommand>(runtime.Command);
            Assert.Equal(new CommandId("cmd_queue_action"), command.CommandId);
            Assert.Equal(new SessionId("sess_queue_action"), command.SessionId);
            Assert.Equal(7, command.BaseRevision);
            var target = type == "send_queued_now"
                ? Assert.IsType<SendQueuedNowCommand>(command).TargetCommandId
                : Assert.IsType<RemoveQueuedInputCommand>(command).TargetCommandId;
            Assert.Equal(new CommandId("cmd_queued"), target);
        }
        finally
        {
            await harness.DisposeAsync();
        }
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

    private sealed class CommandRecordingGatewayHarness(IAgentRuntime runtime) : GatewayHarness
    {
        protected override RemoteGatewayOptions CreateOptions(int port) => new()
        {
            Runtime = () => runtime,
            Port = port,
            DataDirectory = DataDirectory,
            ProductVersion = Version,
        };
    }

    private sealed class CommandRecordingRuntime : IAgentRuntime
    {
        public ConversationCommand? Command { get; private set; }

        public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken)
        {
            Command = command;
            return ValueTask.FromResult(CommandAck.AcceptedAt(command.CommandId, command.BaseRevision + 1));
        }

        public ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
            string? workspaceRoot, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<int> UnloadIdleSessionsAsync(
            TimeSpan idleThreshold, SessionId? keepOpen, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IConversationProjection Subscribe(SessionId sessionId) => throw new NotSupportedException();

        public IConversationProjection? TryObserve(SessionId sessionId) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<CommandAck> ReadAckAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<CommandAck>(body, GatewayJson.Options)
            ?? throw new InvalidOperationException("The response carried no command ack.");
    }
}
