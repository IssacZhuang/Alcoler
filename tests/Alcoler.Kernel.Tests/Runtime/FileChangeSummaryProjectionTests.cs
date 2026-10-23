using System.Text.Json;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class FileChangeSummaryProjectionTests
{
    [Fact]
    public void TurnCompletion_AggregatesChangedFilesAndSurvivesSerialization()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "alcoler-file-change-tests");
        var builder = new ProjectionBuilder();
        var events = new EventAppender(builder);
        var turnId = StrongId.NewTurnId();

        events.Apply(new SessionStartedEvent(workspaceRoot, "session", PermissionMode.Build, null));
        events.Apply(new TurnStartedEvent(turnId, StrongId.NewCommandId()));
        ApplyToolResult(events, builder, "Edit", Path.Combine(workspaceRoot, "src", "AgentRuntime.cs"), 2, 5);
        ApplyToolResult(events, builder, "Write", Path.Combine(workspaceRoot, "README.md"), 4, 0);
        ApplyToolResult(events, builder, "Edit", Path.Combine(workspaceRoot, "src", "AgentRuntime.cs"), 1, 2);
        events.Apply(new TurnCompletedEvent(turnId, TurnOutcome.Completed));

        var summary = Assert.IsType<FileChangeSummaryRow>(builder.Rows()[^1]);
        Assert.Equal("file-changes:" + turnId.Value, summary.RowId);
        Assert.Equal(7, summary.Added);
        Assert.Equal(7, summary.Removed);
        Assert.Collection(summary.Files,
            file =>
            {
                Assert.Equal("README.md", file.Path);
                Assert.Equal(4, file.Added);
                Assert.Equal(0, file.Removed);
            },
            file =>
            {
                Assert.Equal("src/AgentRuntime.cs", file.Path);
                Assert.Equal(3, file.Added);
                Assert.Equal(7, file.Removed);
            });

        var snapshot = new ConversationSnapshot(2, 2, builder.Rows(), builder.State);
        var roundTrip = AlcolerJson.Deserialize<ConversationSnapshot>(
            AlcolerJson.Serialize(snapshot))!;
        var restored = Assert.IsType<FileChangeSummaryRow>(roundTrip.Rows[^1]);
        Assert.Equal(summary.Files, restored.Files);
        Assert.Equal(summary.Added, restored.Added);
        Assert.Equal(summary.Removed, restored.Removed);
    }

    [Fact]
    public void TurnsWithoutFileChanges_DoNotAppendOrCarrySummaryRows()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "alcoler-file-change-tests");
        var builder = new ProjectionBuilder();
        var events = new EventAppender(builder);

        events.Apply(new SessionStartedEvent(workspaceRoot, "session", PermissionMode.Build, null));

        var firstTurn = StrongId.NewTurnId();
        events.Apply(new TurnStartedEvent(firstTurn, StrongId.NewCommandId()));
        ApplyToolResult(events, builder, "Edit", Path.Combine(workspaceRoot, "first.cs"), 1, 0);
        events.Apply(new TurnCompletedEvent(firstTurn, TurnOutcome.Completed));
        Assert.Single(builder.Rows().OfType<FileChangeSummaryRow>());

        var secondTurn = StrongId.NewTurnId();
        events.Apply(new TurnStartedEvent(secondTurn, StrongId.NewCommandId()));
        events.Apply(new TurnCompletedEvent(secondTurn, TurnOutcome.Completed));
        Assert.Single(builder.Rows().OfType<FileChangeSummaryRow>());
    }

    private static void ApplyToolResult(
        EventAppender events,
        ProjectionBuilder builder,
        string toolName,
        string path,
        int added,
        int removed)
    {
        var turn = Assert.IsType<TurnId>(builder.State.ActiveTurn);
        var callId = StrongId.NewToolCallId();
        events.Apply(new ToolCallStartedEvent(
            turn, callId, toolName, JsonSerializer.SerializeToElement(new { file_path = path })));
        var result = ToolExecutionResult.Text(
            "updated",
            fileChange: new FileChangeStats(added, removed),
            fileChangePath: path);
        events.Apply(new ToolCallResultEvent(
            turn, callId, toolName, result, TimeSpan.FromMilliseconds(1)));
    }

    private sealed class EventAppender(ProjectionBuilder builder)
    {
        private readonly SessionId _sessionId = StrongId.NewSessionId();
        private long _sequence;

        public void Apply(SessionEvent sessionEvent) =>
            builder.Apply(new SequencedEvent(++_sequence, _sessionId, DateTimeOffset.UtcNow, sessionEvent));
    }
}
