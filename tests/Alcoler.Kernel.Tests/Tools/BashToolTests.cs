using Xunit;
using System.Diagnostics;
using Alcoler.Adapters.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class BashToolTests
{
    [Fact]
    public async Task EchoCommand_RoundTripsOutput()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo hello"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("hello", result.SingleText());
        Assert.Contains("Exit code: 0", result.SingleText());
    }

    [Fact]
    public async Task NonZeroExitCode_MarksErrorResult()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "exit 3"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Exit code: 3", result.SingleText());
    }

    [Fact]
    public async Task WorkingDirectory_IsWorkspaceRoot()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo wd123 > wd-probe.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "wd-probe.txt")));
    }

    [Fact]
    public async Task Timeout_KillsProcessTree_AndReportsTimeout()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "ping -n 20 127.0.0.1", "timeout_ms": 1000}"""),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.Contains("timed out", result.SingleText());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }
}
