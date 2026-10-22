using System.Collections.Concurrent;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Browser;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Terminal;
using Alcoler.Ui;
using Alcoler.ViewModel;
using Avalonia;
using Avalonia.Media;
using Xunit;

namespace Alcoler.Tests;

public sealed class WorkbenchViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("alcoler-workbench-lifecycle-").FullName;

    private string DatabasePath => Path.Combine(_root, "sessions.sqlite");

    [Fact]
    public async Task Draft_FirstSubmitPersistsAndOpensOneConversation()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store, Reply("Started from a draft."));
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        var draft = Assert.IsType<DraftConversationViewModel>(workbench.Draft);
        draft.Composer.Draft = "Begin this conversation.";
        Assert.Empty(await runtime.ListSessionsAsync(null, 200, CancellationToken.None));
        Assert.Null(workbench.Active);

        await draft.SendAsync();

        var persisted = Assert.Single(await runtime.ListSessionsAsync(null, 200, CancellationToken.None));
        var conversation = Assert.IsType<ConversationViewModel>(workbench.Active);
        Assert.Equal(persisted.Id, conversation.SessionId);
        Assert.Same(draft.Composer, conversation.Composer);
        Assert.Empty(conversation.Composer.Draft);
        await WaitUntilAsync(() => conversation.Items.OfType<AssistantMessageItemViewModel>()
            .Any(item => item.MarkdownText == "Started from a draft." && !item.IsStreaming), scheduler);
        Assert.Equal("Begin this conversation.", Assert.Single(conversation.Items.OfType<UserMessageItemViewModel>()).Text);
        await WaitUntilAsync(() => workbench.GitPane is { IsLoading: false }, scheduler);
    }

    [Fact]
    public async Task ForkFromAssistantMessage_OpensTheChildAndAddsItToTheSidebar()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store, Reply("parent answer"), Reply("child answer"));
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(_root);
        var draft = Assert.IsType<DraftConversationViewModel>(workbench.Draft);
        draft.Composer.Draft = "parent question";
        await draft.SendAsync();
        await WaitUntilAsync(() => workbench.Active is ConversationViewModel conversation
            && conversation.Items.OfType<AssistantMessageItemViewModel>()
                .Any(item => item.MarkdownText == "parent answer" && !item.IsStreaming), scheduler);

        var parent = Assert.IsType<ConversationViewModel>(workbench.Active);
        var assistant = Assert.Single(parent.Items.OfType<AssistantMessageItemViewModel>());
        Assert.True(assistant.CanFork);

        await parent.ForkFromMessageAsync(assistant);

        await WaitUntilAsync(() => workbench.Active is ConversationViewModel child
            && child.SessionId != parent.SessionId
            && child.Title == "Fork of parent question"
            && child.Items.OfType<AssistantMessageItemViewModel>()
                .Any(item => item.MarkdownText == "parent answer" && item.CanFork), scheduler);
        var forked = Assert.IsType<ConversationViewModel>(workbench.Active);
        Assert.Equal(["parent question"], forked.Items.OfType<UserMessageItemViewModel>().Select(item => item.Text));
        Assert.Empty(forked.Composer.Draft);
        await WaitUntilAsync(
            () => workbench.Sidebar.Groups.SelectMany(group => group.Sessions).Count() == 2,
            scheduler);
    }

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
    public async Task Sidebar_AfterRestart_HidesChildSessionsWhileDirectResumePreservesParentMetadata()
    {
        SessionHandle parent;
        SessionHandle child;
        await using (var store = new SqliteSessionStore(DatabasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await using var runtime = CreateRuntime(store);
            parent = await runtime.CreateSessionAsync(new SessionOptions(_root, "Parent"), CancellationToken.None);
            child = await runtime.CreateSessionAsync(
                new SessionOptions(_root, "Child", ParentSessionId: parent.Id), CancellationToken.None);
            Assert.Equal(parent.Id, Assert.Single(await runtime.ListSessionsAsync(null, 1, CancellationToken.None)).Id);
        }

        await using var reopenedStore = new SqliteSessionStore(DatabasePath);
        await reopenedStore.InitializeAsync(CancellationToken.None);
        await using var reopenedRuntime = CreateRuntime(reopenedStore);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(reopenedRuntime, scheduler, new WorkbenchConfig([]));
        await workbench.Sidebar.RefreshAsync();
        Assert.Equal(parent.Id, Assert.Single(Assert.Single(workbench.Sidebar.Groups).Sessions).Id);

        var resumed = await reopenedRuntime.ResumeSessionAsync(child.Id, CancellationToken.None);
        Assert.Equal(child.Id, resumed.Id);
        using var projection = Assert.IsAssignableFrom<IConversationProjection>(reopenedRuntime.TryObserve(child.Id));
        Assert.Equal("Child", projection.Snapshot().State.Title);
        var ack = await reopenedRuntime.SubmitAsync(
            new RenameSessionCommand(StrongId.NewCommandId(), child.Id, 0, "Renamed child"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        Assert.Equal(parent.Id, (await reopenedStore.GetSessionAsync(child.Id, CancellationToken.None))!.ParentSessionId);
        await workbench.Sidebar.RefreshAsync();
        Assert.Equal(parent.Id, Assert.Single(Assert.Single(workbench.Sidebar.Groups).Sessions).Id);
        await WaitUntilAsync(() => workbench.GitPane is null or { IsLoading: false }, scheduler);
    }

    [Fact]
    public async Task AutoArchive_HidesStaleSessionsAndArchivedViewCanRestoreThem()
    {
        SessionHandle stale;
        await using (var store = new SqliteSessionStore(DatabasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await using var runtime = CreateRuntime(store);
            stale = await runtime.CreateSessionAsync(new SessionOptions(_root, "Stale"), CancellationToken.None);
            await runtime.CreateSessionAsync(new SessionOptions(_root, "Recent"), CancellationToken.None);
            var record = Assert.IsType<SessionRecord>(
                await store.GetSessionAsync(stale.Id, CancellationToken.None));
            await store.UpsertSessionAsync(
                record with { UpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(30) },
                CancellationToken.None);
        }

        await using var reopenedStore = new SqliteSessionStore(DatabasePath);
        await reopenedStore.InitializeAsync(CancellationToken.None);
        await using var reopenedRuntime = CreateRuntime(reopenedStore);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(
            reopenedRuntime, scheduler, new WorkbenchConfig([], AutoArchiveSessions: true, AutoArchiveOlderThanDays: 7));

        await workbench.Sidebar.RefreshAsync();

        Assert.Equal(
            new[] { "Recent" },
            workbench.Sidebar.Groups.SelectMany(group => group.Sessions)
                .Select(session => session.Title).ToArray());
        Assert.Single(await reopenedStore.ListSessionsAsync(
            null, 10, SessionListKind.Archived, CancellationToken.None));

        await workbench.ToggleArchivedViewAsync();

        var archived = Assert.Single(workbench.Sidebar.Groups.SelectMany(group => group.Sessions));
        Assert.Equal("Stale", archived.Title);
        Assert.True(archived.IsArchived);
        Assert.Equal("Archived", workbench.Sidebar.HeaderTitle);

        await workbench.SetSessionArchivedAsync(stale.Id, archived: false);

        Assert.Empty(workbench.Sidebar.Groups.SelectMany(group => group.Sessions));
        await workbench.ToggleArchivedViewAsync();
        Assert.Equal(
            new[] { "Recent", "Stale" },
            workbench.Sidebar.Groups.SelectMany(group => group.Sessions)
                .Select(session => session.Title).ToArray());
    }

    [Fact]
    public async Task UnsentMode_AfterRuntimeRestart_RestoresSelectionWithoutChangingPersistedSessionPolicy()
    {
        var configPath = Path.Combine(_root, "config.json");
        var config = new WorkbenchConfig([]);
        config.Save(configPath);
        SessionHandle session;
        await using (var store = new SqliteSessionStore(DatabasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await using var runtime = CreateRuntime(store);
            session = await runtime.CreateSessionAsync(new SessionOptions(_root), CancellationToken.None);
            var scheduler = new QueuedUiScheduler();
            using var workbench = new WorkbenchViewModel(runtime, scheduler, config,
                saveComposer: composer => (WorkbenchConfig.LoadOrDefault(configPath) with { Composer = composer }).Save(configPath));
            await workbench.OpenSessionAsync(session.Id);
            await WaitUntilAsync(() => workbench.Active!.Title.Length > 0, scheduler);
            workbench.Active!.Composer.Mode = PermissionMode.Plan;
            Assert.Equal(PermissionMode.Build, Assert.Single(await runtime.ListSessionsAsync(null, 200, CancellationToken.None)).PermissionMode);
            await WaitUntilAsync(() => workbench.GitPane is { IsLoading: false }, scheduler);
        }
        await using var reopenedStore = new SqliteSessionStore(DatabasePath);
        await reopenedStore.InitializeAsync(CancellationToken.None);
        await using var reopenedRuntime = CreateRuntime(reopenedStore, Reply("Plan restored."));
        var reopenedScheduler = new QueuedUiScheduler();
        using var restarted = new WorkbenchViewModel(reopenedRuntime, reopenedScheduler, WorkbenchConfig.LoadOrDefault(configPath),
            saveComposer: composer => (WorkbenchConfig.LoadOrDefault(configPath) with { Composer = composer }).Save(configPath));

        await restarted.OpenSessionAsync(session.Id);
        await WaitUntilAsync(() => restarted.Active!.CurrentSelection is not null, reopenedScheduler);

        var conversation = restarted.Active!;
        Assert.Equal(PermissionMode.Plan, conversation.Composer.Mode);
        Assert.Equal(PermissionMode.Build, conversation.Composer.SessionMode);
        conversation.Composer.Draft = "Continue in plan mode.";
        await conversation.SendAsync();
        await WaitUntilAsync(() => conversation.Items.OfType<AssistantMessageItemViewModel>()
            .Any(item => item.MarkdownText == "Plan restored." && !item.IsStreaming), reopenedScheduler);
        Assert.Equal(PermissionMode.Plan, Assert.Single(await reopenedRuntime.ListSessionsAsync(null, 200, CancellationToken.None)).PermissionMode);
        Assert.False(WorkbenchConfig.LoadOrDefault(configPath).Composer!.PendingSessionModes!.ContainsKey(session.Id.Value));
        Assert.Equal(PermissionMode.Plan, conversation.Composer.Mode);
        await WaitUntilAsync(() => restarted.GitPane is { IsLoading: false }, reopenedScheduler);
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
    public async Task SameWorkspace_DifferentSessionShellsReplaceTerminalWithoutReplacingGitPane()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var shells = Alcoler.Adapters.Tools.ShellResolver.AvailableShells();
        var cmd = shells.First(shell => shell.Dialect == ShellDialect.Cmd);
        var bash = shells.FirstOrDefault(shell => shell.Dialect == ShellDialect.GitBash);
        if (bash is null)
            return;
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store);
        var first = await runtime.CreateSessionAsync(new SessionOptions(_root, Shell: cmd), CancellationToken.None);
        var second = await runtime.CreateSessionAsync(new SessionOptions(_root, Shell: bash), CancellationToken.None);
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        await workbench.OpenSessionAsync(first.Id);
        var firstTerminal = workbench.Terminal;
        var git = workbench.GitPane;

        await workbench.OpenSessionAsync(second.Id);

        Assert.NotSame(firstTerminal, workbench.Terminal);
        Assert.Same(git, workbench.GitPane);
        await WaitUntilAsync(() => git is { IsLoading: false }, scheduler);
    }

    [Fact]
    public void ApplySettings_PublishesLiveShellPreferenceWithoutMutatingOriginalConfig()
    {
        var original = new WorkbenchConfig([]);
        WorkbenchConfig? published = null;
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), InlineUiScheduler.Instance,
            original, applySettings: config => published = config);
        var selected = new ShellEnvironment(@"C:\Windows\System32\cmd.exe", ShellDialect.Cmd);
        var updated = original with { IntegratedTerminalShell = selected };

        workbench.ApplySettings(updated);

        Assert.Same(updated, published);
        Assert.Null(original.IntegratedTerminalShell);
        Assert.Empty(workbench.ModelChoices);
    }

    [Fact]
    public void ApplySettings_RefreshesModelChoicesForNewSessions()
    {
        using var workbench = new WorkbenchViewModel(
            new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));
        var updated = new WorkbenchConfig([
            new WorkbenchProviderConfig("openai-compatible", "https://example.test/v1", "key", [
                new WorkbenchModelConfig("first", ReasoningLevels: ["low", "high"]),
                new WorkbenchModelConfig("second", ReasoningLevels: ["low", "medium", "high"]),
            ], Id: "custom"),
        ], new WorkbenchDefaultModelConfig("custom", "first"));

        workbench.ApplySettings(updated);

        Assert.Equal(["first", "second"], workbench.ModelChoices.Select(choice => choice.ModelId));
    }

    [Fact]
    public void DocumentLauncherAndGenericOpen_SynchronizeVisibilityWithoutImplicitGitOrBottomTerminal()
    {
        using var workbench = new WorkbenchViewModel(
            new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));

        workbench.ToggleSidePaneCommand.Execute(null);

        Assert.True(workbench.IsSidePaneVisible);
        Assert.True(workbench.Documents.IsVisible);
        Assert.Empty(workbench.Documents.Items);
        Assert.False(workbench.Documents.HasDocuments);
        Assert.Null(workbench.Terminal);
        Assert.False(workbench.IsTerminalVisible);
        var content = new object();
        var document = new DocumentViewModel("extension", "extension.example", "Extension", "", content,
            workbench.CurrentWorkspaceRoot, workbench.CurrentDocumentOwnerId);
        Assert.Same(document, workbench.OpenDocument(document));
        Assert.Same(content, workbench.Documents.ActiveDocument!.Content);
        workbench.Documents.Collapse();
        Assert.False(workbench.IsSidePaneVisible);
        Assert.Same(document, Assert.Single(workbench.Documents.Items));
        workbench.IsSidePaneVisible = true;
        Assert.True(workbench.Documents.IsVisible);
        workbench.CloseSidePaneCommand.Execute(null);
        Assert.False(workbench.Documents.IsVisible);
    }

    [Fact]
    public async Task ShowGitPane_OpensWorkspaceGlobalDocument_AndRetainsOldWorkspaceContent()
    {
        var firstRoot = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        var scheduler = new QueuedUiScheduler();
        using var workbench = new WorkbenchViewModel(new FakeAgentRuntime(), scheduler, new WorkbenchConfig([]));
        workbench.ShowNewTask(firstRoot);
        var firstPane = workbench.GitPane;

        workbench.ShowGitPaneCommand.Execute(null);

        var first = Assert.Single(workbench.Documents.Items);
        Assert.Equal("git", first.Kind);
        Assert.Null(first.OwnerId);
        Assert.False(first.OwnsContent);
        Assert.Same(firstPane, first.Content);
        Assert.True(workbench.IsSidePaneVisible);
        workbench.ShowGitPaneCommand.Execute(null);
        Assert.Same(first, Assert.Single(workbench.Documents.Items));
        workbench.ShowNewTask(secondRoot);
        Assert.Empty(workbench.Documents.VisibleItems);
        workbench.ShowGitPaneCommand.Execute(null);
        var second = workbench.Documents.ActiveDocument!;
        Assert.NotSame(first.Content, second.Content);
        Assert.Equal(firstRoot, first.WorkspaceRoot);
        Assert.Same(firstPane, first.Content);
        workbench.ShowNewTask(firstRoot);
        Assert.Same(first, workbench.Documents.ActiveDocument);
        Assert.Same(firstPane, workbench.GitPane);
        await WaitUntilAsync(() => !((GitPaneViewModel)first.Content).IsLoading
            && !((GitPaneViewModel)second.Content).IsLoading, scheduler);
    }

    [Fact]
    public async Task BrowserDocuments_HaveIndependentSessionPresentations_AndCloseDoesNotDisposeController()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store);
        var first = await runtime.CreateSessionAsync(new SessionOptions(_root), CancellationToken.None);
        var second = await runtime.CreateSessionAsync(new SessionOptions(_root), CancellationToken.None);
        using var controller = new TrackingBrowserController();
        using var browser = new BrowserPaneViewModel(controller, InlineUiScheduler.Instance, _ => new TrackingImage());
        using var workbench = new WorkbenchViewModel(
            runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]), browserPane: browser);
        await workbench.OpenSessionAsync(first.Id);

        workbench.ShowBrowserPaneCommand.Execute(null);

        var firstDocument = Assert.Single(workbench.Documents.Items);
        var firstView = Assert.IsType<BrowserPaneViewModel>(firstDocument.Content);
        var firstImage = Assert.IsType<TrackingImage>(firstView.Screenshot);
        Assert.NotSame(browser, firstView);
        Assert.Equal(first.Id, firstView.ActiveSessionId);
        Assert.Equal(first.Id.Value, firstDocument.OwnerId);
        workbench.ShowBrowserPaneCommand.Execute(null);
        Assert.Same(firstDocument, Assert.Single(workbench.Documents.Items));
        await workbench.OpenSessionAsync(second.Id);
        Assert.Empty(workbench.Documents.VisibleItems);
        Assert.Equal(first.Id, firstView.ActiveSessionId);
        workbench.ShowBrowserPaneCommand.Execute(null);
        var secondDocument = workbench.Documents.ActiveDocument!;
        var secondView = Assert.IsType<BrowserPaneViewModel>(secondDocument.Content);
        Assert.NotSame(firstView, secondView);
        Assert.Equal(second.Id, secondView.ActiveSessionId);
        Assert.Equal(3, controller.SubscriberCount);

        workbench.Documents.Close(secondDocument);

        Assert.Equal(2, controller.SubscriberCount);
        Assert.Null(secondView.Screenshot);
        Assert.Equal(0, controller.DisposeCount);
        Assert.Equal(0, firstImage.DisposeCount);
        await workbench.OpenSessionAsync(first.Id);
        Assert.Same(firstDocument, workbench.Documents.ActiveDocument);
        workbench.Documents.Close(firstDocument);
        Assert.Equal(1, firstImage.DisposeCount);
        Assert.Equal(1, controller.SubscriberCount);
        Assert.Equal(0, controller.DisposeCount);
    }

    [Fact]
    public async Task BrowserActivity_OpensSessionDocumentAfterStatusReachesUiScheduler()
    {
        var sessionId = StrongId.NewSessionId();
        var runtime = new FakeAgentRuntime();
        runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var scheduler = new QueuedUiScheduler();
        using var controller = new TrackingBrowserController();
        using var browser = new BrowserPaneViewModel(controller, scheduler);
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]), browserPane: browser);
        await workbench.OpenSessionAsync(sessionId);
        controller.RaiseStatusChanged(sessionId, new BrowserSessionStatus(BrowserSessionState.Busy));
        scheduler.RunPending();
        Assert.Empty(workbench.Documents.Items);
        Assert.False(workbench.IsSidePaneVisible);

        controller.RaiseStatusChanged(sessionId, BrowserStatus("first"));
        Assert.Empty(workbench.Documents.Items);
        scheduler.RunPending();

        var document = Assert.Single(workbench.Documents.VisibleItems);
        var view = Assert.IsType<BrowserPaneViewModel>(document.Content);
        Assert.Equal("browser", document.Kind);
        Assert.Equal(sessionId.Value, document.OwnerId);
        Assert.Equal(sessionId, view.ActiveSessionId);
        Assert.Equal("first", view.ActiveTabId);
        Assert.NotSame(browser, view);
        Assert.Null(browser.PagePresenter);
        Assert.Same(document, workbench.Documents.ActiveDocument);
        Assert.True(workbench.Documents.IsVisible);
        Assert.True(workbench.IsSidePaneVisible);
        await WaitUntilAsync(() => workbench.GitPane is { IsLoading: false }, scheduler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserActivity_ExistingTabsOpenOnSessionBindingWithoutMixingOwners(bool sameTabId)
    {
        var first = StrongId.NewSessionId();
        var second = StrongId.NewSessionId();
        var runtime = new FakeAgentRuntime();
        runtime.RegisterSession(first, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        runtime.RegisterSession(second, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var scheduler = new QueuedUiScheduler();
        using var controller = new TrackingBrowserController();
        controller.RaiseStatusChanged(first, BrowserStatus("first"));
        controller.RaiseStatusChanged(second, BrowserStatus(sameTabId ? "first" : "second"));
        using var browser = new BrowserPaneViewModel(controller, scheduler);
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]), browserPane: browser);

        await workbench.OpenSessionAsync(first);

        var firstDocument = Assert.Single(workbench.Documents.VisibleItems);
        Assert.Equal(first.Value, firstDocument.OwnerId);
        await workbench.OpenSessionAsync(second);
        var secondDocument = Assert.Single(workbench.Documents.VisibleItems);
        Assert.Equal(second.Value, secondDocument.OwnerId);
        Assert.NotSame(firstDocument, secondDocument);
        Assert.Equal(second, Assert.IsType<BrowserPaneViewModel>(secondDocument.Content).ActiveSessionId);
        await workbench.OpenSessionAsync(first);
        Assert.Same(firstDocument, workbench.Documents.ActiveDocument);
        Assert.Equal(2, workbench.Documents.Items.Count);
        await WaitUntilAsync(() => workbench.GitPane is { IsLoading: false }, scheduler);
    }

    [Fact]
    public async Task BrowserActivity_LaterTabsAndStatusDoNotOverrideDocumentSelectionOrDismissal()
    {
        var sessionId = StrongId.NewSessionId();
        var runtime = new FakeAgentRuntime();
        runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var scheduler = new QueuedUiScheduler();
        using var controller = new TrackingBrowserController();
        using var browser = new BrowserPaneViewModel(controller, scheduler);
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]), browserPane: browser);
        await workbench.OpenSessionAsync(sessionId);
        controller.RaiseStatusChanged(sessionId, BrowserStatus("first"));
        scheduler.RunPending();
        var browserDocument = Assert.Single(workbench.Documents.VisibleItems);
        var other = workbench.OpenDocument(new DocumentViewModel("text", "text", "Notes", "", new object(),
            workbench.CurrentWorkspaceRoot, workbench.CurrentDocumentOwnerId));

        controller.RaiseStatusChanged(sessionId, BrowserStatus("first") with { State = BrowserSessionState.Busy });
        controller.RaiseStatusChanged(sessionId, BrowserStatus("second"));
        scheduler.RunPending();

        Assert.Same(other, workbench.Documents.ActiveDocument);
        Assert.Equal(2, workbench.Documents.Items.Count);
        workbench.Documents.Collapse();
        controller.RaiseStatusChanged(sessionId, BrowserStatus("third"));
        scheduler.RunPending();
        Assert.False(workbench.IsSidePaneVisible);
        workbench.Documents.Close(browserDocument);
        controller.RaiseStatusChanged(sessionId, new BrowserSessionStatus(BrowserSessionState.Idle, Tabs: []));
        controller.RaiseStatusChanged(sessionId, BrowserStatus("fourth"));
        scheduler.RunPending();
        Assert.Same(other, Assert.Single(workbench.Documents.Items));
        Assert.False(workbench.IsSidePaneVisible);
        await WaitUntilAsync(() => workbench.GitPane is { IsLoading: false }, scheduler);
    }

    [Fact]
    public async Task BrowserActivity_BackgroundAndStaleQueuedStatusCannotOpenWrongSessionDocument()
    {
        var first = StrongId.NewSessionId();
        var second = StrongId.NewSessionId();
        var runtime = new FakeAgentRuntime();
        runtime.RegisterSession(first, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        runtime.RegisterSession(second, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var scheduler = new QueuedUiScheduler();
        using var controller = new TrackingBrowserController();
        using var browser = new BrowserPaneViewModel(controller, scheduler);
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]), browserPane: browser);
        await workbench.OpenSessionAsync(first);
        controller.RaiseStatusChanged(first, BrowserStatus("first"));

        await workbench.OpenSessionAsync(second);
        scheduler.RunPending();
        controller.RaiseStatusChanged(first, BrowserStatus("background"));
        scheduler.RunPending();

        Assert.Empty(workbench.Documents.Items);
        Assert.False(workbench.IsSidePaneVisible);
        await workbench.OpenSessionAsync(first);
        Assert.Equal(first.Value, Assert.Single(workbench.Documents.VisibleItems).OwnerId);
        Assert.Equal("background", browser.ActiveTabId);
        workbench.ShowNewTask(_root);
        controller.RaiseStatusChanged(first, BrowserStatus("after-draft"));
        scheduler.RunPending();
        Assert.Empty(workbench.Documents.VisibleItems);
        Assert.False(workbench.IsSidePaneVisible);
        await WaitUntilAsync(() => workbench.GitPane is { IsLoading: false }, scheduler);
    }

    [Fact]
    public async Task BrowserActivity_DisposeReleasesAutomaticDocumentSubscription()
    {
        var sessionId = StrongId.NewSessionId();
        var runtime = new FakeAgentRuntime();
        runtime.RegisterSession(sessionId, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        var scheduler = new QueuedUiScheduler();
        using var controller = new TrackingBrowserController();
        using var browser = new BrowserPaneViewModel(controller, scheduler);
        using var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]), browserPane: browser);
        await workbench.OpenSessionAsync(sessionId);
        await WaitUntilAsync(() => workbench.GitPane is { IsLoading: false }, scheduler);
        controller.RaiseStatusChanged(sessionId, BrowserStatus("queued"));

        workbench.Dispose();
        scheduler.RunPending();
        browser.ActiveTabId = "after-disposal";
        controller.RaiseStatusChanged(sessionId, BrowserStatus("after-disposal"));
        scheduler.RunPending();

        Assert.Empty(workbench.Documents.Items);
        Assert.Equal(0, controller.SubscriberCount);
    }

    private static BrowserSessionStatus BrowserStatus(string tabId) => new(BrowserSessionState.Launched,
        Tabs: [new BrowserTabStatus(tabId, "https://example.com", "Example", IsActive: true)], ActiveTabId: tabId);

    [Fact]
    public async Task NewDraft_UnbindsLegacyBrowser_AndOpensUnboundPlaceholderPresentation()
    {
        await using var store = new SqliteSessionStore(DatabasePath);
        await store.InitializeAsync(CancellationToken.None);
        await using var runtime = CreateRuntime(store);
        var session = await runtime.CreateSessionAsync(new SessionOptions(_root), CancellationToken.None);
        using var controller = new TrackingBrowserController();
        using var browser = new BrowserPaneViewModel(controller, InlineUiScheduler.Instance, _ => new TrackingImage());
        using var workbench = new WorkbenchViewModel(
            runtime, InlineUiScheduler.Instance, new WorkbenchConfig([]), browserPane: browser);
        await workbench.OpenSessionAsync(session.Id);
        Assert.Equal(session.Id, browser.ActiveSessionId);
        Assert.True(browser.HasScreenshot);

        workbench.ShowNewTask(_root);
        workbench.ShowBrowserPaneCommand.Execute(null);

        var draftDocument = Assert.Single(workbench.Documents.VisibleItems);
        var draftView = Assert.IsType<BrowserPaneViewModel>(draftDocument.Content);
        Assert.Null(browser.ActiveSessionId);
        Assert.False(browser.HasScreenshot);
        Assert.Null(draftView.ActiveSessionId);
        Assert.False(draftView.CanInteract);
        Assert.False(draftView.HasScreenshot);
        Assert.StartsWith("draft:", draftDocument.OwnerId);
    }

    [Fact]
    public void ShowBrowserPane_WithoutControllerOpensGenericPlaceholderDocument()
    {
        using var workbench = new WorkbenchViewModel(
            new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]));

        workbench.ShowBrowserPaneCommand.Execute(null);

        var document = Assert.Single(workbench.Documents.VisibleItems);
        Assert.Equal("browser", document.Kind);
        Assert.Equal(workbench.CurrentDocumentOwnerId, document.OwnerId);
        Assert.Contains("not available", Assert.IsType<string>(document.Content));
        Assert.True(workbench.IsSidePaneVisible);
    }

    [Fact]
    public void Dispose_DisposesBrowserPaneAndReleasesItsStatusSubscriptionAndScreenshot()
    {
        using var controller = new TrackingBrowserController();
        var image = new TrackingImage();
        using var browserPane = new BrowserPaneViewModel(controller, InlineUiScheduler.Instance, _ => image);
        using var workbench = new WorkbenchViewModel(
            new FakeAgentRuntime(), InlineUiScheduler.Instance, new WorkbenchConfig([]), browserPane: browserPane);
        browserPane.Bind(StrongId.NewSessionId());
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

        public int DisposeCount { get; private set; }

        private readonly Dictionary<SessionId, BrowserSessionStatus> _statuses = [];

        public void RaiseStatusChanged(SessionId sessionId, BrowserSessionStatus status)
        {
            _statuses[sessionId] = status;
            StatusChanged?.Invoke(this, new BrowserStatusChangedEventArgs(sessionId, status));
        }

        public BrowserSessionStatus? GetStatus(SessionId sessionId) => _statuses.GetValueOrDefault(sessionId) ?? new(
            BrowserSessionState.Launched, LatestScreenshotUri: "screenshot.png", UpdatedAt: DateTimeOffset.UtcNow);

        public Task<BrowserActionResult> ExecuteAsync(SessionId sessionId, BrowserAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAsync(SessionId sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task TakeoverAsync(SessionId sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose() => DisposeCount++;
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
