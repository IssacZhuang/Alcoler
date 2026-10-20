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
    public async Task Write_NormalizesIdsStatusesAndRendersCheckboxes()
    {
        var store = new InMemoryTodoStore();
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            Context("""
            {"todos": [
              {"content": "alpha"},
              {"content": "beta", "status": "IN_PROGRESS"},
              {"content": "gamma", "status": "Completed"},
              {"content": "delta", "id": "custom", "status": "pending"}
            ]}
            """),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            """
            Todo list updated: 1 done, 1 in progress, 2 pending
            - [ ] alpha
            - [~] beta
            - [x] gamma
            - [ ] delta
            """,
            result.SingleText());

        var stored = store.Get(Session);
        Assert.Equal(["todo-1", "todo-2", "todo-3", "custom"], stored.Select(item => item.Id));
        Assert.Equal(
            [TodoItemStatus.Pending, TodoItemStatus.InProgress, TodoItemStatus.Completed, TodoItemStatus.Pending],
            stored.Select(item => item.Status));
    }

    [Fact]
    public async Task Write_EmptyList_ReplacesEverythingAndRendersZeroCounts()
    {
        var store = new InMemoryTodoStore();
        store.Replace(Session, [new TodoItem("todo-1", "stale", TodoItemStatus.Pending)]);
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(Context("""{"todos": []}"""), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Todo list updated: 0 done, 0 in progress, 0 pending", result.SingleText());
        Assert.Empty(store.Get(Session));
    }

    [Fact]
    public async Task Write_CapsItemsAtLimitWithNote()
    {
        var store = new InMemoryTodoStore();
        var tool = new TodoWriteTool(store);
        var items = string.Join(",", Enumerable.Range(1, 105).Select(i => $$"""{"content": "task {{i}}"}"""));

        var result = await tool.ExecuteAsync(Context($$"""{"todos": [{{items}}]}"""), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(TodoWriteTool.MaxItems, store.Get(Session).Count);
        var text = result.SingleText();
        Assert.Contains($"[todo list truncated at {TodoWriteTool.MaxItems} items]", text);
        Assert.DoesNotContain("task 101", text);
    }

    [Fact]
    public async Task Write_TruncatesTotalContentWithNote()
    {
        var store = new InMemoryTodoStore();
        var tool = new TodoWriteTool(store);
        var first = new string('a', 10_000);
        var second = new string('b', 10_000);
        var json = $$"""{"todos": [{"content": "{{first}}"}, {"content": "{{second}}"}, {"content": "tail"}]}""";

        var result = await tool.ExecuteAsync(Context(json), CancellationToken.None);

        Assert.False(result.IsError);
        var stored = store.Get(Session);
        Assert.Equal(TodoWriteTool.MaxTotalContentChars, stored.Sum(item => item.Content.Length));
        Assert.Equal(string.Empty, stored[2].Content);
        Assert.Contains(
            $"[todo content truncated at the {TodoWriteTool.MaxTotalContentChars:N0} character total limit]",
            result.SingleText());
    }

    [Fact]
    public async Task Write_UnknownStatus_ReturnsError()
    {
        var store = new InMemoryTodoStore();
        var tool = new TodoWriteTool(store);

        var result = await tool.ExecuteAsync(
            Context("""{"todos": [{"content": "alpha", "status": "blocked"}]}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("pending, in_progress, completed", result.SingleText());
        Assert.Empty(store.Get(Session));
    }

    [Fact]
    public async Task ValidateInput_RejectsMissingContent()
    {
        var tool = new TodoWriteTool(new InMemoryTodoStore());

        var issue = await tool.ValidateInputAsync(
            JsonDocument.Parse("""{"todos": [{"status": "pending"}]}""").RootElement,
            CancellationToken.None);

        Assert.NotNull(issue);
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
