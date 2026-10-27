using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class TurnInputSignalTests
{
    [Fact]
    public async Task Pulse_WakesWaiter_AndAdvancesVersion()
    {
        var signal = new TurnInputSignal();
        var seen = signal.Version;
        var wait = signal.WaitAsync(seen, CancellationToken.None);
        Assert.False(wait.IsCompleted);

        signal.Pulse();

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(seen + 1, signal.Version);
    }

    [Fact]
    public void Wait_WithStaleVersion_CompletesImmediately()
    {
        var signal = new TurnInputSignal();
        signal.Pulse();

        Assert.Equal(1, signal.Version);
        Assert.True(signal.WaitAsync(0, CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task Waiter_WakesOncePerPulse()
    {
        var signal = new TurnInputSignal();
        var first = signal.WaitAsync(signal.Version, CancellationToken.None);
        signal.Pulse();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        var second = signal.WaitAsync(signal.Version, CancellationToken.None);
        Assert.False(second.IsCompleted);
        signal.Pulse();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
