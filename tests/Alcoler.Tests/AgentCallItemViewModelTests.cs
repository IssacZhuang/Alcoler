using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Subagents;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class AgentCallItemViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParallelAgents_PairByToolIdAndKeepDispatchOrderAcrossReset(bool reset)
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = StrongId.NewSessionId();
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        using var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, TestSnapshots.State());
        using var model = new ConversationViewModel(runtime, store);
        var first = Tool("first", "First task");
        var second = Tool("second", "Second task");
        var firstNotice = Notice("a", first);
        var secondNotice = Notice("b", second);
        var answer = new AssistantMessageRow("answer", StrongId.NewMessageId(),
            [new TextPart(StrongId.NewPartId(), "Parent answer")], false, Now);
        Apply(model, true, first, second, secondNotice, firstNotice, answer);
        var firstItem = Assert.IsType<AgentCallItemViewModel>(model.Items[0]);
        var secondItem = Assert.IsType<AgentCallItemViewModel>(model.Items[1]);
        Assert.Equal(new[] { "first", "second", "answer" }, model.Items.Select(item => item.RowId));
        Assert.Equal(firstNotice.Subagent!.ChildSessionId, firstItem.Activity!.ChildSessionId);
        Assert.Equal(secondNotice.Subagent!.ChildSessionId, secondItem.Activity!.ChildSessionId);
        Assert.Equal("Explore", firstItem.AgentType);
        Assert.True(firstItem.IsExplore);
        Assert.True(firstItem.IsRunning);
        Assert.Equal("2", model.Status.Agents.Count.ToString());

        var completed = firstNotice with
        {
            Subagent = firstNotice.Subagent with { Status = SubagentStatus.Completed, ResultSummary = "# Child output\nPrivate result", EndedAt = Now },
        };
        if (reset)
            Apply(model, true, first, second, secondNotice, completed, answer);
        else
            Apply(model, false, completed);

        Assert.Same(firstItem, model.Items[0]);
        Assert.Same(secondItem, model.Items[1]);
        Assert.False(firstItem.IsRunning);
        Assert.Empty(firstItem.StatusText);
        Assert.DoesNotContain("Child output", firstItem.AccessibleText);
        Assert.DoesNotContain("Private result", firstItem.ToolTipText);
        Assert.Equal(new[] { "first", "second", "answer" }, model.Items.Select(item => item.RowId));
        Assert.Single(model.Status.Agents);
        Assert.Single(model.Status.EndedAgents);
    }

    [Fact]
    public void LegacyAgentOutput_PairsByExactIdAndDoesNotGuessFromArrivalOrder()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = StrongId.NewSessionId();
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        using var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, TestSnapshots.State());
        using var model = new ConversationViewModel(runtime, store);
        var first = Tool("first", "First task") with
        {
            Output = [new ToolResultText("# Actual result\nagentId: old-a (continue this agent)\n<usage>duration_ms: 1</usage>")],
        };
        var second = Tool("second", "Second task");
        var firstNotice = Notice("old-a", first) with { Subagent = Notice("old-a", first).Subagent! with { ParentToolCallId = null, AgentType = null } };
        var secondNotice = Notice("orphan", second) with { Subagent = Notice("orphan", second).Subagent! with { ParentToolCallId = null } };
        Apply(model, true, first, second, secondNotice, firstNotice);

        Assert.Equal(new[] { "first", "second", "notice-orphan" }, model.Items.Select(item => item.RowId));
        Assert.Equal(firstNotice.Subagent!.ChildSessionId, Assert.IsType<AgentCallItemViewModel>(model.Items[0]).Activity!.ChildSessionId);
        Assert.Null(Assert.IsType<AgentCallItemViewModel>(model.Items[1]).Activity);
        Assert.Equal(secondNotice.Subagent!.ChildSessionId, Assert.IsType<AgentCallItemViewModel>(model.Items[2]).Activity!.ChildSessionId);
        Assert.Equal("Explore", Assert.IsType<AgentCallItemViewModel>(model.Items[0]).AgentType);
    }

    [Fact]
    public void LateDispatchAndLifecycleRemoval_ReconcileWithoutDuplicateOrStaleState()
    {
        var runtime = new FakeAgentRuntime();
        var sessionId = StrongId.NewSessionId();
        var projection = runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        using var store = new ProjectionStore(() => projection, sessionId, InlineUiScheduler.Instance, TestSnapshots.State());
        using var model = new ConversationViewModel(runtime, store);
        var tool = Tool("tool", "Inspect workspace");
        var notice = Notice("a", tool);
        Apply(model, false, notice);
        Assert.False(Assert.IsType<AgentCallItemViewModel>(Assert.Single(model.Items)).IsDispatch);
        Apply(model, false, tool);
        var item = Assert.IsType<AgentCallItemViewModel>(Assert.Single(model.Items));
        Assert.Equal(tool.RowId, item.RowId);
        var failure = notice with { Subagent = notice.Subagent! with { Status = SubagentStatus.Failed, ResultSummary = "Child failure" } };
        Apply(model, false, failure);
        Assert.Same(item, Assert.Single(model.Items));
        Assert.Equal("Failed", item.StatusText);
        Assert.Equal("Child failure", item.ToolTipText);

        model.ApplyUpdate(new ProjectionUpdate(3, TestSnapshots.State(), [new RowRemoved(tool.RowId)], false));
        Assert.Equal(notice.RowId, Assert.Single(model.Items).RowId);
        Apply(model, false, tool);
        model.ApplyUpdate(new ProjectionUpdate(4, TestSnapshots.State(), [new RowRemoved(notice.RowId)], false));
        Assert.Null(Assert.IsType<AgentCallItemViewModel>(Assert.Single(model.Items)).Activity);
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("Task")]
    [InlineData("subagent")]
    public void Factory_UsesCompactPresentationForDispatchAliases(string name)
    {
        var tool = Tool("tool", "Inspect workspace") with { ToolName = name, Input = JsonSerializer.SerializeToElement(new { description = "Inspect workspace" }) };
        var item = Assert.IsType<AgentCallItemViewModel>(TimelineItemFactory.Create(tool));
        Assert.Equal("general-purpose", item.AgentType);
        Assert.Equal("Inspect workspace", item.Title);
        Assert.Null(item.Activity);
    }

    private static ToolCallRow Tool(string id, string title) => new(id, new ToolCallId(id), "Agent",
        JsonSerializer.SerializeToElement(new { description = title, subagent_type = "Explore" }), ToolCallStatus.Succeeded,
        null, false, TimeSpan.FromMilliseconds(1), Now);

    private static SystemNoticeRow Notice(string id, ToolCallRow tool) => new($"notice-{id}", "subagent", "Raw result must not appear in parent",
        new SubagentState(new SubagentId(id), new SessionId($"child-{id}"), tool.Input.GetProperty("description").GetString()!,
            SubagentStatus.Running, null, Now, null, ParentToolCallId: tool.Id, AgentType: "Explore"));

    private static void Apply(ConversationViewModel model, bool reset, params ConversationRow[] rows) =>
        model.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), reset ? [] : rows.Select(row => (RowOp)new RowUpserted(row)).ToArray(), reset,
            reset ? rows : null));
}
