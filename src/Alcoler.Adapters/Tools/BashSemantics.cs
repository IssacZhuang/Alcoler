using System.Text.RegularExpressions;

namespace Alcoler.Adapters.Tools;

public enum BashFlagValueKind
{
    None,
    String,
    Number,
    OptionalString,
    Char,
    Brace,
    Eof,
}

public sealed record BashReadonlyCommandPolicy(
    bool AllowAnyArgs = false,
    bool CommandOnly = false,
    IReadOnlyDictionary<string, BashFlagValueKind>? SafeFlags = null,
    bool RespectsDoubleDash = true,
    bool AllowCompactNumericCountFlag = false,
    Func<IReadOnlyList<string>, bool>? DangerousArgs = null);

/// <summary>
/// Readonly command classifier plus friendly interpretations for exit code 1 of
/// search/diff/test commands. The classifier is deliberately conservative: any
/// syntax the analyzer cannot model, any expansion,
/// and any non-whitelisted option makes the command non-readonly.
/// </summary>
public static partial class BashSemantics
{
    public static bool IsReadOnlyCommand(string command)
    {
        var analysis = BashCommandAnalyzer.Analyze(command);
        if (!analysis.IsPermissionSafe || analysis.Commands.Count == 0)
            return false;
        if (ContainsGitAndDirectoryChange(analysis.Commands))
            return false;

        var hasReadOnlyCommand = false;
        foreach (var commandPart in analysis.Commands)
        {
            if (HasKnownWriteOption(commandPart))
                return false;
            var verdict = EvaluateReadonlyPolicy(commandPart);
            if (verdict is not true)
                return false;
            hasReadOnlyCommand = true;
        }

        return hasReadOnlyCommand;
    }

    internal static bool EvaluateReadonlyPolicy(BashCommandInvocation commandPart)
    {
        if (!AreEnvAssignmentsAllowed(commandPart))
            return false;
        if (!AreRedirectsAllowed(commandPart))
            return false;

        var argv = StripSafeCommandWrappers(commandPart.Argv);
        if (argv.Count == 0)
            return false;
        if (argv.Any(IsUnsafeWindowsUncPath))
            return false;
        if (argv[0] == "git")
            return IsGitReadOnlyCommand(argv);
        if (argv[0] == "find")
            return IsSafeFindArgv(argv);
        if (BashReadonlyPolicyTables.AllowAnyArgCommands.Contains(argv[0]))
            return true;
        if (argv[0] == "cd" && argv.Count > 2)
            return false;
        if (!BashReadonlyPolicyTables.CommandPolicies.TryGetValue(argv[0], out var policy))
            return false;
        if (policy.DangerousArgs is { } dangerous && dangerous(argv.Skip(1).ToList()))
            return false;
        return IsArgvAllowedByPolicy(argv, policy, argv[0], startIndex: 1);
    }

    internal static bool HasKnownWriteOption(BashCommandInvocation commandPart)
    {
        var argv = StripSafeCommandWrappers(commandPart.Argv);
        var commandName = argv.Count > 0 ? argv[0] : null;
        if (commandName == "sed")
        {
            for (var index = 1; index < argv.Count; index++)
            {
                if (IsSedInPlaceOption(argv[index]))
                    return true;
            }
            return false;
        }
        if (commandName == "find")
        {
            for (var index = 1; index < argv.Count; index++)
            {
                if (BashReadonlyPolicyTables.FindWriteOptions.Contains(argv[index]))
                    return true;
            }
            return false;
        }
        if (commandName == "tree")
        {
            var args = new List<string>(argv.Count - 1);
            for (var index = 1; index < argv.Count; index++)
                args.Add(argv[index]);
            return TreeArgsHaveOutputOption(args);
        }
        if (commandName == "git")
        {
            for (var index = 1; index < argv.Count; index++)
            {
                if (HasDangerousGitGlobalOptionWord(argv[index]))
                    return true;
            }
            return false;
        }
        return false;
    }

    private static bool ContainsGitAndDirectoryChange(IReadOnlyList<BashCommandInvocation> commands) =>
        commands.Any(static c => c.Name == "git") && commands.Any(static c => c.Name == "cd");

    private static bool AreEnvAssignmentsAllowed(BashCommandInvocation commandPart) =>
        commandPart.EnvAssignments.All(static assignment =>
            BashReadonlyPolicyTables.SafeEnvAssignments.Contains(assignment.Name));

    private static bool AreRedirectsAllowed(BashCommandInvocation commandPart)
    {
        foreach (var redirect in commandPart.Redirects)
        {
            if (DeviceRedirectTarget().IsMatch(redirect.Target))
                return false;
            if (redirect.Operator == ">&" && DigitsOnly().IsMatch(redirect.Target))
                continue;
            if (redirect.Target == "/dev/null")
                continue;
            if (BashReadonlyPolicyTables.SafeInputRedirects.Contains(redirect.Operator))
            {
                if (IsUnsafeWindowsUncPath(redirect.Target))
                    return false;
                continue;
            }
            return false;
        }

        return true;
    }

    private static bool IsUnsafeWindowsUncPath(string value) => UncPath().IsMatch(value);

    private static IReadOnlyList<string> StripSafeCommandWrappers(IReadOnlyList<string> argv)
    {
        var start = 0;
        for (; ; )
        {
            var count = argv.Count - start;
            if (count > 0 && argv[start] == "command")
            {
                var index = start + 1;
                while (index < argv.Count && OnlyPDashes().IsMatch(argv[index]))
                    index++;
                if (index < argv.Count && argv[index] == "--")
                    index++;
                if (index >= argv.Count || argv[index].StartsWith('-'))
                    return SliceArgv(argv, start);
                start = index;
                continue;
            }
            if (count > 0 && argv[start] == "builtin")
            {
                var index = start + (count > 1 && argv[start + 1] == "--" ? 2 : 1);
                if (index >= argv.Count)
                    return SliceArgv(argv, start);
                start = index;
                continue;
            }
            if (count > 1 && argv[start] == "noglob")
            {
                start++;
                continue;
            }
            return SliceArgv(argv, start);
        }
    }

    private static IReadOnlyList<string> SliceArgv(IReadOnlyList<string> argv, int start)
    {
        if (start == 0)
            return argv;
        var slice = new List<string>(argv.Count - start);
        for (var index = start; index < argv.Count; index++)
            slice.Add(argv[index]);
        return slice;
    }

    private static bool IsGitReadOnlyCommand(IReadOnlyList<string> argv)
    {
        var normalized = NormalizeGitArgv(argv);
        if (normalized is null)
            return false;
        foreach (var (prefix, policy) in BashReadonlyPolicyTables.GitSubcommandPolicies)
        {
            if (normalized.Count < prefix.Length)
                continue;
            var matches = true;
            for (var index = 0; index < prefix.Length; index++)
            {
                if (normalized[index] != prefix[index])
                {
                    matches = false;
                    break;
                }
            }
            if (!matches)
                continue;
            if (policy.DangerousArgs is { } dangerous)
            {
                var args = new List<string>(normalized.Count - prefix.Length);
                for (var index = prefix.Length; index < normalized.Count; index++)
                    args.Add(normalized[index]);
                if (dangerous(args))
                    return false;
            }
            return IsArgvAllowedByPolicy(normalized, policy, "git", startIndex: prefix.Length);
        }

        return false;
    }

    private static IReadOnlyList<string>? NormalizeGitArgv(IReadOnlyList<string> argv)
    {
        var normalized = new List<string> { "git" };
        for (var index = 1; index < argv.Count; index++)
        {
            var word = argv[index];
            if (BashReadonlyPolicyTables.GitGlobalNoValueFlags.Contains(word))
                continue;
            if (HasDangerousGitGlobalOptionWord(word))
                return null;
            if (word.StartsWith('-'))
                return null;
            for (var tail = index; tail < argv.Count; tail++)
                normalized.Add(argv[tail]);
            return normalized;
        }

        return null;
    }

    private static bool HasDangerousGitGlobalOptionWord(string word)
    {
        foreach (var flag in BashReadonlyPolicyTables.GitGlobalDangerousFlags)
        {
            if (word.Length > flag.Length && word.StartsWith(flag, StringComparison.Ordinal)
                && (flag == "-C" || word[flag.Length] != '-'))
                return true;
            if (word.StartsWith(flag + "=", StringComparison.Ordinal))
                return true;
        }
        return BashReadonlyPolicyTables.GitGlobalDangerousFlags.Contains(word);
    }

    private static bool IsSafeFindArgv(IReadOnlyList<string> argv)
    {
        for (var index = 1; index < argv.Count; index++)
        {
            var word = argv[index];
            if (BashReadonlyPolicyTables.FindWriteOptions.Contains(word))
                return false;
            if (BashReadonlyPolicyTables.FindValueOptions.Contains(word) || NewerVariant().IsMatch(word))
                index++;
        }
        return true;
    }

    private static bool IsArgvAllowedByPolicy(
        IReadOnlyList<string> argv,
        BashReadonlyCommandPolicy policy,
        string commandName,
        int startIndex)
    {
        if (argv.Count == 0)
            return false;
        if (policy.AllowAnyArgs)
            return true;
        if (policy.CommandOnly)
            return argv.Count == startIndex;
        if (policy.SafeFlags is null)
            return false;

        var safeFlags = policy.SafeFlags;
        var index = startIndex;
        while (index < argv.Count)
        {
            var word = argv[index];

            if (word == "--")
            {
                if (!policy.RespectsDoubleDash)
                {
                    index++;
                    continue;
                }
                break;
            }

            if ((commandName == "head" || commandName == "tail") && CompactNumber().IsMatch(word))
            {
                index++;
                continue;
            }

            if (policy.AllowCompactNumericCountFlag && CompactNumber().IsMatch(word))
            {
                index++;
                continue;
            }

            if (word.StartsWith('-') && word.Length > 1 && OptionPattern().IsMatch(word))
            {
                var equalsIndex = word.IndexOf('=');
                var flag = equalsIndex < 0 ? word : word[..equalsIndex];
                var hasInlineValue = equalsIndex >= 0;
                var inlineValue = hasInlineValue ? word[(equalsIndex + 1)..] : string.Empty;
                if (!safeFlags.TryGetValue(flag, out var kind))
                {
                    if (TryParseShortFlagWithAttachedValue(word, safeFlags, out var attached))
                    {
                        if (!IsOptionLikeStringValueAllowed(attached.Value, commandName, attached.Flag))
                            return false;
                        if (!MatchesFlagValueKind(attached.Value, attached.Kind))
                            return false;
                        index++;
                        continue;
                    }
                    if (IsShortFlagClusterAllowed(flag, safeFlags))
                    {
                        index++;
                        continue;
                    }
                    return false;
                }

                if (kind == BashFlagValueKind.None)
                {
                    if (hasInlineValue)
                        return false;
                    index++;
                    continue;
                }

                if (kind == BashFlagValueKind.OptionalString)
                {
                    // A separate argv that looks like an option is the next flag, not this
                    // flag's value, so only the current word is consumed.
                    index++;
                    continue;
                }

                var value = hasInlineValue ? inlineValue
                    : index + 1 < argv.Count ? argv[index + 1]
                    : null;
                if (value is null)
                    return false;
                if (kind == BashFlagValueKind.String
                    && !hasInlineValue
                    && !IsOptionLikeStringValueAllowed(value, commandName, flag))
                    return false;
                if (!MatchesFlagValueKind(value, kind))
                    return false;
                index += hasInlineValue ? 1 : 2;
                continue;
            }

            index++;
        }

        return true;
    }

    private static bool TryParseShortFlagWithAttachedValue(
        string word,
        IReadOnlyDictionary<string, BashFlagValueKind> safeFlags,
        out (string Flag, BashFlagValueKind Kind, string Value) attached)
    {
        attached = default;
        if (!word.StartsWith('-') || word.StartsWith("--") || word.Length <= 2)
            return false;
        var flag = word[..2];
        if (!safeFlags.TryGetValue(flag, out var kind) || kind == BashFlagValueKind.None)
            return false;
        attached = (flag, kind, word[2..]);
        return true;
    }

    private static bool IsShortFlagClusterAllowed(
        string flag,
        IReadOnlyDictionary<string, BashFlagValueKind> safeFlags)
    {
        if (!flag.StartsWith('-') || flag.StartsWith("--") || flag.Length <= 2)
            return false;
        foreach (var c in flag[1..])
        {
            if (!safeFlags.TryGetValue($"-{c}", out var kind) || kind != BashFlagValueKind.None)
                return false;
        }
        return true;
    }

    private static bool IsOptionLikeStringValueAllowed(string value, string commandName, string flag)
    {
        if (!value.StartsWith('-') || value.Length <= 1 || !OptionPattern().IsMatch(value))
            return true;
        return commandName == "git" && flag == "--sort" && ShortLetterOption().IsMatch(value);
    }

    private static bool MatchesFlagValueKind(string value, BashFlagValueKind kind) => kind switch
    {
        BashFlagValueKind.None => false,
        BashFlagValueKind.Number => DigitsOnly().IsMatch(value),
        BashFlagValueKind.OptionalString or BashFlagValueKind.String => true,
        BashFlagValueKind.Char => value.Length == 1,
        BashFlagValueKind.Brace => value == "{}",
        BashFlagValueKind.Eof => value == "EOF",
        _ => false,
    };

    private static bool IsSedInPlaceOption(string arg) =>
        arg.StartsWith("-i", StringComparison.Ordinal)
        || arg == "--in-place"
        || arg.StartsWith("--in-place=", StringComparison.Ordinal);

    internal static bool SedCommandIsDangerous(IReadOnlyList<string> args)
    {
        var firstScriptSeen = false;
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.Length == 0)
                continue;
            if (IsSedInPlaceOption(arg))
                return true;
            if (arg is "-e" or "--expression")
            {
                index++;
                if (index < args.Count && SedScriptWritesToFile(args[index]))
                    return true;
                continue;
            }
            if (arg.StartsWith("--expression=", StringComparison.Ordinal)
                && SedScriptWritesToFile(arg["--expression=".Length..]))
                return true;
            if (arg is "-l" or "--line-length")
            {
                index++;
                continue;
            }
            if (arg.StartsWith("--line-length=", StringComparison.Ordinal))
                continue;
            if (arg == "--")
            {
                var script = index + 1 < args.Count ? args[index + 1] : string.Empty;
                return SedScriptWritesToFile(script);
            }
            if (!arg.StartsWith('-') && !firstScriptSeen)
            {
                firstScriptSeen = true;
                if (SedScriptWritesToFile(arg))
                    return true;
            }
        }
        return false;
    }

    private static bool SedScriptWritesToFile(string script) =>
        SedWriteFileOption().IsMatch(script);

    internal static bool TreeArgsHaveOutputOption(IReadOnlyList<string> args)
    {
        foreach (var word in args)
        {
            if (word == "--")
                return false;
            if (word is "-o" or "--output" || word.StartsWith("--output=", StringComparison.Ordinal))
                return true;
            if (word.StartsWith('-') && !word.StartsWith("--") && word[1..].Contains('o'))
                return true;
        }
        return false;
    }

    internal static bool GitRevisionFormatCommandIsDangerous(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            var value = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..]
                : index + 1 < args.Count ? args[index + 1]
                : null;
            if ((arg is "--format" or "--pretty"
                    || arg.StartsWith("--format=", StringComparison.Ordinal)
                    || arg.StartsWith("--pretty=", StringComparison.Ordinal))
                && value is not null
                && GitSignatureFormat().IsMatch(value))
                return true;
        }
        return false;
    }

    internal static bool GitReflogCommandIsDangerous(IReadOnlyList<string> args)
    {
        var firstPositional = args.FirstOrDefault(arg => arg.Length > 0 && !arg.StartsWith('-'));
        if (firstPositional is not null && !BashReadonlyPolicyTables.GitReflogAllowedSubcommands.Contains(firstPositional))
            return true;
        return args.Any(BashReadonlyPolicyTables.GitReflogDangerousSubcommands.Contains);
    }

    internal static bool GitLsRemoteCommandIsDangerous(IReadOnlyList<string> args)
    {
        var afterDoubleDash = false;
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!afterDoubleDash && arg == "--")
            {
                afterDoubleDash = true;
                continue;
            }
            if (!afterDoubleDash && (arg.StartsWith('-') || arg.Length == 0))
            {
                if (arg == "--sort")
                    index++;
                continue;
            }
            return true;
        }
        return false;
    }

    internal static bool GitListLikeCommandIsDangerous(IReadOnlyList<string> args, IReadOnlyList<string> valueFlags)
    {
        var hasList = false;
        var afterDoubleDash = false;
        var previousFlag = string.Empty;
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.Length == 0)
                continue;
            if (arg == "--" && !afterDoubleDash)
            {
                afterDoubleDash = true;
                previousFlag = string.Empty;
                continue;
            }
            if (!afterDoubleDash && arg.StartsWith('-'))
            {
                if (arg == "--list" || arg == "-l"
                    || (arg.Length > 1 && arg[0] == '-' && arg[1] != '-' && arg[1..].Contains('l')))
                    hasList = true;
                previousFlag = arg.Contains('=') ? arg[..arg.IndexOf('=')] : arg;
                if (!arg.Contains('=') && valueFlags.Contains(previousFlag))
                    index++;
                continue;
            }
            if (!hasList && previousFlag is not "--merged" and not "--no-merged")
                return true;
        }
        return false;
    }

    [GeneratedRegex("^\\d+$")]
    private static partial Regex DigitsOnly();

    [GeneratedRegex("^-[a-zA-Z0-9_-]")]
    private static partial Regex OptionPattern();

    [GeneratedRegex("^-[a-zA-Z]")]
    private static partial Regex ShortLetterOption();

    [GeneratedRegex("^-\\d+$")]
    private static partial Regex CompactNumber();

    [GeneratedRegex("^-p+$")]
    private static partial Regex OnlyPDashes();

    [GeneratedRegex("^(?://|\\\\\\\\)[^/\\\\]")]
    private static partial Regex UncPath();

    [GeneratedRegex("^/dev/(?:tcp|udp)/")]
    private static partial Regex DeviceRedirectTarget();

    [GeneratedRegex("^-newer[aBcm][aBcmt]$")]
    private static partial Regex NewerVariant();

    [GeneratedRegex("%[-+ ]?G|%\\(\\*?signature")]
    private static partial Regex GitSignatureFormat();

    [GeneratedRegex("(?:^|[;{\\n])\\s*(?:[0-9,$!+~-]+)?\\s*w(?:\\s|$)")]
    private static partial Regex SedWriteFileOption();
}
