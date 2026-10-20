using Alcoler.Workbench.Terminal;
using Xunit;
using Assert = Xunit.Assert;

namespace Alcoler.Workbench.Tests.Terminal;

/// <summary>
/// Real PTY round-trip. All waits are bounded; if the environment cannot spawn a
/// PTY these fail on timeout rather than hang.
/// </summary>
public class TerminalServiceTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Shell_Spawns_EchoesBack_AndExits()
    {
        using var service = new PortaPtyTerminalService();
        var output = new LockFreeAppend();

        var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OutputReceived += OnChunk;
        service.Exited += (_, code) => exited.TrySetResult(code);

        try
        {
            await service.StartAsync(Path.GetTempPath(), 120, 30, CancellationToken.None);
            Assert.True(service.IsRunning);

            var first = await Task.WhenAny(firstChunk.Task, Task.Delay(WaitLimit));
            Assert.True(first == firstChunk.Task, "No output chunk arrived within 10 seconds of spawn.");

            var marker = "alcoler_pty_probe_" + Guid.NewGuid().ToString("N")[..6];
            await service.WriteAsync("echo " + marker + "\r", CancellationToken.None);
            Assert.True(
                await WaitForAsync(() => output.Text.Contains(marker, StringComparison.Ordinal)),
                "Marker not observed in PTY output within 10 seconds.");

            await service.WriteAsync("exit\r", CancellationToken.None);
            var finished = await Task.WhenAny(exited.Task, Task.Delay(WaitLimit));
            Assert.True(finished == exited.Task, "Shell did not exit within 10 seconds of 'exit'.");
        }
        finally
        {
            service.OutputReceived -= OnChunk;
            await service.StopAsync();
        }

        void OnChunk(object? sender, string chunk)
        {
            output.Append(chunk);
            firstChunk.TrySetResult();
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(100);
        }
        return condition();
    }

    private sealed class LockFreeAppend
    {
        private readonly object _gate = new();
        private readonly System.Text.StringBuilder _builder = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _builder.ToString();
                }
            }
        }

        public void Append(string chunk)
        {
            lock (_gate)
            {
                _builder.Append(chunk);
            }
        }
    }
}
