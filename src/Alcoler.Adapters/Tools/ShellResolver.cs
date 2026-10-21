using Alcoler.Contracts.Tools;

namespace Alcoler.Adapters.Tools;

public readonly record struct ShellSpec(string FileName, string Arguments);

public static class ShellResolver
{
    public static ShellSpec Resolve() => Resolve(Environment.GetEnvironmentVariable);

    internal static ShellSpec Resolve(Func<string, string?> getEnvironmentVariable)
    {
        var shell = ResolveEnvironment(null, null, OperatingSystem.IsWindows(), getEnvironmentVariable,
            File.Exists, Environment.GetFolderPath);
        return new ShellSpec(shell.FileName, shell.Arguments);
    }

    public static IReadOnlyList<ShellEnvironment> AvailableShells() => AvailableShells(
        OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, File.Exists, Environment.GetFolderPath);

    public static ShellEnvironment ResolveEnvironment(ShellEnvironment? preferred = null, ShellEnvironment? snapshot = null) =>
        ResolveEnvironment(preferred, snapshot, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable,
            File.Exists, Environment.GetFolderPath);

    internal static ShellEnvironment ResolveEnvironment(
        ShellEnvironment? preferred,
        ShellEnvironment? snapshot,
        bool isWindows,
        Func<string, string?> getEnvironmentVariable,
        Func<string, bool> fileExists,
        Func<Environment.SpecialFolder, string> getFolderPath)
    {
        var available = AvailableShells(isWindows, getEnvironmentVariable, fileExists, getFolderPath);
        return Validate(snapshot) ?? Validate(preferred)
            ?? (isWindows ? available.FirstOrDefault(shell => shell.Dialect == ShellDialect.GitBash) : null)
            ?? available[0];

        ShellEnvironment? Validate(ShellEnvironment? shell)
        {
            if (shell is null || string.IsNullOrWhiteSpace(shell.FileName))
                return null;
            if (!isWindows)
                return shell.Dialect == ShellDialect.Posix && fileExists(shell.FileName) ? shell : null;
            if (shell.Dialect == ShellDialect.Cmd)
                return string.Equals(Path.GetFileName(shell.FileName), "cmd.exe", StringComparison.OrdinalIgnoreCase)
                    && fileExists(shell.FileName) ? shell : available[0];
            return shell.Dialect == ShellDialect.GitBash
                && IsUsableWindowsBash(shell.FileName, getFolderPath) && fileExists(shell.FileName) ? shell : null;
        }
    }

    internal static IReadOnlyList<ShellEnvironment> AvailableShells(
        bool isWindows,
        Func<string, string?> getEnvironmentVariable,
        Func<string, bool> fileExists,
        Func<Environment.SpecialFolder, string> getFolderPath)
    {
        var path = getEnvironmentVariable("PATH") ?? getEnvironmentVariable("Path") ?? "";
        var directories = path.Split(isWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"')).Where(directory => directory.Length > 0).ToArray();
        if (!isWindows)
        {
            var shells = directories.Select(directory => Path.Combine(directory, "bash"))
                .Append("/bin/sh").Where(fileExists).Distinct(StringComparer.Ordinal)
                .Select(fileName => new ShellEnvironment(fileName, ShellDialect.Posix)).ToArray();
            return shells.Length > 0 ? shells : [new ShellEnvironment("/bin/sh", ShellDialect.Posix)];
        }

        var comSpec = getEnvironmentVariable("ComSpec");
        var system = getFolderPath(Environment.SpecialFolder.System);
        var cmd = !string.IsNullOrWhiteSpace(comSpec) && fileExists(comSpec) ? comSpec
            : !string.IsNullOrEmpty(system) ? Path.Combine(system, "cmd.exe") : "cmd.exe";
        var result = new List<ShellEnvironment> { new(cmd, ShellDialect.Cmd) };
        var roots = new[]
        {
            getFolderPath(Environment.SpecialFolder.ProgramFiles),
            getFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            UnderProfile("AppData", "Local", "Programs"),
        };
        var fixedCandidates = roots.Where(root => !string.IsNullOrEmpty(root))
            .SelectMany(root => BashUnderGitRoot(Path.Combine(root, "Git")));
        var gitPathCandidates = directories.Where(directory => fileExists(Path.Combine(directory, "git.exe")))
            .SelectMany(directory =>
            {
                var root = Path.GetDirectoryName(directory) ?? directory;
                if (Path.GetFileName(root) is "mingw64" or "mingw32" or "usr")
                    root = Path.GetDirectoryName(root) ?? root;
                return BashUnderGitRoot(root);
            });
        var pathCandidates = directories.Select(directory => Path.Combine(directory, "bash.exe"));
        foreach (var candidate in fixedCandidates.Concat(gitPathCandidates).Concat(pathCandidates)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsUsableWindowsBash(candidate, getFolderPath) && fileExists(candidate))
            {
                result.Add(new ShellEnvironment(candidate, ShellDialect.GitBash));
                break;
            }
        }
        return result;

        string UnderProfile(params string[] parts)
        {
            var profile = getFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(profile) ? "" : Path.Combine([profile, .. parts]);
        }
    }

    private static IEnumerable<string> BashUnderGitRoot(string root)
    {
        yield return Path.Combine(root, "bin", "bash.exe");
        yield return Path.Combine(root, "usr", "bin", "bash.exe");
    }

    private static bool IsUsableWindowsBash(string candidate, Func<Environment.SpecialFolder, string> getFolderPath)
    {
        if (!string.Equals(Path.GetFileName(candidate), "bash.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var system = getFolderPath(Environment.SpecialFolder.System);
        var profile = getFolderPath(Environment.SpecialFolder.UserProfile);
        var windowsApps = string.IsNullOrEmpty(profile) ? ""
            : Path.Combine(profile, "AppData", "Local", "Microsoft", "WindowsApps");
        // These bash.exe binaries launch WSL instead of sharing the workspace filesystem.
        return !Under(candidate, system) && !Under(candidate, windowsApps);

        static bool Under(string path, string root) => !string.IsNullOrEmpty(root)
            && path.Replace('\\', '/').StartsWith(root.Replace('\\', '/').TrimEnd('/') + '/',
                StringComparison.OrdinalIgnoreCase);
    }
}
