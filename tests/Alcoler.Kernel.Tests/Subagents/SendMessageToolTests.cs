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
    public async Task CompletedAgent_ResumesInBackground_AndReceivesRawPrompt()
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
        Assert.Contains("was stopped (completed); resumed it in the background with your message", result.SingleText());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (childProjection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != TurnPhase.Idle || !snapshot.Rows.OfType<UserMessageRow>()
                   .Any(row => row.Text.Contains("check the kernel again"))))
            await Task.Delay(20, timeout.Token);
        var delivered = childProjection.Snapshot().Rows
            .OfType<UserMessageRow>()
            .Select(static row => row.Text)
            .FirstOrDefault(text => text.Contains("check the kernel again"));
        Assert.NotNull(delivered);
        // A resumed agent receives the raw message as its next prompt, with no wrapper.
        Assert.Equal("check the kernel again", delivered);
    }

    [Fact]
    public async Task BusyAgent_ReceivesCoordinatorGuide_InItsActiveTurn()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new FakeTool("Gate", execute: async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return ToolExecutionResult.Text("ready");
        });
        var callId = new ToolCallId("gate-call");
        await using var harness = TestHarness.Create(
            new ScriptStep.Events(
                new Contracts.Models.ModelToolCallReady(new Contracts.Models.ToolCall(callId, "Gate", JsonDocument.Parse("{}").RootElement.Clone())),
                new Contracts.Models.ModelStreamFinished(Contracts.Models.ModelFinishReason.ToolCalls)),
            new ScriptStep.Events(
                new Contracts.Models.ModelTextDelta("finished"),
                new Contracts.Models.ModelStreamFinished(Contracts.Models.ModelFinishReason.Stop)));
        await using var childRuntime = harness.BuildRuntime(gate);
        var session = await childRuntime.CreateSessionAsync(
            new Contracts.Sessions.SessionOptions(harness.Root, PermissionMode: PermissionMode.Yolo), CancellationToken.None);
        var messenger = new SubagentMessenger();
        var agentId = StrongId.NewSubagentId();
        messenger.Register(new SessionId("sess_parent"), new SubagentChannel(agentId, session.Id, "child", childRuntime));
        using var projection = childRuntime.Subscribe(session.Id);
        Assert.True((await childRuntime.SubmitAsync(new SendTextCommand(
            StrongId.NewCommandId(), session.Id, 0, "start"), CancellationToken.None)).Accepted);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await new SendMessageTool(messenger).ExecuteAsync(
                Context($$"""{"to":"{{agentId.Value}}","summary":"narrow scope","message":"focus on Kernel"}"""), CancellationToken.None);
            Assert.False(result.IsError);
            Assert.Contains("was sent to its active turn", result.SingleText());
            release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (projection.Snapshot().State.Phase != TurnPhase.Idle)
                await Task.Delay(10, timeout.Token);
            Assert.Contains(projection.Snapshot().Rows.OfType<UserMessageRow>(), row => row.Text ==
                "The coordinator sent a message while you were working:\nfocus on Kernel\n\nAddress this before completing your current task.");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task RespondToCoordinator_QueuesEscapedEnvelope_AndKeepsContinuationGuidance()
    {
        string? body = null;
        var tool = new RespondToCoordinatorTool(new SubagentId("sub_child"), "Explore", (message, _) =>
        {
            body = message;
            return ValueTask.CompletedTask;
        });
        var result = await tool.ExecuteAsync(
            Context("""{"summary":"Found <entry>","message":"Use \"kernel\" & don't stop."}"""), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("""
            <subagent-message>
            <agent-id>sub_child</agent-id>
            <agent-type>Explore</agent-type>
            <summary>Found &lt;entry&gt;</summary>
            <message>Use &quot;kernel&quot; &amp; don&apos;t stop.</message>
            </subagent-message>
            """.Replace("\r\n", "\n"), body);
        Assert.Contains("was queued for the coordinator.", result.SingleText());
        Assert.EndsWith("Continue the current task unless the coordinator explicitly changed or ended it.", result.SingleText());
    }

    [Fact]
    public async Task RespondToCoordinator_DeliveryFailure_PreservesContinuationBeforeError()
    {
        var tool = new RespondToCoordinatorTool(new SubagentId("sub_child"), "Explore",
            (_, _) => ValueTask.FromException(new InvalidOperationException("coordinator unavailable")));
        var result = await tool.ExecuteAsync(Context("""{"summary":"s","message":"hello"}"""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("failed to queue for the coordinator. Continue the current task", result.SingleText());
        Assert.EndsWith("Failure: coordinator unavailable.", result.SingleText());
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
