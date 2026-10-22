namespace Alcoler.Kernel.Prompts;

public static class ContextPrefix
{
    public const string Lead = "As you answer the user's questions, you can use the following context:";

    public const string Caveat = "      IMPORTANT: this context may or may not be relevant to your tasks. You should not respond to this context unless it is highly relevant to your task.";

    public static string? WrapSections(IReadOnlyList<string> sections)
    {
        if (sections.Count == 0)
            return null;
        return string.Join("\n", Lead, string.Join("\n\n", sections), "", Caveat);
    }

    public static string? ReminderWrapped(IReadOnlyList<string> sections) =>
        WrapSections(sections) is { } body ? SystemReminder.WrapContextPrefix(body) : null;
}
