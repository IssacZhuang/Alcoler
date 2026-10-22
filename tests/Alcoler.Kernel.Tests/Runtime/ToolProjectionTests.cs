using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class ToolProjectionTests
{
    [Fact]
    public void FileChangeStats_SurviveResultEventSerializationAndMessageCommit()
    {
        var sessionId = StrongId.NewSessionId();
        var turnId = StrongId.NewTurnId();
        var callId = StrongId.NewToolCallId();
        var at = DateTimeOffset.UtcNow;
        var builder = new ProjectionBuilder();

        builder.Apply(new SequencedEvent(1, sessionId, at, new ToolCallStartedEvent(
            turnId, callId, "Edit", JsonSerializer.SerializeToElement(new { file_path = "a.cs" }))));

        var result = ToolExecutionResult.Text("updated", fileChange: new FileChangeStats(2, 5));
        var resultEvent = AlcolerJson.Deserialize<SessionEvent>(AlcolerJson.Serialize<SessionEvent>(
            new ToolCallResultEvent(turnId, callId, "Edit", result, TimeSpan.FromMilliseconds(12))))!;
        var resultOps = builder.Apply(new SequencedEvent(2, sessionId, at, resultEvent));

        var row = Assert.IsType<ToolCallRow>(Assert.Single(builder.Rows()));
        Assert.Equal(ToolCallStatus.Succeeded, row.Status);
        Assert.Equal(new FileChangeStats(2, 5), row.FileChange);

        var delta = AlcolerJson.Deserialize<ConversationDelta>(
            AlcolerJson.Serialize(new ConversationDelta(2, resultOps)))!;
        var deltaRow = Assert.IsType<ToolCallRow>(Assert.IsType<RowUpserted>(Assert.Single(delta.Ops)).Row);
        Assert.Equal(new FileChangeStats(2, 5), deltaRow.FileChange);

        var (rows, state) = builder.SnapshotState();
        var snapshot = AlcolerJson.Deserialize<ConversationSnapshot>(
            AlcolerJson.Serialize(new ConversationSnapshot(2, 2, rows, state)))!;
        Assert.Equal(new FileChangeStats(2, 5), Assert.IsType<ToolCallRow>(Assert.Single(snapshot.Rows)).FileChange);

        var message = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.User,
            [new ToolResultPart(StrongId.NewPartId(), callId, result.Content)],
            at);
        builder.Apply(new SequencedEvent(3, sessionId, at, new UserMessageCommittedEvent(message)));

        row = Assert.IsType<ToolCallRow>(Assert.Single(builder.Rows()));
        Assert.Equal(new FileChangeStats(2, 5), row.FileChange);
    }
}
