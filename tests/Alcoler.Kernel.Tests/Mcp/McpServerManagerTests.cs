using Alcoler.Adapters.Mcp;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Mcp;

public class McpServerManagerTests
{
    [Fact]
    public async Task GetToolsAsync_AggregatesPrefixedToolsFromAllServers()
    {
        await using var manager = CreateManager(
            [Server("alpha", "cmd-a"), Server("beta", "cmd-b")],
            config => TransportWithScriptedServer(
                [Definitions.Tool(config.Name == "alpha" ? "echo" : "search")]));

        Assert.All(manager.Statuses, status => Assert.Equal(McpServerState.Starting, status.State));

        var tools = await manager.GetToolsAsync(CancellationToken.None);

        Assert.Equal(["alpha_echo", "beta_search"], tools.Select(tool => tool.Contract.Name));
        Assert.All(manager.Statuses, status =>
        {
            Assert.Equal(McpServerState.Running, status.State);
            Assert.Null(status.Error);
        });
    }

    [Fact]
    public async Task GetToolsAsync_SkipsFailedServerWithoutCrashingAndExposesError()
    {
        await using var manager = CreateManager(
            [Server("broken", "cmd-broken"), Server("healthy", "cmd-healthy")],
            config => config.Name == "broken"
                ? new FailingTransport()
                : TransportWithScriptedServer([Definitions.Tool("echo")]));

        var tools = await manager.GetToolsAsync(CancellationToken.None);

        Assert.Equal(["healthy_echo"], tools.Select(tool => tool.Contract.Name));
        var broken = manager.Statuses.Single(status => status.ServerName == "broken");
        Assert.Equal(McpServerState.Failed, broken.State);
        Assert.NotNull(broken.Error);
        Assert.Contains("pipe broken", broken.Error);
        var healthy = manager.Statuses.Single(status => status.ServerName == "healthy");
        Assert.Equal(McpServerState.Running, healthy.State);
    }

    [Fact]
    public async Task GetToolsAsync_RetriesPreviouslyFailedServer()
    {
        var attempts = 0;
        await using var manager = CreateManager(
            [Server("flaky", "cmd-flaky")],
            _ => ++attempts == 1
                ? new FailingTransport()
                : TransportWithScriptedServer([Definitions.Tool("echo")]));

        Assert.Empty(await manager.GetToolsAsync(CancellationToken.None));
        Assert.Equal(McpServerState.Failed, manager.Statuses.Single().State);

        var tools = await manager.GetToolsAsync(CancellationToken.None);

        Assert.Equal(["flaky_echo"], tools.Select(tool => tool.Contract.Name));
        Assert.Equal(McpServerState.Running, manager.Statuses.Single().State);
    }

    [Fact]
    public async Task StartupTimeout_MarksServerFailed()
    {
        await using var manager = CreateManager(
            [Server("slow", "cmd-slow")],
            _ =>
            {
                var transport = TransportWithScriptedServer([], out var server);
                server.IgnoreMethods.Add("initialize");
                return transport;
            },
            startupTimeout: TimeSpan.FromMilliseconds(150));

        var tools = await manager.GetToolsAsync(CancellationToken.None);

        Assert.Empty(tools);
        var status = manager.Statuses.Single();
        Assert.Equal(McpServerState.Failed, status.State);
        Assert.Contains("did not start", status.Error);
    }

    [Fact]
    public async Task DisposeAsync_DisposesConnections_AndIsIdempotent()
    {
        var manager = CreateManager(
            [Server("alpha", "cmd-a")],
            _ => TransportWithScriptedServer([Definitions.Tool("echo")]));
        var tools = await manager.GetToolsAsync(CancellationToken.None);
        await manager.DisposeAsync();
        await manager.DisposeAsync();

        var result = await tools[0].ExecuteAsync(Context("{}"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("no longer connected", result.SingleText());
    }

    [Fact]
    public async Task Constructor_SkipsDuplicateAndUnnamedServers()
    {
        await using var manager = CreateManager(
            [Server("dup", "cmd"), Server("dup", "cmd"), new McpServerConfig("", "cmd")],
            _ => throw new InvalidOperationException("no server should start"));

        Assert.Equal(["dup"], manager.Statuses.Select(status => status.ServerName));
    }

    private static McpServerConfig Server(string name, string command) =>
        new(name, command, ["--flag"], new Dictionary<string, string> { ["TOOL"] = "1" });

    private static McpServerManager CreateManager(
        IReadOnlyList<McpServerConfig> servers,
        Func<McpServerConfig, IMcpTransport> transportFactory,
        TimeSpan? startupTimeout = null) =>
        new(servers, transportFactory, startupTimeout: startupTimeout);

    private static IMcpTransport TransportWithScriptedServer(IReadOnlyList<McpToolDefinition> tools) =>
        TransportWithScriptedServer(tools, out _);

    private static IMcpTransport TransportWithScriptedServer(
        IReadOnlyList<McpToolDefinition> tools,
        out ScriptedMcpServer server)
    {
        var (client, serverTransport) = MemoryMcpTransport.CreatePair();
        server = new ScriptedMcpServer(serverTransport);
        server.Pages.Add(new McpToolPage(tools));
        return client;
    }

    private static ToolExecutionContext Context(string json) => new(
        new SessionId("sess_mcp"),
        Path.GetTempPath(),
        McpJson.Element(json),
        new LocalToolWorkspace(Path.GetTempPath()),
        TimeProvider.System);
}
