namespace Alcoler.ViewModel;

public enum ComposerCommandKind
{
    Plan,
    Orchestrator,
}

public readonly record struct ComposerCommand(ComposerCommandKind Kind, string Prompt);

public static class ComposerCommands
{
    public const string PlanCommandName = "plan";
    public const string PlanDescription = "Plan without making changes";
    public const string OrchestratorCommandName = "orchestrator";
    public const string OrchestratorDescription = "Orchestrate via subagents: plan, dispatch, accept";

    private static readonly (string Name, string Description, ComposerCommandKind Kind)[] BuiltIns =
    [
        (PlanCommandName, PlanDescription, ComposerCommandKind.Plan),
        (OrchestratorCommandName, OrchestratorDescription, ComposerCommandKind.Orchestrator),
    ];

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
        foreach (var (builtIn, _, kind) in BuiltIns)
        {
            if (!string.Equals(name, builtIn, StringComparison.OrdinalIgnoreCase))
                continue;
            var prompt = separator < 0 ? string.Empty : trimmed[(separator + 1)..].Trim();
            command = new ComposerCommand(kind, prompt);
            return true;
        }
        return false;
    }

    public static bool MatchesSuggestion(string token)
    {
        if (token.Length == 0 || token[0] != '/')
            return false;
        var partial = token[1..];
        foreach (var (name, _, _) in BuiltIns)
        {
            if (name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool IsBuiltIn(string name)
    {
        foreach (var (builtIn, _, _) in BuiltIns)
        {
            if (string.Equals(name, builtIn, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static IEnumerable<(string Name, string Description)> SuggestionsFor(string token)
    {
        var partial = token.Length > 0 && token[0] == '/' ? token[1..] : token;
        foreach (var (name, description, _) in BuiltIns)
        {
            if (name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                yield return (name, description);
        }
    }

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
