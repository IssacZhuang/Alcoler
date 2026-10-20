using System.Text;
using Alcoler.Contracts.Workspace;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Canonical checkbox rendering of a todo list, shared by the todo tools and reusable by UI projections.
/// </summary>
public static class TodoListRenderer
{
    public static string Render(IReadOnlyList<TodoItem> items, string title)
    {
        var builder = new StringBuilder(title)
            .Append(": ")
            .Append(Count(items, TodoItemStatus.Completed)).Append(" done, ")
            .Append(Count(items, TodoItemStatus.InProgress)).Append(" in progress, ")
            .Append(Count(items, TodoItemStatus.Pending)).Append(" pending");
        foreach (var item in items)
            builder.Append('\n').Append(ItemLine(item));
        return builder.ToString();
    }

    public static string ItemLine(TodoItem item) =>
        "- [" + Checkbox(item.Status) + "] " + item.Content;

    private static char Checkbox(TodoItemStatus status) => status switch
    {
        TodoItemStatus.Completed => 'x',
        TodoItemStatus.InProgress => '~',
        _ => ' ',
    };

    private static int Count(IReadOnlyList<TodoItem> items, TodoItemStatus status)
    {
        var count = 0;
        foreach (var item in items)
            if (item.Status == status)
                count++;
        return count;
    }
}
