using System.Collections.Concurrent;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Kernel.Runtime;
using Alcoler.Workbench.Terminal;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Avalonia;
using Avalonia.Media;
using Xunit;

namespace Alcoler.Workbench.Tests;

public sealed class WorkbenchViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("alcoler-workbench-lifecycle-").FullName;

    private string DatabasePath => Path.Combine(_root, "sessions.sqlite");

    [Fact]
    public async Task OpenSession_AfterRuntimeRestart_ResumesPersistedTimelineAndAcceptsAnotherTurn()
    {
        const string title = "Persisted session";
        const string firstPrompt = "Remember this conversation.";
        const string firstReply = "The conversation is persisted.";
        const string nextPrompt = "Continue the reopened conversation.";
        const string nextReply = "The reopened session can continue.";
        SessionHandle session;

        await using (var store = new SqliteSessionStore(DatabasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await using var runtime = CreateRuntime(store, Reply(firstReply));
            session = await runtime.CreateSessionAsync(new SessionOptions(_root, title), CancellationToken.None);
            using var projection = runtime.Subscribe(session.Id);

            var ack = await runtime.SubmitAsync(new SendTextCommand(
                StrongId.NewCommandId(), session.Id, projection.Snapshot().Revision, firstPrompt), CancellationToken.None);

            Assert.True(ack.Accepted, ack.RejectionReason);
            await WaitUntilAsync(() =>
            {
                var snapshot = projection.Snapshot();
                return snapshot.State.Phase == TurnPhase.Idle
                    && snapshot.Rows.OfType<AssistantMessageRow>().Any(row =>
                        !row.IsStreaming && row.Parts.OfType<TextPart>().Any(part => part.Text == firstReply));
            });
        }

        await using var reopenedStore = new SqliteSessionStore(DatabasePath);
        await reopenedStore.InitializeAsync(CancellationToken.None);
        await using var reopenedRuntime = CreateRuntime(reopenedStore, Reply(nextReply));
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(reopenedRuntime, scheduler, new WorkbenchConfig([]));
        await workbench.Sidebar.RefreshAsync();
        var summary = Assert.Single(Assert.Single(workbench.Sidebar.Groups).Sessions);
        Assert.Equal(session.Id, summary.Id);
        Assert.Equal(title, summary.Title);
        Assert.Null(reopenedRuntime.TryObserve(session.Id));

        await workbench.OpenSessionAsync(summary.Id);

        using var resumedProjection = Assert.IsAssignableFrom<IConversationProjection>(reopenedRuntime.TryObserve(session.Id));
        var conversation = Assert.IsType<ConversationViewModel>(workbench.Active);
        await WaitUntilAsync(() => conversation.Items.OfType<AssistantMessageItemViewModel>()
            .Any(item => item.MarkdownText == firstReply), scheduler);
        Assert.Equal(session.Id, conversation.SessionId);
        Assert.Equal(title, conversation.Title);
        Assert.Equal(session.Id, workbench.Sidebar.ActiveSessionId);
        Assert.True(Assert.Single(Assert.Single(workbench.Sidebar.Groups).Sessions).IsActive);
        Assert.Equal(firstPrompt, Assert.Single(conversation.Items.OfType<UserMessageItemViewModel>()).Text);
        Assert.Equal(_root, Assert.IsType<TerminalPaneViewModel>(workbench.Terminal).StartedFrom);

        conversation.Composer.Draft = nextPrompt;
        await conversation.SendAsync();

        Assert.Null(conversation.Banner);
        Assert.Empty(conversation.Composer.Draft);
        await WaitUntilAsync(() => conversation.PhaseText == nameof(TurnPhase.Idle)
            && conversation.Items.OfType<AssistantMessageItemViewModel>()
                .Any(item => item.MarkdownText == nextReply && !item.IsStreaming), scheduler);
        Assert.Equal(new[] { firstPrompt, nextPrompt },
            conversation.Items.OfType<UserMessageItemViewModel>().Select(item => item.Text));
        Assert.Equal(new[] { firstReply, nextReply },
            conversation.Items.OfType<AssistantMessageItemViewModel>().Select(item => item.MarkdownText));
        await WaitUntilAsync(() => workbench.GitPane is { IsLoading: false }, scheduler);
    }

    [Fact]
    public async Task OpenSession_DifferentWorkspace_ReplacesTerminalAndDisposesPreviousPane()
    {
        var firstRoot = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store);
        var first = await runtime.CreateSessionAsync(new SessionOptions(firstRoot), CancellationToken.None);
        var second = await runtime.CreateSessionAsync(new SessionOptions(secondRoot), CancellationToken.None);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(first.Id);
        var initialTerminal = Assert.IsType<TerminalPaneViewModel>(workbench.Terminal);
        Assert.Equal(firstRoot, initialTerminal.StartedFrom);
        initialTerminal.Dispose();
        var service = new TrackingTerminalService();
        using var trackedTerminal = new TerminalPaneViewModel(firstRoot, service, scheduler);
        workbench.Terminal = trackedTerminal;
        var firstGit = Assert.IsType<GitPaneViewModel>(workbench.GitPane);

        await workbench.OpenSessionAsync(second.Id);

        var secondTerminal = Assert.IsType<TerminalPaneViewModel>(workbench.Terminal);
        var secondGit = Assert.IsType<GitPaneViewModel>(workbench.GitPane);
        Assert.NotSame(trackedTerminal, secondTerminal);
        Assert.Equal(secondRoot, secondTerminal.StartedFrom);
        Assert.True(service.IsDisposed);
        Assert.NotSame(firstGit, secondGit);
        Assert.False(workbench.IsTerminalVisible);
        Assert.False(secondTerminal.IsConnected);

        await workbench.OpenSessionAsync(first.Id);

        var returnedTerminal = Assert.IsType<TerminalPaneViewModel>(workbench.Terminal);
        Assert.NotSame(secondTerminal, returnedTerminal);
        Assert.NotSame(trackedTerminal, returnedTerminal);
        Assert.Equal(firstRoot, returnedTerminal.StartedFrom);
        await WaitUntilAsync(() => !firstGit.IsLoading && !secondGit.IsLoading
            && workbench.GitPane is { IsLoading: false }, scheduler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenSession_SameWorkspace_ReusesTerminalAndGitPane(bool differentRootCasing)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var secondRoot = differentRootCasing ? root.ToUpperInvariant() : root;
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store);
        var first = await runtime.CreateSessionAsync(new SessionOptions(root), CancellationToken.None);
        var second = await runtime.CreateSessionAsync(new SessionOptions(secondRoot), CancellationToken.None);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(first.Id);
        var terminal = Assert.IsType<TerminalPaneViewModel>(workbench.Terminal);
        var git = Assert.IsType<GitPaneViewModel>(workbench.GitPane);
        terminal.Resize(83, 19);

        await workbench.OpenSessionAsync(second.Id);

        Assert.Equal(second.Id, Assert.IsType<ConversationViewModel>(workbench.Active).SessionId);
        Assert.Same(terminal, workbench.Terminal);
        Assert.Same(git, workbench.GitPane);
        Assert.Equal(root, terminal.StartedFrom);
        Assert.Equal((83, 19), (terminal.Buffer.Cols, terminal.Buffer.Rows));
        await WaitUntilAsync(() => !git.IsLoading, scheduler);
    }

    [Fact]
    public void Dispose_DisposesBrowserPaneAndReleasesItsStatusSubscriptionAndScreenshot()
    {
        using var controller = new TrackingBrowserController();
        var image = new TrackingImage();
        using var browserPane = new BrowserPaneViewModel(controller, InlineUiScheduler.Instance, _ => image);
        browserPane.Bind(StrongId.NewSessionId());
        using var workbench = new WorkbenchViewModel(
            new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]), browserPane: browserPane);
        Assert.Equal(1, controller.SubscriberCount);
        Assert.Same(image, browserPane.Screenshot);

        workbench.Dispose();

        Assert.Equal(0, controller.SubscriberCount);
        Assert.Equal(1, image.DisposeCount);
        Assert.Null(browserPane.Screenshot);
        Assert.False(browserPane.HasScreenshot);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AgentRuntime CreateRuntime(SqliteSessionStore store, params ScriptStep[] script)
    {
        var artifacts = new FileArtifactStore(Path.Combine(_root, "artifacts"));
        return new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(new SequencedChatModel(script))],
            Tools = [],
            WorkspaceFactory = root => new WorkspaceToolEnvironment(root, artifacts),
            DefaultModel = new ModelSelection("scripted", "scripted-model"),
        });
    }

    private static ScriptStep Reply(string text) => new ScriptStep.Events(
        new ModelTextDelta(text), new ModelStreamFinished(ModelFinishReason.Stop));

    private static async Task WaitUntilAsync(Func<bool> condition, QueuedUiScheduler? scheduler = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            scheduler?.RunPending();
            if (condition())
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, "Lifecycle update did not arrive within 10 seconds.");
            await Task.Delay(10);
        }
    }

    private sealed class QueuedUiScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> _pending = new();

        public void Post(Action action) => _pending.Enqueue(action);

        public void RunPending()
        {
            while (_pending.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private sealed class TrackingTerminalService : IPtyTerminalService
    {
        public bool IsRunning => false;

        public bool IsDisposed { get; private set; }

        public event EventHandler<string>? OutputReceived { add { } remove { } }

        public event EventHandler<int>? Exited { add { } remove { } }

        public Task StartAsync(string workingDirectory, int cols, int rows, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteAsync(string text, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Resize(int cols, int rows) => throw new NotSupportedException();

        public Task StopAsync() => throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;
    }

    private sealed class TrackingBrowserController : IBrowserController
    {
        public event EventHandler<BrowserStatusChangedEventArgs>? StatusChanged;

        public int SubscriberCount => StatusChanged?.GetInvocationList().Length ?? 0;

        public BrowserSessionStatus? GetStatus(SessionId sessionId) => new(
            BrowserSessionState.Launched, LatestScreenshotUri: "screenshot.png", UpdatedAt: DateTimeOffset.UtcNow);

        public Task<BrowserActionResult> ExecuteAsync(SessionId sessionId, BrowserAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task TakeoverAsync(SessionId sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class TrackingImage : IImage, IDisposable
    {
        public Size Size => new(4, 4);

        public int DisposeCount { get; private set; }

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }

        public void Dispose() => DisposeCount++;
    }
}
