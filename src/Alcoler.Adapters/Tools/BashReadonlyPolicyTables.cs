namespace Alcoler.Adapters.Tools;

internal static class BashReadonlyPolicyTables
{
    internal static readonly string[] GitBranchDangerousValueFlags =
        ["--contains", "--no-contains", "--points-at", "--sort"];

    internal static readonly string[] GitTagDangerousValueFlags =
        ["--contains", "--no-contains", "--merged", "--no-merged", "--points-at", "--sort", "--format", "-n"];

    internal static readonly string[] GitReflogAllowedSubcommands = ["show", "list"];
    internal static readonly string[] GitReflogDangerousSubcommands = ["expire", "delete", "exists", "drop", "write"];

    internal static readonly HashSet<string> SafeEnvAssignments = new(StringComparer.Ordinal)
    {
        "ANTHROPIC_API_KEY", "BLOCK_SIZE", "BLOCKSIZE", "CGO_ENABLED", "CHARSET", "CI",
        "CLICOLOR", "CLICOLOR_FORCE", "COLORTERM", "COLUMNS", "DEBIAN_FRONTEND",
        "FORCE_COLOR", "GCC_COLORS", "GIT_TERMINAL_PROMPT", "GO111MODULE", "GOARCH",
        "GOEXPERIMENT", "GOOS", "GREP_COLOR", "GREP_COLORS", "LANG", "LANGUAGE", "LC_ALL",
        "LC_CTYPE", "LC_TIME", "LINES", "LSCOLORS", "LS_COLORS", "NO_COLOR", "NODE_ENV",
        "PYTEST_DEBUG", "PYTEST_DISABLE_PLUGIN_AUTOLOAD", "PYTHONDONTWRITEBYTECODE",
        "PYTHONUNBUFFERED", "RUST_BACKTRACE", "RUST_LOG", "TERM", "TIME_STYLE", "TZ",
    };

    internal static readonly string[] SafeInputRedirects = ["<", "<&", "<<<"];

    internal static readonly HashSet<string> GitGlobalDangerousFlags = new(StringComparer.Ordinal)
    {
        "-c", "-C", "--attr-source", "--bare", "--config-env", "--exec-path", "--git-dir",
        "--namespace", "--shallow-file", "--super-prefix", "--work-tree",
    };

    internal static readonly HashSet<string> GitGlobalNoValueFlags = new(StringComparer.Ordinal)
    {
        "--no-pager", "--paginate",
    };

    internal static readonly HashSet<string> FindWriteOptions = new(StringComparer.Ordinal)
    {
        "-delete", "-exec", "-execdir", "-files0-from", "-fls", "-fprint", "-fprint0",
        "-fprintf", "-ok", "-okdir",
    };

    internal static readonly HashSet<string> FindValueOptions = new(StringComparer.Ordinal)
    {
        "-Bmin", "-Bnewer", "-Btime", "-D", "-amin", "-anewer", "-atime", "-cmin",
        "-cnewer", "-context", "-ctime", "-f", "-flags", "-fstype", "-gid", "-group",
        "-ilname", "-iname", "-inum", "-ipath", "-iregex", "-iwholename", "-lname",
        "-links", "-maxdepth", "-mindepth", "-mmin", "-mnewer", "-mtime", "-name",
        "-newer", "-path", "-perm", "-printf", "-regex", "-regextype", "-samefile",
        "-size", "-type", "-used", "-user", "-wholename", "-xattrname", "-xtype", "-uid",
    };

    internal static readonly HashSet<string> AllowAnyArgCommands = new(StringComparer.Ordinal)
    {
        "basename", "cal", "cat", "cmp", "column", "comm", "cut", "df", "diff", "dirname",
        "du", "expand", "expr", "false", "fmt", "fold", "free", "groups", "head",
        "hexdump", "id", "locale", "nl", "nproc", "numfmt", "od", "paste", "pr",
        "readlink", "realpath", "rev", "seq", "sleep", "stat", "strings", "tac", "tail",
        "tr", "true", "tsort", "type", "uname", "unexpand", "uptime", "wc", "which",
    };

    internal static readonly IReadOnlyDictionary<string, BashReadonlyCommandPolicy> CommandPolicies =
        new Dictionary<string, BashReadonlyCommandPolicy>(StringComparer.Ordinal)
        {
            ["alias"] = new(CommandOnly: true),
            ["cat"] = new(SafeFlags: BashFlags.CatSafeFlags),
            ["cd"] = new(AllowAnyArgs: true),
            ["cut"] = new(AllowAnyArgs: true),
            ["diff"] = new(AllowAnyArgs: true),
            ["dirname"] = new(AllowAnyArgs: true),
            ["du"] = new(AllowAnyArgs: true),
            ["egrep"] = new(SafeFlags: BashFlags.GrepSafeFlags),
            ["false"] = new(CommandOnly: true),
            ["fd"] = new(SafeFlags: BashFlags.FdSafeFlags),
            ["fdfind"] = new(SafeFlags: BashFlags.FdSafeFlags),
            ["fgrep"] = new(SafeFlags: BashFlags.GrepSafeFlags),
            ["file"] = new(SafeFlags: BashFlags.FileSafeFlags),
            ["grep"] = new(SafeFlags: BashFlags.GrepSafeFlags),
            ["head"] = new(SafeFlags: BashFlags.HeadTailSafeFlags),
            ["ls"] = new(AllowAnyArgs: true),
            ["pwd"] = new(CommandOnly: true),
            ["readlink"] = new(AllowAnyArgs: true),
            ["realpath"] = new(AllowAnyArgs: true),
            ["rev"] = new(AllowAnyArgs: true),
            ["rg"] = new(SafeFlags: BashFlags.RgSafeFlags),
            ["sed"] = new(
                SafeFlags: BashFlags.SedSafeFlags,
                DangerousArgs: BashSemantics.SedCommandIsDangerous),
            ["sort"] = new(SafeFlags: BashFlags.SortSafeFlags),
            ["tail"] = new(SafeFlags: BashFlags.HeadTailSafeFlags),
            ["tree"] = new(
                SafeFlags: BashFlags.TreeSafeFlags,
                DangerousArgs: BashSemantics.TreeArgsHaveOutputOption),
            ["true"] = new(CommandOnly: true),
            ["whoami"] = new(CommandOnly: true),
        };

    internal static readonly (string[] Prefix, BashReadonlyCommandPolicy Policy)[] GitSubcommandPolicies =
    [
        (["git", "blame"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitBlameSafeFlags)),
        (["git", "branch"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitBranchSafeFlags,
            DangerousArgs: args => BashSemantics.GitListLikeCommandIsDangerous(args, GitBranchDangerousValueFlags))),
        (["git", "cat-file"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitCatFileSafeFlags)),
        (["git", "config", "--get"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitConfigGetSafeFlags)),
        (["git", "describe"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitDescribeSafeFlags)),
        (["git", "diff"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitDiffSafeFlags)),
        (["git", "for-each-ref"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitForEachRefSafeFlags,
            DangerousArgs: BashSemantics.GitRevisionFormatCommandIsDangerous)),
        (["git", "grep"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitGrepSafeFlags)),
        (["git", "log"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitLogSafeFlags,
            AllowCompactNumericCountFlag: true,
            DangerousArgs: BashSemantics.GitRevisionFormatCommandIsDangerous)),
        (["git", "ls-files"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitLsFilesSafeFlags)),
        (["git", "ls-remote"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitLsRemoteSafeFlags,
            DangerousArgs: BashSemantics.GitLsRemoteCommandIsDangerous)),
        (["git", "merge-base"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitMergeBaseSafeFlags)),
        (["git", "reflog"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitLogStyleSafeFlags,
            DangerousArgs: BashSemantics.GitReflogCommandIsDangerous)),
        (["git", "remote"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitRemoteSafeFlags,
            DangerousArgs: args => args.Any(arg => arg != "-v" && arg != "--verbose"))),
        (["git", "rev-list"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitRevListSafeFlags,
            DangerousArgs: BashSemantics.GitRevisionFormatCommandIsDangerous)),
        (["git", "rev-parse"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitRevParseSafeFlags)),
        (["git", "shortlog"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitShortlogSafeFlags,
            DangerousArgs: BashSemantics.GitRevisionFormatCommandIsDangerous)),
        (["git", "show"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitShowSafeFlags,
            DangerousArgs: BashSemantics.GitRevisionFormatCommandIsDangerous)),
        (["git", "stash", "list"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitLogStyleSafeFlags)),
        (["git", "stash", "show"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitStashShowSafeFlags)),
        (["git", "status"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitStatusSafeFlags)),
        (["git", "tag"], new BashReadonlyCommandPolicy(
            SafeFlags: BashFlags.GitTagSafeFlags,
            DangerousArgs: args => BashSemantics.GitListLikeCommandIsDangerous(args, GitTagDangerousValueFlags))),
        (["git", "worktree", "list"], new BashReadonlyCommandPolicy(SafeFlags: BashFlags.GitWorktreeListSafeFlags)),
    ];
}

internal static class BashFlags
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
