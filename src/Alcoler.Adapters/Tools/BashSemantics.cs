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
/// Ports ZCode's bash-semantics readonly command classifier and the friendly
/// interpretations for exit code 1 of search/diff/test commands. The classifier is
/// deliberately conservative: any syntax the analyzer cannot model, any expansion,
/// and any non-whitelisted option makes the command non-readonly.
/// </summary>
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

    private static readonly HashSet<string> SafeEnvAssignments = new(StringComparer.Ordinal)
    {
        "ANTHROPIC_API_KEY", "BLOCK_SIZE", "BLOCKSIZE", "CGO_ENABLED", "CHARSET", "CI",
        "CLICOLOR", "CLICOLOR_FORCE", "COLORTERM", "COLUMNS", "DEBIAN_FRONTEND",
        "FORCE_COLOR", "GCC_COLORS", "GIT_TERMINAL_PROMPT", "GO111MODULE", "GOARCH",
        "GOEXPERIMENT", "GOOS", "GREP_COLOR", "GREP_COLORS", "LANG", "LANGUAGE", "LC_ALL",
        "LC_CTYPE", "LC_TIME", "LINES", "LSCOLORS", "LS_COLORS", "NO_COLOR", "NODE_ENV",
        "PYTEST_DEBUG", "PYTEST_DISABLE_PLUGIN_AUTOLOAD", "PYTHONDONTWRITEBYTECODE",
        "PYTHONUNBUFFERED", "RUST_BACKTRACE", "RUST_LOG", "TERM", "TIME_STYLE", "TZ",
    };

    private static readonly string[] SafeInputRedirects = ["<", "<&", "<<<"];

    private static readonly HashSet<string> GitGlobalDangerousFlags = new(StringComparer.Ordinal)
    {
        "-c", "-C", "--attr-source", "--bare", "--config-env", "--exec-path", "--git-dir",
        "--namespace", "--shallow-file", "--super-prefix", "--work-tree",
    };

    private static readonly HashSet<string> GitGlobalNoValueFlags = new(StringComparer.Ordinal)
    {
        "--no-pager", "--paginate",
    };

    private static readonly HashSet<string> FindWriteOptions = new(StringComparer.Ordinal)
    {
        "-delete", "-exec", "-execdir", "-files0-from", "-fls", "-fprint", "-fprint0",
        "-fprintf", "-ok", "-okdir",
    };

    private static readonly HashSet<string> FindValueOptions = new(StringComparer.Ordinal)
    {
        "-Bmin", "-Bnewer", "-Btime", "-D", "-amin", "-anewer", "-atime", "-cmin",
        "-cnewer", "-context", "-ctime", "-f", "-flags", "-fstype", "-gid", "-group",
        "-ilname", "-iname", "-inum", "-ipath", "-iregex", "-iwholename", "-lname",
        "-links", "-maxdepth", "-mindepth", "-mmin", "-mnewer", "-mtime", "-name",
        "-newer", "-path", "-perm", "-printf", "-regex", "-regextype", "-samefile",
        "-size", "-type", "-used", "-user", "-wholename", "-xattrname", "-xtype", "-uid",
    };

    private static readonly HashSet<string> AllowAnyArgCommands = new(StringComparer.Ordinal)
    {
        "basename", "cal", "cat", "cmp", "column", "comm", "cut", "df", "diff", "dirname",
        "du", "expand", "expr", "false", "fmt", "fold", "free", "groups", "head",
        "hexdump", "id", "locale", "nl", "nproc", "numfmt", "od", "paste", "pr",
        "readlink", "realpath", "rev", "seq", "sleep", "stat", "strings", "tac", "tail",
        "tr", "true", "tsort", "type", "uname", "unexpand", "uptime", "wc", "which",
    };

    private static readonly IReadOnlyDictionary<string, BashReadonlyCommandPolicy> CommandPolicies =
        new Dictionary<string, BashReadonlyCommandPolicy>(StringComparer.Ordinal)
        {
            ["alias"] = new(CommandOnly: true),
            ["cat"] = new(SafeFlags: Flags.CatSafeFlags),
            ["cd"] = new(AllowAnyArgs: true),
            ["cut"] = new(AllowAnyArgs: true),
            ["diff"] = new(AllowAnyArgs: true),
            ["dirname"] = new(AllowAnyArgs: true),
            ["du"] = new(AllowAnyArgs: true),
            ["egrep"] = new(SafeFlags: Flags.GrepSafeFlags),
            ["false"] = new(CommandOnly: true),
            ["fd"] = new(SafeFlags: Flags.FdSafeFlags),
            ["fdfind"] = new(SafeFlags: Flags.FdSafeFlags),
            ["fgrep"] = new(SafeFlags: Flags.GrepSafeFlags),
            ["file"] = new(SafeFlags: Flags.FileSafeFlags),
            ["grep"] = new(SafeFlags: Flags.GrepSafeFlags),
            ["head"] = new(SafeFlags: Flags.HeadTailSafeFlags),
            ["ls"] = new(AllowAnyArgs: true),
            ["pwd"] = new(CommandOnly: true),
            ["readlink"] = new(AllowAnyArgs: true),
            ["realpath"] = new(AllowAnyArgs: true),
            ["rev"] = new(AllowAnyArgs: true),
            ["rg"] = new(SafeFlags: Flags.RgSafeFlags),
            ["sed"] = new(
                SafeFlags: Flags.SedSafeFlags,
                DangerousArgs: SedCommandIsDangerous),
            ["sort"] = new(SafeFlags: Flags.SortSafeFlags),
            ["tail"] = new(SafeFlags: Flags.HeadTailSafeFlags),
            ["tree"] = new(
                SafeFlags: Flags.TreeSafeFlags,
                DangerousArgs: TreeArgsHaveOutputOption),
            ["true"] = new(CommandOnly: true),
            ["whoami"] = new(CommandOnly: true),
        };

    private static readonly (string[] Prefix, BashReadonlyCommandPolicy Policy)[] GitSubcommandPolicies =
    [
        (["git", "blame"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitBlameSafeFlags)),
        (["git", "branch"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitBranchSafeFlags,
            DangerousArgs: args => GitListLikeCommandIsDangerous(
                args,
                ["--contains", "--no-contains", "--points-at", "--sort"]))),
        (["git", "cat-file"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitCatFileSafeFlags)),
        (["git", "config", "--get"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitConfigGetSafeFlags)),
        (["git", "describe"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitDescribeSafeFlags)),
        (["git", "diff"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitDiffSafeFlags)),
        (["git", "for-each-ref"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitForEachRefSafeFlags,
            DangerousArgs: GitRevisionFormatCommandIsDangerous)),
        (["git", "grep"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitGrepSafeFlags)),
        (["git", "log"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitLogSafeFlags,
            AllowCompactNumericCountFlag: true,
            DangerousArgs: GitRevisionFormatCommandIsDangerous)),
        (["git", "ls-files"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitLsFilesSafeFlags)),
        (["git", "ls-remote"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitLsRemoteSafeFlags,
            DangerousArgs: GitLsRemoteCommandIsDangerous)),
        (["git", "merge-base"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitMergeBaseSafeFlags)),
        (["git", "reflog"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitLogStyleSafeFlags,
            DangerousArgs: GitReflogCommandIsDangerous)),
        (["git", "remote"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitRemoteSafeFlags,
            DangerousArgs: args => args.Any(arg => arg != "-v" && arg != "--verbose"))),
        (["git", "rev-list"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitRevListSafeFlags,
            DangerousArgs: GitRevisionFormatCommandIsDangerous)),
        (["git", "rev-parse"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitRevParseSafeFlags)),
        (["git", "shortlog"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitShortlogSafeFlags,
            DangerousArgs: GitRevisionFormatCommandIsDangerous)),
        (["git", "show"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitShowSafeFlags,
            DangerousArgs: GitRevisionFormatCommandIsDangerous)),
        (["git", "stash", "list"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitLogStyleSafeFlags)),
        (["git", "stash", "show"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitStashShowSafeFlags)),
        (["git", "status"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitStatusSafeFlags)),
        (["git", "tag"], new BashReadonlyCommandPolicy(
            SafeFlags: Flags.GitTagSafeFlags,
            DangerousArgs: args => GitListLikeCommandIsDangerous(
                args,
                ["--contains", "--no-contains", "--merged", "--no-merged", "--points-at", "--sort", "--format", "-n"]))),
        (["git", "worktree", "list"], new BashReadonlyCommandPolicy(SafeFlags: Flags.GitWorktreeListSafeFlags)),
    ];

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
        if (AllowAnyArgCommands.Contains(argv[0]))
            return true;
        if (argv[0] == "cd" && argv.Count > 2)
            return false;
        if (!CommandPolicies.TryGetValue(argv[0], out var policy))
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
            return argv.Skip(1).Any(IsSedInPlaceOption);
        if (commandName == "find")
            return argv.Skip(1).Any(FindWriteOptions.Contains);
        if (commandName == "tree")
            return TreeArgsHaveOutputOption(argv.Skip(1).ToList());
        if (commandName == "git")
            return argv.Skip(1).Any(HasDangerousGitGlobalOptionWord);
        return false;
    }

    private static bool ContainsGitAndDirectoryChange(IReadOnlyList<BashCommandInvocation> commands) =>
        commands.Any(static c => c.Name == "git") && commands.Any(static c => c.Name == "cd");

    private static bool AreEnvAssignmentsAllowed(BashCommandInvocation commandPart) =>
        commandPart.EnvAssignments.All(static assignment => SafeEnvAssignments.Contains(assignment.Name));

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
            if (SafeInputRedirects.Contains(redirect.Operator))
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
        var stripped = argv.ToList();
        for (; ; )
        {
            if (stripped.Count > 0 && stripped[0] == "command")
            {
                var index = 1;
                while (index < stripped.Count && OnlyPDashes().IsMatch(stripped[index]))
                    index++;
                if (index < stripped.Count && stripped[index] == "--")
                    index++;
                if (index >= stripped.Count || stripped[index].StartsWith('-'))
                    return stripped;
                stripped = stripped.GetRange(index, stripped.Count - index);
                continue;
            }
            if (stripped.Count > 0 && stripped[0] == "builtin")
            {
                var index = stripped.Count > 1 && stripped[1] == "--" ? 2 : 1;
                if (index >= stripped.Count)
                    return stripped;
                stripped = stripped.GetRange(index, stripped.Count - index);
                continue;
            }
            if (stripped.Count > 1 && stripped[0] == "noglob")
            {
                stripped = stripped.GetRange(1, stripped.Count - 1);
                continue;
            }
            return stripped;
        }
    }

    private static bool IsGitReadOnlyCommand(IReadOnlyList<string> argv)
    {
        var normalized = NormalizeGitArgv(argv);
        if (normalized is null)
            return false;
        foreach (var (prefix, policy) in GitSubcommandPolicies)
        {
            if (normalized.Count < prefix.Length)
                continue;
            var matches = prefix.Select((word, index) => normalized[index] == word).All(equal => equal);
            if (!matches)
                continue;
            var args = normalized.Skip(prefix.Length).ToList();
            if (policy.DangerousArgs is { } dangerous && dangerous(args))
                return false;
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
            if (GitGlobalNoValueFlags.Contains(word))
                continue;
            if (HasDangerousGitGlobalOptionWord(word))
                return null;
            if (word.StartsWith('-'))
                return null;
            normalized.AddRange(argv.Skip(index));
            return normalized;
        }

        return null;
    }

    private static bool HasDangerousGitGlobalOptionWord(string word)
    {
        foreach (var flag in GitGlobalDangerousFlags)
        {
            if (word.Length > flag.Length && word.StartsWith(flag, StringComparison.Ordinal)
                && (flag == "-C" || word[flag.Length] != '-'))
                return true;
            if (word.StartsWith(flag + "=", StringComparison.Ordinal))
                return true;
        }
        return GitGlobalDangerousFlags.Contains(word);
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

    private static bool IsSafeFindArgv(IReadOnlyList<string> argv)
    {
        for (var index = 1; index < argv.Count; index++)
        {
            var word = argv[index];
            if (FindWriteOptions.Contains(word))
                return false;
            if (FindValueOptions.Contains(word) || NewerVariant().IsMatch(word))
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

    private static bool SedCommandIsDangerous(IReadOnlyList<string> args)
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

    private static bool TreeArgsHaveOutputOption(IReadOnlyList<string> args)
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

    private static bool GitRevisionFormatCommandIsDangerous(IReadOnlyList<string> args)
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

    private static bool GitReflogCommandIsDangerous(IReadOnlyList<string> args)
    {
        string[] allowed = ["show", "list"];
        string[] dangerous = ["expire", "delete", "exists", "drop", "write"];
        var firstPositional = args.FirstOrDefault(arg => arg.Length > 0 && !arg.StartsWith('-'));
        if (firstPositional is not null && !allowed.Contains(firstPositional))
            return true;
        return args.Any(dangerous.Contains);
    }

    private static bool GitLsRemoteCommandIsDangerous(IReadOnlyList<string> args)
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

    private static bool GitListLikeCommandIsDangerous(IReadOnlyList<string> args, IReadOnlyList<string> valueFlags)
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

    private static class Flags
    {
        private const BashFlagValueKind None = BashFlagValueKind.None;
        private const BashFlagValueKind Number = BashFlagValueKind.Number;
        private const BashFlagValueKind String = BashFlagValueKind.String;

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> CatSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-A"] = None, ["-E"] = None, ["-T"] = None, ["-b"] = None, ["-e"] = None,
                ["-n"] = None, ["-s"] = None, ["-t"] = None, ["-u"] = None, ["-v"] = None,
                ["--number"] = None, ["--number-nonblank"] = None, ["--show-all"] = None,
                ["--show-ends"] = None, ["--show-nonprinting"] = None, ["--show-tabs"] = None,
                ["--squeeze-blank"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> HeadTailSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-c"] = Number, ["-n"] = Number, ["-q"] = None, ["-v"] = None,
                ["--bytes"] = Number, ["--lines"] = Number, ["--quiet"] = None,
                ["--silent"] = None, ["--verbose"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GrepSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-A"] = Number, ["-B"] = Number, ["-C"] = Number, ["-E"] = None, ["-F"] = None,
                ["-G"] = None, ["-H"] = None, ["-I"] = None, ["-L"] = None, ["-P"] = None,
                ["-R"] = None, ["-a"] = None, ["-b"] = None, ["-c"] = None, ["-D"] = String,
                ["-d"] = String, ["-e"] = String, ["-f"] = String, ["-h"] = None, ["-i"] = None,
                ["-l"] = None, ["-m"] = Number, ["-n"] = None, ["-o"] = None, ["-q"] = None,
                ["-r"] = None, ["-s"] = None, ["-T"] = None, ["-u"] = None, ["-U"] = None,
                ["-v"] = None, ["-V"] = None, ["-w"] = None, ["-x"] = None, ["-Z"] = None,
                ["-z"] = None,
                ["--after-context"] = Number, ["--basic-regexp"] = None,
                ["--before-context"] = Number, ["--binary"] = None,
                ["--binary-files"] = String, ["--byte-offset"] = None, ["--color"] = String,
                ["--colour"] = String, ["--context"] = Number, ["--count"] = None,
                ["--dereference-recursive"] = None, ["--directories"] = String,
                ["--devices"] = String, ["--exclude"] = String, ["--exclude-dir"] = String,
                ["--exclude-from"] = String, ["--extended-regexp"] = None, ["--file"] = String,
                ["--files-with-matches"] = None, ["--files-without-match"] = None,
                ["--fixed-strings"] = None, ["--group-separator"] = String, ["--help"] = None,
                ["--ignore-case"] = None, ["--include"] = String, ["--initial-tab"] = None,
                ["--invert-match"] = None, ["--label"] = String, ["--line-buffered"] = None,
                ["--line-number"] = None, ["--line-regexp"] = None, ["--max-count"] = Number,
                ["--no-group-separator"] = None, ["--no-filename"] = None,
                ["--no-ignore-case"] = None, ["--no-messages"] = None, ["--null"] = None,
                ["--null-data"] = None, ["--only-matching"] = None,
                ["--perl-regexp"] = None, ["--quiet"] = None, ["--recursive"] = None,
                ["--regexp"] = String, ["--silent"] = None, ["--text"] = None,
                ["--unix-byte-offsets"] = None, ["--version"] = None,
                ["--with-filename"] = None, ["--word-regexp"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> RgSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-A"] = Number, ["-B"] = Number, ["-C"] = Number, ["-F"] = None, ["-H"] = None,
                ["-L"] = None, ["-N"] = None, ["-S"] = None, ["-T"] = String, ["-c"] = None,
                ["-e"] = String, ["-g"] = String, ["-h"] = None, ["-i"] = None, ["-l"] = None,
                ["-m"] = Number, ["-n"] = None, ["-s"] = None, ["-t"] = String, ["-u"] = None,
                ["-v"] = None, ["-w"] = None, ["-x"] = None,
                ["--after-context"] = Number, ["--before-context"] = Number,
                ["--case-sensitive"] = None, ["--context"] = Number, ["--count"] = None,
                ["--count-matches"] = None, ["--files"] = None,
                ["--files-with-matches"] = None, ["--fixed-strings"] = None,
                ["--follow"] = None, ["--glob"] = String, ["--hidden"] = None,
                ["--ignore-case"] = None, ["--ignore-file"] = String, ["--iglob"] = String,
                ["--invert-match"] = None, ["--json"] = None, ["--line-number"] = None,
                ["--line-regexp"] = None, ["--max-count"] = Number, ["--max-depth"] = Number,
                ["--max-filesize"] = String, ["--no-filename"] = None, ["--no-ignore"] = None,
                ["--no-ignore-parent"] = None, ["--no-ignore-vcs"] = None,
                ["--no-line-number"] = None, ["--pcre2"] = None, ["--regexp"] = String,
                ["--smart-case"] = None, ["--sort"] = String, ["--sortr"] = String,
                ["--type"] = String, ["--type-not"] = String, ["--unrestricted"] = None,
                ["--with-filename"] = None, ["--word-regexp"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> FdSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-H"] = None, ["-I"] = None, ["-L"] = None, ["-V"] = None, ["-a"] = None,
                ["-E"] = String, ["-S"] = String, ["-c"] = String, ["-d"] = Number,
                ["-e"] = String, ["-g"] = None, ["-h"] = None, ["-i"] = None,
                ["-j"] = Number, ["-o"] = String, ["-p"] = None, ["-q"] = None,
                ["-s"] = None, ["-t"] = String,
                ["--absolute-path"] = None, ["--and"] = String,
                ["--base-directory"] = String, ["--batch-size"] = Number,
                ["--case-sensitive"] = None, ["--changed-before"] = String,
                ["--changed-within"] = String, ["--color"] = String,
                ["--exact-depth"] = Number, ["--exclude"] = String,
                ["--extension"] = String, ["--fixed-strings"] = None, ["--follow"] = None,
                ["--format"] = String, ["--full-path"] = None, ["--glob"] = None,
                ["--help"] = None, ["--hidden"] = None, ["--hyperlink"] = String,
                ["--ignore-case"] = None, ["--ignore-file"] = String,
                ["--max-buffer-time"] = String, ["--max-depth"] = Number,
                ["--max-results"] = Number, ["--min-depth"] = Number, ["--no-ignore"] = None,
                ["--no-ignore-parent"] = None, ["--no-ignore-vcs"] = None,
                ["--no-require-git"] = None, ["--one-file-system"] = None,
                ["--owner"] = String, ["--path-separator"] = String, ["--print0"] = None,
                ["--prune"] = None, ["--quiet"] = None, ["--regex"] = None,
                ["--search-path"] = String, ["--show-errors"] = None, ["--size"] = String,
                ["--strip-cwd-prefix"] = None, ["--threads"] = Number, ["--type"] = String,
                ["--version"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> SortSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-C"] = None, ["-M"] = None, ["-R"] = None, ["-V"] = None, ["-b"] = None,
                ["-c"] = None, ["-d"] = None, ["-f"] = None, ["-g"] = None, ["-h"] = None,
                ["-i"] = None, ["-m"] = None, ["-n"] = None, ["-r"] = None, ["-s"] = None,
                ["-u"] = None, ["-z"] = None, ["-S"] = String, ["-k"] = String,
                ["-t"] = String,
                ["--batch-size"] = Number, ["--buffer-size"] = String, ["--check"] = None,
                ["--check-char-order"] = None, ["--dictionary-order"] = None,
                ["--field-separator"] = String, ["--general-numeric-sort"] = None,
                ["--help"] = None, ["--human-numeric-sort"] = None, ["--ignore-case"] = None,
                ["--ignore-leading-blanks"] = None, ["--ignore-nonprinting"] = None,
                ["--key"] = String, ["--merge"] = None, ["--month-sort"] = None,
                ["--numeric-sort"] = None, ["--parallel"] = Number, ["--random-sort"] = None,
                ["--reverse"] = None, ["--sort"] = String, ["--stable"] = None,
                ["--unique"] = None, ["--version"] = None, ["--version-sort"] = None,
                ["--zero-terminated"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> FileSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-0"] = None, ["-L"] = None, ["-b"] = None, ["-c"] = None, ["-h"] = None,
                ["-i"] = None, ["-k"] = None, ["-l"] = None, ["-n"] = None, ["-p"] = None,
                ["-r"] = None, ["-s"] = None, ["-v"] = None, ["-F"] = String,
                ["-f"] = String, ["-m"] = String,
                ["--apple"] = None, ["--brief"] = None, ["--check-encoding"] = None,
                ["--dereference"] = None, ["--exclude"] = String,
                ["--exclude-quiet"] = String, ["--help"] = None, ["--keep-going"] = None,
                ["--list"] = None, ["--magic-file"] = String, ["--mime"] = None,
                ["--mime-encoding"] = None, ["--mime-type"] = None, ["--no-buffer"] = None,
                ["--no-dereference"] = None, ["--preserve-date"] = None,
                ["--print0"] = None, ["--raw"] = None, ["--separator"] = String,
                ["--special-files"] = None, ["--version"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> SedSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-E"] = None, ["-n"] = None, ["-r"] = None, ["-s"] = None, ["-u"] = None,
                ["-z"] = None, ["-e"] = String, ["-l"] = Number,
                ["--debug"] = None, ["--expression"] = String, ["--help"] = None,
                ["--line-length"] = Number, ["--posix"] = None, ["--quiet"] = None,
                ["--regexp-extended"] = None, ["--separate"] = None, ["--silent"] = None,
                ["--unbuffered"] = None, ["--version"] = None,
                ["--zero-terminated"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> TreeSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-a"] = None, ["-c"] = None, ["-d"] = None, ["-f"] = None, ["-g"] = None,
                ["-h"] = None, ["-i"] = None, ["-l"] = None, ["-n"] = None, ["-p"] = None,
                ["-q"] = None, ["-r"] = None, ["-s"] = None, ["-t"] = None, ["-u"] = None,
                ["-v"] = None, ["-x"] = None, ["-D"] = None, ["-F"] = None, ["-J"] = None,
                ["-N"] = None, ["-Q"] = None, ["-S"] = None, ["-U"] = None, ["-X"] = None,
                ["-A"] = None, ["-C"] = None, ["-H"] = String, ["-I"] = String,
                ["-L"] = Number, ["-P"] = String, ["-T"] = String,
                ["--charset"] = String, ["--device"] = None, ["--dirsfirst"] = None,
                ["--du"] = None, ["--fflinks"] = None, ["--filelimit"] = Number,
                ["--filesfirst"] = None, ["--fromfile"] = None, ["--fromtabfile"] = None,
                ["--gitfile"] = String, ["--gitignore"] = None, ["--help"] = None,
                ["--hintro"] = String, ["--houtro"] = String, ["--hyperlink"] = None,
                ["--ignore-case"] = None, ["--inodes"] = None, ["--info"] = None,
                ["--infofile"] = String, ["--matchdirs"] = None, ["--metafirst"] = None,
                ["--nolinks"] = None, ["--noreport"] = None, ["--prune"] = None,
                ["--scheme"] = String, ["--si"] = None, ["--sort"] = String,
                ["--timefmt"] = String, ["--version"] = None,
            };

        private static IReadOnlyDictionary<string, BashFlagValueKind> Merge(
            params IReadOnlyDictionary<string, BashFlagValueKind>[] tables)
        {
            var merged = new Dictionary<string, BashFlagValueKind>(StringComparer.Ordinal);
            foreach (var table in tables)
                foreach (var entry in table)
                    merged[entry.Key] = entry.Value;
            return merged;
        }

        private static IReadOnlyDictionary<string, BashFlagValueKind> NoneFlags(params string[] flags)
        {
            var map = new Dictionary<string, BashFlagValueKind>(StringComparer.Ordinal);
            foreach (var flag in flags)
                map[flag] = None;
            return map;
        }

        private static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitRefFlags =
            NoneFlags("--all", "--branches", "--remotes", "--tags");

        private static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitDateFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["--after"] = String, ["--before"] = String, ["--since"] = String,
                ["--until"] = String,
            };

        private static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitLogStyleFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["--date"] = String, ["--decorate"] = None, ["--graph"] = None,
                ["--no-decorate"] = None, ["--oneline"] = None, ["--relative-date"] = None,
            };

        private static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitCountFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-n"] = Number, ["--max-count"] = Number,
            };

        private static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitStatFlags =
            NoneFlags("--name-only", "--name-status", "--numstat", "--shortstat", "--stat");

        private static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitColorFlags =
            NoneFlags("--color", "--no-color");

        private static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitPatchFlags =
            NoneFlags("-p", "-s", "--no-ext-diff", "--no-patch", "--patch");

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitLogStyleSafeFlags =
            Merge(GitLogStyleFlags, GitRefFlags, GitDateFlags, GitCountFlags, GitStatFlags, GitColorFlags, GitPatchFlags);

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitStatusSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-b"] = None, ["-s"] = None, ["-v"] = None, ["-M"] = String, ["-u"] = String,
                ["--ahead-behind"] = None, ["--branch"] = None, ["--column"] = None,
                ["--find-renames"] = String, ["--ignore-submodules"] = String,
                ["--ignored"] = None, ["--long"] = None, ["--no-ahead-behind"] = None,
                ["--no-column"] = None, ["--no-renames"] = None, ["--porcelain"] = None,
                ["--renames"] = None, ["--short"] = None, ["--untracked-files"] = String,
                ["--verbose"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitDiffSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-B"] = None, ["-C"] = None, ["-D"] = None, ["-M"] = None, ["-R"] = None,
                ["-l"] = None, ["-p"] = None, ["-s"] = None, ["-u"] = None,
                ["-G"] = String, ["-O"] = String, ["-S"] = String,
                ["--cached"] = None, ["--check"] = None, ["--color"] = None,
                ["--color-words"] = None, ["--diff-algorithm"] = String,
                ["--diff-filter"] = String, ["--dirstat"] = None, ["--exit-code"] = None,
                ["--find-copies"] = None, ["--find-copies-harder"] = None,
                ["--find-renames"] = None, ["--full-index"] = None,
                ["--function-context"] = None, ["--histogram"] = None,
                ["--ignore-all-space"] = None, ["--ignore-blank-lines"] = None,
                ["--ignore-space-at-eol"] = None, ["--ignore-space-change"] = None,
                ["--inter-hunk-context"] = Number, ["--irreversible-delete"] = None,
                ["--minimal"] = None, ["--name-only"] = None, ["--name-status"] = None,
                ["--no-ext-diff"] = None, ["--no-index"] = None, ["--no-renames"] = None,
                ["--no-color"] = None, ["--numstat"] = None, ["--patch-with-stat"] = None,
                ["--patience"] = None, ["--pickaxe-all"] = None, ["--pickaxe-regex"] = None,
                ["--quiet"] = None, ["--relative"] = String, ["--shortstat"] = None,
                ["--staged"] = None, ["--stat"] = None, ["--summary"] = None,
                ["--word-diff"] = None, ["--word-diff-regex"] = String,
                ["--ws-error-highlight"] = String,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitLogSafeFlags =
            Merge(
                GitLogStyleSafeFlags,
                new Dictionary<string, BashFlagValueKind>
                {
                    ["-G"] = String, ["-S"] = String, ["--abbrev-commit"] = None,
                    ["--ancestry-path"] = None, ["--author"] = String,
                    ["--author-date-order"] = None, ["--boundary"] = None,
                    ["--cherry-mark"] = None, ["--cherry-pick"] = None,
                    ["--committer"] = String, ["--date-order"] = None, ["--dense"] = None,
                    ["--diff-filter"] = String, ["--first-parent"] = None,
                    ["--format"] = String, ["--full-history"] = None, ["--grep"] = String,
                    ["--left-right"] = None, ["--max-age"] = Number, ["--min-age"] = Number,
                    ["--merges"] = None, ["--no-max-parents"] = None, ["--no-merges"] = None,
                    ["--no-min-parents"] = None, ["--no-walk"] = None,
                    ["--pickaxe-all"] = None, ["--pickaxe-regex"] = None,
                    ["--pretty"] = String, ["--reverse"] = None, ["--simplify-merges"] = None,
                    ["--skip"] = Number, ["--source"] = None, ["--sparse"] = None,
                    ["--topo-order"] = None, ["--walk-reflogs"] = None,
                });

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitShowSafeFlags =
            Merge(
                GitLogStyleFlags, GitStatFlags, GitColorFlags, GitPatchFlags,
                new Dictionary<string, BashFlagValueKind>
                {
                    ["-m"] = None, ["--abbrev-commit"] = None, ["--color-words"] = None,
                    ["--diff-filter"] = String, ["--first-parent"] = None,
                    ["--format"] = String, ["--pretty"] = String, ["--quiet"] = None,
                    ["--raw"] = None, ["--word-diff"] = None, ["--word-diff-regex"] = String,
                });

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitGrepSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-A"] = Number, ["-B"] = Number, ["-C"] = Number, ["-E"] = None,
                ["-F"] = None, ["-G"] = None, ["-H"] = None, ["-L"] = None, ["-P"] = None,
                ["-c"] = None, ["-e"] = String, ["-h"] = None, ["-i"] = None, ["-l"] = None,
                ["-n"] = None, ["-o"] = None, ["-q"] = None, ["-v"] = None, ["-w"] = None,
                ["--after-context"] = Number, ["--and"] = None,
                ["--basic-regexp"] = None, ["--break"] = None, ["--cached"] = None,
                ["--color"] = None, ["--context"] = Number, ["--count"] = None,
                ["--extended-regexp"] = None, ["--files-with-matches"] = None,
                ["--files-without-match"] = None, ["--fixed-strings"] = None,
                ["--full-name"] = None, ["--heading"] = None, ["--ignore-case"] = None,
                ["--invert-match"] = None, ["--line-number"] = None,
                ["--max-depth"] = Number, ["--no-color"] = None, ["--no-index"] = None,
                ["--not"] = None, ["--only-matching"] = None, ["--or"] = None,
                ["--perl-regexp"] = None, ["--quiet"] = None,
                ["--recurse-submodules"] = None, ["--threads"] = Number,
                ["--untracked"] = None, ["--word-regexp"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitBlameSafeFlags =
            Merge(
                GitColorFlags,
                new Dictionary<string, BashFlagValueKind>
                {
                    ["-C"] = None, ["-L"] = String, ["-M"] = None, ["-e"] = None,
                    ["-f"] = None, ["-l"] = None, ["-n"] = None, ["-p"] = None,
                    ["-s"] = None, ["-t"] = None, ["-w"] = None,
                    ["--abbrev"] = Number, ["--date"] = String, ["--ignore-rev"] = String,
                    ["--ignore-revs-file"] = String, ["--incremental"] = None,
                    ["--line-porcelain"] = None, ["--porcelain"] = None, ["--root"] = None,
                    ["--score-debug"] = None, ["--show-email"] = None,
                    ["--show-name"] = None, ["--show-number"] = None,
                    ["--show-stats"] = None,
                });

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitBranchSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-a"] = None, ["-i"] = None, ["-l"] = None, ["-r"] = None, ["-v"] = None,
                ["-vv"] = None,
                ["--abbrev"] = Number, ["--all"] = None, ["--color"] = None,
                ["--column"] = None, ["--contains"] = String, ["--ignore-case"] = None,
                ["--list"] = None, ["--merged"] = None, ["--no-abbrev"] = None,
                ["--no-color"] = None, ["--no-column"] = None, ["--no-contains"] = String,
                ["--no-merged"] = None, ["--points-at"] = String, ["--remotes"] = None,
                ["--show-current"] = None, ["--sort"] = String, ["--verbose"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitCatFileSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-e"] = None, ["-p"] = None, ["-s"] = None, ["-t"] = None,
                ["--allow-undetermined-type"] = None, ["--batch-check"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitConfigGetSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-z"] = None, ["--bool"] = None, ["--bool-or-int"] = None,
                ["--default"] = String, ["--expiry-date"] = None, ["--global"] = None,
                ["--int"] = None, ["--local"] = None, ["--name-only"] = None,
                ["--null"] = None, ["--path"] = None, ["--show-origin"] = None,
                ["--show-scope"] = None, ["--system"] = None, ["--type"] = String,
                ["--worktree"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitDescribeSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["--abbrev"] = Number, ["--always"] = None, ["--broken"] = None,
                ["--candidates"] = Number, ["--contains"] = None, ["--dirty"] = None,
                ["--exclude"] = String, ["--exact-match"] = None, ["--first-match"] = None,
                ["--long"] = None, ["--match"] = String, ["--tags"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitForEachRefSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["--contains"] = String, ["--count"] = Number, ["--format"] = String,
                ["--merged"] = String, ["--no-contains"] = String, ["--no-merged"] = String,
                ["--points-at"] = String, ["--sort"] = String,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitLsFilesSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-c"] = None, ["-d"] = None, ["-f"] = None, ["-i"] = None, ["-k"] = None,
                ["-m"] = None, ["-o"] = None, ["-s"] = None, ["-t"] = None, ["-u"] = None,
                ["-v"] = None, ["-z"] = None, ["-x"] = String, ["-X"] = String,
                ["--abbrev"] = Number, ["--cached"] = None, ["--debug"] = None,
                ["--deleted"] = None, ["--directory"] = None, ["--eol"] = None,
                ["--error-unmatch"] = None, ["--exclude"] = String,
                ["--exclude-from"] = String, ["--exclude-per-directory"] = String,
                ["--exclude-standard"] = None, ["--full-name"] = None, ["--ignored"] = None,
                ["--killed"] = None, ["--modified"] = None,
                ["--no-empty-directory"] = None, ["--others"] = None,
                ["--recurse-submodules"] = None, ["--stage"] = None, ["--unmerged"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitLsRemoteSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-b"] = None, ["-h"] = None, ["-q"] = None, ["-t"] = None,
                ["--branches"] = None, ["--exit-code"] = None, ["--get-url"] = None,
                ["--heads"] = None, ["--quiet"] = None, ["--refs"] = None,
                ["--sort"] = String, ["--symref"] = None, ["--tags"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitMergeBaseSafeFlags =
            NoneFlags("--all", "--fork-point", "--independent", "--is-ancestor", "--octopus");

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitRemoteSafeFlags =
            NoneFlags("-v", "--verbose");

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitRevListSafeFlags =
            Merge(
                GitRefFlags, GitDateFlags, GitCountFlags,
                new Dictionary<string, BashFlagValueKind>
                {
                    ["--abbrev"] = Number, ["--abbrev-commit"] = None,
                    ["--ancestry-path"] = None, ["--count"] = None, ["--dense"] = None,
                    ["--first-parent"] = None, ["--format"] = String,
                    ["--full-history"] = None, ["--graph"] = None, ["--grep"] = String,
                    ["--max-age"] = Number, ["--max-parents"] = Number, ["--merges"] = None,
                    ["--min-age"] = Number, ["--min-parents"] = Number,
                    ["--no-max-parents"] = None, ["--no-merges"] = None,
                    ["--no-min-parents"] = None, ["--oneline"] = None,
                    ["--pretty"] = String, ["--reverse"] = None, ["--skip"] = Number,
                    ["--source"] = None, ["--sparse"] = None, ["--walk-reflogs"] = None,
                });

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitRevParseSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["--abbrev-ref"] = None, ["--absolute-git-dir"] = None,
                ["--git-common-dir"] = None, ["--git-dir"] = None,
                ["--is-bare-repository"] = None, ["--is-inside-git-dir"] = None,
                ["--is-inside-work-tree"] = None, ["--is-shallow-repository"] = None,
                ["--is-shallow-update"] = None, ["--path-prefix"] = None,
                ["--short"] = String, ["--show-cdup"] = None, ["--show-prefix"] = None,
                ["--show-superproject-working-tree"] = None, ["--show-toplevel"] = None,
                ["--symbolic"] = None, ["--symbolic-full-name"] = None, ["--verify"] = None,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitShortlogSafeFlags =
            Merge(
                GitRefFlags, GitDateFlags,
                new Dictionary<string, BashFlagValueKind>
                {
                    ["-c"] = None, ["-e"] = None, ["-n"] = None, ["-s"] = None,
                    ["--author"] = String, ["--committer"] = None, ["--email"] = None,
                    ["--format"] = String, ["--group"] = String, ["--no-merges"] = None,
                    ["--numbered"] = None, ["--summary"] = None,
                });

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitStashShowSafeFlags =
            Merge(
                GitStatFlags, GitColorFlags, GitPatchFlags,
                new Dictionary<string, BashFlagValueKind>
                {
                    ["--abbrev"] = Number, ["--diff-filter"] = String,
                    ["--word-diff"] = None, ["--word-diff-regex"] = String,
                });

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitTagSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-i"] = None, ["-l"] = None, ["-n"] = Number, ["--column"] = None,
                ["--contains"] = String, ["--format"] = String, ["--ignore-case"] = None,
                ["--list"] = None, ["--merged"] = String, ["--no-column"] = None,
                ["--no-contains"] = String, ["--no-merged"] = String,
                ["--points-at"] = String, ["--sort"] = String,
            };

        internal static readonly IReadOnlyDictionary<string, BashFlagValueKind> GitWorktreeListSafeFlags =
            new Dictionary<string, BashFlagValueKind>
            {
                ["-v"] = None, ["--expire"] = String, ["--porcelain"] = None,
                ["--verbose"] = None,
            };
    }
}
