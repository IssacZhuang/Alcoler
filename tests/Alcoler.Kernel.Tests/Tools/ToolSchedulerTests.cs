using Xunit;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Tests.Tools;

public class ToolSchedulerTests
{
    private static ToolScheduler Scheduler(params FakeTool[] tools) => new(new ToolRegistry(tools));

    private static ScheduledToolCall Call(FakeTool tool) => new(tool.Call());

    [Fact]
    public void ParallelSafeCalls_RunInOneGroup()
    {
        var read = new FakeTool("ReadFile");
        var grep = new FakeTool("Grep");
        var groups = Scheduler(read, grep).Schedule([Call(read), Call(grep), Call(read)]);

        var group = Assert.Single(groups);
        Assert.True(group.Parallel);
        Assert.Equal(3, group.Calls.Count);
    }

    [Fact]
    public void UnsafeCall_GetsOwnGroupBetweenParallelGroups()
    {
        var read = new FakeTool("ReadFile");
        var write = new FakeTool("WriteFile", FakeToolMetadata.Serial());
        var groups = Scheduler(read, write).Schedule([Call(read), Call(write), Call(read)]);

        Assert.Equal(3, groups.Count);
        Assert.True(groups[0].Parallel);
        Assert.False(groups[1].Parallel);
        Assert.True(groups[2].Parallel);
        Assert.All(groups, group => Assert.Single(group.Calls));
    }

    [Fact]
    public void TwoUnsafeCalls_NeverShareAGroup()
    {
        var write = new FakeTool("WriteFile", FakeToolMetadata.Serial());
        var bash = new FakeTool("Bash", FakeToolMetadata.SystemSideEffect());
        var groups = Scheduler(write, bash).Schedule([Call(write), Call(bash)]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group =>
        {
            Assert.False(group.Parallel);
            Assert.Single(group.Calls);
        });
    }

    [Fact]
    public void DestructiveTool_IsNeverParallel()
    {
        var destructive = new FakeTool("Danger", FakeToolMetadata.Destructive());
        Assert.False(ToolScheduler.CanRunParallel(destructive));

        var read = new FakeTool("ReadFile");
        var groups = Scheduler(destructive, read).Schedule([Call(destructive), Call(read)]);
        Assert.Equal(2, groups.Count);
        Assert.False(groups[0].Parallel);
        Assert.True(groups[1].Parallel);
    }

    [Fact]
    public void SystemSideEffect_IsNotParallel_EvenWhenConcurrentSafe()
    {
        var bash = new FakeTool("Bash", FakeToolMetadata.SystemSideEffect());
        Assert.False(ToolScheduler.CanRunParallel(bash));
    }

    [Fact]
    public void NullTool_IsNotParallel()
    {
        Assert.False(ToolScheduler.CanRunParallel(null));
    }

    [Fact]
    public void ParallelGroup_CapsAtMaxConcurrency()
    {
        var read = new FakeTool("ReadFile");
        var calls = Enumerable.Range(0, 25).Select(_ => Call(read)).ToList();
        var groups = Scheduler(read).Schedule(calls);

        Assert.Equal([10, 10, 5], groups.Select(static group => group.Calls.Count));
        Assert.All(groups, group => Assert.True(group.Parallel));
        Assert.Equal(ToolScheduler.MaxConcurrency, groups[0].Calls.Count);
    }

    [Fact]
    public void UnknownTool_GetsOwnSerialGroup()
    {
        var read = new FakeTool("ReadFile");
        var unknown = new ScheduledToolCall(new ToolCall(
            StrongId.NewToolCallId(),
            "DoesNotExist",
            System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone()));
        var groups = Scheduler(read).Schedule([Call(read), unknown]);

        Assert.Equal(2, groups.Count);
        Assert.True(groups[0].Parallel);
        Assert.False(groups[1].Parallel);
        Assert.Same(unknown, groups[1].Calls[0]);
    }
}
