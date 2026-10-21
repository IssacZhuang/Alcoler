using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Subagents;
using Xunit;

namespace Alcoler.Kernel.Tests.Subagents;

public class SubagentTaskRegistryTests
{
    [Fact]
    public void Register_FindAndUpdate_RoundTripsTaskInfo()
    {
        var registry = new SubagentTaskRegistry();
        var info = Info();

        registry.Register(info, static _ => ValueTask.CompletedTask);

        var found = registry.Find("sub_test");
        Assert.NotNull(found);
        Assert.Equal(info, found);
        Assert.False(found.Notified);

        registry.Update(info.Id, current => current with
        {
            Status = SubagentStatus.Completed,
            Result = "done",
            EndedAt = current.StartedAt.AddSeconds(1),
        });

        var updated = registry.Find("sub_test");
        Assert.NotNull(updated);
        Assert.Equal(SubagentStatus.Completed, updated.Status);
        Assert.Equal("done", updated.Result);
        Assert.NotNull(updated.EndedAt);
    }

    [Fact]
    public void Register_ReplacesPriorEntryForSameId()
    {
        var registry = new SubagentTaskRegistry();
        var info = Info();
        registry.Register(info, static _ => ValueTask.CompletedTask);
        registry.TryMarkNotified(info.Id.Value);

        registry.Register(info with { Description = "replacement" }, static _ => ValueTask.CompletedTask);

        var found = registry.Find(info.Id.Value);
        Assert.NotNull(found);
        Assert.Equal("replacement", found.Description);
        Assert.False(found.Notified);
    }

    [Fact]
    public void Update_MissingEntry_IsNoOp()
    {
        var registry = new SubagentTaskRegistry();

        registry.Update(new SubagentId("sub_missing"), static info => info with { Status = SubagentStatus.Failed });

        Assert.Null(registry.Find("sub_missing"));
    }

    [Fact]
    public void TryMarkNotified_IsIdempotent_AndFindExposesTheFlag()
    {
        var registry = new SubagentTaskRegistry();
        var info = Info();
        registry.Register(info, static _ => ValueTask.CompletedTask);

        Assert.True(registry.TryMarkNotified(info.Id.Value));
        Assert.False(registry.TryMarkNotified(info.Id.Value));
        var found = registry.Find(info.Id.Value);
        Assert.NotNull(found);
        Assert.True(found.Notified);
        Assert.False(registry.TryMarkNotified("sub_missing"));
    }

    [Fact]
    public async Task TryStopAsync_RunningTask_InvokesDelegate_AndReturnsTrue()
    {
        var registry = new SubagentTaskRegistry();
        var info = Info();
        var stopped = false;
        registry.Register(info, _ =>
        {
            stopped = true;
            return ValueTask.CompletedTask;
        });

        Assert.True(await registry.TryStopAsync(info.Id.Value, CancellationToken.None));
        Assert.True(stopped);
    }

    [Fact]
    public async Task TryStopAsync_TerminalTask_DoesNotInvokeDelegate_AndReturnsFalse()
    {
        var registry = new SubagentTaskRegistry();
        var info = Info() with { Status = SubagentStatus.Completed };
        var stopped = false;
        registry.Register(info, _ =>
        {
            stopped = true;
            return ValueTask.CompletedTask;
        });

        Assert.False(await registry.TryStopAsync(info.Id.Value, CancellationToken.None));
        Assert.False(stopped);
    }

    [Fact]
    public async Task TryStopAsync_AbsentTask_ReturnsFalse()
    {
        var registry = new SubagentTaskRegistry();

        Assert.False(await registry.TryStopAsync("sub_missing", CancellationToken.None));
    }

    [Fact]
    public async Task TryStopAsync_DelegateThrows_ReturnsFalse()
    {
        var registry = new SubagentTaskRegistry();
        var info = Info();
        registry.Register(info, static _ => throw new InvalidOperationException("stop failed"));

        Assert.False(await registry.TryStopAsync(info.Id.Value, CancellationToken.None));
    }

    [Fact]
    public void Restore_ReinstatesSnapshotIncludingNotifiedFlag()
    {
        var registry = new SubagentTaskRegistry();
        var info = Info() with { Status = SubagentStatus.Completed, Result = "done" };
        registry.Register(info, static _ => ValueTask.CompletedTask);
        registry.TryMarkNotified(info.Id.Value);
        var snapshot = registry.Find(info.Id.Value);

        registry.Update(info.Id, current => current with { Status = SubagentStatus.Running, Result = null });
        registry.Restore(info.Id, snapshot!);

        var restored = registry.Find(info.Id.Value);
        Assert.NotNull(restored);
        Assert.Equal(SubagentStatus.Completed, restored.Status);
        Assert.Equal("done", restored.Result);
        Assert.True(restored.Notified);
    }

    private static SubagentTaskInfo Info() =>
        new(
            new SubagentId("sub_test"),
            new SessionId("sess_child"),
            "general-purpose",
            "investigate",
            "investigate the workspace",
            OutputFile: null,
            SubagentStatus.Running,
            Result: null,
            Error: null,
            ToolUses: null,
            DateTimeOffset.UtcNow,
            EndedAt: null);
}
