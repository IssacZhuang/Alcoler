namespace Alcoler.Workbench.Git;

public enum ChangeState
{
    Added,
    Modified,
    Deleted,
    Renamed,
}

public sealed record GitFileChange(string Path, ChangeState State);

public sealed record GitChangeSummary(int AddedLines, int RemovedLines);

public sealed record GitRepositoryStatus(
    IReadOnlyList<GitFileChange> Staged,
    IReadOnlyList<GitFileChange> Unstaged,
    IReadOnlyList<GitFileChange> Untracked);

/// <summary>Ahead/behind are null when the branch has no upstream.</summary>
public sealed record GitBranch(string Name, bool IsCurrent, int? AheadOfUpstream, int? BehindOfUpstream);

/// <summary>Message is the first line of the commit message only.</summary>
public sealed record GitCommit(string Sha, string Message, string Author, DateTimeOffset When);

/// <summary>
/// Read-oriented facade over the repository containing a workspace. Implementations must be
/// safe to call from any thread and reuse one underlying repository handle per root.
/// </summary>
public interface IGitService : IDisposable
{
    /// <summary>Returns the working-tree root, or null when the path is not inside a work tree.</summary>
    string? FindRepositoryRoot(string workspaceRoot);

    /// <summary>Returns null when HEAD is detached or the branch is unborn.</summary>
    string? GetCurrentBranchName(string repositoryRoot);

    GitRepositoryStatus GetStatus(string repositoryRoot);

    /// <summary>Staged plus unstaged text lines; untracked and binary files contribute no lines.</summary>
    GitChangeSummary GetChangeSummary(string repositoryRoot) => new(0, 0);

    IReadOnlyList<GitBranch> GetBranches(string repositoryRoot);

    IReadOnlyList<GitCommit> GetLog(string repositoryRoot, int limit);

    /// <summary>Throws <see cref="KeyNotFoundException"/> when the branch does not exist.</summary>
    void Checkout(string repositoryRoot, string branchName);
}
