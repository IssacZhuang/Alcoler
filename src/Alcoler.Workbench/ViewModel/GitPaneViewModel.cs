using System.Collections.ObjectModel;
using Alcoler.Workbench.Git;
using Alcoler.Workbench.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.Workbench.ViewModel;

public sealed partial class GitBranchViewModel : ObservableObject
{
    public GitBranchViewModel(string name, bool isCurrent, int? aheadOfUpstream, int? behindOfUpstream)
    {
        Name = name;
        _isCurrent = isCurrent;
        AheadOfUpstream = aheadOfUpstream;
        BehindOfUpstream = behindOfUpstream;
    }

    public string Name { get; }

    public int? AheadOfUpstream { get; }

    public int? BehindOfUpstream { get; }

    [ObservableProperty]
    private bool _isCurrent;

    public string TrackingText => AheadOfUpstream is null && BehindOfUpstream is null
        ? string.Empty
        : $"↑{AheadOfUpstream ?? 0} ↓{BehindOfUpstream ?? 0}";
}

public sealed class GitFileChangeViewModel
{
    public GitFileChangeViewModel(string path, ChangeState state)
    {
        Path = path;
        State = state;
    }

    public string Path { get; }

    public ChangeState State { get; }

    public string StateText => State.ToString().ToLowerInvariant();

    public string FileName => Path.Replace('\\', '/').Split('/')[^1];

    public string DirectoryName
    {
        get
        {
            var path = Path.Replace('\\', '/');
            var index = path.LastIndexOf('/');
            return index >= 0 ? path[..index] : string.Empty;
        }
    }

    public string StatusCode => State switch
    {
        ChangeState.Added => "A",
        ChangeState.Modified => "M",
        ChangeState.Deleted => "D",
        ChangeState.Renamed => "R",
        _ => string.Empty,
    };

    public bool IsAdded => State == ChangeState.Added;

    public bool IsModified => State == ChangeState.Modified;

    public bool IsDeleted => State == ChangeState.Deleted;

    public bool IsRenamed => State == ChangeState.Renamed;
}

public sealed class GitCommitViewModel
{
    public GitCommitViewModel(string sha, string message, string author, DateTimeOffset when)
    {
        Sha = sha;
        Message = message;
        Author = author;
        When = when;
    }

    public string Sha { get; }

    public string Message { get; }

    public string Author { get; }

    public DateTimeOffset When { get; }

    public string ShaShort => Sha.Length > 7 ? Sha[..7] : Sha;

    public string WhenText => RelativeTime.Format(When);
}

public sealed partial class GitPaneViewModel : ObservableObject
{
    public const int RecentCommitLimit = 20;

    private readonly IGitService _gitService;
    private readonly IUiScheduler _scheduler;
    private readonly string _workspaceRoot;
    private string? _repositoryRoot;

    public GitPaneViewModel(string workspaceRoot, IGitService gitService, IUiScheduler scheduler)
    {
        _workspaceRoot = workspaceRoot;
        _gitService = gitService;
        _scheduler = scheduler;
    }

    public ObservableCollection<GitBranchViewModel> Branches { get; } = [];

    public ObservableCollection<GitFileChangeViewModel> Staged { get; } = [];

    public ObservableCollection<GitFileChangeViewModel> Unstaged { get; } = [];

    public ObservableCollection<GitFileChangeViewModel> Untracked { get; } = [];

    public ObservableCollection<GitCommitViewModel> RecentCommits { get; } = [];

    [ObservableProperty]
    private bool _isRepo;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _banner;

    [ObservableProperty]
    private string _currentBranch = string.Empty;

    // Entry points run on the UI thread; results of background git calls are marshaled through
    // the scheduler so observable state never mutates off the UI thread.
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading)
        {
            return;
        }
        IsLoading = true;
        Banner = null;
        try
        {
            var snapshot = await Task.Run(CaptureSnapshot);
            _scheduler.Post(() => ApplySnapshot(snapshot));
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            _scheduler.Post(() => Banner = $"Git refresh failed: {message}");
        }
        finally
        {
            _scheduler.Post(() => IsLoading = false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckout))]
    public async Task CheckoutAsync(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName) || IsBusy || _repositoryRoot is null)
        {
            return;
        }
        IsBusy = true;
        Banner = null;
        var repositoryRoot = _repositoryRoot;
        try
        {
            await Task.Run(() => _gitService.Checkout(repositoryRoot, branchName));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            _scheduler.Post(() => Banner = $"Checkout of '{branchName}' failed: {message}");
        }
        finally
        {
            _scheduler.Post(() => IsBusy = false);
        }
    }

    public void ClearBanner() => Banner = null;

    private bool CanCheckout(string branchName) => !IsBusy;

    partial void OnIsBusyChanged(bool value) => CheckoutCommand.NotifyCanExecuteChanged();

    private GitSnapshot? CaptureSnapshot()
    {
        var repositoryRoot = _gitService.FindRepositoryRoot(_workspaceRoot);
        if (repositoryRoot is null)
        {
            return null;
        }
        return new GitSnapshot(
            repositoryRoot,
            _gitService.GetCurrentBranchName(repositoryRoot),
            _gitService.GetBranches(repositoryRoot),
            _gitService.GetStatus(repositoryRoot),
            _gitService.GetLog(repositoryRoot, RecentCommitLimit));
    }

    private void ApplySnapshot(GitSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            IsRepo = false;
            CurrentBranch = string.Empty;
            _repositoryRoot = null;
            Branches.Clear();
            Staged.Clear();
            Unstaged.Clear();
            Untracked.Clear();
            RecentCommits.Clear();
            return;
        }

        IsRepo = true;
        _repositoryRoot = snapshot.RepositoryRoot;
        CurrentBranch = snapshot.CurrentBranch ?? string.Empty;
        ReplaceAll(Branches, snapshot.Branches.Select(b =>
            new GitBranchViewModel(b.Name, b.IsCurrent, b.AheadOfUpstream, b.BehindOfUpstream)));
        ReplaceAll(Staged, snapshot.Status.Staged.Select(f => new GitFileChangeViewModel(f.Path, f.State)));
        ReplaceAll(Unstaged, snapshot.Status.Unstaged.Select(f => new GitFileChangeViewModel(f.Path, f.State)));
        ReplaceAll(Untracked, snapshot.Status.Untracked.Select(f => new GitFileChangeViewModel(f.Path, f.State)));
        ReplaceAll(RecentCommits, snapshot.Commits.Select(c =>
            new GitCommitViewModel(c.Sha, c.Message, c.Author, c.When)));
    }

    private static void ReplaceAll<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private sealed record GitSnapshot(
        string RepositoryRoot,
        string? CurrentBranch,
        IReadOnlyList<GitBranch> Branches,
        GitRepositoryStatus Status,
        IReadOnlyList<GitCommit> Commits);
}
