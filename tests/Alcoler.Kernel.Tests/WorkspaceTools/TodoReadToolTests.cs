using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.WorkspaceTools;

public class TodoReadToolTests
{
    private static readonly SessionId Session = new("sess_read");

    private static ToolExecutionContext Context() =>
        WorkspaceToolTestContext.ForSession(Session, "{}");

    [Fact]
    public async Task Read_ReturnsCurrentListAsReferenceJson()
    {
        var store = new InMemoryTodoStore();
        var write = new TodoWriteTool(store);
        await write.ExecuteAsync(
            WorkspaceToolTestContext.ForSession(
                Session,
                """{"todos": [{"content": "alpha", "status": "completed", "priority": "high"}, {"content": "beta", "status": "pending", "priority": "low"}]}"""),
            CancellationToken.None);

        var result = await new TodoReadTool(store).ExecuteAsync(Context(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            """{"todos":[{"content":"alpha","status":"completed","priority":"high"},{"content":"beta","status":"pending","priority":"low"}]}""",
            result.SingleText());
    }

    [Fact]
    public async Task Read_ReflectsLatestWrite()
    {
        var store = new InMemoryTodoStore();
        var write = new TodoWriteTool(store);
        var read = new TodoReadTool(store);

        await write.ExecuteAsync(
            WorkspaceToolTestContext.ForSession(
                Session,
                """{"todos": [{"content": "stale", "status": "pending", "priority": "low"}]}"""),
            CancellationToken.None);
        await write.ExecuteAsync(
            WorkspaceToolTestContext.ForSession(
                Session,
                """{"todos": [{"content": "fresh", "status": "in_progress", "priority": "medium"}]}"""),
            CancellationToken.None);
        var result = await read.ExecuteAsync(Context(), CancellationToken.None);

        var text = result.SingleText();
        Assert.DoesNotContain("stale", text);
        Assert.Contains("\"in_progress\"", text);
        Assert.Contains("fresh", text);
    }

    [Fact]
    public async Task Read_EmptyStore_ReturnsEmptyTodosArray()
    {
        var result = await new TodoReadTool(new InMemoryTodoStore()).ExecuteAsync(Context(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("""{"todos":[]}""", result.SingleText());
    }

    [Fact]
    public void Contract_DescriptionMatchesReference()
    {
        var contract = new TodoReadTool(new InMemoryTodoStore()).Contract;

        Assert.Equal("TodoRead", contract.Name);
        Assert.Equal("Read the current session todo list", contract.Description);
    }

    [Fact]
    public void Metadata_AllowsPlanModeWithoutApproval()
    {
        var metadata = new TodoReadTool(new InMemoryTodoStore()).Metadata;

        Assert.True(metadata.ReadOnly);
        Assert.True(metadata.AllowedInPlanMode);
        Assert.False(metadata.NeedsApproval);
        Assert.False(metadata.RequiresUserInteraction);
    }
}
