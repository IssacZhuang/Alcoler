using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Kernel.Runtime;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using static Alcoler.Tests.Views.ShellSmokeTests;

namespace Alcoler.Tests;

public sealed class WorkspaceRemovalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("alcoler-workspace-removal-").FullName;

    private string DatabasePath => Path.Combine(_root, "sessions.sqlite");

    [Fact]
    public async Task RemoveWorkspace_HidesSidebarGroupButKeepsSessions()
    {
        var alpha = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        var beta = Directory.CreateDirectory(Path.Combine(_root, "beta")).FullName;
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store);
        var alphaSession = await runtime.CreateSessionAsync(new SessionOptions(alpha, "Alpha"), CancellationToken.None);
        await runtime.CreateSessionAsync(new SessionOptions(beta, "Beta"), CancellationToken.None);
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        await workbench.Sidebar.RefreshAsync();
        Assert.Equal(2, workbench.Sidebar.Groups.Count);

        await workbench.RemoveWorkspaceAsync(alpha + Path.DirectorySeparatorChar);

        var group = Assert.Single(workbench.Sidebar.Groups);
        Assert.Equal(beta, group.WorkspaceRoot);
        Assert.Equal(2, (await runtime.ListSessionsAsync(null, 200, CancellationToken.None)).Count);
        Assert.NotNull(await store.GetSessionAsync(alphaSession.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RemovedWorkspace_PersistsAcrossRestart_AndReturnsWhenTaskStartedThereAgain()
    {
        var configPath = Path.Combine(_root, "config.json");
        new WorkbenchConfig([]).Save(configPath);
        var alpha = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        SessionHandle first;
        await using (var store = new SqliteSessionStore(DatabasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await using var runtime = CreateRuntime(store);
            first = await runtime.CreateSessionAsync(new SessionOptions(alpha, "Alpha"), CancellationToken.None);
            using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance,
                WorkbenchConfig.LoadOrDefault(configPath),
                saveComposer: composer => (WorkbenchConfig.LoadOrDefault(configPath) with { Composer = composer }).Save(configPath));
            await workbench.Sidebar.RefreshAsync();
            Assert.Single(workbench.Sidebar.Groups);

            await workbench.RemoveWorkspaceAsync(alpha);

            Assert.Empty(workbench.Sidebar.Groups);
        }

        await using var reopenedStore = new SqliteSessionStore(DatabasePath);
        await reopenedStore.InitializeAsync(CancellationToken.None);
        await using var reopenedRuntime = CreateRuntime(reopenedStore);
        using var restarted = new WorkbenchViewModel(reopenedRuntime, InlineUiScheduler.Instance,
            WorkbenchConfig.LoadOrDefault(configPath));
        await restarted.Sidebar.RefreshAsync();
        Assert.Empty(restarted.Sidebar.Groups);
        Assert.Equal(first.Id, Assert.Single(await reopenedRuntime.ListSessionsAsync(null, 200, CancellationToken.None)).Id);

        await restarted.NewSessionAsync(alpha, "Alpha again");

        var group = Assert.Single(restarted.Sidebar.Groups);
        Assert.Equal(alpha, group.WorkspaceRoot);
        Assert.Equal(2, group.Sessions.Count);
    }

    [Fact]
    public async Task RemoveWorkspace_ClosesActiveConversationAndMovesDraftToRemainingWorkspace()
    {
        var alpha = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        var beta = Directory.CreateDirectory(Path.Combine(_root, "beta")).FullName;
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store);
        var alphaSession = await runtime.CreateSessionAsync(new SessionOptions(alpha, "Alpha"), CancellationToken.None);
        var betaSession = await runtime.CreateSessionAsync(new SessionOptions(beta, "Beta"), CancellationToken.None);
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(alphaSession.Id);
        Assert.NotNull(workbench.Active);

        await workbench.RemoveWorkspaceAsync(alpha);

        Assert.Null(workbench.Active);
        Assert.NotNull(workbench.Draft);
        Assert.Equal(beta, workbench.Draft.WorkspaceRoot);
        Assert.Equal(beta, Assert.Single(workbench.Sidebar.Groups).WorkspaceRoot);
        Assert.Equal(2, (await runtime.ListSessionsAsync(null, 200, CancellationToken.None)).Count);
        Assert.NotNull(await store.GetSessionAsync(betaSession.Id, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveWorkspace_KeepsActiveConversationUntilSidebarRefreshCompletes()
    {
        var alpha = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        var beta = Directory.CreateDirectory(Path.Combine(_root, "beta")).FullName;
        var runtime = new WorkspaceRuntime();
        var alphaSession = runtime.AddSession(alpha, "Alpha");
        var betaSession = runtime.AddSession(beta, "Beta");
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(alphaSession.Id);
        var conversation = Assert.IsType<ConversationViewModel>(workbench.Active);
        conversation.Composer.Draft = "Keep this session draft";
        var refresh = runtime.DelayNextList();

        var removing = workbench.RemoveWorkspaceAsync(alpha);
        try
        {
            Assert.False(removing.IsCompleted);
            Assert.Same(conversation, workbench.Active);
            Assert.Same(conversation, workbench.ComposerHost);
            Assert.False(workbench.IsDraft);
            Assert.Equal(alpha, workbench.CurrentWorkspaceRoot);
            Assert.Equal("Keep this session draft", workbench.ComposerHost!.Composer.Draft);
        }
        finally
        {
            refresh.TrySetResult([alphaSession, betaSession]);
            await removing;
        }

        Assert.Null(workbench.Active);
        var draft = Assert.IsType<DraftConversationViewModel>(workbench.Draft);
        Assert.Same(draft, workbench.ComposerHost);
        Assert.Equal(beta, draft.WorkspaceRoot);
        Assert.Equal("beta", draft.WorkspaceName);
        Assert.Equal(beta, Assert.Single(workbench.Sidebar.Groups).WorkspaceRoot);
        await workbench.OpenSessionAsync(alphaSession.Id);
        Assert.Equal("Keep this session draft", workbench.Active!.Composer.Draft);
    }

    [Fact]
    public async Task RemoveWorkspace_FailedSidebarRefreshKeepsActiveConversationAndPropagatesFailure()
    {
        var alpha = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        var runtime = new WorkspaceRuntime();
        var session = runtime.AddSession(alpha, "Alpha");
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(session.Id);
        var conversation = Assert.IsType<ConversationViewModel>(workbench.Active);
        conversation.Composer.Draft = "Keep this session draft";
        var refresh = runtime.DelayNextList();

        var removing = workbench.RemoveWorkspaceAsync(alpha);
        refresh.SetException(new IOException("Session list unavailable"));

        var exception = await Assert.ThrowsAsync<IOException>(() => removing);
        Assert.Equal("Session list unavailable", exception.Message);
        Assert.Same(conversation, workbench.Active);
        Assert.Same(conversation, workbench.ComposerHost);
        Assert.False(workbench.IsDraft);
        Assert.Equal(alpha, workbench.CurrentWorkspaceRoot);
        Assert.Equal("Keep this session draft", workbench.ComposerHost!.Composer.Draft);
        Assert.False(workbench.Sidebar.IsLoading);
    }

    [Fact]
    public async Task RemoveWorkspace_DoesNotSupersedeNewerPendingSessionNavigation()
    {
        var alpha = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        var beta = Directory.CreateDirectory(Path.Combine(_root, "beta")).FullName;
        var runtime = new WorkspaceRuntime();
        var alphaSession = runtime.AddSession(alpha, "Alpha");
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(alphaSession.Id);
        var conversation = Assert.IsType<ConversationViewModel>(workbench.Active);
        var betaSession = runtime.AddSession(beta, "Beta");
        var removalRefresh = runtime.DelayNextList();
        var removing = workbench.RemoveWorkspaceAsync(alpha);
        var navigationRefresh = runtime.DelayNextList();
        var opening = workbench.OpenSessionAsync(betaSession.Id);

        removalRefresh.SetResult([alphaSession, betaSession]);
        try
        {
            Assert.False(opening.IsCompleted);
            await removing;
            Assert.Same(conversation, workbench.Active);
            Assert.Same(conversation, workbench.ComposerHost);
        }
        finally
        {
            navigationRefresh.TrySetResult([alphaSession, betaSession]);
            await opening;
        }

        Assert.Equal(betaSession.Id, workbench.Active!.SessionId);
        Assert.Same(workbench.Active, workbench.ComposerHost);
        Assert.Equal(beta, workbench.CurrentWorkspaceRoot);
    }

    [Fact]
    public async Task RemoveWorkspace_AlsoHidesArchivedSessionsOfThatWorkspace()
    {
        var alpha = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        var beta = Directory.CreateDirectory(Path.Combine(_root, "beta")).FullName;
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store);
        var alphaSession = await runtime.CreateSessionAsync(new SessionOptions(alpha, "Archived alpha"), CancellationToken.None);
        await runtime.CreateSessionAsync(new SessionOptions(beta, "Beta"), CancellationToken.None);
        await runtime.SetSessionArchivedAsync(alphaSession.Id, true, CancellationToken.None);
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        await workbench.Sidebar.RefreshAsync();
        await workbench.ToggleArchivedViewAsync();
        Assert.Single(workbench.Sidebar.Groups.SelectMany(group => group.Sessions));

        await workbench.RemoveWorkspaceAsync(alpha);

        Assert.Empty(workbench.Sidebar.Groups.SelectMany(group => group.Sessions));
        await workbench.ToggleArchivedViewAsync();
        Assert.Equal("Beta", Assert.Single(workbench.Sidebar.Groups.SelectMany(group => group.Sessions)).Title);
        Assert.Single(await runtime.ListSessionsAsync(null, 200, SessionListKind.Archived, CancellationToken.None));
    }

    [Fact]
    public async Task ShowNewTask_InRemovedWorkspace_RevealsItAgain()
    {
        var alpha = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store);
        await runtime.CreateSessionAsync(new SessionOptions(alpha, "Alpha"), CancellationToken.None);
        using var workbench = new WorkbenchViewModel(runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]));
        await workbench.Sidebar.RefreshAsync();
        await workbench.RemoveWorkspaceAsync(alpha);
        Assert.Empty(workbench.Sidebar.Groups);

        workbench.ShowNewTask(alpha);
        await workbench.Sidebar.RefreshAsync();

        Assert.Equal(alpha, Assert.Single(workbench.Sidebar.Groups).WorkspaceRoot);
    }

    [Fact]
    public void SessionGroup_ReportsRunningSessions()
    {
        var sidebar = new SessionSidebarViewModel(() => Task.CompletedTask);
        var running = new SessionSummaryViewModel(StrongId.NewSessionId(), "Running", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        var idle = new SessionSummaryViewModel(StrongId.NewSessionId(), "Idle", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        sidebar.ApplySummaries([running, idle]);

        Assert.False(sidebar.Groups[0].HasRunningSessions);
        running.Indicator = SessionActivityIndicator.Running;
        Assert.True(sidebar.Groups[0].HasRunningSessions);
        running.Indicator = SessionActivityIndicator.None;
        Assert.False(sidebar.Groups[0].HasRunningSessions);
    }

    [AvaloniaFact]
    public async Task GroupContextFlyout_RemoveProjectHidesTheGroupWithoutConfirmWhenIdle()
    {
        using var viewModel = CreateWorkbench();
        var session = new SessionSummaryViewModel(
            StrongId.NewSessionId(), "Alpha", @"C:\ws\alpha", DateTimeOffset.UtcNow);
        viewModel.Sidebar.ApplySummaries([session]);
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            var sidebar = Assert.IsType<Alcoler.Views.SidebarView>(
                Assert.Single(window.GetVisualDescendants().OfType<Alcoler.Views.SidebarView>()));
            var header = Assert.Single(sidebar.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("groupHeader")
                    && button.DataContext is SessionGroupViewModel);
            var menu = Assert.IsType<MenuFlyout>(header.ContextFlyout);
            menu.ShowAt(header);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var remove = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Remove project"));
                Assert.False(((SessionGroupViewModel)header.DataContext!).HasRunningSessions);
                remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(50);
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(viewModel.Sidebar.Groups,
                    group => string.Equals(group.WorkspaceRoot, @"C:\ws\alpha", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                menu.Hide();
            }
        }
        finally
        {
            window.Close();
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class WorkspaceRuntime : IAgentRuntime
    {
        private readonly FakeAgentRuntime _inner = new();
        private readonly List<SessionSummary> _summaries = [];
        private readonly Queue<Task<IReadOnlyList<SessionSummary>>> _pendingLists = [];

        public SessionSummary AddSession(string root, string title)
        {
            var now = DateTimeOffset.UtcNow;
            var summary = new SessionSummary(StrongId.NewSessionId(), title, root, PermissionMode.Build, null, now, now);
            _summaries.Add(summary);
            _inner.RegisterSession(summary.Id, new ConversationSnapshot(0, 0, [], TestSnapshots.State() with { Title = title }));
            return summary;
        }

        public TaskCompletionSource<IReadOnlyList<SessionSummary>> DelayNextList()
        {
            var result = new TaskCompletionSource<IReadOnlyList<SessionSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingLists.Enqueue(result.Task);
            return result;
        }

        public ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken) =>
            _inner.CreateSessionAsync(options, cancellationToken);

        public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            _inner.ResumeSessionAsync(sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken) =>
            _pendingLists.TryDequeue(out var pending)
                ? new ValueTask<IReadOnlyList<SessionSummary>>(pending)
                : ValueTask.FromResult<IReadOnlyList<SessionSummary>>(_summaries.ToArray());

        public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken) =>
            _inner.SubmitAsync(command, cancellationToken);

        public ValueTask<int> UnloadIdleSessionsAsync(
            TimeSpan idleThreshold, SessionId? keepOpen, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public IConversationProjection Subscribe(SessionId sessionId) => _inner.Subscribe(sessionId);

        public IConversationProjection? TryObserve(SessionId sessionId) => _inner.TryObserve(sessionId);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private AgentRuntime CreateRuntime(SqliteSessionStore store)
    {
        var artifacts = new FileArtifactStore(Path.Combine(_root, "artifacts"));
        return new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(new SequencedChatModel([]))],
            Tools = [],
            WorkspaceFactory = root => new WorkspaceToolEnvironment(root, artifacts),
            DefaultModel = new ModelSelection("scripted", "scripted-model"),
        });
    }
}
