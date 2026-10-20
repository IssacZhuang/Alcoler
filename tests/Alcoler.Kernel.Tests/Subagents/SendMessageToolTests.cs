using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tests.EndToEnd;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Subagents;

public class SendMessageToolTests
{
    [Fact]
    public async Task UnknownRecipient_ReportsError()
    {
        var messenger = new SubagentMessenger();
        var tool = new SendMessageTool(messenger);

        var result = await tool.ExecuteAsync(
            Context("""{"to": "sub_nope", "summary": "s", "message": "hello"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("No active local_agent task found for target sub_nope", result.SingleText());
    }

    [Fact]
    public async Task CompletedAgent_ResumesInBackground_AndReceivesWrappedPrompt()
    {
        await using var harness = TestHarness.Create(
            new ScriptStep.Events(
                new Contracts.Models.ModelStreamFinished(Contracts.Models.ModelFinishReason.Stop)),
            new ScriptStep.Events(
                new Contracts.Models.ModelTextDelta("resumed answer"),
                new Contracts.Models.ModelStreamFinished(Contracts.Models.ModelFinishReason.Stop)));

        await using var childRuntime = harness.BuildRuntime();
        var childSession = await childRuntime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo),
            CancellationToken.None);

        var messenger = new SubagentMessenger();
        var parent = new SessionId("sess_parent");
        var agentId = StrongId.NewSubagentId();
        messenger.Register(parent, new SubagentChannel(agentId, childSession.Id, "probe child", childRuntime));

        using var childProjection = childRuntime.Subscribe(childSession.Id);
        await WaitForPhaseAsync(childProjection, TurnPhase.Idle);

        var tool = new SendMessageTool(messenger);
        var result = await tool.ExecuteAsync(
            Context($$"""{"to": "{{agentId.Value}}", "summary": "follow up", "message": "check the kernel again"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("resumed it in the background with your message", result.SingleText());

        await WaitForPhaseAsync(childProjection, TurnPhase.Idle);
        var delivered = childProjection.Snapshot().Rows
            .OfType<UserMessageRow>()
            .Select(static row => row.Text)
            .FirstOrDefault(text => text.Contains("check the kernel again"));
        Assert.NotNull(delivered);
        // A resumed agent receives the raw message as its next prompt, with no wrapper.
        Assert.Equal("check the kernel again", delivered);
    }

    [Fact]
    public void Messenger_Find_AcceptsSubIdAndSessionId()
    {
        var messenger = new SubagentMessenger();
        var parent = new SessionId("sess_parent");
        var agentId = StrongId.NewSubagentId();
        var childSession = StrongId.NewSessionId();
        var channel = new SubagentChannel(agentId, childSession, "d", null!);
        messenger.Register(parent, channel);

        Assert.Same(channel, messenger.Find(parent, agentId.Value));
        Assert.Same(channel, messenger.Find(parent, childSession.Value));
        Assert.Null(messenger.Find(new SessionId("sess_other"), agentId.Value));
    }

    internal static ToolExecutionContext Context(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return new ToolExecutionContext(
            new SessionId("sess_parent"),
            Path.GetTempPath(),
            document.RootElement.Clone(),
            null!,
            TimeProvider.System);
    }

    private static async Task WaitForPhaseAsync(IConversationProjection projection, TurnPhase phase)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (projection.Snapshot().State.Phase != phase)
        {
            if (deadline.IsCompleted)
                throw new TimeoutException($"Phase never reached {phase}.");
            await Task.Delay(20);
        }
    }
}
