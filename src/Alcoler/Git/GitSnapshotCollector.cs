using System.Diagnostics;
using Alcoler.Kernel.Prompts;

namespace Alcoler.Git;

/// <summary>
/// Collects the conversation-start git snapshot by shelling out to git, mirroring the
/// reference collector: 3s per-command timeout, failures yield unknown/empty facts rather
/// than errors, and provider-visible output is never taken from a failed command.
/// </summary>
public static class GitSnapshotCollector
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    public static GitSnapshot? Collect(string workingDirectory)
    {
        if (ReadTrimmed(workingDirectory, "rev-parse", "--is-inside-work-tree") != "true")
            return null;

        var branch = ReadTrimmed(workingDirectory, "rev-parse", "--abbrev-ref", "HEAD") is { Length: > 0 } b ? b : "HEAD";
        return new GitSnapshot(
            branch,
            ResolveMainBranch(workingDirectory),
            ReadTrimmed(workingDirectory, "config", "user.name"),
            ReadTrimmed(workingDirectory, "--no-optional-locks", "status", "--short"),
            ReadLines(workingDirectory, "--no-optional-locks", "log", "--oneline", "-n", "5"));
    }

    private static string? ResolveMainBranch(string workingDirectory)
    {
        var candidates = new List<string> { "main", "master" };
        if (ReadTrimmed(workingDirectory, "symbolic-ref", "--short", "refs/remotes/origin/HEAD")
            is { Length: > 0 } originHead && originHead.StartsWith("origin/", StringComparison.Ordinal))
        {
            candidates.Insert(0, originHead["origin/".Length..]);
        }
        return candidates.FirstOrDefault(candidate =>
            Run(workingDirectory, "show-ref", "--verify", "--quiet", $"refs/remotes/origin/{candidate}").ExitCode == 0)
            ?? "main";
    }

    private static string? ReadTrimmed(string workingDirectory, params string[] arguments)
    {
        var output = Run(workingDirectory, arguments).StandardOutput.Trim();
        return output.Length > 0 ? output : null;
    }

    private static IReadOnlyList<string> ReadLines(string workingDirectory, params string[] arguments) =>
        Run(workingDirectory, arguments).StandardOutput
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();

    private static (int ExitCode, string StandardOutput) Run(string workingDirectory, params string[] arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                Arguments = string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return (1, "");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)CommandTimeout.TotalMilliseconds) && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                return (1, "");
            }
            return (process.ExitCode, stdout.Result);
        }
        catch
        {
            return (1, "");
        }
    }
}
