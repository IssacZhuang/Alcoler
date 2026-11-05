namespace Alcoler.Adapters.Tools;

public static partial class BashSemantics
{
    public const string NoMatchesFound = "No matches found";
    public const string FilesDiffer = "Files differ";
    public const string ConditionIsFalse = "Condition is false";
    public const string SomeDirectoriesWereInaccessible = "Some directories were inaccessible";

    private static readonly string[] SemanticNonErrorMessages =
    [
        ConditionIsFalse,
        FilesDiffer,
        NoMatchesFound,
        SomeDirectoriesWereInaccessible,
    ];

    private static readonly HashSet<string> NoMatchCommands = new(StringComparer.Ordinal)
    {
        "egrep", "fgrep", "grep", "rg",
    };

    public static bool IsSemanticNonError(string? message) =>
        message is not null && SemanticNonErrorMessages.Contains(message);

    /// <summary>
    /// Friendly interpretation for exit code 1 of grep-like, find, diff, and test
    /// commands; any other command returns null and renders as a plain failure.
    /// </summary>
    public static string? SemanticNonErrorExit(string command, int exitCode)
    {
        if (exitCode != 1)
            return null;
        var analysis = BashCommandAnalyzer.Analyze(command);
        if (analysis.HasParseErrors || analysis.HasUnsupportedSyntax || analysis.Commands.Count == 0)
            return null;
        var statusCommand = analysis.Commands[^1];
        var name = statusCommand.Name;
        if (name == "git")
        {
            var subcommand = GitSemanticSubcommandName(statusCommand.Argv);
            if (subcommand == "grep")
                name = "grep";
            else if (subcommand == "diff")
                name = "diff";
        }
        if (NoMatchCommands.Contains(name))
            return NoMatchesFound;
        if (name == "find")
            return SomeDirectoriesWereInaccessible;
        if (name == "diff")
            return FilesDiffer;
        if (name is "test" or "[")
            return ConditionIsFalse;
        return null;
    }

    private static string? GitSemanticSubcommandName(IReadOnlyList<string> argv)
    {
        for (var index = 1; index < argv.Count; index++)
        {
            var arg = argv[index];
            if (arg.StartsWith('-'))
            {
                if (arg is "-C" or "-c")
                    index++;
                continue;
            }
            return arg;
        }
        return null;
    }
}
