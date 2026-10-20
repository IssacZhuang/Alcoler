using System.Runtime.InteropServices;

namespace Alcoler.Kernel.Prompts;

/// <summary>
/// Machine-level environment facts shared by main sessions and subagents; platform and
/// osVersion follow the reference's process.platform / "platform release arch" conventions.
/// </summary>
public static class RuntimeEnvironmentFacts
{
    public static string Platform =>
        OperatingSystem.IsWindows() ? "win32"
        : OperatingSystem.IsMacOS() ? "darwin"
        : OperatingSystem.IsLinux() ? "linux"
        : RuntimeInformation.OSDescription.ToLowerInvariant();

    public static string Shell
    {
        get
        {
            var shellPath = Environment.GetEnvironmentVariable("SHELL")
                ?? Environment.GetEnvironmentVariable("ComSpec")
                ?? Environment.GetEnvironmentVariable("COMSPEC");
            return string.IsNullOrWhiteSpace(shellPath) ? "unknown" : Path.GetFileName(shellPath);
        }
    }

    public static string OsVersion => $"{Platform} {Environment.OSVersion.Version} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";

    public static bool IsGitRepository(string workspaceRoot)
    {
        for (var directory = new DirectoryInfo(workspaceRoot); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) == 0
                && (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))))
            {
                return true;
            }
        }
        return false;
    }
}
