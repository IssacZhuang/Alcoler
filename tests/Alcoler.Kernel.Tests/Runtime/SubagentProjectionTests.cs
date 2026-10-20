using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Subagents;
using Alcoler.Kernel.Runtime;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SubagentProjectionTests
{
    [Fact]
    public void StartedNotice_PreservesTypedStateAndTextThroughSerialization()
    {
        var state = RunningState();
        var builder = new ProjectionBuilder();
        var ops = builder.Apply(new SequencedEvent(
            1, StrongId.NewSessionId(), state.StartedAt, new SubagentStartedEvent(state)));

        var notice = Assert.IsType<SystemNoticeRow>(Assert.IsType<RowAppended>(Assert.Single(ops)).Row);
        Assert.Equal($"subagent:{state.Id.Value}", notice.RowId);
        Assert.Equal("subagent", notice.Kind);
        Assert.Equal("Subagent started: reader", notice.Text);
        Assert.Equal(state, notice.Subagent);
        Assert.Equal(notice, Assert.Single(builder.Rows()));

        var restored = AlcolerJson.Deserialize<ConversationDelta>(
            AlcolerJson.Serialize(new ConversationDelta(1, ops)));
        Assert.NotNull(restored);
        Assert.Equal(notice, Assert.IsType<SystemNoticeRow>(Assert.IsType<RowAppended>(Assert.Single(restored.Ops)).Row));
    }

    [Theory]
    [InlineData(SubagentStatus.Running, null, "Subagent reader: Running")]
    [InlineData(SubagentStatus.Completed, "Found it", "Subagent reader: Completed - Found it")]
    [InlineData(SubagentStatus.Failed, "Read failed", "Subagent reader: Failed - Read failed")]
    [InlineData(SubagentStatus.Cancelled, null, "Subagent reader: Cancelled")]
    public async Task StatusNotice_PreservesTypedStateAndTextThroughDeltaSnapshotAndReplay(
        SubagentStatus status, string? summary, string expectedText)
    {
        var running = RunningState();
        var store = new SessionEventStore(StrongId.NewSessionId(), TimeProvider.System);
        store.Append(new SubagentStartedEvent(running));
        using var projection = new LiveConversationProjection(store, 0, () => 7);
        var started = Assert.IsType<SystemNoticeRow>(Assert.Single(projection.Snapshot().Rows));
        Assert.Equal(running, started.Subagent);
        Assert.Equal("Subagent started: reader", started.Text);

        var changed = running with
        {
            Status = status,
            ResultSummary = summary,
            EndedAt = status == SubagentStatus.Running ? null : running.StartedAt.AddSeconds(1),
        };
        SessionEvent changedEvent = new SubagentStatusChangedEvent(changed);
        store.Append(AlcolerJson.Deserialize<SessionEvent>(AlcolerJson.Serialize(changedEvent))!);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var deltas = projection.Deltas(timeout.Token).GetAsyncEnumerator();
        Assert.True(await deltas.MoveNextAsync());
        var delta = AlcolerJson.Deserialize<ConversationDelta>(AlcolerJson.Serialize(deltas.Current));
        Assert.NotNull(delta);
        var updated = Assert.IsType<SystemNoticeRow>(Assert.IsType<RowUpserted>(Assert.Single(delta.Ops)).Row);
        Assert.Equal(started.RowId, updated.RowId);
        Assert.Equal("subagent", updated.Kind);
        Assert.Equal(expectedText, updated.Text);
        Assert.Equal(changed, updated.Subagent);

        var snapshot = AlcolerJson.Deserialize<ConversationSnapshot>(AlcolerJson.Serialize(projection.Snapshot()));
        Assert.NotNull(snapshot);
        Assert.Equal(updated, Assert.Single(snapshot.Rows));

        using var replay = new LiveConversationProjection(store, 0, () => 7);
        var replayed = replay.Snapshot();
        Assert.Equal(snapshot.Sequence, replayed.Sequence);
        Assert.Equal(updated, Assert.Single(replayed.Rows));
    }

    [Theory]
    [InlineData("subagent", "Subagent started: reader")]
    [InlineData("turn_error", "Read failed")]
    public void LegacyNotice_WithoutTypedMetadataStillDeserializes(string kind, string text)
    {
        var constructed = new SystemNoticeRow("legacy", kind, text);
        Assert.Null(constructed.Subagent);

        var json = $$"""{"$type":"system_notice","rowId":"legacy","kind":"{{kind}}","text":"{{text}}"}""";
        var restored = Assert.IsType<SystemNoticeRow>(AlcolerJson.Deserialize<ConversationRow>(json));
        Assert.Equal(constructed, restored);
        Assert.Null(restored.Subagent);
    }

    [Fact]
    public void OtherNotices_DoNotGainSubagentMetadata()
    {
        var builder = new ProjectionBuilder();
        builder.Apply(new SequencedEvent(
            1, StrongId.NewSessionId(), DateTimeOffset.UtcNow,
            new TurnFailedEvent(StrongId.NewTurnId(), "Read failed")));

        var notice = Assert.IsType<SystemNoticeRow>(Assert.Single(builder.Rows()));
        Assert.Equal("turn_error", notice.Kind);
        Assert.Equal("Read failed", notice.Text);
        Assert.Null(notice.Subagent);
    }

    private static SubagentState RunningState() => new(
        StrongId.NewSubagentId(),
        StrongId.NewSessionId(),
        "reader",
        SubagentStatus.Running,
        null,
        DateTimeOffset.UtcNow,
        null);
}
