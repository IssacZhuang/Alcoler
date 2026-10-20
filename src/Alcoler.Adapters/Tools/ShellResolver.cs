namespace Alcoler.Adapters.Tools;

public readonly record struct ShellSpec(string FileName, string Arguments);

public static class ShellResolver
{
    public static ShellSpec Resolve() => Resolve(Environment.GetEnvironmentVariable);

    internal static ShellSpec Resolve(Func<string, string?> getEnvironmentVariable)
    {
        if (FindBash(getEnvironmentVariable) is { } bash)
            return new ShellSpec(bash, "-c");
        if (OperatingSystem.IsWindows())
            return new ShellSpec("cmd.exe", "/c");
        return new ShellSpec("/bin/sh", "-c");
    }

    private static string? FindBash(Func<string, string?> getEnvironmentVariable)
    {
        var path = getEnvironmentVariable("PATH") ?? getEnvironmentVariable("Path");
        if (!OperatingSystem.IsWindows())
        {
            if (string.IsNullOrEmpty(path))
                return null;
            return path.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Combine(directory, "bash"))
                .FirstOrDefault(File.Exists);
        }

        // System32\bash.exe and the WindowsApps alias launch WSL, whose filesystem view differs from the
        // workspace; only a git-bash style bash over the Windows filesystem is a usable shell here.
        var wslLauncherRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "Local", "Microsoft", "WindowsApps"),
        };
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (wslLauncherRoots.Any(root => !string.IsNullOrEmpty(root)
                        && directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var candidate = Path.Combine(directory, "bash.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        var gitRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "Local", "Programs"),
        };
        return gitRoots
            .Where(static root => !string.IsNullOrEmpty(root))
            .SelectMany(static root => new[]
            {
                Path.Combine(root, "Git", "bin", "bash.exe"),
                Path.Combine(root, "Git", "usr", "bin", "bash.exe"),
            })
            .FirstOrDefault(File.Exists);
    }
}
