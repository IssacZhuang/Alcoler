namespace Alcoler.ViewModel;

public enum ComposerCommandKind
{
    Plan,
}

public readonly record struct ComposerCommand(ComposerCommandKind Kind, string Prompt);

public static class ComposerCommands
{
    public const string PlanCommandName = "plan";
    public const string PlanDescription = "Plan without making changes";

    public static bool TryParse(string? text, out ComposerCommand command)
    {
        command = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '/')
            return false;

        var separator = FindSeparator(trimmed);
        var name = separator < 0 ? trimmed[1..] : trimmed[1..separator];
        if (!string.Equals(name, PlanCommandName, StringComparison.OrdinalIgnoreCase))
            return false;

        var prompt = separator < 0 ? string.Empty : trimmed[(separator + 1)..].Trim();
        command = new ComposerCommand(ComposerCommandKind.Plan, prompt);
        return true;
    }

    public static bool MatchesSuggestion(string token)
    {
        if (token.Length == 0 || token[0] != '/')
            return false;
        return PlanCommandName.StartsWith(token[1..], StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBuiltIn(string name) =>
        string.Equals(name, PlanCommandName, StringComparison.OrdinalIgnoreCase);

    private static int FindSeparator(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsWhiteSpace(text[index]))
                return index;
        }
        return -1;
    }
}
