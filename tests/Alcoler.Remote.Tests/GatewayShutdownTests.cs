using System.Diagnostics;
using System.Net.WebSockets;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

public sealed class GatewayShutdownTests
{
    [Fact]
    public async Task StopAsync_WithConnectedWebSocket_CompletesWithinShutdownBudget()
    {
        var harness = new GatewayHarness();
        await harness.InitializeAsync();
        try
        {
            var (sessionId, token) = await harness.NewSessionAsync();

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = await WebSocketFrames.ConnectAsync(
                new Uri($"{harness.BaseAddress}api/sessions/{sessionId.Value}/stream?token={Uri.EscapeDataString(token)}"),
                cancellation.Token);
            try
            {
                var snapshotFrame = await WebSocketFrames.ReceiveUntilAsync(
                    socket,
                    document => document.RootElement.GetProperty("type").GetString() == "snapshot",
                    TimeSpan.FromSeconds(15));
                Assert.NotNull(snapshotFrame);

                // Idle streams never drain on their own; without a bounded shutdown the
                // host would sit on them far longer than desktop exit can afford.
                var clock = Stopwatch.StartNew();
                await harness.Gateway.StopAsync().WaitAsync(TimeSpan.FromSeconds(12));
                clock.Stop();
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12),
                    $"gateway stop with a connected stream took {clock.Elapsed}");
            }
            finally
            {
                socket.Abort();
                socket.Dispose();
            }
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }
}
