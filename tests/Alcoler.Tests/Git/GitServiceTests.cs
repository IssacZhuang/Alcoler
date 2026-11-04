using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Alcoler.Git;
using LibGit2Sharp;
using Xunit;

namespace Alcoler.Tests.Git;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "alcoler-git-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            // LibGit2Sharp marks object files read-only, which blocks recursive delete on Windows.
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class GitServiceTests : IDisposable
{
    private readonly TempDirectory _workspace = new();
    private readonly LibGit2GitService _service = new();

    public void Dispose()
    {
        _service.Dispose();
        _workspace.Dispose();
    }

    private string InitRepo(string name = "repo")
    {
        var path = System.IO.Path.Combine(_workspace.Path, name);
        Repository.Init(path);
        return path;
    }

    private static void Commit(Repository repository, string message, DateTimeOffset? when = null)
    {
        Commands.Stage(repository, "*");
        var author = new Signature("Alcoler Tests", "tests@alcoler.local", when ?? DateTimeOffset.Now);
        repository.Commit(message, author, author);
    }

    private static string FilePath(string repositoryRoot, string name) =>
        System.IO.Path.Combine(repositoryRoot, name);

    [Fact]
    public void FindRepositoryRoot_ReturnsNull_OutsideWorkTree()
    {
        var plain = System.IO.Path.Combine(_workspace.Path, "plain");
        Directory.CreateDirectory(plain);

        Assert.Null(_service.FindRepositoryRoot(plain));
        Assert.Null(_service.FindRepositoryRoot(System.IO.Path.Combine(plain, "missing")));
        Assert.Null(_service.FindRepositoryRoot(string.Empty));
    }

    [Fact]
    public void FindRepositoryRoot_LocatesWorkTree_FromNestedDirectory()
    {
        var repositoryRoot = InitRepo();
        var nested = System.IO.Path.Combine(repositoryRoot, "src", "deep");
        Directory.CreateDirectory(nested);

        var found = _service.FindRepositoryRoot(nested);

        Assert.NotNull(found);
        Assert.True(string.Equals(Normalize(repositoryRoot), Normalize(found), StringComparison.OrdinalIgnoreCase),
            $"expected {repositoryRoot}, got {found}");
    }

    [Fact]
    public void FindRepositoryRoot_And_Status_Agree_OnOneRepositoryInstancePerRoot()
    {
        var repositoryRoot = InitRepo();
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "a");
            Commit(repository, "initial");
        }

        var root = _service.FindRepositoryRoot(repositoryRoot);
        Assert.NotNull(root);

        var cached = Assert.Single(CachedRepositories(_service));
        var nested = FilePath(repositoryRoot, "nested");
        Directory.CreateDirectory(nested);
        string[] paths = [root!, root! + System.IO.Path.DirectorySeparatorChar, Repository.Discover(root!), nested];
        for (var index = 0; index < 20; index++)
        {
            Assert.Equal(root, _service.FindRepositoryRoot(paths[index % paths.Length]));
            Assert.Same(cached, Assert.Single(CachedRepositories(_service)));
        }

        var status = _service.GetStatus(root!);
        Assert.Empty(status.Staged);
        Assert.Empty(status.Unstaged);
        Assert.Empty(status.Untracked);
        Assert.Same(cached, Assert.Single(CachedRepositories(_service)));
    }

    [Fact]
    public void RepositoryCache_EvictsLeastRecentlyUsed_AtCapacity()
    {
        var time = new CapturedTimerProvider();
        using var service = new LibGit2GitService(time);
        var roots = new List<string>();
        var repositories = new List<Repository>();
        for (var index = 0; index < LibGit2GitService.MaxCachedRepositories; index++)
        {
            var root = InitRepo($"repo-{index}");
            roots.Add(root);
            Assert.NotNull(service.FindRepositoryRoot(root));
            repositories.Add(CachedRepositories(service).Last());
            time.Advance(TimeSpan.FromSeconds(1));
        }
        service.GetStatus(roots[0]);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.NotNull(service.FindRepositoryRoot(InitRepo("overflow")));

        var cached = CachedRepositories(service);
        Assert.Equal(LibGit2GitService.MaxCachedRepositories, cached.Length);
        Assert.Contains(repositories[0], cached);
        Assert.DoesNotContain(repositories[1], cached);
        Assert.True(RepositoryHandle(repositories[1]).IsClosed);
        Assert.False(RepositoryHandle(repositories[0]).IsClosed);
    }

    [Fact]
    public void RepositoryCache_ReleasesIdleRepositories_AndReopensOnUse()
    {
        var time = new CapturedTimerProvider();
        using var service = new LibGit2GitService(time);
        var root = InitRepo();
        Assert.NotNull(service.FindRepositoryRoot(root));
        var repository = Assert.Single(CachedRepositories(service));

        time.Advance(LibGit2GitService.RepositoryIdleTimeout);

        Assert.Empty(CachedRepositories(service));
        Assert.True(RepositoryHandle(repository).IsClosed);
        Assert.True(Assert.Single(time.Timers).IsDisposed);
        Assert.NotNull(service.FindRepositoryRoot(root));
        Assert.NotSame(repository, Assert.Single(CachedRepositories(service)));
        Assert.Empty(service.GetStatus(root).Untracked);
    }

    [Fact]
    public void RepositoryCache_RefreshesIdleDeadline_AndIgnoresEarlyCallbacks()
    {
        var time = new CapturedTimerProvider();
        using var service = new LibGit2GitService(time);
        var root = InitRepo();
        Assert.NotNull(service.FindRepositoryRoot(root));
        var repository = Assert.Single(CachedRepositories(service));
        var timer = Assert.Single(time.Timers);
        var halfTimeout = LibGit2GitService.RepositoryIdleTimeout / 2;

        time.Advance(halfTimeout);
        service.GetStatus(root);
        time.Advance(halfTimeout);
        timer.Fire();

        Assert.Same(repository, Assert.Single(CachedRepositories(service)));
        Assert.False(RepositoryHandle(repository).IsClosed);
        Assert.Equal(time.GetTimestamp() + halfTimeout.Ticks, timer.DueAt);

        time.Advance(halfTimeout);

        Assert.Empty(CachedRepositories(service));
        Assert.True(RepositoryHandle(repository).IsClosed);
    }

    [Fact]
    public void Dispose_ClosesAllRepositories_AndIgnoresQueuedEviction()
    {
        var time = new CapturedTimerProvider();
        var service = new LibGit2GitService(time);
        Assert.NotNull(service.FindRepositoryRoot(InitRepo("first")));
        Assert.NotNull(service.FindRepositoryRoot(InitRepo("second")));
        var repositories = CachedRepositories(service);
        var timer = Assert.Single(time.Timers);

        service.Dispose();
        time.Advance(LibGit2GitService.RepositoryIdleTimeout);
        timer.Fire();
        service.Dispose();

        Assert.Empty(CachedRepositories(service));
        Assert.All(repositories, repository => Assert.True(RepositoryHandle(repository).IsClosed));
        Assert.True(timer.IsDisposed);
        Assert.Single(time.Timers);
        Assert.Throws<ObjectDisposedException>(() => service.FindRepositoryRoot(_workspace.Path));
        Assert.Throws<ObjectDisposedException>(() => service.GetStatus(_workspace.Path));
    }

    [Fact]
    public async Task Dispose_WaitsForRunningRepositoryOperation()
    {
        using var service = new LibGit2GitService();
        var root = InitRepo();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposing = new ManualResetEventSlim();
        Repository? repository = null;
        var method = typeof(LibGit2GitService)
            .GetMethod("WithRepository", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int));
        var operation = Task.Run(() => method.Invoke(service, [root, (Func<Repository, int>)(value =>
        {
            repository = value;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The repository operation was not released.");
            return value.RetrieveStatus().Count();
        })]));
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            disposal = Task.Run(() =>
            {
                disposing.Set();
                service.Dispose();
            });
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(disposal.IsCompleted);
            Assert.False(RepositoryHandle(repository!).IsClosed);
        }
        finally
        {
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            if (disposal is not null)
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(RepositoryHandle(repository!).IsClosed);
        Assert.Throws<ObjectDisposedException>(() => service.GetStatus(root));
    }

    [Fact]
    public async Task RepositoryCache_DoesNotEvictRunningOperation()
    {
        var time = new CapturedTimerProvider();
        using var service = new LibGit2GitService(time);
        var root = InitRepo();
        Assert.NotNull(service.FindRepositoryRoot(root));
        var repository = Assert.Single(CachedRepositories(service));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var evicting = new ManualResetEventSlim();
        var method = typeof(LibGit2GitService)
            .GetMethod("WithRepository", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int));
        var operation = Task.Run(() => method.Invoke(service, [root, (Func<Repository, int>)(value =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The repository operation was not released.");
            return value.RetrieveStatus().Count();
        })]));
        Task? eviction = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            time.Advance(LibGit2GitService.RepositoryIdleTimeout, fireTimers: false);
            var timer = Assert.Single(time.Timers);
            eviction = Task.Run(() =>
            {
                evicting.Set();
                timer.Fire();
            });
            Assert.True(evicting.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(eviction.IsCompleted);
            Assert.False(RepositoryHandle(repository).IsClosed);
        }
        finally
        {
            release.Set();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            if (eviction is not null)
                await eviction.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Same(repository, Assert.Single(CachedRepositories(service)));
        Assert.False(RepositoryHandle(repository).IsClosed);

        time.Advance(LibGit2GitService.RepositoryIdleTimeout);

        Assert.Empty(CachedRepositories(service));
        Assert.True(RepositoryHandle(repository).IsClosed);
    }

    [Fact]
    public void GetStatus_GroupsStagedUnstagedAndUntracked()
    {
        var repositoryRoot = InitRepo();
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(FilePath(repositoryRoot, "kept.txt"), "one");
            Commit(repository, "initial");

            File.WriteAllText(FilePath(repositoryRoot, "kept.txt"), "one two");
            File.WriteAllText(FilePath(repositoryRoot, "added.txt"), "new content");
            Commands.Stage(repository, "added.txt");
            File.WriteAllText(FilePath(repositoryRoot, "untracked.txt"), "loose");
        }

        var status = _service.GetStatus(repositoryRoot);

        Assert.Equal([new GitFileChange("kept.txt", ChangeState.Modified)], status.Unstaged);
        Assert.Equal([new GitFileChange("added.txt", ChangeState.Added)], status.Staged);
        Assert.Equal([new GitFileChange("untracked.txt", ChangeState.Added)], status.Untracked);
    }

    [Fact]
    public void GetStatus_ReportsSamePath_InBothStagedAndUnstagedGroups()
    {
        var repositoryRoot = InitRepo();
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "one");
            Commit(repository, "initial");

            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "one");
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "one two");
            Commands.Stage(repository, "a.txt");
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "one two three");
        }

        var status = _service.GetStatus(repositoryRoot);

        Assert.Contains(status.Staged, f => f.Path == "a.txt" && f.State == ChangeState.Modified);
        Assert.Contains(status.Unstaged, f => f.Path == "a.txt" && f.State == ChangeState.Modified);
    }

    [Fact]
    public void GetChangeSummary_EmptyAndCleanRepository_ReturnsZero()
    {
        var repositoryRoot = InitRepo();
        Assert.Equal(new GitChangeSummary(0, 0), _service.GetChangeSummary(repositoryRoot));

        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "one\ntwo\n");
            Commit(repository, "initial");
        }

        Assert.Equal(new GitChangeSummary(0, 0), _service.GetChangeSummary(repositoryRoot));
    }

    [Fact]
    public void GetChangeSummary_ReloadsIndex_AfterExternalStagingAndCommit()
    {
        var repositoryRoot = InitRepo();
        using var repository = new Repository(repositoryRoot);
        File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "original\n");
        Commit(repository, "initial");
        Assert.Equal(new GitChangeSummary(0, 0), _service.GetChangeSummary(repositoryRoot));

        File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "staged-one\nstaged-two\n");
        Commands.Stage(repository, "a.txt");
        Assert.Equal(new GitChangeSummary(2, 1), _service.GetChangeSummary(repositoryRoot));

        File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "original\n");
        Assert.Equal(new GitChangeSummary(3, 3), _service.GetChangeSummary(repositoryRoot));

        Commands.Stage(repository, "a.txt");
        Assert.Equal(new GitChangeSummary(0, 0), _service.GetChangeSummary(repositoryRoot));

        File.AppendAllText(FilePath(repositoryRoot, "a.txt"), "committed\n");
        Commit(repository, "external commit");
        Assert.Equal(new GitChangeSummary(0, 0), _service.GetChangeSummary(repositoryRoot));
    }

    [Fact]
    public void GetChangeSummary_SumsStagedAndUnstagedLines_WithoutNettingSamePath()
    {
        var repositoryRoot = InitRepo();
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(FilePath(repositoryRoot, "shared.txt"), "original\nkept\n");
            File.WriteAllText(FilePath(repositoryRoot, "deleted.txt"), "delete-one\ndelete-two\n");
            Commit(repository, "initial");

            File.WriteAllText(FilePath(repositoryRoot, "shared.txt"), "staged\nkept\n");
            File.WriteAllText(FilePath(repositoryRoot, "added.txt"), "added-one\nadded-two\n");
            Commands.Stage(repository, "*");
            File.WriteAllText(FilePath(repositoryRoot, "shared.txt"), "original\nkept\n");
            File.Delete(FilePath(repositoryRoot, "deleted.txt"));
        }

        var summary = _service.GetChangeSummary(repositoryRoot);
        var status = _service.GetStatus(repositoryRoot);

        Assert.Equal(new GitChangeSummary(4, 4), summary);
        Assert.Contains(status.Staged, f => f.Path == "shared.txt");
        Assert.Contains(status.Unstaged, f => f.Path == "shared.txt");
    }

    [Fact]
    public void GetChangeSummary_UnbornRepository_CountsStagedAndUnstagedLines()
    {
        var repositoryRoot = InitRepo();
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "one\ntwo\n");
            Commands.Stage(repository, "a.txt");
            File.AppendAllText(FilePath(repositoryRoot, "a.txt"), "three\n");
        }

        Assert.Equal(new GitChangeSummary(3, 0), _service.GetChangeSummary(repositoryRoot));
    }

    [Fact]
    public void GetChangeSummary_UntrackedAndBinaryFiles_ContributeNoLines()
    {
        var repositoryRoot = InitRepo();
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllBytes(FilePath(repositoryRoot, "tracked.bin"), [0, 1, 2]);
            Commit(repository, "initial");

            File.WriteAllBytes(FilePath(repositoryRoot, "tracked.bin"), [0, 3, 4]);
            Commands.Stage(repository, "tracked.bin");
            File.WriteAllBytes(FilePath(repositoryRoot, "tracked.bin"), [0, 5, 6]);
            Directory.CreateDirectory(FilePath(repositoryRoot, "nested"));
            File.WriteAllText(FilePath(repositoryRoot, "nested/new.txt"), "untracked\ntext\n");
            File.WriteAllBytes(FilePath(repositoryRoot, "nested/new.bin"), [0, 7, 8]);
        }

        Assert.Equal(new GitChangeSummary(0, 0), _service.GetChangeSummary(repositoryRoot));
        var status = _service.GetStatus(repositoryRoot);
        Assert.Single(status.Staged, f => f.Path == "tracked.bin");
        Assert.Single(status.Unstaged, f => f.Path == "tracked.bin");
        Assert.Equal(2, status.Untracked.Count);
    }

    [Fact]
    public void GetChangeSummary_RepeatedQueries_DoNotModifyRepositoryOrWorkTree()
    {
        var repositoryRoot = InitRepo();
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "original\n");
            Commit(repository, "initial");
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "staged\n");
            Commands.Stage(repository, "a.txt");
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "unstaged\n");
            File.WriteAllText(FilePath(repositoryRoot, "new.txt"), "untracked\n");
        }
        var before = Directory.EnumerateFiles(repositoryRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (
                Content: File.ReadAllBytes(path),
                LastWrite: File.GetLastWriteTimeUtc(path)));

        Assert.Equal(new GitChangeSummary(2, 2), _service.GetChangeSummary(repositoryRoot));
        Assert.Equal(new GitChangeSummary(2, 2), _service.GetChangeSummary(repositoryRoot));
        _service.GetStatus(repositoryRoot);
        _service.GetCurrentBranchName(repositoryRoot);

        Assert.Equal(before.Keys.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(repositoryRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var (path, original) in before)
        {
            Assert.Equal(original.Content, File.ReadAllBytes(path));
            Assert.Equal(original.LastWrite, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void GetBranches_And_Checkout_SwitchCurrentBranch()
    {
        var repositoryRoot = InitRepo();
        string initialBranch;
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "a");
            Commit(repository, "initial");
            initialBranch = repository.Head.FriendlyName;
            repository.CreateBranch("feature");
        }

        var before = _service.GetBranches(repositoryRoot);
        Assert.Contains(before, b => b.Name == initialBranch && b.IsCurrent
            && b.AheadOfUpstream is null && b.BehindOfUpstream is null);
        Assert.Contains(before, b => b.Name == "feature" && !b.IsCurrent);

        _service.Checkout(repositoryRoot, "feature");
        Assert.Equal("feature", _service.GetCurrentBranchName(repositoryRoot));

        var after = _service.GetBranches(repositoryRoot);
        Assert.Contains(after, b => b.Name == "feature" && b.IsCurrent);
        Assert.Contains(after, b => b.Name == initialBranch && !b.IsCurrent);
    }

    [Fact]
    public void Checkout_UnknownBranch_Throws()
    {
        var repositoryRoot = InitRepo();
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "a");
            Commit(repository, "initial");
        }

        Assert.Throws<KeyNotFoundException>(() => _service.Checkout(repositoryRoot, "no-such-branch"));
    }

    [Fact]
    public void GetBranches_ReportsAheadBehind_AgainstUpstream()
    {
        var originRoot = InitRepo("origin");
        using (var origin = new Repository(originRoot))
        {
            File.WriteAllText(FilePath(originRoot, "a.txt"), "a");
            Commit(origin, "initial");
        }
        var cloneRoot = System.IO.Path.Combine(_workspace.Path, "clone");
        Repository.Clone(originRoot, cloneRoot);
        using (var clone = new Repository(cloneRoot))
        {
            File.WriteAllText(FilePath(cloneRoot, "b.txt"), "b");
            Commit(clone, "local work");
        }

        var branches = _service.GetBranches(cloneRoot);

        var tracked = Assert.Single(branches, b => b.AheadOfUpstream == 1 && b.BehindOfUpstream == 0);
        Assert.True(tracked.IsCurrent);
    }

    [Fact]
    public void GetLog_OrdersNewestFirst_TruncatesLimit_AndUsesFirstLine()
    {
        var repositoryRoot = InitRepo();
        CommitLog(repositoryRoot);
        using var repository = new Repository(repositoryRoot);
        var tipSha = repository.Head.Tip.Sha;

        var log = _service.GetLog(repositoryRoot, 2);

        Assert.Equal(2, log.Count);
        Assert.Equal(tipSha, log[0].Sha);
        Assert.Equal("third", log[0].Message);
        Assert.Equal("second", log[1].Message);
        Assert.Equal("Alcoler Tests", log[0].Author);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 2, 0, TimeSpan.Zero), log[0].When);
    }

    [Fact]
    public void ExecuteWithBusyRetry_Succeeds_AfterOneRetry()
    {
        var calls = 0;
        var result = LibGit2GitService.ExecuteWithBusyRetry(() =>
        {
            calls++;
            if (calls == 1)
            {
                throw new LockedFileException("index.lock exists");
            }
            return 7;
        }, TimeSpan.Zero);

        Assert.Equal(7, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ExecuteWithBusyRetry_SurfacesError_AfterSingleRetry()
    {
        var calls = 0;
        Assert.Throws<LockedFileException>(() => LibGit2GitService.ExecuteWithBusyRetry<int>(() =>
        {
            calls++;
            throw new LockedFileException("still busy");
        }, TimeSpan.Zero));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndBlocksFurtherUse()
    {
        var repositoryRoot = InitRepo();
        var service = new LibGit2GitService();
        Assert.NotNull(service.FindRepositoryRoot(repositoryRoot));

        service.Dispose();
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.GetStatus(repositoryRoot));
    }

    private static Repository[] CachedRepositories(LibGit2GitService service)
    {
        var cache = (IDictionary)typeof(LibGit2GitService)
            .GetField("_repositories", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        return cache.Values.Cast<object>()
            .Select(entry => (Repository)entry.GetType().GetProperty("Repository")!.GetValue(entry)!)
            .ToArray();
    }

    private static SafeHandle RepositoryHandle(Repository repository) =>
        (SafeHandle)typeof(Repository)
            .GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(repository)!;

    private sealed class CapturedTimerProvider : TimeProvider
    {
        private long _timestamp;
        public List<CapturedTimer> Timers { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new CapturedTimer(this, callback, state);
            timer.Change(dueTime, period);
            Timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed, bool fireTimers = true)
        {
            _timestamp += elapsed.Ticks;
            if (!fireTimers)
                return;
            foreach (var timer in Timers.ToArray())
                timer.FireIfDue();
        }
    }

    private sealed class CapturedTimer(CapturedTimerProvider time, TimerCallback callback, object? state) : ITimer
    {
        public bool IsDisposed { get; private set; }
        public long? DueAt { get; private set; }
        public void Fire() => callback(state);

        public void FireIfDue()
        {
            if (!IsDisposed && DueAt is { } dueAt && dueAt <= time.GetTimestamp())
            {
                DueAt = null;
                Fire();
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed)
                return false;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : time.GetTimestamp() + dueTime.Ticks;
            return true;
        }

        public void Dispose()
        {
            IsDisposed = true;
            DueAt = null;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private void CommitLog(string repositoryRoot)
    {
        using var repository = new Repository(repositoryRoot);
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "1");
        Commit(repository, "first", baseTime);
        File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "2");
        Commit(repository, "second\n\nlong body", baseTime.AddMinutes(1));
        File.WriteAllText(FilePath(repositoryRoot, "a.txt"), "3");
        Commit(repository, "third", baseTime.AddMinutes(2));
    }

    private static string Normalize(string path) =>
        System.IO.Path.GetFullPath(path)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
}
