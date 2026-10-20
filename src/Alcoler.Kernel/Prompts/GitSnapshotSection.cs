using System.Text.RegularExpressions;

namespace Alcoler.Kernel.Prompts;

/// <summary>
/// Porcelain git facts captured at conversation start; a null <see cref="StatusPorcelain"/> means
/// git state could not be determined and renders as "(unknown)", an empty one as "(clean)".
/// </summary>
public sealed record GitSnapshot(
    string? Branch,
    string? MainBranch,
    string? UserName,
    string? StatusPorcelain,
    IReadOnlyList<string> RecentCommits);

public static partial class GitSnapshotSection
{
    public const string Header = "gitStatus: This is the git status at the start of the conversation. Note that this status is a snapshot in time, and will not update during the conversation.";

    private const int MaxStatusChars = 2000;

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex NewLinePattern();

    public static string Render(GitSnapshot snapshot, string platform) =>
        string.Join("\n", Lines(snapshot, StatusToolName(platform)));

    public static string StatusToolName(string platform) =>
        platform == "win32" ? "PowerShell" : "Bash";

    private static IEnumerable<string> Lines(GitSnapshot snapshot, string statusTool)
    {
        yield return Header;
        if (snapshot.Branch is { Length: > 0 })
        {
            yield return "";
            yield return $"Current branch: {snapshot.Branch}";
        }
        if (snapshot.MainBranch is { Length: > 0 })
        {
            yield return "";
            yield return $"Main branch (you will usually use this for PRs): {snapshot.MainBranch}";
        }
        if (snapshot.UserName is { Length: > 0 })
        {
            yield return "";
            yield return $"Git user: {snapshot.UserName}";
        }
        yield return "";
        yield return $"Status:\n{FormatStatus(snapshot.StatusPorcelain, statusTool)}";
        yield return "";
        yield return $"Recent commits:\n{string.Join("\n", snapshot.RecentCommits)}";
    }

    private static string FormatStatus(string? statusPorcelain, string statusTool)
    {
        if (statusPorcelain is null)
            return "(unknown)";
        // Truncation applies before line splitting so the suffix lands on its own line, matching the reference.
        var bounded = statusPorcelain.Length > MaxStatusChars
            ? statusPorcelain[..MaxStatusChars]
                + "\n... (truncated because it exceeds 2k characters. If you need more information, run \"git status\" using "
                + statusTool + ")"
            : statusPorcelain;
        var lines = NewLinePattern()
            .Split(bounded)
            .Where(line => line.Length > 0)
            .ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : "(clean)";
    }
}
