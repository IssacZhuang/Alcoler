using System.Text.Json;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Alcoler.Contracts.Workspace;
using Alcoler.Kernel.Tests.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.WorkspaceTools;

public class TodoWriteToolTests
{
    private static readonly SessionId Session = new("sess_write");

    private static ToolExecutionContext Context(string json) =>
        WorkspaceToolTestContext.ForSession(Session, json);

    [Fact]
    public async Task Write_ReturnsReferenceJsonShape_WithOldTodosAndSummary()
    {
        var store = new InMemoryTodoStore();
        store.Replace(Session, [new TodoItem("stale", TodoItemStatus.Completed, TodoPriority.Low)]);
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            Context("""
            {"todos": [
              {"content": "alpha", "status": "pending", "priority": "medium"},
              {"content": "beta", "status": "IN_PROGRESS", "priority": "HIGH"},
              {"content": "gamma", "status": "Completed", "priority": "low"}
            ]}
            """),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            """{"oldTodos":[{"content":"stale","status":"completed","priority":"low"}],"todos":[{"content":"alpha","status":"pending","priority":"medium"},{"content":"beta","status":"in_progress","priority":"high"},{"content":"gamma","status":"completed","priority":"low"}],"summary":{"total":3,"pending":1,"inProgress":1,"completed":1}}""",
            result.SingleText());

        var stored = store.Get(Session);
        Assert.Equal(
            [TodoItemStatus.Pending, TodoItemStatus.InProgress, TodoItemStatus.Completed],
            stored.Select(item => item.Status));
        Assert.Equal(
            [TodoPriority.Medium, TodoPriority.High, TodoPriority.Low],
            stored.Select(item => item.Priority));
    }

    [Fact]
    public async Task Write_EmptyList_ReplacesEverythingWithZeroCounts()
    {
        var store = new InMemoryTodoStore();
        store.Replace(Session, [new TodoItem("stale", TodoItemStatus.Pending, TodoPriority.Medium)]);
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(Context("""{"todos": []}"""), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            """{"oldTodos":[{"content":"stale","status":"pending","priority":"medium"}],"todos":[],"summary":{"total":0,"pending":0,"inProgress":0,"completed":0}}""",
            result.SingleText());
        Assert.Empty(store.Get(Session));
    }

    [Fact]
    public async Task Write_UnknownStatus_ReturnsError()
    {
        var store = new InMemoryTodoStore();
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            Context("""{"todos": [{"content": "alpha", "status": "blocked", "priority": "low"}]}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("pending, in_progress, completed", result.SingleText());
        Assert.Empty(store.Get(Session));
    }

    [Fact]
    public async Task Write_UnknownPriority_ReturnsError()
    {
        var store = new InMemoryTodoStore();
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            Context("""{"todos": [{"content": "alpha", "status": "pending", "priority": "urgent"}]}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("high, medium, low", result.SingleText());
        Assert.Empty(store.Get(Session));
    }

    [Fact]
    public async Task ValidateInput_RejectsMissingContentStatusOrPriority()
    {
        var tool = new TodoWriteTool(new InMemoryTodoStore());

        foreach (var json in new[]
        {
            """{"todos": [{"status": "pending", "priority": "low"}]}""",
            """{"todos": [{"content": "alpha", "priority": "low"}]}""",
            """{"todos": [{"content": "alpha", "status": "pending"}]}"""
        })
        {
            var issue = await tool.ValidateInputAsync(
                JsonDocument.Parse(json).RootElement,
                CancellationToken.None);
            Assert.NotNull(issue);
        }

        Assert.Null(await tool.ValidateInputAsync(
            JsonDocument.Parse("""{"todos": [{"content": "alpha", "status": "pending", "priority": "low"}]}""").RootElement,
            CancellationToken.None));
    }

    [Fact]
    public void Contract_ExposesReferenceSchema()
    {
        var contract = new TodoWriteTool(new InMemoryTodoStore()).Contract;

        Assert.Equal("TodoWrite", contract.Name);
        Assert.StartsWith(
            "Create and update a task list for the current session. The list is rendered to the user as your working plan.",
            contract.Description);
        Assert.Contains("\"priority\"", contract.InputSchema.GetRawText());
        Assert.DoesNotContain("\"id\"", contract.InputSchema.GetRawText());
    }

    [Fact]
    public void Metadata_AllowsPlanModeWithoutApproval()
    {
        var metadata = new TodoWriteTool(new InMemoryTodoStore()).Metadata;

        Assert.True(metadata.ReadOnly);
        Assert.True(metadata.AllowedInPlanMode);
        Assert.False(metadata.NeedsApproval);
        Assert.False(metadata.RequiresUserInteraction);
    }
}
