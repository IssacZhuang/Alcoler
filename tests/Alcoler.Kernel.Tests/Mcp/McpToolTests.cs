using System.Text.Json;
using Alcoler.Adapters.Mcp;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Mcp;

public class McpToolTests
{
    [Fact]
    public void Contract_UsesServerUnderscoreToolName_AndPassesDescriptionAndSchemaThrough()
    {
        var tool = NewTool(schemaJson: """{"type":"object","properties":{"city":{"type":"string"}}}""");

        Assert.Equal("test_echo", tool.Contract.Name);
        Assert.Equal("Echoes input", tool.Contract.Description);
        Assert.Equal("string", tool.Contract.InputSchema.GetProperty("properties").GetProperty("city").GetProperty("type").GetString());
    }

    [Fact]
    public void Contract_MissingOrNonObjectInputSchema_FallsBackToPermissiveObjectSchema()
    {
        Assert.Equal("object", NewTool(schemaJson: null).Contract.InputSchema.GetProperty("type").GetString());
        Assert.Equal("object", NewTool(schemaJson: "\"object\"").Contract.InputSchema.GetProperty("type").GetString());
    }

    [Fact]
    public void Metadata_WithoutAnnotations_IsConservative()
    {
        var metadata = NewTool().Metadata;

        Assert.False(metadata.ReadOnly);
        Assert.False(metadata.Destructive);
        Assert.False(metadata.ConcurrentSafe);
        Assert.Equal(SideEffectScope.System, metadata.SideEffectScope);
        Assert.Equal(RiskLevel.Medium, metadata.RiskLevel);
        Assert.True(metadata.NeedsApproval);
        Assert.False(metadata.AllowedInPlanMode);
    }

    [Fact]
    public void Metadata_ReadOnlyHint_MapsToReadOnlyLowRiskNoApproval()
    {
        var metadata = NewTool("""{"readOnlyHint":true}""").Metadata;

        Assert.True(metadata.ReadOnly);
        Assert.Equal(SideEffectScope.None, metadata.SideEffectScope);
        Assert.Equal(RiskLevel.Low, metadata.RiskLevel);
        Assert.False(metadata.NeedsApproval);
        Assert.True(metadata.AllowedInPlanMode);
        Assert.True(metadata.ConcurrentSafe);
    }

    [Fact]
    public void Metadata_DestructiveHint_MapsToDestructiveHighRiskApproval()
    {
        var metadata = NewTool("""{"destructiveHint":true}""").Metadata;

        Assert.True(metadata.Destructive);
        Assert.Equal(RiskLevel.High, metadata.RiskLevel);
        Assert.True(metadata.NeedsApproval);
        Assert.False(metadata.AllowedInPlanMode);
    }

    [Fact]
    public void Metadata_IdempotentHint_MapsToConcurrentSafe()
    {
        var metadata = NewTool("""{"idempotentHint":true}""").Metadata;

        Assert.True(metadata.ConcurrentSafe);
        Assert.True(metadata.NeedsApproval);
    }

    [Fact]
    public async Task ExecuteAsync_SendsArgumentsAndMapsTextResult()
    {
        var (connection, server, tool) = await ConnectedToolAsync();
        await using (connection)
        {
            JsonElement? seenParams = null;
            server.CallResponder = parameters =>
            {
                seenParams = parameters;
                return ScriptedCallResponse.TextParts(false, "result text");
            };

            var result = await tool.ExecuteAsync(Context("""{"city":"ams"}"""), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.Equal("result text", result.SingleText());
            Assert.NotNull(seenParams);
            Assert.Equal("echo", seenParams!.Value.GetProperty("name").GetString());
            Assert.Equal("ams", seenParams.Value.GetProperty("arguments").GetProperty("city").GetString());
        }
    }

    [Fact]
    public async Task ExecuteAsync_MapsServerIsErrorWithAllTextParts()
    {
        var (connection, server, tool) = await ConnectedToolAsync();
        await using (connection)
        {
            server.CallResponder = _ => ScriptedCallResponse.TextParts(true, "first", "second");

            var result = await tool.ExecuteAsync(Context("{}"), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Equal(2, result.Content.Count);
            Assert.Equal("first", result.Content.OfType<ToolResultText>().First().Text);
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonObjectArguments_ReturnsErrorWithoutCallingServer()
    {
        var (connection, server, tool) = await ConnectedToolAsync();
        await using (connection)
        {
            var result = await tool.ExecuteAsync(Context("\"just a string\""), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains("JSON object", result.SingleText());
            Assert.DoesNotContain(server.ReceivedMessages, message =>
                message.TryGetProperty("method", out var method) && method.GetString() == "tools/call");
        }
    }

    [Fact]
    public async Task ExecuteAsync_RemoteJsonRpcError_MapsToErrorResult()
    {
        var (connection, server, tool) = await ConnectedToolAsync();
        await using (connection)
        {
            server.CallResponder = _ => new ScriptedCallResponse(ErrorCode: -32602, ErrorMessage: "bad arguments");

            var result = await tool.ExecuteAsync(Context("{}"), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains("-32602", result.SingleText());
            Assert.Contains("bad arguments", result.SingleText());
        }
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsTimeoutError()
    {
        var (connection, server, _) = await ConnectedToolAsync();
        await using (connection)
        {
            server.IgnoreMethods.Add("tools/call");
            var tool = new McpTool(connection, Definitions.Tool("echo"), TimeSpan.FromMilliseconds(150));

            var result = await tool.ExecuteAsync(Context("{}"), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Contains("timed out", result.SingleText());
        }
    }

    [Fact]
    public async Task ExecuteAsync_ExternalCancellation_Propagates()
    {
        var (connection, server, _) = await ConnectedToolAsync();
        await using (connection)
        {
            server.IgnoreMethods.Add("tools/call");
            var tool = new McpTool(connection, Definitions.Tool("echo"), TimeSpan.FromSeconds(30));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await tool.ExecuteAsync(Context("{}"), cts.Token));
        }
    }

    [Fact]
    public async Task ExecuteAsync_AfterConnectionDisposal_ReturnsError()
    {
        var (connection, server, tool) = await ConnectedToolAsync();
        await connection.DisposeAsync();
        await server.DisposeAsync();

        var result = await tool.ExecuteAsync(Context("{}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("no longer connected", result.SingleText());
    }

    [Fact]
    public void Metadata_DefaultTimeoutMatchesCallTimeout()
    {
        var tool = new McpTool(DisconnectedConnection(), Definitions.Tool("echo"), TimeSpan.FromSeconds(5));

        Assert.Equal(5_000, tool.Metadata.DefaultTimeoutMs);
        Assert.Equal(120_000, (int)McpTool.DefaultCallTimeout.TotalMilliseconds);
    }

    private static McpServerConnection DisconnectedConnection()
    {
        var (client, _) = MemoryMcpTransport.CreatePair();
        return new McpServerConnection("test", client);
    }

    private static McpTool NewTool(string? annotationsJson = null, string? schemaJson = Definitions.ObjectSchema) =>
        new(
            DisconnectedConnection(),
            Definitions.Tool("echo", "Echoes input", schemaJson, annotationsJson));

    private static async Task<(McpServerConnection Connection, ScriptedMcpServer Server, McpTool Tool)> ConnectedToolAsync()
    {
        var (connection, server) = McpTestHarness.Connect(s => s.Pages.Add(new McpToolPage([Definitions.Tool("echo", "Echoes input")])));
        await connection.InitializeAsync(CancellationToken.None);
        var definitions = await connection.ListToolsAsync(CancellationToken.None);
        return (connection, server, new McpTool(connection, definitions[0], TimeSpan.FromSeconds(5)));
    }

    private static ToolExecutionContext Context(string json) =>
        new(
            new SessionId("sess_mcp"),
            Path.GetTempPath(),
            JsonDocument.Parse(json).RootElement.Clone(),
            new LocalToolWorkspace(Path.GetTempPath()),
            TimeProvider.System);
}
