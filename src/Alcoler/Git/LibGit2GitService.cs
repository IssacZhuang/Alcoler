using LibGit2Sharp;

namespace Alcoler.Git;

public sealed class LibGit2GitService : IGitService
{
    internal const int MaxCachedRepositories = 4;
    internal static readonly TimeSpan RepositoryIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromMilliseconds(150);

    private readonly object _sync = new();
    private readonly Dictionary<string, CachedRepository> _repositories = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private ITimer? _evictionTimer;
    private bool _disposed;

    public LibGit2GitService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string? FindRepositoryRoot(string workspaceRoot)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            {
                return null;
            }
            try
            {
                return WithRepository(workspaceRoot, repository =>
                    repository.Info.IsBare ? null : NormalizeKey(repository.Info.WorkingDirectory));
            }
            catch (LibGit2SharpException)
            {
                return null;
            }
        }
    }

    public string? GetCurrentBranchName(string repositoryRoot) =>
        WithRepository(repositoryRoot, repository =>
            repository.Info.IsHeadDetached ? null : repository.Head.FriendlyName);

    public GitRepositoryStatus GetStatus(string repositoryRoot) =>
        WithRepository(repositoryRoot, repository =>
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
        });

    public GitChangeSummary GetChangeSummary(string repositoryRoot) =>
        WithRepository(repositoryRoot, repository =>
        {
            // Diff uses the cached index; RetrieveStatus reloads it from disk without writing it.
            repository.RetrieveStatus(new StatusOptions
            {
                Show = StatusShowOption.IndexOnly,
                DetectRenamesInIndex = false,
                IncludeIgnored = false,
                IncludeUntracked = false,
            });
            using var staged = repository.Diff.Compare<Patch>(repository.Head.Tip?.Tree, DiffTargets.Index);
            using var unstaged = repository.Diff.Compare<Patch>();
            return new GitChangeSummary(
                staged.LinesAdded + unstaged.LinesAdded,
                staged.LinesDeleted + unstaged.LinesDeleted);
        });

    public IReadOnlyList<GitBranch> GetBranches(string repositoryRoot) =>
        WithRepository<IReadOnlyList<GitBranch>>(repositoryRoot, repository => repository.Branches
            .Where(b => !b.IsRemote)
            .Select(b => new GitBranch(
                b.FriendlyName,
                b.IsCurrentRepositoryHead,
                b.IsTracking ? b.TrackingDetails.AheadBy : null,
                b.IsTracking ? b.TrackingDetails.BehindBy : null))
            .OrderByDescending(b => b.IsCurrent)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    public IReadOnlyList<GitCommit> GetLog(string repositoryRoot, int limit) =>
        WithRepository<IReadOnlyList<GitCommit>>(repositoryRoot, repository => repository.Commits
            .Take(limit)
            .Select(c => new GitCommit(c.Sha, FirstLine(c.Message), c.Author.Name, c.Author.When))
            .ToList());

    public void Checkout(string repositoryRoot, string branchName) =>
        WithRepository<object?>(repositoryRoot, repository =>
        {
            var branch = repository.Branches.FirstOrDefault(b =>
                    !b.IsRemote && string.Equals(b.FriendlyName, branchName, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"Branch not found: {branchName}");
            Commands.Checkout(repository, branch);
            return null;
        });

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _evictionTimer?.Dispose();
            _evictionTimer = null;
            foreach (var entry in _repositories.Values)
            {
                entry.Repository.Dispose();
            }
            _repositories.Clear();
        }
    }

    private T WithRepository<T>(string repositoryRoot, Func<Repository, T> operation)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = Open(repositoryRoot);
            try
            {
                return ExecuteWithBusyRetry(() => operation(entry.Repository), BusyRetryDelay);
            }
            finally
            {
                entry.LastUsed = _timeProvider.GetTimestamp();
                ScheduleEviction();
            }
        }
    }

    private CachedRepository Open(string repositoryRoot)
    {
        var key = NormalizeKey(Repository.Discover(repositoryRoot) ?? repositoryRoot);
        if (_repositories.TryGetValue(key, out var cached))
        {
            return cached;
        }
        if (_repositories.Count >= MaxCachedRepositories)
        {
            var oldest = _repositories.MinBy(pair => pair.Value.LastUsed);
            oldest.Value.Repository.Dispose();
            _repositories.Remove(oldest.Key);
        }
        var entry = new CachedRepository(new Repository(key), _timeProvider.GetTimestamp());
        _repositories.Add(key, entry);
        return entry;
    }

    private void EvictIdleRepositories()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            var now = _timeProvider.GetTimestamp();
            foreach (var (key, entry) in _repositories.ToArray())
            {
                if (_timeProvider.GetElapsedTime(entry.LastUsed, now) >= RepositoryIdleTimeout)
                {
                    entry.Repository.Dispose();
                    _repositories.Remove(key);
                }
            }
            ScheduleEviction();
        }
    }

    private void ScheduleEviction()
    {
        if (_repositories.Count == 0)
        {
            _evictionTimer?.Dispose();
            _evictionTimer = null;
            return;
        }
        var oldest = _repositories.MinBy(pair => pair.Value.LastUsed).Value;
        var dueTime = RepositoryIdleTimeout - _timeProvider.GetElapsedTime(oldest.LastUsed);
        if (dueTime < TimeSpan.Zero)
        {
            dueTime = TimeSpan.Zero;
        }
        _evictionTimer ??= _timeProvider.CreateTimer(
            static state => ((LibGit2GitService)state!).EvictIdleRepositories(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _evictionTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
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
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path ?? string.Empty));

    private sealed class CachedRepository(Repository repository, long lastUsed)
    {
        public Repository Repository { get; } = repository;
        public long LastUsed { get; set; } = lastUsed;
    }
}
