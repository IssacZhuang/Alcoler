using Alcoler.Workbench.Git;
using LibGit2Sharp;
using Xunit;

namespace Alcoler.Workbench.Tests.Git;

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

        // The work-tree root returned by detection must be a valid key for the other calls.
        var status = _service.GetStatus(root!);
        Assert.Empty(status.Staged);
        Assert.Empty(status.Unstaged);
        Assert.Empty(status.Untracked);
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
