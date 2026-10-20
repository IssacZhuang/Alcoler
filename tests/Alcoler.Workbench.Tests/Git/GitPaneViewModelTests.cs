using Alcoler.Workbench.Git;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibGit2Sharp;
using Xunit;

namespace Alcoler.Workbench.Tests.Git;

public sealed class GitPaneViewModelTests
{
    private sealed class FakeGitService : IGitService
    {
        public string? Root { get; set; }

        public string? BranchName { get; set; } = "main";

        public IReadOnlyList<GitBranch> Branches { get; set; } = [];

        public GitRepositoryStatus Status { get; set; } = new([], [], []);

        public GitChangeSummary ChangeSummary { get; set; } = new(0, 0);

        public IReadOnlyList<GitCommit> Log { get; set; } = [];

        public Exception? CheckoutError { get; set; }

        public Exception? StatusError { get; set; }

        public Action<string>? CheckoutAction { get; set; }

        public List<string> CheckedOut { get; } = [];

        public int LastLogLimit { get; private set; }

        public string? FindRepositoryRoot(string workspaceRoot) => Root;

        public string? GetCurrentBranchName(string repositoryRoot) => BranchName;

        public GitRepositoryStatus GetStatus(string repositoryRoot)
        {
            if (StatusError is { } error)
            {
                throw error;
            }
            return Status;
        }

        public GitChangeSummary GetChangeSummary(string repositoryRoot) => ChangeSummary;

        public IReadOnlyList<GitBranch> GetBranches(string repositoryRoot) => Branches;

        public IReadOnlyList<GitCommit> GetLog(string repositoryRoot, int limit)
        {
            LastLogLimit = limit;
            return [.. Log.Take(limit)];
        }

        public void Checkout(string repositoryRoot, string branchName)
        {
            CheckedOut.Add(branchName);
            CheckoutAction?.Invoke(branchName);
            if (CheckoutError is { } error)
            {
                throw error;
            }
        }

        public void Dispose()
        {
        }
    }

    private static GitPaneViewModel Create(FakeGitService git, string workspaceRoot = @"C:\ws\demo") =>
        new(workspaceRoot, git, InlineUiScheduler.Instance);

    private static IReadOnlyList<GitCommit> ManyCommits(int count) =>
        [.. Enumerable.Range(0, count).Select(i =>
            new GitCommit(new string((char)('a' + i % 26), 40), $"commit {i}", "author", DateTimeOffset.UtcNow))];

    [Fact]
    public async Task LoadAsync_NonRepo_SetsEmptyState()
    {
        var git = new FakeGitService { Root = null };
        var viewModel = Create(git);

        await viewModel.LoadAsync();

        Assert.False(viewModel.IsRepo);
        Assert.Equal(string.Empty, viewModel.CurrentBranch);
        Assert.Equal(0, viewModel.AddedLines);
        Assert.Equal(0, viewModel.RemovedLines);
        Assert.Equal(0, viewModel.ChangedFileCount);
        Assert.False(viewModel.HasChanges);
        Assert.Empty(viewModel.Branches);
        Assert.Empty(viewModel.Staged);
        Assert.Empty(viewModel.Unstaged);
        Assert.Empty(viewModel.Untracked);
        Assert.Empty(viewModel.RecentCommits);
        Assert.Null(viewModel.Banner);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task LoadAsync_PopulatesBranchesStatusAndLog()
    {
        var git = new FakeGitService
        {
            Root = @"C:\ws\demo\.git",
            BranchName = "main",
            Branches =
            [
                new GitBranch("main", true, 2, 1),
                new GitBranch("dev", false, null, null),
            ],
            Status = new GitRepositoryStatus(
                [new GitFileChange("staged.txt", ChangeState.Added)],
                [new GitFileChange("modified.txt", ChangeState.Modified)],
                [new GitFileChange("new.txt", ChangeState.Added)]),
            ChangeSummary = new GitChangeSummary(7, 3),
            Log = ManyCommits(30),
        };
        var viewModel = Create(git);

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsRepo);
        Assert.Equal("main", viewModel.CurrentBranch);
        Assert.Null(viewModel.Banner);

        Assert.Equal(2, viewModel.Branches.Count);
        Assert.True(viewModel.Branches[0].IsCurrent);
        Assert.Equal("↑2 ↓1", viewModel.Branches[0].TrackingText);
        Assert.Equal(string.Empty, viewModel.Branches[1].TrackingText);

        Assert.Single(viewModel.Staged, f => f.Path == "staged.txt" && f.StateText == "added");
        Assert.Single(viewModel.Unstaged, f => f.Path == "modified.txt" && f.StateText == "modified");
        Assert.Single(viewModel.Untracked, f => f.Path == "new.txt" && f.StateText == "added");
        Assert.Equal(7, viewModel.AddedLines);
        Assert.Equal(3, viewModel.RemovedLines);
        Assert.Equal(3, viewModel.ChangedFileCount);
        Assert.True(viewModel.HasChanges);

        Assert.Equal(GitPaneViewModel.RecentCommitLimit, git.LastLogLimit);
        Assert.Equal(GitPaneViewModel.RecentCommitLimit, viewModel.RecentCommits.Count);
        Assert.Equal("commit 0", viewModel.RecentCommits[0].Message);
    }

    [Fact]
    public async Task LoadAsync_RealRepository_CountsDistinctPathsAcrossAllBuckets()
    {
        using var workspace = new TempDirectory();
        var repositoryRoot = System.IO.Path.Combine(workspace.Path, "repo");
        Repository.Init(repositoryRoot);
        string branch;
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(System.IO.Path.Combine(repositoryRoot, "shared.txt"), "original\n");
            File.WriteAllBytes(System.IO.Path.Combine(repositoryRoot, "tracked.bin"), [0, 1, 2]);
            Commands.Stage(repository, "*");
            var author = new Signature("Alcoler Tests", "tests@alcoler.local", DateTimeOffset.Now);
            repository.Commit("initial", author, author);
            branch = repository.Head.FriendlyName;
            File.WriteAllText(System.IO.Path.Combine(repositoryRoot, "shared.txt"), "staged\n");
            Commands.Stage(repository, "shared.txt");
            File.WriteAllText(System.IO.Path.Combine(repositoryRoot, "shared.txt"), "original\n");
            File.WriteAllBytes(System.IO.Path.Combine(repositoryRoot, "tracked.bin"), [0, 3, 4]);
            File.WriteAllText(System.IO.Path.Combine(repositoryRoot, "new.txt"), "untracked\ntext\n");
        }
        using var git = new LibGit2GitService();
        var viewModel = new GitPaneViewModel(repositoryRoot, git, InlineUiScheduler.Instance);

        await viewModel.LoadAsync();

        Assert.Equal(branch, viewModel.CurrentBranch);
        Assert.Equal(2, viewModel.AddedLines);
        Assert.Equal(2, viewModel.RemovedLines);
        Assert.Equal(3, viewModel.ChangedFileCount);
        Assert.True(viewModel.HasChanges);
        Assert.Contains(viewModel.Staged, f => f.Path == "shared.txt");
        Assert.Contains(viewModel.Unstaged, f => f.Path == "shared.txt");
        Assert.Single(viewModel.Untracked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAsync_ZeroLineChanges_KeepFileCountFallback(bool untracked)
    {
        var change = new GitFileChange(
            untracked ? "new.txt" : "tracked.bin", untracked ? ChangeState.Added : ChangeState.Modified);
        var git = new FakeGitService
        {
            Root = @"C:\ws\demo",
            Status = untracked ? new([], [], [change]) : new([], [change], []),
        };
        var viewModel = Create(git);

        await viewModel.LoadAsync();

        Assert.Equal(0, viewModel.AddedLines);
        Assert.Equal(0, viewModel.RemovedLines);
        Assert.Equal(1, viewModel.ChangedFileCount);
        Assert.True(viewModel.HasChanges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAsync_RefreshesAndClearsSummary_WithNotifications(bool remainsRepository)
    {
        var git = new FakeGitService
        {
            Root = @"C:\ws\demo",
            Status = new([new GitFileChange("a.txt", ChangeState.Modified)], [], []),
            ChangeSummary = new(5, 2),
        };
        var viewModel = Create(git);
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        await viewModel.LoadAsync();

        Assert.Contains(nameof(GitPaneViewModel.AddedLines), notifications);
        Assert.Contains(nameof(GitPaneViewModel.RemovedLines), notifications);
        Assert.Contains(nameof(GitPaneViewModel.ChangedFileCount), notifications);
        Assert.Contains(nameof(GitPaneViewModel.HasChanges), notifications);

        git.ChangeSummary = new(8, 4);
        await viewModel.LoadAsync();
        Assert.Equal(8, viewModel.AddedLines);
        Assert.Equal(4, viewModel.RemovedLines);

        notifications.Clear();
        git.Root = remainsRepository ? git.Root : null;
        git.Status = new([], [], []);
        git.ChangeSummary = new(0, 0);
        await viewModel.LoadAsync();

        Assert.Equal(remainsRepository, viewModel.IsRepo);
        Assert.Equal(0, viewModel.AddedLines);
        Assert.Equal(0, viewModel.RemovedLines);
        Assert.Equal(0, viewModel.ChangedFileCount);
        Assert.False(viewModel.HasChanges);
        Assert.Contains(nameof(GitPaneViewModel.AddedLines), notifications);
        Assert.Contains(nameof(GitPaneViewModel.RemovedLines), notifications);
        Assert.Contains(nameof(GitPaneViewModel.ChangedFileCount), notifications);
        Assert.Contains(nameof(GitPaneViewModel.HasChanges), notifications);
    }

    [Theory]
    [InlineData("src/Pane.cs", ChangeState.Modified, "Pane.cs", "src", "M")]
    [InlineData("src\\Views\\Pane.axaml", ChangeState.Added, "Pane.axaml", "src/Views", "A")]
    [InlineData("deleted.txt", ChangeState.Deleted, "deleted.txt", "", "D")]
    [InlineData("renamed.txt", ChangeState.Renamed, "renamed.txt", "", "R")]
    public void FileChange_ExposesCompactPathAndStatus(
        string path, ChangeState state, string fileName, string directory, string statusCode)
    {
        var change = new GitFileChangeViewModel(path, state);

        Assert.Equal(fileName, change.FileName);
        Assert.Equal(directory, change.DirectoryName);
        Assert.Equal(statusCode, change.StatusCode);
        Assert.Equal(state == ChangeState.Added, change.IsAdded);
        Assert.Equal(state == ChangeState.Modified, change.IsModified);
        Assert.Equal(state == ChangeState.Deleted, change.IsDeleted);
        Assert.Equal(state == ChangeState.Renamed, change.IsRenamed);
    }

    [Fact]
    public async Task LoadAsync_Failure_SurfacesBanner()
    {
        var git = new FakeGitService
        {
            Root = @"C:\ws\demo\.git",
            StatusError = new InvalidOperationException("boom"),
        };
        var viewModel = Create(git);

        await viewModel.LoadAsync();

        Assert.NotNull(viewModel.Banner);
        Assert.Contains("boom", viewModel.Banner);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task CheckoutAsync_SwitchesBranch_AndReloads()
    {
        var git = new FakeGitService { Root = @"C:\ws\demo\.git", BranchName = "main" };
        git.CheckoutAction = name =>
        {
            git.BranchName = name;
            git.Branches = [new GitBranch(name, true, null, null)];
        };
        var viewModel = Create(git);
        await viewModel.LoadAsync();

        await viewModel.CheckoutAsync("feature");

        Assert.Equal(new[] { "feature" }, git.CheckedOut);
        Assert.Equal("feature", viewModel.CurrentBranch);
        Assert.Null(viewModel.Banner);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task CheckoutAsync_BusyError_SurfacesBanner()
    {
        var git = new FakeGitService
        {
            Root = @"C:\ws\demo\.git",
            BranchName = "main",
            CheckoutError = new InvalidOperationException("repository busy"),
        };
        var viewModel = Create(git);
        await viewModel.LoadAsync();

        await viewModel.CheckoutAsync("feature");

        Assert.NotNull(viewModel.Banner);
        Assert.Contains("feature", viewModel.Banner);
        Assert.Contains("repository busy", viewModel.Banner);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("main", viewModel.CurrentBranch);
    }

    [Fact]
    public async Task CheckoutAsync_GuardedWhileBusy()
    {
        var git = new FakeGitService { Root = @"C:\ws\demo\.git" };
        var viewModel = Create(git);
        await viewModel.LoadAsync();
        viewModel.IsBusy = true;

        await viewModel.CheckoutAsync("feature");

        Assert.Empty(git.CheckedOut);
        Assert.False(viewModel.CheckoutCommand.CanExecute("feature"));
    }

    [Fact]
    public async Task CheckoutAsync_IgnoredBeforeRepositoryDetected()
    {
        var git = new FakeGitService();
        var viewModel = Create(git);

        await viewModel.CheckoutAsync("feature");

        Assert.Empty(git.CheckedOut);
    }

    [Fact]
    public async Task GeneratedCommands_RelayToViewModel()
    {
        var git = new FakeGitService { Root = @"C:\ws\demo\.git" };
        var viewModel = Create(git);

        await viewModel.LoadCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsRepo);

        await viewModel.CheckoutCommand.ExecuteAsync("dev");
        Assert.Equal(new[] { "dev" }, git.CheckedOut);
    }

    [AvaloniaFact]
    public async Task GitPaneView_ShowsEmptyState_WhenNotARepository()
    {
        using var workspace = new TempDirectory();
        using var git = new LibGit2GitService();
        var viewModel = new GitPaneViewModel(workspace.Path, git, InlineUiScheduler.Instance);
        await viewModel.LoadAsync();

        var window = new Window { Content = new Alcoler.Workbench.Views.GitPaneView { DataContext = viewModel } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var empty = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PART_GitEmpty");
        Assert.True(empty.IsVisible);
        Assert.Equal("Not a git repository", empty.Text);

        var current = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PART_CurrentBranch");
        // The header hides via the parent panel; a collapsed parent leaves the child's IsVisible set.
        var header = Assert.IsType<StackPanel>(current.GetVisualParent());
        Assert.False(header.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task GitPaneView_RendersBranchesStatusAndLog()
    {
        using var workspace = new TempDirectory();
        var repositoryRoot = System.IO.Path.Combine(workspace.Path, "repo");
        Repository.Init(repositoryRoot);
        using (var repository = new Repository(repositoryRoot))
        {
            File.WriteAllText(System.IO.Path.Combine(repositoryRoot, "readme.md"), "hello");
            Commands.Stage(repository, "*");
            var author = new Signature("Alcoler Tests", "tests@alcoler.local", DateTimeOffset.Now);
            repository.Commit("initial commit\n\nbody", author, author);
            repository.CreateBranch("feature");
        }
        File.WriteAllText(System.IO.Path.Combine(repositoryRoot, "scratch.txt"), "uncommitted");

        using var git = new LibGit2GitService();
        var viewModel = new GitPaneViewModel(repositoryRoot, git, InlineUiScheduler.Instance);
        await viewModel.LoadAsync();

        var window = new Window { Width = 360, Height = 640, Content = new Alcoler.Workbench.Views.GitPaneView { DataContext = viewModel } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var current = window.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PART_CurrentBranch");
        Assert.Equal(viewModel.CurrentBranch, current.Text);

        var branchButtons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("branchItem"))
            .ToList();
        Assert.Equal(2, branchButtons.Count);
        Assert.Equal(2, viewModel.Branches.Count);
        Assert.Single(viewModel.Branches, b => b.IsCurrent);
        Assert.Single(viewModel.Untracked, f => f.Path == "scratch.txt");
        Assert.Single(viewModel.RecentCommits, c => c.Message == "initial commit");
        var row = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("fileChangeRow"));
        Assert.Equal(32, row.Height);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "scratch.txt");
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "A");
        Assert.IsType<GitFileChangeViewModel>(row.DataContext);
        Assert.DoesNotContain("branchItem", row.Classes);
        Assert.DoesNotContain(row.GetVisualDescendants().OfType<Button>(), _ => true);

        window.Close();
    }
}
