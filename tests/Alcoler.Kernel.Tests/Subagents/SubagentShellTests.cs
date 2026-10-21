using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tests.Runtime;
using Alcoler.Kernel.Tests.Tools;
using Alcoler.Kernel.Tests.WorkspaceTools;
using Xunit;

namespace Alcoler.Kernel.Tests.Subagents;

public class SubagentShellTests
{
    [Fact]
    public async Task ChildInheritsExecutedParentSnapshotInsteadOfChangedPreference_AndKeepsItOnFollowUp()
    {
        await using var fixture = await ShellSnapshotFixture.CreateAsync();
        var preference = ShellSnapshotFixture.Cmd;
        var resolutions = new List<ShellEnvironment?>();
        var inspect = new FakeTool("InspectShell");
        var messenger = new SubagentMessenger();
        var model = new ShellSnapshotModel(
            ShellSnapshotModel.Call("Agent", new { prompt = "Inspect the shell", description = "shell child" }),
            ShellSnapshotModel.Call("InspectShell"), ShellSnapshotModel.Reply("child complete"), ShellSnapshotModel.Reply("parent complete"),
            ShellSnapshotModel.Call("InspectShell"), ShellSnapshotModel.Reply("child follow-up complete"), ShellSnapshotModel.Reply("notification complete"));
        model.BeforeRequest = (_, _) =>
        {
            preference = ShellSnapshotFixture.GitBash;
            return ValueTask.CompletedTask;
        };
        await using var runtime = fixture.Runtime(model, saved =>
        {
            resolutions.Add(saved);
            return saved ?? preference;
        }, [inspect], messenger: messenger);
        var parent = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        await ShellSnapshotFixture.SendAndWaitAsync(runtime, parent.Id, 1, "delegate");

        var firstContext = Assert.Single(inspect.ExecutedContexts);
        Assert.NotEqual(parent.Id, firstContext.SessionId);
        Assert.Equal(ShellSnapshotFixture.Cmd, firstContext.Shell!.Environment);
        var childRecord = await fixture.RecordAsync(firstContext.SessionId);
        Assert.Equal(parent.Id, childRecord.ParentSessionId);
        Assert.Equal(ShellSnapshotFixture.Cmd, childRecord.Shell);
        Assert.Equal(new ShellEnvironment?[] { null, ShellSnapshotFixture.Cmd }, resolutions);
        Assert.All(model.Requests, request => Assert.Equal("cmd.exe", ShellSnapshotFixture.PromptShell(request)));

        var channel = Assert.IsType<SubagentChannel>(messenger.Find(parent.Id, firstContext.SessionId.Value));
        try
        {
            await ShellSnapshotFixture.SendAndWaitAsync(channel.Runtime, firstContext.SessionId, 2, "follow up");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (model.Requests.Count < 7 || runtime.GetSessionActivity(parent.Id)!.Phase != Contracts.Projections.TurnPhase.Idle)
                await Task.Delay(10, timeout.Token);
            Assert.Equal(2, resolutions.Count);
            Assert.Equal(2, inspect.ExecutedContexts.Count);
            Assert.All(inspect.ExecutedContexts, context => Assert.Equal(ShellSnapshotFixture.Cmd, context.Shell!.Environment));
            Assert.All(model.Requests, request => Assert.Equal("cmd.exe", ShellSnapshotFixture.PromptShell(request)));
        }
        finally
        {
            await channel.Runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task NonresidentParent_LoadsStoredSnapshotAndBuildsPromptFromValidatedFallback()
    {
        await using var fixture = await ShellSnapshotFixture.CreateAsync();
        var stale = new ShellEnvironment("removed-git-bash", ShellDialect.GitBash);
        var parent = await fixture.SeedAsync(stale);
        var resolutions = new List<ShellEnvironment?>();
        var inspect = new FakeTool("InspectShell");
        var messenger = new SubagentMessenger();
        var model = new ShellSnapshotModel(ShellSnapshotModel.Call("InspectShell"), ShellSnapshotModel.Reply("child complete"));
        await using var runtime = fixture.Runtime(model, saved =>
        {
            resolutions.Add(saved);
            return ShellSnapshotFixture.Cmd;
        }, [inspect], messenger: messenger);
        var agent = SubagentWiring.CreateTool(() => runtime, messenger);
        var context = new ToolExecutionContext(parent.Id, fixture.Root,
            JsonSerializer.SerializeToElement(new { prompt = "Inspect the shell", description = "detached parent child" }),
            NoArtifactWorkspace.Instance, TimeProvider.System);
        model.BeforeRequest = async (_, ct) =>
        {
            var sessions = await fixture.Store.LoadMessagesAsync(parent.Id, ct);
            Assert.Empty(sessions);
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await agent.ExecuteAsync(context, timeout.Token);
        Assert.False(result.IsError);
        var childContext = Assert.Single(inspect.ExecutedContexts);
        var channel = Assert.IsType<SubagentChannel>(messenger.Find(parent.Id, childContext.SessionId.Value));
        try
        {
            Assert.Equal(stale, Assert.Single(resolutions));
            Assert.Equal(ShellSnapshotFixture.Cmd, childContext.Shell!.Environment);
            Assert.Equal(ShellSnapshotFixture.Cmd, (await fixture.RecordAsync(childContext.SessionId)).Shell);
            Assert.Equal(stale, (await fixture.RecordAsync(parent.Id)).Shell);
            Assert.All(model.Requests, request => Assert.Equal("cmd.exe", ShellSnapshotFixture.PromptShell(request)));
            Assert.Null(runtime.GetSessionShell(parent.Id));
        }
        finally
        {
            await channel.Runtime.DisposeAsync();
        }
    }
}
