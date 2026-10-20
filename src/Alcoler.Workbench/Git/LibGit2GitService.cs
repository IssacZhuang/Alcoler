using LibGit2Sharp;

namespace Alcoler.Workbench.Git;

public sealed class LibGit2GitService : IGitService
{
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(150);

    private readonly object _sync = new();
    private readonly Dictionary<string, Repository> _repositories = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public string? FindRepositoryRoot(string workspaceRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return null;
        }
        try
        {
            var discovered = Repository.Discover(workspaceRoot);
            if (discovered is null)
            {
                return null;
            }
            var repository = Open(discovered);
            return repository.Info.IsBare ? null : NormalizeKey(repository.Info.WorkingDirectory);
        }
        catch (LibGit2SharpException)
        {
            return null;
        }
    }

    public string? GetCurrentBranchName(string repositoryRoot)
    {
        var repository = Open(repositoryRoot);
        return ExecuteWithBusyRetry(() => repository.Info.IsHeadDetached ? null : repository.Head.FriendlyName, BusyRetryDelay);
    }

    public GitRepositoryStatus GetStatus(string repositoryRoot)
    {
        var repository = Open(repositoryRoot);
        return ExecuteWithBusyRetry<GitRepositoryStatus>(() =>
        {
            var staged = new List<GitFileChange>();
            var unstaged = new List<GitFileChange>();
            var untracked = new List<GitFileChange>();
            // LibGit2Sharp 0.32 exposes status only through RetrieveStatus; untracked dirs are
            // recursed so nested additions are listed individually.
            var options = new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
            };
            foreach (var entry in repository.RetrieveStatus(options))
            {
                var state = entry.State;
                if (state.HasFlag(FileStatus.NewInWorkdir))
                {
                    untracked.Add(new GitFileChange(entry.FilePath, ChangeState.Added));
                    continue;
                }
                AddIndexedChange(staged, state, entry.FilePath,
                    FileStatus.NewInIndex, FileStatus.ModifiedInIndex, FileStatus.DeletedFromIndex,
                    FileStatus.RenamedInIndex, FileStatus.TypeChangeInIndex);
                AddIndexedChange(unstaged, state, entry.FilePath,
                    FileStatus.ModifiedInWorkdir, FileStatus.DeletedFromWorkdir,
                    FileStatus.RenamedInWorkdir, FileStatus.TypeChangeInWorkdir);
            }
            return new GitRepositoryStatus(staged, unstaged, untracked);
        }, BusyRetryDelay);
    }

    public IReadOnlyList<GitBranch> GetBranches(string repositoryRoot)
    {
        var repository = Open(repositoryRoot);
        return ExecuteWithBusyRetry(() => repository.Branches
            .Where(b => !b.IsRemote)
            .Select(b => new GitBranch(
                b.FriendlyName,
                b.IsCurrentRepositoryHead,
                b.IsTracking ? b.TrackingDetails.AheadBy : null,
                b.IsTracking ? b.TrackingDetails.BehindBy : null))
            .OrderByDescending(b => b.IsCurrent)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList(), BusyRetryDelay);
    }

    public IReadOnlyList<GitCommit> GetLog(string repositoryRoot, int limit)
    {
        var repository = Open(repositoryRoot);
        return ExecuteWithBusyRetry(() => repository.Commits
            .Take(limit)
            .Select(c => new GitCommit(c.Sha, FirstLine(c.Message), c.Author.Name, c.Author.When))
            .ToList(), BusyRetryDelay);
    }

    public void Checkout(string repositoryRoot, string branchName)
    {
        var repository = Open(repositoryRoot);
        ExecuteWithBusyRetry<object?>(() =>
        {
            var branch = repository.Branches.FirstOrDefault(b =>
                    !b.IsRemote && string.Equals(b.FriendlyName, branchName, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"Branch not found: {branchName}");
            Commands.Checkout(repository, branch);
            return null;
        }, BusyRetryDelay);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var repository in _repositories.Values)
            {
                repository.Dispose();
            }
            _repositories.Clear();
        }
    }

    // Callers hand back whatever form of root they hold (work tree or .git path), so cache entries
    // are keyed by the repository's own working directory.
    private Repository Open(string repositoryRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_repositories.TryGetValue(NormalizeKey(repositoryRoot), out var cached))
            {
                return cached;
            }
            var repository = new Repository(repositoryRoot);
            _repositories[NormalizeKey(repository.Info.WorkingDirectory ?? repositoryRoot)] = repository;
            return repository;
        }
    }

    private static void AddIndexedChange(
        List<GitFileChange> bucket,
        FileStatus state,
        string path,
        params FileStatus[] flags)
    {
        foreach (var flag in flags)
        {
            if (!state.HasFlag(flag))
            {
                continue;
            }
            bucket.Add(new GitFileChange(path, flag switch
            {
                FileStatus.NewInIndex => ChangeState.Added,
                FileStatus.DeletedFromIndex or FileStatus.DeletedFromWorkdir => ChangeState.Deleted,
                FileStatus.RenamedInIndex or FileStatus.RenamedInWorkdir => ChangeState.Renamed,
                _ => ChangeState.Modified,
            }));
        }
    }

    private static string FirstLine(string message)
    {
        var lineBreak = message.IndexOf('\n');
        return (lineBreak < 0 ? message : message[..lineBreak]).Trim();
    }

    // A concurrent git process can hold index.lock for a moment; one retry is enough for UI-driven reads.
    internal static T ExecuteWithBusyRetry<T>(Func<T> operation, TimeSpan retryDelay)
    {
        try
        {
            return operation();
        }
        catch (LockedFileException)
        {
            Thread.Sleep(retryDelay);
            return operation();
        }
    }

    private static string NormalizeKey(string? path) =>
        Path.GetFullPath(path ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
