using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Workspace;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.WorkspaceTools;

public class InMemoryTodoStoreTests
{
    private static readonly SessionId SessionA = new("sess_a");
    private static readonly SessionId SessionB = new("sess_b");

    [Fact]
    public void Get_ReturnsEmpty_WhenNothingStored()
    {
        var store = new InMemoryTodoStore();

        Assert.Empty(store.Get(SessionA));
    }

    [Fact]
    public void Replace_IsWholesale()
    {
        var store = new InMemoryTodoStore();
        store.Replace(SessionA, [new TodoItem("todo-1", "first", TodoItemStatus.Pending)]);

        var stored = store.Replace(SessionA, [new TodoItem("todo-2", "second", TodoItemStatus.Completed)]);

        Assert.Equal("second", store.Get(SessionA).Single().Content);
        Assert.Equal(stored, store.Get(SessionA));
    }

    [Fact]
    public void Sessions_AreIsolated()
    {
        var store = new InMemoryTodoStore();
        store.Replace(SessionA, [new TodoItem("todo-1", "alpha", TodoItemStatus.Pending)]);
        store.Replace(SessionB, [new TodoItem("todo-1", "beta", TodoItemStatus.InProgress)]);

        Assert.Equal("alpha", store.Get(SessionA).Single().Content);
        Assert.Equal("beta", store.Get(SessionB).Single().Content);
    }

    [Fact]
    public void Clear_RemovesOnlyTheTargetSession()
    {
        var store = new InMemoryTodoStore();
        store.Replace(SessionA, [new TodoItem("todo-1", "alpha", TodoItemStatus.Pending)]);
        store.Replace(SessionB, [new TodoItem("todo-1", "beta", TodoItemStatus.Pending)]);

        store.Clear(SessionA);

        Assert.Empty(store.Get(SessionA));
        Assert.Single(store.Get(SessionB));
    }

    [Fact]
    public void Replace_DoesNotAliasCallerList()
    {
        var store = new InMemoryTodoStore();
        var items = new List<TodoItem> { new("todo-1", "alpha", TodoItemStatus.Pending) };

        store.Replace(SessionA, items);
        items.Add(new TodoItem("todo-2", "mutated-after-replace", TodoItemStatus.Pending));

        Assert.Single(store.Get(SessionA));
    }
}
