using System.Text;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Workspace;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Runtime;

// Mirrors ZCode runtime-reminders todo throttling: after enough model steps without a
// TodoWrite call and enough steps since the last reminder, a one-shot user reminder is
// appended to the outgoing request, including the current todo list when non-empty.
public static class TodoReminderPolicy
{
    public const string TodoWriteToolName = "TodoWrite";
    public const int StepsSinceWriteThreshold = 10;
    public const int StepsBetweenReminders = 10;

    public const string ReminderBody =
        "The TodoWrite tool hasn't been used recently. If you're working on tasks that would benefit " +
        "from tracking progress, consider using the TodoWrite tool to track progress. Also consider " +
        "cleaning up the todo list if it is stale and no longer matches what you are working " +
        "on. Only use it if it's relevant to the current work. This is just a gentle reminder - ignore " +
        "if not applicable.";

    public static bool ShouldRemind(ToolRegistry tools, int stepsSinceTodoWrite, int stepsSinceReminder) =>
        tools.TryFind(TodoWriteToolName) is not null
        && stepsSinceTodoWrite >= StepsSinceWriteThreshold
        && stepsSinceReminder >= StepsBetweenReminders;

    public static string BuildReminderBody(IReadOnlyList<TodoItem>? todos = null)
    {
        var body = new StringBuilder(ReminderBody);
        if (todos is { Count: > 0 })
        {
            body.Append("\n\nHere are the existing contents of your todo list:\n\n[");
            for (var i = 0; i < todos.Count; i++)
                body.Append(i == 0 ? "" : "\n").Append(i + 1).Append(". [").Append(StatusName(todos[i].Status)).Append("] ").Append(todos[i].Content);
            body.Append(']');
        }
        return body.ToString();
    }

    public static ModelInputMessage BuildReminderMessage(IReadOnlyList<TodoItem>? todos = null) =>
        new(MessageRole.User, [new InputTextBlock($"<system-reminder>\n{BuildReminderBody(todos)}\n</system-reminder>")]);

    public static bool IsTodoReminder(string text) => text.Contains(ReminderBody, StringComparison.Ordinal);

    private static string StatusName(TodoItemStatus status) => status switch
    {
        TodoItemStatus.Completed => "completed",
        TodoItemStatus.InProgress => "in_progress",
        _ => "pending",
    };
}
