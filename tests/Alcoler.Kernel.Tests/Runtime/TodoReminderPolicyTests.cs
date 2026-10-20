using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class TodoReminderPolicyTests
{
    private static ToolRegistry RegistryWithTodoWrite() =>
        new([new TodoWriteTool(new InMemoryTodoStore())]);

    [Fact]
    public void ShouldRemind_RequiresBothCountersAtThreshold()
    {
        var registry = RegistryWithTodoWrite();

        Assert.False(TodoReminderPolicy.ShouldRemind(registry, 9, 10));
        Assert.False(TodoReminderPolicy.ShouldRemind(registry, 10, 9));
        Assert.True(TodoReminderPolicy.ShouldRemind(registry, 10, 10));
        Assert.True(TodoReminderPolicy.ShouldRemind(registry, 25, 11));
    }

    [Fact]
    public void ShouldRemind_RequiresTodoWriteToolInRegistry()
    {
        var registry = new ToolRegistry([]);

        Assert.False(TodoReminderPolicy.ShouldRemind(registry, 10, 10));
    }

    [Fact]
    public void ReminderMessage_IsAWrappedUserTextBlock()
    {
        var message = TodoReminderPolicy.BuildReminderMessage();

        Assert.Equal(MessageRole.User, message.Role);
        var text = Assert.IsType<InputTextBlock>(Assert.Single(message.Content)).Text;
        Assert.StartsWith("<system-reminder>\n", text);
        Assert.EndsWith("\n</system-reminder>", text);
        Assert.Contains("The TodoWrite tool hasn't been used recently.", text);
    }
}
