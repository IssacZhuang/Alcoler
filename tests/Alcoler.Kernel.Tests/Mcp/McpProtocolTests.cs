using System.Text.Json;
using Alcoler.Adapters.Mcp;
using Xunit;

namespace Alcoler.Kernel.Tests.Mcp;

public class McpProtocolTests
{
    [Fact]
    public async Task InitializeAsync_SendsSpecShapedHandshakeThenInitializedNotification()
    {
        var (connection, server) = McpTestHarness.Connect();
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);

            var request = (await McpTestWait.MessagesAsync(server, "initialize", 1))[0];
            Assert.Equal("2.0", request.GetProperty("jsonrpc").GetString());
            Assert.Equal(JsonValueKind.Number, request.GetProperty("id").ValueKind);
            var parameters = request.GetProperty("params");
            Assert.Equal("2025-06-18", parameters.GetProperty("protocolVersion").GetString());
            Assert.Equal(JsonValueKind.Object, parameters.GetProperty("capabilities").ValueKind);
            Assert.False(string.IsNullOrEmpty(parameters.GetProperty("clientInfo").GetProperty("name").GetString()));

            var notification = (await McpTestWait.MessagesAsync(server, "notifications/initialized", 1))[0];
            Assert.False(notification.TryGetProperty("id", out _));
            Assert.Equal("2025-06-18", connection.ProtocolVersion);
            Assert.Equal("scripted", connection.ServerInfo?.Name);
        }
    }

    [Fact]
    public async Task InitializeAsync_Accepts2024_11_05ServerVersion()
    {
        var (connection, server) = McpTestHarness.Connect(s => s.ProtocolVersion = "2024-11-05");
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            Assert.Equal("2024-11-05", connection.ProtocolVersion);
        }
    }

    [Fact]
    public async Task InitializeAsync_UnknownServerVersion_FallsBackTo2024_11_05()
    {
        var (connection, server) = McpTestHarness.Connect(s => s.ProtocolVersion = "1999-09-09");
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            Assert.Equal("2024-11-05", connection.ProtocolVersion);
        }
    }

    [Fact]
    public async Task ReceiveLoop_ToleratesCarriageReturnsBlankAndMalformedLines()
    {
        var (connection, server) = McpTestHarness.Connect(s =>
        {
            s.TerminateResponsesWithCarriageReturn = true;
            s.JunkLinesBeforeFirstResponse = ["", "not-json"];
            s.Pages.Add(new McpToolPage([Definitions.Tool("alpha")]));
        });
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            var tools = await connection.ListToolsAsync(CancellationToken.None);
            Assert.Equal(["alpha"], tools.Select(tool => tool.Name));
        }
    }

    [Fact]
    public async Task ListToolsAsync_FollowsNextCursorPagination()
    {
        var (connection, server) = McpTestHarness.Connect();
        server.Pages.Add(new McpToolPage([Definitions.Tool("alpha"), Definitions.Tool("beta")], "page-2"));
        server.Pages.Add(new McpToolPage([Definitions.Tool("gamma")]));
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            var tools = await connection.ListToolsAsync(CancellationToken.None);

            Assert.Equal(["alpha", "beta", "gamma"], tools.Select(tool => tool.Name));
            var listRequests = await McpTestWait.MessagesAsync(server, "tools/list", 2);
            Assert.False(listRequests[0].GetProperty("params").TryGetProperty("cursor", out _));
            Assert.Equal("page-2", listRequests[1].GetProperty("params").GetProperty("cursor").GetString());
        }
    }

    [Fact]
    public async Task ListToolsAsync_SkipsToolsWithoutName()
    {
        var (connection, server) = McpTestHarness.Connect();
        server.Pages.Add(new McpToolPage([Definitions.Tool(null, "anonymous"), Definitions.Tool("named")]));
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            var tools = await connection.ListToolsAsync(CancellationToken.None);
            Assert.Equal(["named"], tools.Select(tool => tool.Name));
        }
    }

    [Fact]
    public async Task CallToolAsync_SendsNameAndArgumentsAndReturnsTextParts()
    {
        var (connection, server) = McpTestHarness.Connect();
        server.CallResponder = _ => ScriptedCallResponse.TextParts(false, "hello", new string('x', 200_000));
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            var outcome = await connection.CallToolAsync(
                "echo",
                Element("""{"city":"ams"}"""),
                CancellationToken.None);

            Assert.False(outcome.IsError);
            Assert.Equal(2, outcome.TextParts.Count);
            Assert.Equal("hello", outcome.TextParts[0]);
            Assert.Equal(200_000, outcome.TextParts[1].Length);

            var call = (await McpTestWait.MessagesAsync(server, "tools/call", 1))[0];
            Assert.Equal("echo", call.GetProperty("params").GetProperty("name").GetString());
            Assert.Equal("ams", call.GetProperty("params").GetProperty("arguments").GetProperty("city").GetString());
        }
    }

    [Fact]
    public async Task CallToolAsync_MapsIsErrorAndSkipsNonTextParts()
    {
        var (connection, server) = McpTestHarness.Connect();
        server.CallResponder = _ => new ScriptedCallResponse(
            """{"content":[{"type":"image","data":"AAAA"},{"type":"text","text":"boom"}],"isError":true}""");
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            var outcome = await connection.CallToolAsync("echo", null, CancellationToken.None);

            Assert.True(outcome.IsError);
            Assert.Equal(["boom"], outcome.TextParts);
        }
    }

    [Fact]
    public async Task CallToolAsync_JsonRpcError_ThrowsRemoteExceptionWithCode()
    {
        var (connection, server) = McpTestHarness.Connect();
        server.CallResponder = _ => new ScriptedCallResponse(ErrorCode: -32602, ErrorMessage: "bad arguments");
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            var exception = await Assert.ThrowsAsync<McpRemoteException>(
                () => connection.CallToolAsync("echo", null, CancellationToken.None));

            Assert.Equal(-32602, exception.Code);
            Assert.Contains("bad arguments", exception.Message);
        }
    }

    [Fact]
    public async Task ServerNotificationsAndRequests_AreIgnoredWithoutBreakingTheConnection()
    {
        var (connection, server) = McpTestHarness.Connect();
        server.Pages.Add(new McpToolPage([Definitions.Tool("alpha")]));
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            await server.SendRawAsync("""{"jsonrpc":"2.0","method":"notifications/progress","params":{}}""");
            await server.SendRawAsync("""{"jsonrpc":"2.0","id":99,"method":"sampling/createMessage","params":{}}""");

            var tools = await connection.ListToolsAsync(CancellationToken.None);
            Assert.Equal(["alpha"], tools.Select(tool => tool.Name));
        }
    }

    [Fact]
    public async Task RequestIdsAreUniquePerConnection()
    {
        var (connection, server) = McpTestHarness.Connect();
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            await connection.ListToolsAsync(CancellationToken.None);
            await connection.CallToolAsync("echo", null, CancellationToken.None);

            var ids = new HashSet<string>();
            foreach (var message in server.ReceivedMessages)
            {
                if (message.TryGetProperty("id", out var id))
                    ids.Add(id.GetRawText());
            }
            Assert.Equal(3, ids.Count);
        }
    }

    [Fact]
    public async Task DisposeAsync_FailsPendingRequests()
    {
        var (connection, server) = McpTestHarness.Connect();
        server.IgnoreMethods.Add("tools/call");
        await using (connection)
        {
            await connection.InitializeAsync(CancellationToken.None);
            var pending = connection.CallToolAsync("echo", null, CancellationToken.None);
            await connection.DisposeAsync();

            await Assert.ThrowsAsync<McpTransportException>(() => pending);
        }
        await server.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task InitializeAsync_PropagatesCallerCancellation()
    {
        var (connection, server) = McpTestHarness.Connect();
        server.IgnoreMethods.Add("initialize");
        await using (connection)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => connection.InitializeAsync(cts.Token));
        }
    }

    [Fact]
    public async Task TransportFailure_FailsPendingInitialize()
    {
        var connection = new McpServerConnection("broken", new FailingTransport());
        await using (connection)
        {
            var exception = await Assert.ThrowsAsync<McpTransportException>(
                () => connection.InitializeAsync(CancellationToken.None));
            Assert.Contains("pipe broken", exception.Message);
        }
    }

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
