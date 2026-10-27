using System.Text.Json;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Subagents;
using Alcoler.Contracts.Tools;
using Alcoler.Git;
using Alcoler.Stores;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class ConversationStatusViewModelTests
{
    private static readonly SessionId Session = new("status-session");
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptySession_AndUnrelatedRows_HaveNoContent()
    {
        using var model = Create();
        Reset(model,
            new UserMessageRow("user", "hello", Now),
            new AssistantMessageRow("assistant", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), "# Not a plan\n- [ ] Not a todo")], true, Now),
            new SystemNoticeRow("notice", "subagent_started", "Agent task-1 is running"),
            Tool("task", "Task", new { description = "Untyped agent" }, ToolCallStatus.Running));
        model.RefreshActivities(Now);

        Assert.False(model.HasContent);
        Assert.True(model.IsCardVisible);
        Assert.Empty(model.SummaryText);
        Assert.Empty(model.SummaryIconPath);
        Assert.False(model.HasTodos);
        Assert.False(model.HasPlans);
        Assert.False(model.HasTerminals);
        Assert.False(model.HasAgents);
        Assert.False(model.HasGit);
        Assert.Equal("0/0", model.TodoCountText);
        Assert.Equal("0", model.TerminalCountText);
        Assert.Empty(model.AgentCountText);
    }

    [Fact]
    public void SessionStart_KeepsCardVisibleAfterStatusContentDisappears()
    {
        using var model = Create();
        Assert.False(model.IsCardVisible);

        Reset(model, new UserMessageRow("user", "hello", Now));
        Assert.False(model.HasContent);
        Assert.True(model.IsCardVisible);

        Apply(model, new RowUpserted(Todo("todos", ("Task", "pending"))));
        Assert.True(model.HasContent);
        Assert.True(model.IsCardVisible);

        Apply(model, new RowRemoved("todos"));
        Assert.False(model.HasContent);
        Assert.True(model.IsCardVisible);

        Reset(model);
        Assert.False(model.HasContent);
        Assert.True(model.IsCardVisible);
    }

    [Fact]
    public void Todos_LatestSuccessfulSnapshotWinsWithoutMerging()
    {
        using var model = Create();
        Reset(model, Todo("first", ("Old task", "completed")),
            Todo("second", ("Second", "pending"), ("First", "in_progress")));

        Assert.Equal(new[] { "Second", "First" }, model.Todos.Select(todo => todo.Content));
        Assert.Equal("0/2", model.TodoCountText);
        Assert.True(model.HasTodos);
        Assert.True(model.HasContent);
        Assert.False(model.Todos[0].IsCompleted);
        Assert.False(model.Todos[0].IsActive);
        Assert.True(model.Todos[1].IsActive);
        Assert.NotEqual(model.Todos[0].IconPath, model.Todos[1].IconPath);
        Assert.Equal("First", model.SummaryText);
    }

    [Theory]
    [InlineData(ToolCallStatus.Pending, false)]
    [InlineData(ToolCallStatus.Running, false)]
    [InlineData(ToolCallStatus.AwaitingPermission, false)]
    [InlineData(ToolCallStatus.Failed, true)]
    [InlineData(ToolCallStatus.Cancelled, false)]
    [InlineData(ToolCallStatus.Succeeded, true)]
    public void Todos_UnsuccessfulWritesDoNotOverrideLastGood(ToolCallStatus status, bool isError)
    {
        using var model = Create();
        var replacement = Todo("replacement", ("Replacement", "completed")) with { Status = status, IsError = isError };
        Reset(model, Todo("good", ("Keep", "in_progress")), replacement);
        Assert.Equal("Keep", Assert.Single(model.Todos).Content);

        Apply(model, new RowUpserted(replacement with { Status = ToolCallStatus.Succeeded, IsError = false }));
        Assert.Equal("Replacement", Assert.Single(model.Todos).Content);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"todos\":null}")]
    [InlineData("{\"todos\":{}}")]
    [InlineData("{\"todos\":[null]}")]
    [InlineData("{\"todos\":[{\"content\":\"   \"}]}")]
    [InlineData("{\"todos\":[{\"content\":5}]}")]
    [InlineData("{\"todos\":[{\"content\":\"Bad\",\"status\":false}]}")]
    [InlineData("{\"todos\":[{\"content\":\"Good\"},{\"content\":\"Bad\",\"status\":\"unknown\"}]}")]
    public void Todos_MalformedWritesDoNotPartiallyReplaceLastGood(string json)
    {
        using var model = Create();
        Reset(model, Todo("good", ("Keep", "pending")), Tool("bad", "TodoWrite", Json(json)));

        Assert.Equal("Keep", Assert.Single(model.Todos).Content);
        Apply(model, new RowUpserted(Tool("good", "TodoWrite", Json(json))));
        Assert.Equal("Keep", Assert.Single(model.Todos).Content);
    }

    [Fact]
    public void Todos_MissingStatusDefaultsToPending_AndCaseMatchesToolNormalization()
    {
        using var model = Create();
        Reset(model, Tool("todos", "TodoWrite", new
        {
            todos = new object[] { new { content = "Default" }, new { content = "Done", status = "COMPLETED" } },
        }));

        Assert.Equal("pending", model.Todos[0].Status);
        Assert.True(model.Todos[1].IsCompleted);
        Assert.Equal("1/2", model.TodoCountText);
    }

    [Fact]
    public void Todos_MatchKernelItemLimit_WithoutParsingTruncatedTail()
    {
        using var model = Create();
        var input = Enumerable.Range(0, 100).Select(index => (object)new { content = $"Task {index}" })
            .Append(new { content = "Ignored", status = "malformed" }).ToArray();
        Reset(model, Tool("todos", "TodoWrite", new { todos = input }));

        Assert.Equal(100, model.Todos.Count);
        Assert.Equal("Task 99", model.Todos[^1].Content);
        Assert.Equal("0/100", model.TodoCountText);
    }

    [Fact]
    public void Todos_MatchKernelContentBudget_WhileStillValidatingRetainedEntries()
    {
        using var model = Create();
        Reset(model, Todo("todos", (new string('a', 19_998), "completed"), ("abcdef", "pending"), ("retained", "pending")));

        Assert.Equal(20_000, model.Todos.Sum(todo => todo.Content.Length));
        Assert.Equal("ab", model.Todos[1].Content);
        Assert.Empty(model.Todos[2].Content);
        var bounded = model.Todos;
        Apply(model, new RowUpserted(Tool("todos", "TodoWrite", new
        {
            todos = new object[] { new { content = new string('a', 20_001) }, new { content = "Bad", status = "invalid" } },
        })));
        Assert.Same(bounded, model.Todos);
    }

    [Fact]
    public void Todos_EmptySuccessfulWriteClears_AndRemovalRestoresPriorSnapshot()
    {
        using var model = Create();
        Reset(model, Todo("first", ("Keep", "pending")));
        Apply(model, new RowAppended(Todo("clear")));

        Assert.Empty(model.Todos);
        Assert.Empty(model.VisibleTodos);
        Assert.Empty(model.EarlierTodos);
        Assert.Empty(model.LaterTodos);
        Assert.False(model.HasTodos);
        Assert.False(model.HasContent);

        Apply(model, new RowRemoved("clear"));
        Assert.Equal("Keep", Assert.Single(model.Todos).Content);
        Apply(model, new RowRemoved("first"));
        Assert.False(model.HasContent);
    }

    [Fact]
    public void Todos_UpsertMaintainsSnapshotOrder_AndResetReordersOrRemoves()
    {
        using var model = Create();
        var first = Todo("first", ("First", "pending"));
        var second = Todo("second", ("Second", "pending"));
        Reset(model, first, second);
        Apply(model, new RowUpserted(Todo("first", ("First updated", "completed"))));
        Assert.Equal("Second", Assert.Single(model.Todos).Content);

        Reset(model, second, first);
        Assert.Equal("First", Assert.Single(model.Todos).Content);
        Reset(model, second);
        Assert.Equal("Second", Assert.Single(model.Todos).Content);
        model.ApplyUpdate(new ProjectionUpdate(4, TestSnapshots.State(), [], Reset: true));
        Assert.False(model.HasContent);
    }

    [Fact]
    public void Todos_ResubscribeWithMalformedSameRowPreservesLastGoodUntilRowRemoved()
    {
        using var model = Create();
        var good = Todo("todos", ("Keep", "pending"));
        Reset(model, good);
        Reset(model, good with { Input = Json("{\"todos\":false}") });
        Assert.Equal("Keep", Assert.Single(model.Todos).Content);

        Reset(model);
        Reset(model, good with { Input = Json("{\"todos\":false}") });
        Assert.Empty(model.Todos);
    }

    [Theory]
    [InlineData(6, 2, 0, 6)]
    [InlineData(7, 0, 0, 3)]
    [InlineData(9, 4, 3, 3)]
    [InlineData(9, 8, 6, 3)]
    public void Todos_LongListsShowThreeAroundFirstActive(int count, int active, int start, int visible)
    {
        using var model = Create();
        Reset(model, Todo("todos", Enumerable.Range(0, count)
            .Select(index => ($"Task {index}", index == active ? "in_progress" : "pending")).ToArray()));

        Assert.Equal(Enumerable.Range(start, visible).Select(index => $"Task {index}"),
            model.VisibleTodos.Select(todo => todo.Content));
        Assert.Equal(start, model.EarlierTodos.Count);
        Assert.Equal(count - start - visible, model.LaterTodos.Count);
        Assert.Equal(model.Todos, model.EarlierTodos.Concat(model.VisibleTodos).Concat(model.LaterTodos));
    }

    [Fact]
    public void Todos_WindowUsesFirstUnfinished_OrLastCompletedWhenNoActiveItem()
    {
        using var model = Create();
        Reset(model, Todo("todos", Enumerable.Range(0, 9)
            .Select(index => ($"Task {index}", index < 5 ? "completed" : "pending")).ToArray()));
        Assert.Equal("Task 4", model.VisibleTodos[0].Content);

        Apply(model, new RowUpserted(Todo("todos", Enumerable.Range(0, 9)
            .Select(index => ($"Task {index}", "completed")).ToArray())));
        Assert.Equal("Task 6", model.VisibleTodos[0].Content);
        Assert.Equal("9/9", model.TodoCountText);
        Assert.Equal("Task 8", model.SummaryText);
    }

    [Fact]
    public void Plans_OnlySuccessfulNonErrorValidPlans_LatestFirst()
    {
        using var model = Create();
        var first = Plan("first", "# First\n\nOriginal markdown");
        var second = Plan("second", "## Heading\nBody", "Explicit summary");
        Reset(model, first,
            Plan("pending", "# Pending") with { Status = ToolCallStatus.Pending },
            Plan("failed", "# Failed") with { Status = ToolCallStatus.Failed },
            Plan("error", "# Error") with { IsError = true },
            Tool("missing", "ExitPlanMode", new { summary = "No body" }),
            Plan("blank", "   "), second);

        Assert.True(model.HasPlans);
        Assert.Equal(new[] { "second", "first" }, model.Plans.Select(plan => plan.RowId));
        Assert.Equal("Explicit summary", model.Plans[0].Title);
        Assert.Equal("## Heading\nBody", model.Plans[0].Markdown);
        Assert.Equal("First", model.Plans[1].Title);
        Assert.Equal("Explicit summary", model.SummaryText);

        Apply(model, new RowUpserted(first with { Input = JsonSerializer.SerializeToElement(new { plan = "# Updated" }) }));
        Assert.Equal(new[] { "second", "first" }, model.Plans.Select(plan => plan.RowId));
        Assert.Equal("Updated", model.Plans[1].Title);
        Apply(model, new RowRemoved("second"));
        Assert.Equal("Updated", Assert.Single(model.Plans).Title);
        Reset(model);
        Assert.False(model.HasPlans);
    }

    [Theory]
    [InlineData("Intro\n\n### Real heading ###\nbody", "Real heading")]
    [InlineData("```markdown\n# Code\n```\n## Actual", "Actual")]
    [InlineData("No heading\nbody", "Plan")]
    public void Plans_TitleFallsBackToFirstMarkdownHeading(string markdown, string expected)
    {
        using var model = Create();
        Reset(model, Plan("plan", markdown, "   "));
        Assert.Equal(expected, Assert.Single(model.Plans).Title);
    }

    [Fact]
    public async Task Terminals_OnlyForegroundPendingOrRunningBash_WithOutputAndNoGenericStop()
    {
        var runtime = new FakeAgentRuntime();
        using var model = new ConversationStatusViewModel(Session, runtime);
        var running = Tool("running", "Bash", new { description = "Run unit tests", command = "dotnet test" }, ToolCallStatus.Running)
            with { StartedAt = Now.AddSeconds(-65), Output = [new ToolResultText("first"), new ToolResultText("second")] };
        Reset(model, running,
            Tool("pending", "Bash", new { command = "pwd" }, ToolCallStatus.Pending),
            Tool("permission", "Bash", new { command = "private" }, ToolCallStatus.AwaitingPermission),
            Tool("complete", "Bash", new { command = "done" }),
            Tool("failed", "Bash", new { command = "failed" }, ToolCallStatus.Failed),
            Tool("background", "Bash", new { command = "detached", run_in_background = true }, ToolCallStatus.Running));
        model.RefreshActivities(Now);

        Assert.Equal(2, model.Terminals.Count);
        Assert.Equal("2", model.TerminalCountText);
        Assert.Equal("2 terminals running", model.SummaryText);
        var activity = model.Terminals[0];
        Assert.Equal("Run unit tests", activity.Title);
        Assert.Equal(running.Id.Value, activity.Id);
        Assert.Equal("running", activity.RowId);
        Assert.Equal(Now.AddSeconds(-65), activity.StartedAt);
        Assert.Equal("1m 5s", activity.DurationText);
        Assert.Equal("first\nsecond", activity.OutputText);
        Assert.Equal("Running", activity.StatusText);
        Assert.False(activity.CanStop);
        Assert.Null(activity.BackgroundJobId);
        Assert.False(await model.StopActivityAsync(activity));
        Assert.Empty(runtime.Submitted);
        Assert.Equal("pwd", model.Terminals[1].Title);
        Assert.Equal("Pending", model.Terminals[1].StatusText);

        Apply(model, new RowUpserted(running with { Status = ToolCallStatus.Succeeded }));
        Assert.Single(model.Terminals);
        Apply(model, new RowRemoved("pending"));
        Assert.False(model.HasTerminals);
        Assert.False(model.HasContent);
    }

    [Theory]
    [InlineData(-5, "0s")]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m 0s")]
    [InlineData(3661, "1h 1m")]
    public void Activities_UseExplicitRefreshTime_AndClampFutureStarts(int elapsedSeconds, string expected)
    {
        using var model = Create();
        Reset(model, Tool("bash", "Bash", new { command = "pwd" }, ToolCallStatus.Running)
            with { StartedAt = Now.AddSeconds(-elapsedSeconds) });
        model.RefreshActivities(Now);
        Assert.Equal(expected, Assert.Single(model.Terminals).DurationText);
    }

    [Fact]
    public async Task DetachedJobs_AreLiveAuthoritativeAndSessionScoped()
    {
        var runtime = new ActivityRuntime();
        var other = new SessionId("other");
        var running = Job("running", BackgroundJobStatus.Running);
        runtime.Jobs[Session] = [running, Job("done", BackgroundJobStatus.Completed), Job("failed", BackgroundJobStatus.Failed),
            Job("stopped", BackgroundJobStatus.Stopped)];
        runtime.Jobs[other] = [Job("foreign", BackgroundJobStatus.Running)];
        using var model = new ConversationStatusViewModel(Session, runtime);
        using var otherModel = new ConversationStatusViewModel(other, runtime);
        Reset(model, Tool("completed-bash", "Bash", new { command = "same command", run_in_background = true }));
        model.RefreshActivities(Now);
        otherModel.RefreshActivities(Now);

        var activity = Assert.Single(model.Terminals);
        Assert.Equal(running.Id, activity.BackgroundJobId);
        Assert.Equal(running.OutputPath, activity.OutputPath);
        Assert.Null(activity.RowId);
        Assert.Equal("running", activity.Title);
        Assert.Equal("1m 0s", activity.DurationText);
        Assert.True(activity.CanStop);
        Assert.False(await model.StopActivityAsync(Assert.Single(otherModel.Terminals)));
        Assert.True(await model.StopActivityAsync(activity));
        Assert.Equal((Session, running.Id), Assert.Single(runtime.Stops));
        Assert.Empty(model.Terminals);
        Assert.Single(otherModel.Terminals);
        Assert.Empty(runtime.Inner.Submitted);
    }

    [Fact]
    public async Task DetachedJobs_CompletedRowDoesNotResurrectJob_AndStaleStopIsRejected()
    {
        var runtime = new ActivityRuntime();
        var job = Job("job", BackgroundJobStatus.Running);
        runtime.Jobs[Session] = [job];
        using var model = new ConversationStatusViewModel(Session, runtime);
        Reset(model, Tool("bash", "Bash", new { command = "long command" }));
        model.RefreshActivities(Now);
        var stale = Assert.Single(model.Terminals);

        runtime.Jobs[Session] = [job with { Status = BackgroundJobStatus.Completed }];
        Assert.False(await model.StopActivityAsync(stale));
        Assert.Empty(runtime.Stops);
        model.RefreshActivities(Now.AddMinutes(1));
        Assert.False(model.HasTerminals);
        Assert.False(model.HasContent);
        Assert.Empty(model.SummaryText);
    }

    [Fact]
    public void Agents_TypedLifecycleSeparatesActiveAndEnded_WithoutTaskDoubleCounting()
    {
        using var model = Create();
        var started = Agent("start", "one", SubagentStatus.Running);
        var second = Agent("second", "two", SubagentStatus.Running);
        Reset(model, started, second,
            Tool("task", "Task", new { description = "one" }, ToolCallStatus.Running));
        model.RefreshActivities(Now);
        Assert.Equal(2, model.Agents.Count);
        Assert.Empty(model.EndedAgents);
        Assert.Equal("2 running", model.AgentCountText);
        Assert.All(model.Agents, agent => Assert.True(agent.CanStop));
        Assert.Equal("2 agents running", model.SummaryText);

        var ended = Agent("end", "one", SubagentStatus.Completed);
        Apply(model, new RowAppended(ended));
        model.RefreshActivities(Now);
        Assert.Equal("two", Assert.Single(model.Agents).Id);
        var activity = Assert.Single(model.EndedAgents);
        Assert.Equal("one", activity.Id);
        Assert.Equal("end", activity.RowId);
        Assert.Equal(new SessionId("child-one"), activity.ChildSessionId);
        Assert.Equal("Completed", activity.StatusText);
        Assert.Equal("Result one", activity.OutputText);
        Assert.Equal("2m 0s", activity.DurationText);
        Assert.False(activity.CanStop);
        Assert.Equal("1 running", model.AgentCountText);

        model.RefreshActivities(Now.AddHours(3));
        Assert.Equal("2m 0s", Assert.Single(model.EndedAgents).DurationText);
        Apply(model, new RowUpserted(second with
        {
            Subagent = second.Subagent! with { Status = SubagentStatus.Cancelled, EndedAt = Now },
        }));
        Assert.Empty(model.Agents);
        Assert.Equal(2, model.EndedAgents.Count);
        Assert.True(model.HasAgents);
        Assert.Empty(model.AgentCountText);
        Assert.True(model.HasContent);
        Assert.False(model.HasRunningActivities);
        Assert.Equal("2 ended agents", model.SummaryText);
        Apply(model, new RowRemoved("end"));
        Assert.Equal("one", Assert.Single(model.Agents).Id);
    }

    [Fact]
    public void Agents_FourDistinctTypedRows_SurviveBatchedCompletionAndSnapshotResetWithoutToolDoubleCounting()
    {
        using var model = Create();
        var running = Enumerable.Range(1, 4)
            .Select(index => Agent($"subagent:{index}", index.ToString(), SubagentStatus.Running)).ToArray();
        var calls = Enumerable.Range(1, 4)
            .Select(index => Tool($"call:{index}", "Agent", new { description = $"Agent {index}" }, ToolCallStatus.Running)).ToArray();
        Reset(model, [.. calls]);
        Assert.Empty(model.Agents);
        Apply(model, running.Select(row => (RowOp)new RowAppended(row)).ToArray());
        Assert.Equal(4, model.Agents.Count);
        Assert.Equal("4 running", model.AgentCountText);

        var latest = running.Select((row, index) => index == 3 ? row : row with
        {
            Subagent = row.Subagent! with { Status = SubagentStatus.Completed, EndedAt = Now },
        }).ToArray();
        Apply(model, latest.Take(3).Select(row => (RowOp)new RowUpserted(row)).ToArray());
        Assert.Equal("4", Assert.Single(model.Agents).Id);
        Assert.Equal(3, model.EndedAgents.Count);
        Assert.Equal("1 running", model.AgentCountText);
        Assert.Equal("1 agent running", model.SummaryText);

        Reset(model, [.. calls, .. latest]);
        Assert.Equal("4", Assert.Single(model.Agents).Id);
        Assert.Equal(3, model.EndedAgents.Count);
        Assert.Equal("1 running", model.AgentCountText);
        Apply(model, new RowUpserted(latest[0]));
        Assert.Equal("1 running", model.AgentCountText);
    }

    [Theory]
    [InlineData(null, "Running")]
    [InlineData("   ", "Running")]
    [InlineData("Explore", "Explore · Running")]
    public void Agents_TypeIdentityComesOnlyFromTypedLifecycle(string? agentType, string expected)
    {
        using var model = Create();
        var agent = Agent("agent", "one", SubagentStatus.Running);
        Reset(model, Tool("call", "Agent", new { description = "Agent one", subagent_type = "Untyped" }, ToolCallStatus.Running),
            agent with { Subagent = agent.Subagent! with { AgentType = agentType } });
        var activity = Assert.Single(model.Agents);
        Assert.Equal(agentType, activity.AgentType);
        Assert.Equal("Running", activity.StatusText);
        Assert.Equal(expected, activity.DetailText);
    }

    [Fact]
    public void Agents_ActiveOrderFollowsStartTimeAfterReorderedCompletionsAndSnapshotReset()
    {
        using var model = Create();
        var first = Agent("first", "one", SubagentStatus.Running, Now.AddMinutes(-6));
        var second = Agent("second", "two", SubagentStatus.Running, Now.AddMinutes(-5));
        var third = Agent("third", "three", SubagentStatus.Running, Now.AddMinutes(-4));
        var fourth = Agent("fourth", "four", SubagentStatus.Running, Now.AddMinutes(-3));
        Reset(model, third, fourth, first, second);
        model.RefreshActivities(Now);
        Assert.Equal(new[] { "one", "two", "three", "four" }, model.Agents.Select(agent => agent.Id));

        var secondEnded = second with
        {
            RowId = "second-ended",
            Subagent = second.Subagent! with { Status = SubagentStatus.Completed, EndedAt = Now },
        };
        var fourthEnded = fourth with
        {
            RowId = "fourth-ended",
            Subagent = fourth.Subagent! with { Status = SubagentStatus.Failed, EndedAt = Now },
        };
        Apply(model, new RowAppended(fourthEnded), new RowAppended(secondEnded));
        Assert.Equal(new[] { "one", "three" }, model.Agents.Select(agent => agent.Id));
        Assert.Equal("2 running", model.AgentCountText);
        Assert.Equal(2, model.EndedAgents.Count);

        Reset(model, third, second, fourth, first, secondEnded, fourthEnded);
        model.RefreshActivities(Now);
        Assert.Equal(new[] { "one", "three" }, model.Agents.Select(agent => agent.Id));
        Assert.Equal(new[] { "6m 0s", "4m 0s" }, model.Agents.Select(agent => agent.DurationText));
        Assert.Equal("2 running", model.AgentCountText);
        Assert.Equal(2, model.EndedAgents.Count);
    }

    [Fact]
    public void EndedAgents_SummaryUsesEndedCountOnlyWhenNoHigherPriorityContentRemains()
    {
        using var model = Create();
        var ended = Enumerable.Range(1, 5)
            .Select(index => Agent($"ended-{index}", index.ToString(), SubagentStatus.Completed)).ToArray();
        Reset(model, [Todo("todos", ("Keep working", "pending")), .. ended]);
        Assert.Equal("Keep working", model.SummaryText);
        Assert.Empty(model.AgentCountText);

        Apply(model, new RowRemoved("todos"));
        Assert.True(model.HasContent);
        Assert.Equal("5 ended agents", model.SummaryText);
        Assert.NotEmpty(model.SummaryIconPath);
        Assert.False(model.HasRunningActivities);
    }

    [Fact]
    public async Task RunningAgents_StopThroughTheSessionRuntimeScopedToTheirSession()
    {
        var runtime = new ActivityRuntime();
        using var model = new ConversationStatusViewModel(Session, runtime);
        Reset(model, Agent("agent", "one", SubagentStatus.Running));
        model.RefreshActivities(Now);
        var activity = Assert.Single(model.Agents);
        Assert.True(activity.CanStop);

        Assert.True(await model.StopActivityAsync(activity));
        Assert.Equal((Session, "one"), Assert.Single(runtime.AgentStops));
        runtime.AgentStopResult = false;
        Assert.False(await model.StopActivityAsync(activity));
        Assert.Equal(2, runtime.AgentStops.Count);
    }

    [Theory]
    [InlineData(SubagentStatus.Completed)]
    [InlineData(SubagentStatus.Failed)]
    [InlineData(SubagentStatus.Cancelled)]
    public void EndedAgentsAlone_KeepCardAccessibleWithEndedSummaryAndNoRunningCount(SubagentStatus status)
    {
        using var model = Create();
        Reset(model, Agent("agent", "one", status));
        Assert.Empty(model.Agents);
        var ended = Assert.Single(model.EndedAgents);
        Assert.Equal(status, ended.AgentStatus);
        Assert.Equal(status == SubagentStatus.Completed, ended.IsCompleted);
        Assert.Equal(status == SubagentStatus.Failed, ended.IsFailed);
        Assert.Equal(status == SubagentStatus.Cancelled, ended.IsCancelled);
        Assert.True(model.HasAgents);
        Assert.True(model.HasContent);
        Assert.False(model.HasRunningActivities);
        Assert.Empty(model.AgentCountText);
        Assert.Equal("1 ended agent", model.SummaryText);
        Assert.NotEmpty(model.SummaryIconPath);

        Apply(model, new RowRemoved("agent"));
        Assert.False(model.HasContent);
        Assert.Empty(model.SummaryText);
        Assert.Empty(model.SummaryIconPath);
    }

    [Fact]
    public void Summary_PrioritizesActiveThenPendingThenGitThenCompletedThenPlanThenActivities()
    {
        using var model = Create();
        var git = new GitPaneViewModel("workspace", new FakeGitService(), InlineUiScheduler.Instance)
        {
            IsRepo = true,
            ChangedFileCount = 2,
            AddedLines = 12,
            RemovedLines = 3,
        };
        model.BindGit(git);
        var bash = Tool("bash", "Bash", new { command = "pwd" }, ToolCallStatus.Running);
        var plan = Plan("plan", "# Latest plan");
        var agent = Agent("agent", "one", SubagentStatus.Running);
        Reset(model, bash, plan, agent,
            Todo("todos", ("Pending", "pending"), ("Active", "in_progress"), ("Done", "completed")));
        Assert.Equal("Active", model.SummaryText);
        Assert.Equal(model.Todos[1].IconPath, model.SummaryIconPath);

        Apply(model, new RowUpserted(Todo("todos", ("Pending", "pending"), ("Done", "completed"))));
        Assert.Equal("Pending", model.SummaryText);
        Apply(model, new RowUpserted(Todo("todos", ("Earlier done", "completed"), ("Last done", "completed"))));
        Assert.Equal("2 changed files", model.SummaryText);
        model.BindGit(null);
        Assert.Equal("Last done", model.SummaryText);
        Apply(model, new RowRemoved("todos"));
        Assert.Equal("Latest plan", model.SummaryText);
        Apply(model, new RowRemoved("plan"));
        Assert.Equal("1 terminal and 1 agent running", model.SummaryText);
        var mixedIcon = model.SummaryIconPath;
        Apply(model, new RowRemoved("agent"));
        Assert.Equal("1 terminal running", model.SummaryText);
        Assert.NotEqual(mixedIcon, model.SummaryIconPath);
        Apply(model, new RowRemoved("bash"));
        Assert.False(model.HasContent);
        Assert.Empty(model.SummaryIconPath);
    }

    [Fact]
    public async Task Git_BindsLiveSnapshotCounts_AndUnsubscribesOnRebindAndDispose()
    {
        var service = new FakeGitService
        {
            Root = "repo",
            Status = new GitRepositoryStatus([new GitFileChange("one.cs", ChangeState.Modified)], [], []),
            Summary = new GitChangeSummary(10, 4),
        };
        var git = new GitPaneViewModel("workspace", service, InlineUiScheduler.Instance);
        using var model = Create();
        model.BindGit(git);
        Assert.False(model.HasContent);
        await git.LoadAsync();

        Assert.Same(git, model.Git);
        Assert.True(model.HasGit);
        Assert.True(model.HasContent);
        Assert.Equal("+10", model.GitAddedText);
        Assert.Equal("-4", model.GitRemovedText);
        Assert.Equal("1 changed file", model.SummaryText);

        List<string?> notifications = [];
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        service.Summary = new GitChangeSummary(20, 9);
        await git.LoadAsync();
        Assert.Equal("+20", model.GitAddedText);
        Assert.Equal("-9", model.GitRemovedText);
        Assert.Contains(nameof(model.GitAddedText), notifications);
        Assert.Contains(nameof(model.GitRemovedText), notifications);

        var replacement = new GitPaneViewModel("other", new FakeGitService(), InlineUiScheduler.Instance);
        model.BindGit(replacement);
        notifications.Clear();
        git.AddedLines = 100;
        Assert.Empty(notifications);
        Assert.False(model.HasContent);
        model.Dispose();
        replacement.ChangedFileCount = 1;
        replacement.IsRepo = true;
        Assert.Empty(notifications);
    }

    [Fact]
    public void Git_WithoutTextLineChanges_UsesFileCountFallback()
    {
        var git = new GitPaneViewModel("workspace", new FakeGitService(), InlineUiScheduler.Instance)
        {
            IsRepo = true,
            ChangedFileCount = 2,
        };
        using var model = Create();
        model.BindGit(git);
        Assert.True(model.HasGit);
        Assert.False(model.HasGitLineChanges);
        Assert.Equal("2 files", model.GitFileCountText);
        Assert.Equal("2 changed files", model.SummaryText);
        git.AddedLines = 1;
        Assert.True(model.HasGitLineChanges);
        git.AddedLines = 0;
        git.RemovedLines = 1;
        Assert.True(model.HasGitLineChanges);
    }

    [Fact]
    public async Task Git_RefreshesOnlyOnBusyToIdleBoundary_AndNotDuringStreaming()
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeGitService
        {
            Root = "repo",
            Status = new GitRepositoryStatus([], [new GitFileChange("changed.cs", ChangeState.Modified)], []),
            Summary = new GitChangeSummary(7, 2),
        };
        var git = new GitPaneViewModel("workspace", service, InlineUiScheduler.Instance);
        git.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(git.IsLoading) && !git.IsLoading)
            {
                loaded.TrySetResult();
            }
        };
        using var model = Create();
        model.BindGit(git);
        Reset(model);
        Assert.Equal(0, service.RefreshCount);
        PatchPhase(model, TurnPhase.Streaming);
        for (var index = 0; index < 20; index++)
        {
            model.ApplyUpdate(new ProjectionUpdate(index + 2, TestSnapshots.State(TurnPhase.Streaming),
                [new RowUpserted(new AssistantMessageRow("stream", StrongId.NewMessageId(), [], true, Now))], Reset: false));
        }
        PatchPhase(model, TurnPhase.ExecutingTools);
        Assert.Equal(0, service.RefreshCount);

        PatchPhase(model, TurnPhase.Idle);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, service.RefreshCount);
        Assert.True(model.HasGit);
        Assert.Equal("+7", model.GitAddedText);
        Assert.Equal("-2", model.GitRemovedText);
        PatchPhase(model, TurnPhase.Idle);
        PatchPhase(model, TurnPhase.Error);
        PatchPhase(model, TurnPhase.Idle);
        Assert.Equal(1, service.RefreshCount);
    }

    [Fact]
    public void Git_NoChangesOrNoRepositoryDoesNotCreateContent()
    {
        using var model = Create();
        var git = new GitPaneViewModel("workspace", new FakeGitService(), InlineUiScheduler.Instance) { IsRepo = true };
        model.BindGit(git);
        Assert.False(model.HasGit);
        git.ChangedFileCount = 2;
        Assert.True(model.HasGit);
        git.IsRepo = false;
        Assert.False(model.HasGit);
        Assert.False(model.HasContent);
    }

    [Fact]
    public void StreamingAndStateUpdates_DoNotRebuildStatusCollectionsOrRefreshJobs()
    {
        var runtime = new ActivityRuntime();
        using var model = new ConversationStatusViewModel(Session, runtime);
        Reset(model, Todo("todos", ("Keep", "pending")), Plan("plan", "# Plan"),
            Tool("bash", "Bash", new { command = "pwd" }, ToolCallStatus.Running),
            Agent("agent", "one", SubagentStatus.Running));
        var todos = model.Todos;
        var visible = model.VisibleTodos;
        var plans = model.Plans;
        var terminals = model.Terminals;
        var agents = model.Agents;
        var queries = runtime.Queries;
        List<string?> notifications = [];
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        for (var index = 0; index < 20; index++)
        {
            Apply(model, new RowUpserted(new AssistantMessageRow("stream", StrongId.NewMessageId(),
                [new TextPart(StrongId.NewPartId(), new string('a', index))], true, Now)));
        }
        Apply(model, new StatePatched(TestSnapshots.State(TurnPhase.Streaming)));

        Assert.Same(todos, model.Todos);
        Assert.Same(visible, model.VisibleTodos);
        Assert.Same(plans, model.Plans);
        Assert.Same(terminals, model.Terminals);
        Assert.Same(agents, model.Agents);
        Assert.Equal(queries, runtime.Queries);
        Assert.Empty(notifications);
    }

    [Fact]
    public void ReplacingRelevantRowWithUnrelatedRowRemovesContent()
    {
        using var model = Create();
        Reset(model, Todo("row", ("Task", "pending")));
        Apply(model, new RowUpserted(new UserMessageRow("row", "replacement", Now)));
        Assert.False(model.HasTodos);
        Assert.False(model.HasContent);
    }

    [Fact]
    public async Task DisposedModel_DoesNotApplyUpdatesRefreshOrStopJobs()
    {
        var runtime = new ActivityRuntime();
        runtime.Jobs[Session] = [Job("running", BackgroundJobStatus.Running)];
        using var model = new ConversationStatusViewModel(Session, runtime);
        model.RefreshActivities(Now);
        var activity = Assert.Single(model.Terminals);
        var queries = runtime.Queries;
        model.Dispose();

        Reset(model, Todo("todos", ("Task", "pending")));
        model.RefreshActivities(Now);
        Assert.False(await model.StopActivityAsync(activity));
        Assert.False(model.HasTodos);
        Assert.Equal(queries, runtime.Queries);
        Assert.Empty(runtime.Stops);
    }

    private static ConversationStatusViewModel Create() => new(Session, new FakeAgentRuntime());

    private static void Reset(ConversationStatusViewModel model, params ConversationRow[] rows) =>
        model.ApplyUpdate(new ProjectionUpdate(1, TestSnapshots.State(), [], Reset: true, ResetRows: rows));

    private static void Apply(ConversationStatusViewModel model, params RowOp[] ops) =>
        model.ApplyUpdate(new ProjectionUpdate(2, TestSnapshots.State(), ops, Reset: false));

    private static void PatchPhase(ConversationStatusViewModel model, TurnPhase phase)
    {
        var state = TestSnapshots.State(phase);
        model.ApplyUpdate(new ProjectionUpdate(2, state, [new StatePatched(state)], Reset: false));
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static ToolCallRow Tool(string rowId, string name, object input, ToolCallStatus status = ToolCallStatus.Succeeded) =>
        new(rowId, StrongId.NewToolCallId(), name, JsonSerializer.SerializeToElement(input), status, null, false, null, Now);

    private static ToolCallRow Todo(string rowId, params (string Content, string Status)[] items) =>
        Tool(rowId, "TodoWrite", new { todos = items.Select(item => new { content = item.Content, status = item.Status }).ToArray() });

    private static ToolCallRow Plan(string rowId, string markdown, string? summary = null) =>
        Tool(rowId, "ExitPlanMode", new { plan = markdown, summary });

    private static SystemNoticeRow Agent(string rowId, string id, SubagentStatus status, DateTimeOffset? startedAt = null) =>
        new(rowId, "arbitrary-kind", "Text is not parsed", new SubagentState(new SubagentId(id), new SessionId($"child-{id}"),
            $"Agent {id}", status, status == SubagentStatus.Running ? null : $"Result {id}", startedAt ?? Now.AddMinutes(-3),
            status == SubagentStatus.Running ? null : Now.AddMinutes(-1)));

    private static BackgroundJobInfo Job(string id, BackgroundJobStatus status) =>
        new(new BackgroundJobId(id), id, Now.AddMinutes(-1), status, OutputPath: $"{id}.output");

    private sealed class ActivityRuntime : IAgentRuntime, ISessionActivityRuntime
    {
        public FakeAgentRuntime Inner { get; } = new();
        public Dictionary<SessionId, IReadOnlyList<BackgroundJobInfo>> Jobs { get; } = [];
        public List<(SessionId SessionId, BackgroundJobId JobId)> Stops { get; } = [];
        public List<(SessionId SessionId, string TaskId)> AgentStops { get; } = [];
        public bool AgentStopResult { get; set; } = true;
        public int Queries { get; private set; }

        public ValueTask<bool> TryStopSubagentAsync(SessionId sessionId, string taskId)
        {
            AgentStops.Add((sessionId, taskId));
            return ValueTask.FromResult(AgentStopResult);
        }

        public IReadOnlyList<BackgroundJobInfo> GetBackgroundJobs(SessionId sessionId)
        {
            Queries++;
            return Jobs.GetValueOrDefault(sessionId) ?? [];
        }

        public bool StopBackgroundJob(SessionId sessionId, BackgroundJobId jobId)
        {
            Stops.Add((sessionId, jobId));
            if (Jobs.GetValueOrDefault(sessionId) is not { } jobs
                || !jobs.Any(job => job.Id == jobId && job.Status == BackgroundJobStatus.Running))
            {
                return false;
            }
            Jobs[sessionId] = jobs.Select(job => job.Id == jobId ? job with { Status = BackgroundJobStatus.Stopped } : job).ToArray();
            return true;
        }

        public ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken) =>
            Inner.CreateSessionAsync(options, cancellationToken);

        public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            Inner.ResumeSessionAsync(sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken) =>
            Inner.ListSessionsAsync(workspaceRoot, limit, cancellationToken);

        public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken) =>
            Inner.SubmitAsync(command, cancellationToken);

        public ValueTask<int> UnloadIdleSessionsAsync(
            TimeSpan idleThreshold, SessionId? keepOpen, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public IConversationProjection Subscribe(SessionId sessionId) => Inner.Subscribe(sessionId);
        public IConversationProjection? TryObserve(SessionId sessionId) => Inner.TryObserve(sessionId);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeGitService : IGitService
    {
        public string? Root { get; set; }
        public GitRepositoryStatus Status { get; set; } = new([], [], []);
        public GitChangeSummary Summary { get; set; } = new(0, 0);
        public int RefreshCount { get; private set; }

        public string? FindRepositoryRoot(string workspaceRoot)
        {
            RefreshCount++;
            return Root;
        }
        public string? GetCurrentBranchName(string repositoryRoot) => "main";
        public GitRepositoryStatus GetStatus(string repositoryRoot) => Status;
        public GitChangeSummary GetChangeSummary(string repositoryRoot) => Summary;
        public IReadOnlyList<GitBranch> GetBranches(string repositoryRoot) => [];
        public IReadOnlyList<GitCommit> GetLog(string repositoryRoot, int limit) => [];
        public void Checkout(string repositoryRoot, string branchName) => throw new NotSupportedException();
        public void Dispose()
        {
        }
    }
}
