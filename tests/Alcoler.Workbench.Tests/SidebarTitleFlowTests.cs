using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Kernel.Runtime;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Xunit;

namespace Alcoler.Workbench.Tests;

public sealed class SidebarTitleFlowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("alcoler-sidebar-title-").FullName;

    [Fact]
    public void SummaryTitle_NotifiesTitleAndDisplayTitle()
    {
        var summary = new SessionSummaryViewModel(StrongId.NewSessionId(), "", _root, DateTimeOffset.UtcNow);
        var changed = new List<string?>();
        summary.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.Equal("Untitled session", summary.DisplayTitle);

        summary.Title = "Updated title";

        Assert.Equal("Updated title", summary.DisplayTitle);
        Assert.Equal([nameof(SessionSummaryViewModel.Title), nameof(SessionSummaryViewModel.DisplayTitle)], changed);
        changed.Clear();
        summary.Title = "Updated title";
        Assert.Empty(changed);
        summary.Title = " ";
        Assert.Equal("Untitled session", summary.DisplayTitle);
    }

    [Fact]
    public async Task TitleEvent_MarshalsInPlaceAndUpdatesActiveHeaderWithoutRefreshing()
    {
        var runtime = new TitleRuntime();
        var older = runtime.AddSession(_root, "Older", DateTimeOffset.UtcNow.AddHours(-1));
        runtime.AddSession(_root, "Newer", DateTimeOffset.UtcNow);
        var scheduler = new QueuedUiScheduler();
        using var workbench = CreateWorkbench(runtime, scheduler);
        await OpenSessionAsync(workbench, older.Id, scheduler);
        var active = workbench.Active!;
        var group = Assert.Single(workbench.Sidebar.Groups);
        var row = FindSession(workbench, older.Id);
        var order = group.Sessions.ToArray();
        group.IsExpanded = false;
        workbench.Sidebar.ApplyActivity(older.Id, new SessionActivitySnapshot(TurnPhase.Streaming, null));
        var requests = runtime.ListCount;
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        var callbackThread = 0;
        row.PropertyChanged += (_, _) => callbackThread = Environment.CurrentManagedThreadId;

        await Task.Run(() => runtime.PublishTitle(older.Id, "Live title"));

        Assert.Equal("Older", row.Title);
        Assert.Equal("Older", active.Title);
        var uiThread = Environment.CurrentManagedThreadId;
        scheduler.RunPending();
        Assert.Equal(uiThread, callbackThread);
        Assert.Same(group, Assert.Single(workbench.Sidebar.Groups));
        Assert.Same(row, FindSession(workbench, older.Id));
        Assert.Equal(order, group.Sessions.ToArray());
        Assert.False(group.IsExpanded);
        Assert.Equal(older.UpdatedAt, row.UpdatedAt);
        Assert.True(row.IsActive);
        Assert.True(row.IsRunning);
        Assert.Equal("Live title", row.DisplayTitle);
        Assert.Equal("Live title", active.Title);
        Assert.Equal("Live title", active.HeaderTitle);
        Assert.Equal([nameof(SessionSummaryViewModel.Title), nameof(SessionSummaryViewModel.DisplayTitle)], changed);
        Assert.Equal(requests, runtime.ListCount);
    }

    [Theory]
    [InlineData(TurnOutcome.Completed, SessionActivityIndicator.Completed)]
    [InlineData(TurnOutcome.Failed, SessionActivityIndicator.Failed)]
    public async Task InactiveTitleEvent_PreservesUnreadActivityAndDoesNotChangeActiveHeader(
        TurnOutcome outcome, SessionActivityIndicator expectedIndicator)
    {
        var runtime = new TitleRuntime();
        var watched = runtime.AddSession(_root, "Watched", DateTimeOffset.UtcNow.AddMinutes(-1));
        var current = runtime.AddSession(_root, "Current", DateTimeOffset.UtcNow);
        var scheduler = new QueuedUiScheduler();
        using var workbench = CreateWorkbench(runtime, scheduler);
        await OpenSessionAsync(workbench, current.Id, scheduler);
        var watchedRow = FindSession(workbench, watched.Id);
        workbench.Sidebar.ApplyActivity(watched.Id, new SessionActivitySnapshot(TurnPhase.Idle, outcome));
        var requests = runtime.ListCount;

        runtime.PublishTitle(watched.Id, "Background title");
        runtime.PublishTitle(StrongId.NewSessionId(), "Not listed");
        scheduler.RunPending();

        Assert.Same(watchedRow, FindSession(workbench, watched.Id));
        Assert.Equal("Background title", watchedRow.Title);
        Assert.Equal(expectedIndicator, watchedRow.Indicator);
        Assert.False(watchedRow.IsActive);
        Assert.Equal(current.Id, workbench.Sidebar.ActiveSessionId);
        Assert.Equal("Current", workbench.Active!.Title);
        Assert.Equal(2, workbench.Sidebar.Groups.SelectMany(group => group.Sessions).Count());
        Assert.Equal(requests, runtime.ListCount);
    }

    [Fact]
    public async Task OverlappingRefreshes_DoNotOverwriteTitlesPublishedAfterEachRequestStarts()
    {
        var runtime = new TitleRuntime();
        var initial = runtime.AddSession(_root, "Initial", DateTimeOffset.UtcNow);
        var scheduler = new QueuedUiScheduler();
        using var workbench = CreateWorkbench(runtime, scheduler);
        await workbench.Sidebar.RefreshAsync();
        var firstResult = runtime.DelayNextList();
        var firstRefresh = workbench.RefreshFromRuntimeAsync();
        runtime.PublishTitle(initial.Id, "Fallback");
        scheduler.RunPending();
        var secondResult = runtime.DelayNextList();
        var secondRefresh = workbench.RefreshFromRuntimeAsync();
        runtime.PublishTitle(initial.Id, "Generated");
        scheduler.RunPending();
        runtime.PublishTitle(initial.Id, "Manual");
        scheduler.RunPending();

        secondResult.SetResult([initial with { Title = "Fallback" }]);
        await secondRefresh;
        Assert.Equal("Manual", FindSession(workbench, initial.Id).Title);
        firstResult.SetResult([initial]);
        await firstRefresh;
        Assert.Equal("Manual", FindSession(workbench, initial.Id).Title);

        runtime.SetStoredTitle(initial.Id, "Later durable title");
        await workbench.RefreshFromRuntimeAsync();
        Assert.Equal("Later durable title", FindSession(workbench, initial.Id).Title);
    }

    [Fact]
    public async Task TitleEvent_DuringInitialRefreshReachesRowsThatAreNotListedYet()
    {
        var runtime = new TitleRuntime();
        var initial = runtime.AddSession(_root, "Initial", DateTimeOffset.UtcNow);
        var scheduler = new QueuedUiScheduler();
        using var workbench = CreateWorkbench(runtime, scheduler);
        var result = runtime.DelayNextList();
        var refresh = workbench.RefreshFromRuntimeAsync();

        runtime.PublishTitle(initial.Id, "Live title");
        scheduler.RunPending();
        Assert.Empty(workbench.Sidebar.Groups);
        result.SetResult([initial]);
        await refresh;

        Assert.Equal("Live title", FindSession(workbench, initial.Id).Title);
    }

    [Fact]
    public async Task FailedRefresh_DoesNotRetainTitleOverridesForLaterRequests()
    {
        var runtime = new TitleRuntime();
        var initial = runtime.AddSession(_root, "Initial", DateTimeOffset.UtcNow);
        var scheduler = new QueuedUiScheduler();
        using var workbench = CreateWorkbench(runtime, scheduler);
        await workbench.Sidebar.RefreshAsync();
        var result = runtime.DelayNextList();
        var refresh = workbench.RefreshFromRuntimeAsync();
        runtime.PublishTitle(initial.Id, "Live title");
        scheduler.RunPending();
        result.SetException(new IOException("List unavailable"));

        await Assert.ThrowsAsync<IOException>(() => refresh);
        Assert.Equal("Live title", FindSession(workbench, initial.Id).Title);
        runtime.SetStoredTitle(initial.Id, "Later durable title");
        await workbench.RefreshFromRuntimeAsync();
        Assert.Equal("Later durable title", FindSession(workbench, initial.Id).Title);
    }

    [Fact]
    public async Task Dispose_UnsubscribesAndIgnoresQueuedTitleEventsAndPendingRefresh()
    {
        var runtime = new TitleRuntime();
        var initial = runtime.AddSession(_root, "Initial", DateTimeOffset.UtcNow);
        var scheduler = new QueuedUiScheduler();
        using var workbench = CreateWorkbench(runtime, scheduler);
        await OpenSessionAsync(workbench, initial.Id, scheduler);
        Assert.Equal(1, runtime.TitleSubscriberCount);
        var active = workbench.Active!;
        var group = Assert.Single(workbench.Sidebar.Groups);
        var row = FindSession(workbench, initial.Id);
        var result = runtime.DelayNextList();
        var refresh = workbench.RefreshFromRuntimeAsync();
        runtime.PublishTitle(initial.Id, "Queued before dispose");

        workbench.Dispose();
        Assert.Equal(0, runtime.TitleSubscriberCount);
        scheduler.RunPending();
        runtime.PublishTitle(initial.Id, "After dispose");
        result.SetResult([initial with { Title = "Stale refresh" }]);
        await refresh;
        scheduler.RunPending();

        Assert.Same(group, Assert.Single(workbench.Sidebar.Groups));
        Assert.Same(row, FindSession(workbench, initial.Id));
        Assert.Equal("Initial", row.Title);
        Assert.Equal("Initial", active.Title);
    }

    [Fact]
    public async Task ManualRename_WithoutTitleEventStillRefreshesSidebar()
    {
        var runtime = new TitleRuntime { PublishRenameEvent = false };
        var initial = runtime.AddSession(_root, "Initial", DateTimeOffset.UtcNow);
        var scheduler = new QueuedUiScheduler();
        using var workbench = CreateWorkbench(runtime, scheduler);
        await OpenSessionAsync(workbench, initial.Id, scheduler);
        var requests = runtime.ListCount;

        await workbench.RenameActiveAsync("Manual title");

        Assert.Equal(requests + 1, runtime.ListCount);
        Assert.Equal("Manual title", FindSession(workbench, initial.Id).Title);
    }

    [Fact]
    public async Task RealRuntime_FirstInputFallbackAndManualRenameReachExistingSidebarRows()
    {
        await using var store = new SqliteSessionStore(Path.Combine(_root, "sessions.sqlite"));
        await store.InitializeAsync(CancellationToken.None);
        var artifacts = new FileArtifactStore(Path.Combine(_root, "artifacts"));
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(new SequencedChatModel([
                new ScriptStep.Events(new ModelTextDelta("Done"), new ModelStreamFinished(ModelFinishReason.Stop))]))],
            Tools = [],
            WorkspaceFactory = root => new WorkspaceToolEnvironment(root, artifacts),
            DefaultModel = new ModelSelection("scripted", "scripted-model"),
        });
        var watched = await runtime.CreateSessionAsync(new SessionOptions(_root), CancellationToken.None);
        var current = await runtime.CreateSessionAsync(new SessionOptions(_root, "Current"), CancellationToken.None);
        var scheduler = new QueuedUiScheduler();
        using var workbench = CreateWorkbench(runtime, scheduler);
        await OpenSessionAsync(workbench, current.Id, scheduler);
        var group = Assert.Single(workbench.Sidebar.Groups);
        var row = FindSession(workbench, watched.Id);
        var updatedAt = row.UpdatedAt;
        group.IsExpanded = false;
        const string firstInput = "Fix sidebar title updates";

        var ack = await runtime.SubmitAsync(
            new SendTextCommand(StrongId.NewCommandId(), watched.Id, 0, firstInput), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        await WaitUntilAsync(() => row.Title == firstInput, scheduler);

        Assert.Same(group, Assert.Single(workbench.Sidebar.Groups));
        Assert.Same(row, FindSession(workbench, watched.Id));
        Assert.False(group.IsExpanded);
        Assert.Equal(updatedAt, row.UpdatedAt);
        Assert.Equal("Current", workbench.Active!.Title);
        ack = await runtime.SubmitAsync(
            new RenameSessionCommand(StrongId.NewCommandId(), current.Id, 0, "Manual current title"), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        scheduler.RunPending();
        Assert.Equal("Manual current title", FindSession(workbench, current.Id).Title);
        Assert.Equal("Manual current title", workbench.Active.Title);
        Assert.Same(row, FindSession(workbench, watched.Id));
    }

    [Fact]
    public async Task RealRuntime_GeneratedTitleUpdatesInactiveRowInPlaceAndSurvivesRestart()
    {
        const string firstInput = "Fix sidebar title updates while another session is active";
        const string generatedTitle = "Live Sidebar Title Updates";
        var databasePath = Path.Combine(_root, "sessions.sqlite");
        var artifacts = new FileArtifactStore(Path.Combine(_root, "artifacts"));
        var model = new GatedTitleChatModel();
        AgentRuntime CreateRuntime(SqliteSessionStore store) => new(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(model)],
            Tools = [],
            WorkspaceFactory = root => new WorkspaceToolEnvironment(root, artifacts),
            DefaultModel = new ModelSelection("scripted", "scripted-model"),
            SessionTitleGenerationEnabled = true,
        });
        SessionId watchedId;
        await using (var store = new SqliteSessionStore(databasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await using var runtime = CreateRuntime(store);
            var watched = await runtime.CreateSessionAsync(new SessionOptions(_root), CancellationToken.None);
            watchedId = watched.Id;
            var current = await runtime.CreateSessionAsync(new SessionOptions(_root, "Current"), CancellationToken.None);
            var scheduler = new QueuedUiScheduler();
            using var workbench = CreateWorkbench(runtime, scheduler);
            await OpenSessionAsync(workbench, current.Id, scheduler);
            var group = Assert.Single(workbench.Sidebar.Groups);
            var row = FindSession(workbench, watched.Id);
            var order = group.Sessions.ToArray();
            var updatedAt = row.UpdatedAt;
            group.IsExpanded = false;

            var ack = await runtime.SubmitAsync(
                new SendTextCommand(StrongId.NewCommandId(), watched.Id, 0, firstInput), CancellationToken.None);
            Assert.True(ack.Accepted, ack.RejectionReason);
            var namingRequest = await model.TitleRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(namingRequest.Tools);
            Assert.Equal(firstInput, Assert.IsType<InputTextBlock>(Assert.Single(
                Assert.Single(namingRequest.Messages, message => message.Role == MessageRole.User).Content)).Text);
            await WaitUntilAsync(() => row.Title == firstInput && row.IsCompleted, scheduler);

            Assert.False(model.TitleResult.Task.IsCompleted);
            Assert.Same(row, FindSession(workbench, watched.Id));
            Assert.False(row.IsActive);
            Assert.Equal(current.Id, workbench.Sidebar.ActiveSessionId);
            Assert.Equal("Current", workbench.Active!.Title);
            model.TitleResult.SetResult("""{"title":"Live Sidebar Title Updates"}""");
            await WaitUntilAsync(() => row.Title == generatedTitle, scheduler);

            Assert.Same(group, Assert.Single(workbench.Sidebar.Groups));
            Assert.Same(row, FindSession(workbench, watched.Id));
            Assert.Equal(order, group.Sessions.ToArray());
            Assert.False(group.IsExpanded);
            Assert.Equal(updatedAt, row.UpdatedAt);
            Assert.Equal(generatedTitle, row.DisplayTitle);
            Assert.False(row.IsActive);
            Assert.True(row.IsCompleted);
            Assert.Equal(current.Id, workbench.Active.SessionId);
            Assert.Equal(current.Id, workbench.Sidebar.ActiveSessionId);
            Assert.Equal("Current", workbench.Active.Title);
            var persisted = await store.GetSessionAsync(watched.Id, CancellationToken.None);
            Assert.Equal(generatedTitle, persisted!.Title);
            Assert.Equal(SessionTitleSource.Generated, persisted.TitleSource);
        }

        await using var reopenedStore = new SqliteSessionStore(databasePath);
        await reopenedStore.InitializeAsync(CancellationToken.None);
        await using var reopenedRuntime = CreateRuntime(reopenedStore);
        var reopenedScheduler = new QueuedUiScheduler();
        using var restarted = CreateWorkbench(reopenedRuntime, reopenedScheduler);
        await restarted.Sidebar.RefreshAsync();

        Assert.Equal(generatedTitle, FindSession(restarted, watchedId).Title);
        Assert.False(FindSession(restarted, watchedId).IsActive);
        Assert.Null(reopenedRuntime.TryObserve(watchedId));
    }

    private WorkbenchViewModel CreateWorkbench(IAgentRuntime runtime, QueuedUiScheduler scheduler)
    {
        var workbench = new WorkbenchViewModel(runtime, scheduler, new WorkbenchConfig([]));
        workbench.FocusWorkspace(_root);
        return workbench;
    }

    private static async Task OpenSessionAsync(WorkbenchViewModel workbench, SessionId sessionId, QueuedUiScheduler scheduler)
    {
        await workbench.OpenSessionAsync(sessionId);
        await WaitUntilAsync(() => workbench.Active!.Title == FindSession(workbench, sessionId).Title
            && workbench.GitPane is { IsLoading: false }, scheduler);
    }

    private static SessionSummaryViewModel FindSession(WorkbenchViewModel workbench, SessionId id) =>
        workbench.Sidebar.Groups.SelectMany(group => group.Sessions).Single(session => session.Id == id);

    private static async Task WaitUntilAsync(Func<bool> condition, QueuedUiScheduler scheduler)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            scheduler.RunPending();
            if (condition())
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, "Title update did not arrive within 10 seconds.");
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class GatedTitleChatModel : IChatModel
    {
        public ModelDescriptor Descriptor { get; } = new(
            "scripted", "scripted-model", "Gated title model",
            new ModelProperties(ContextWindowTokens: 200_000, DefaultMaxOutputTokens: 32_768));

        public TaskCompletionSource<ModelRequest> TitleRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> TitleResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (request.Messages.Where(message => message.Role == MessageRole.System)
                .SelectMany(message => message.Content).OfType<InputTextBlock>()
                .Any(block => block.Text.Contains("This is a title-generation task, not a conversation.", StringComparison.Ordinal)))
            {
                TitleRequest.TrySetResult(request);
                var title = await TitleResult.Task.WaitAsync(cancellationToken);
                yield return new ModelTextDelta(title);
            }
            else
            {
                yield return new ModelTextDelta("Done");
            }
            yield return new ModelStreamFinished(ModelFinishReason.Stop);
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

    private sealed class TitleRuntime : IAgentRuntime
    {
        private readonly Dictionary<SessionId, FakeConversationProjection> _projections = [];
        private readonly Dictionary<SessionId, SessionSummary> _summaries = [];
        private readonly Queue<Task<IReadOnlyList<SessionSummary>>> _pendingLists = [];

        public event Action<SessionId, string>? SessionTitleChanged;

        public int TitleSubscriberCount => SessionTitleChanged?.GetInvocationList().Length ?? 0;

        public int ListCount { get; private set; }

        public bool PublishRenameEvent { get; init; } = true;

        public SessionSummary AddSession(string root, string title, DateTimeOffset updatedAt)
        {
            var summary = new SessionSummary(StrongId.NewSessionId(), title, root, PermissionMode.Build, null, updatedAt, updatedAt);
            _summaries[summary.Id] = summary;
            _projections[summary.Id] = new FakeConversationProjection(
                new ConversationSnapshot(0, 0, [], TestSnapshots.State() with { Title = title }));
            return summary;
        }

        public void SetStoredTitle(SessionId sessionId, string title) =>
            _summaries[sessionId] = _summaries[sessionId] with { Title = title };

        public void PublishTitle(SessionId sessionId, string title)
        {
            if (_summaries.ContainsKey(sessionId))
            {
                SetStoredTitle(sessionId, title);
            }
            SessionTitleChanged?.Invoke(sessionId, title);
        }

        public TaskCompletionSource<IReadOnlyList<SessionSummary>> DelayNextList()
        {
            var result = new TaskCompletionSource<IReadOnlyList<SessionSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingLists.Enqueue(result.Task);
            return result;
        }

        public ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken)
        {
            ListCount++;
            return _pendingLists.TryDequeue(out var pending)
                ? new ValueTask<IReadOnlyList<SessionSummary>>(pending)
                : ValueTask.FromResult<IReadOnlyList<SessionSummary>>(_summaries.Values.ToArray());
        }

        public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken)
        {
            if (command is RenameSessionCommand rename)
            {
                SetStoredTitle(rename.SessionId, rename.Title);
                if (PublishRenameEvent)
                {
                    SessionTitleChanged?.Invoke(rename.SessionId, rename.Title);
                }
            }
            return ValueTask.FromResult(CommandAck.AcceptedAt(command.CommandId, 1));
        }

        public IConversationProjection Subscribe(SessionId sessionId) => _projections[sessionId];

        public IConversationProjection? TryObserve(SessionId sessionId) => _projections.GetValueOrDefault(sessionId);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
