using System.Collections.Concurrent;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Sessions;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;
using Xunit;

namespace Alcoler.Workbench.Tests;

public sealed class DraftConversationLifecycleTests
{
    private readonly QueuedUiScheduler _scheduler = new();

    [Fact]
    public async Task ProjectionScheduler_DefersBackgroundCallbacksUntilDrained()
    {
        var callbackThread = 0;
        await Task.Run(() => _scheduler.Post(() => callbackThread = Environment.CurrentManagedThreadId));

        Assert.Equal(0, callbackThread);
        Assert.True(_scheduler.HasPending);
        var ownerThread = Environment.CurrentManagedThreadId;
        _scheduler.RunPending();
        Assert.Equal(ownerThread, callbackThread);
        Assert.False(_scheduler.HasPending);
    }

    [Fact]
    public void NewTask_DoesNotCreateOrObserveASession_AndRestoresWorkspaceDrafts()
    {
        var runtime = new DraftRuntime();
        using var workbench = Create(runtime);
        var firstRoot = Path.Combine(Path.GetTempPath(), "alcoler-draft-first");
        var secondRoot = Path.Combine(Path.GetTempPath(), "alcoler-draft-second");
        workbench.ShowNewTask(firstRoot);
        var first = Assert.IsType<DraftConversationViewModel>(workbench.Draft);
        first.Composer.Draft = "Unsent first project message";
        first.Composer.Mode = PermissionMode.Plan;
        first.ThoughtLevel = "low";

        workbench.ShowNewTask(secondRoot);
        workbench.Draft!.Composer.Draft = "Other project";
        workbench.ShowNewTask(firstRoot);

        Assert.Same(first, workbench.Draft);
        Assert.Equal("Unsent first project message", workbench.Draft!.Composer.Draft);
        Assert.Equal(PermissionMode.Plan, workbench.Draft.Composer.Mode);
        Assert.Equal("low", workbench.Draft.ThoughtLevel);
        Assert.Null(workbench.Active);
        Assert.Empty(runtime.Created);
        Assert.Equal(0, runtime.SubscribeCount);
        Assert.Empty(runtime.Submitted);
    }

    [Fact]
    public void NewTask_DefaultsToMostRecentProjectBeforeAlphabeticalOrder()
    {
        using var workbench = Create(new DraftRuntime());
        var oldRoot = Path.Combine(Path.GetTempPath(), "aaa-old-project");
        var recentRoot = Path.Combine(Path.GetTempPath(), "zzz-recent-project");
        workbench.Sidebar.ApplySummaries([
            new SessionSummaryViewModel(StrongId.NewSessionId(), "Old", oldRoot, DateTimeOffset.UtcNow.AddDays(-1)),
            new SessionSummaryViewModel(StrongId.NewSessionId(), "Recent", recentRoot, DateTimeOffset.UtcNow),
        ]);

        workbench.ShowNewTask();

        Assert.Equal(recentRoot, workbench.Draft!.WorkspaceRoot);
        workbench.FocusWorkspace(oldRoot);
        workbench.ShowNewTask();
        Assert.Equal(oldRoot, workbench.Draft!.WorkspaceRoot);
    }

    [Fact]
    public async Task WorkspaceDraft_RestoresSelectedModelAndReasoningIndependently()
    {
        using var workbench = Create(new DraftRuntime());
        var original = workbench.Draft!;
        var choice = new ModelChoice("another-provider", "another-model", "Another model",
            new ModelProperties(ReasoningLevels: ["off", "medium", "high"]));
        original.ConfigureModel(choice);
        await original.SwitchModelAsync(choice.Selection);
        original.ThoughtLevel = "medium";
        workbench.ShowNewTask(Path.Combine(Path.GetTempPath(), "alcoler-draft-model-second"));
        Assert.Equal("custom/test-model", workbench.Draft!.ModelText);

        workbench.ShowNewTask(original.WorkspaceRoot);

        Assert.Same(original, workbench.Draft);
        Assert.Equal("another-provider/another-model", workbench.Draft!.ModelText);
        Assert.Equal("medium", workbench.Draft.Selection!.Options!.ReasoningLevel);
    }

    [Fact]
    public async Task FirstSend_SnapshotsSettings_CreatesOnce_AndPreservesNewerEdits()
    {
        var createGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new DraftRuntime { CreateGate = createGate.Task };
        using var workbench = Create(runtime);
        var draft = workbench.Draft!;
        draft.Composer.Draft = "Captured first prompt";
        draft.Composer.Mode = PermissionMode.Plan;
        draft.ThoughtLevel = "low";

        var sending = draft.SendAsync();
        Assert.True(draft.Composer.IsSending);
        await draft.SendAsync();
        draft.Composer.Draft = "Newer text";
        draft.Composer.Mode = PermissionMode.Edit;
        draft.ThoughtLevel = "high";
        createGate.SetResult();
        await sending;
        await ApplyInitialProjectionAsync();

        var created = Assert.Single(runtime.Created);
        Assert.Equal(PermissionMode.Plan, created.PermissionMode);
        Assert.Equal("low", created.ModelSelection!.Options!.ReasoningLevel);
        var sent = Assert.IsType<SendTextCommand>(Assert.Single(runtime.Submitted));
        Assert.Equal("Captured first prompt", sent.Text);
        Assert.Equal(PermissionMode.Plan, sent.Mode);
        Assert.Equal(created.ModelSelection, sent.ModelSelection);
        Assert.Equal(runtime.LastSessionId, workbench.Active!.SessionId);
        Assert.Same(draft.Composer, workbench.Active.Composer);
        Assert.Equal("Newer text", workbench.Active.Composer.Draft);
        Assert.Equal(PermissionMode.Edit, workbench.Active.Composer.Mode);
        Assert.False(draft.Composer.IsSending);
    }

    [Fact]
    public async Task FirstSend_HoldsSendLockAfterOpeningConversation()
    {
        var submitGate = new TaskCompletionSource<CommandAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new DraftRuntime { SubmitHandler = command => new ValueTask<CommandAck>(submitGate.Task) };
        using var workbench = Create(runtime);
        var draft = workbench.Draft!;
        draft.Composer.Draft = "Only send once";

        var sending = draft.SendAsync();
        Assert.NotNull(workbench.Active);
        Assert.True(workbench.Active!.Composer.IsSending);
        await ApplyInitialProjectionAsync();
        Assert.Equal("Test session", workbench.Active.Title);
        Assert.True(workbench.Active.Composer.IsSending);
        await workbench.Active.SendAsync();
        Assert.Single(runtime.Submitted);
        var command = Assert.Single(runtime.Submitted);
        submitGate.SetResult(CommandAck.AcceptedAt(command.CommandId, 1));
        await sending;

        Assert.Empty(workbench.Active.Composer.Draft);
        Assert.Single(runtime.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstSend_RejectionOrAmbiguousFailure_PreservesSessionOriginalTextAndNewerEdits(bool throws)
    {
        var submitGate = new TaskCompletionSource<CommandAck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new DraftRuntime { SubmitHandler = _ => new ValueTask<CommandAck>(submitGate.Task) };
        using var workbench = Create(runtime);
        var draft = workbench.Draft!;
        draft.Composer.Draft = "Original captured prompt";
        var sending = draft.SendAsync();
        await ApplyInitialProjectionAsync();
        workbench.Active!.Composer.Draft = "Newer draft edit";
        if (throws)
        {
            submitGate.SetException(new IOException("Disconnected after submission"));
        }
        else
        {
            submitGate.SetResult(CommandAck.Rejected(runtime.Submitted[0].CommandId, 1, "Rejected for test"));
        }
        await sending;
        var sessionId = workbench.Active.SessionId;

        Assert.Equal("Newer draft edit", workbench.Active.Composer.Draft);
        Assert.Equal("Original captured prompt", workbench.UnsentText);
        Assert.Equal("Original captured prompt", draft.UnsentText);
        Assert.NotNull(workbench.Banner);
        Assert.Single(runtime.Created);
        Assert.Single(runtime.Submitted);
        workbench.ShowNewTask();
        Assert.NotSame(draft, workbench.Draft);
        workbench.ClearBanner();
        await workbench.OpenSessionAsync(sessionId);
        _scheduler.RunPending();
        Assert.Equal("Original captured prompt", workbench.UnsentText);
        Assert.Equal("Newer draft edit", workbench.Active!.Composer.Draft);
        Assert.Single(runtime.Created);
        Assert.Single(runtime.Submitted);
    }

    [Fact]
    public async Task FirstSend_TransfersDraftDocumentsAndScopeState_WithoutSendingTwice()
    {
        var runtime = new DraftRuntime();
        using var workbench = Create(runtime);
        var draft = workbench.Draft!;
        var oldOwner = workbench.CurrentDocumentOwnerId;
        var content = new object();
        var document = workbench.OpenDocument(new DocumentViewModel("custom", "extension.custom", "Custom", "",
            content, workbench.CurrentWorkspaceRoot, oldOwner));
        workbench.Documents.Collapse();
        draft.Composer.Draft = "Send once with a document";

        await draft.SendAsync();
        await ApplyInitialProjectionAsync();

        Assert.Equal(runtime.LastSessionId.Value, document.OwnerId);
        Assert.Equal(workbench.CurrentDocumentOwnerId, document.OwnerId);
        Assert.Same(content, document.Content);
        Assert.Same(document, workbench.Documents.ActiveDocument);
        Assert.False(workbench.Documents.IsVisible);
        Assert.Single(runtime.Created);
        Assert.Single(runtime.Submitted);
        workbench.ShowNewTask();
        Assert.NotEqual(oldOwner, workbench.CurrentDocumentOwnerId);
        Assert.Empty(workbench.Documents.VisibleItems);
        await workbench.OpenSessionAsync(runtime.LastSessionId);
        _scheduler.RunPending();
        Assert.Same(document, workbench.Documents.ActiveDocument);
    }

    [Fact]
    public async Task CreateFailure_KeepsDraftWithoutSubmitting()
    {
        var runtime = new DraftRuntime { CreateFailure = new IOException("Storage unavailable") };
        using var workbench = Create(runtime);
        var draft = workbench.Draft!;
        draft.Composer.Draft = "Keep this prompt";

        await draft.SendAsync();

        Assert.Same(draft, workbench.Draft);
        Assert.Null(workbench.Active);
        Assert.Equal("Keep this prompt", draft.Composer.Draft);
        Assert.False(draft.Composer.IsSending);
        Assert.Contains("Storage unavailable", workbench.Banner);
        Assert.Empty(runtime.Submitted);
    }

    [Fact]
    public async Task NavigationDuringCreate_DoesNotStealFocusOrClearAnotherWorkspaceDraft()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new DraftRuntime { CreateGate = gate.Task };
        using var workbench = Create(runtime);
        var original = workbench.Draft!;
        original.Composer.Draft = "First workspace prompt";
        var sending = original.SendAsync();
        var otherRoot = Path.Combine(Path.GetTempPath(), "alcoler-draft-other");
        workbench.ShowNewTask(otherRoot);
        var other = workbench.Draft!;
        other.Composer.Draft = "Other workspace text";
        gate.SetResult();
        await sending;

        Assert.Same(other, workbench.Draft);
        Assert.Null(workbench.Active);
        Assert.Equal("Other workspace text", other.Composer.Draft);
        Assert.Single(runtime.Created);
        Assert.Single(runtime.Submitted);
    }

    [Fact]
    public async Task SwitchingSessionAndDraft_RestoresUnsentComposerWithoutRetainingTimelineViewModel()
    {
        var runtime = new DraftRuntime();
        using var workbench = Create(runtime);
        var handle = await runtime.CreateSessionAsync(new SessionOptions(Environment.CurrentDirectory), CancellationToken.None);
        await workbench.OpenSessionAsync(handle.Id);
        await ApplyInitialProjectionAsync();
        var original = workbench.Active!;
        original.Composer.Draft = "Session draft";
        original.Composer.Mode = PermissionMode.Plan;
        original.RestoreComposerState("Session draft", PermissionMode.Plan,
            new ModelSelection("custom", "test-model", new ModelCallOptions(ReasoningLevel: "low")));
        workbench.ShowNewTask();
        var draft = workbench.Draft!;
        draft.Composer.Draft = "Workspace draft";

        await workbench.OpenSessionAsync(handle.Id);
        _scheduler.RunPending();

        Assert.NotSame(original, workbench.Active);
        Assert.Same(original.Composer, workbench.Active!.Composer);
        Assert.Equal("Session draft", workbench.Active.Composer.Draft);
        Assert.Equal(PermissionMode.Plan, workbench.Active.Composer.Mode);
        Assert.Equal("low", workbench.Active.ThoughtLevel);
        workbench.ShowNewTask();
        Assert.Same(draft, workbench.Draft);
        Assert.Equal("Workspace draft", workbench.Draft!.Composer.Draft);
    }

    [Theory]
    [InlineData(PermissionMode.Build)]
    [InlineData(PermissionMode.Edit)]
    [InlineData(PermissionMode.Plan)]
    [InlineData(PermissionMode.Yolo)]
    public async Task SelectedMode_IsSavedImmediately_AndRestoredForFreshConversations(PermissionMode mode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"alcoler-composer-{Guid.NewGuid():N}.json");
        try
        {
            var config = new WorkbenchConfig([], Composer: new WorkbenchComposerConfig(
                mode == PermissionMode.Build ? PermissionMode.Plan : PermissionMode.Build));
            config.Save(path);
            var runtime = new DraftRuntime();
            using (var workbench = new WorkbenchViewModel(runtime, _scheduler, config,
                saveComposer: composer => (WorkbenchConfig.LoadOrDefault(path) with { Composer = composer }).Save(path)))
            {
                workbench.Draft!.Composer.Mode = mode;
                Assert.Equal(mode, WorkbenchConfig.LoadOrDefault(path).Composer!.DefaultMode);
                Assert.Empty(runtime.Created);
                Assert.Empty(runtime.Submitted);
                workbench.Draft.Composer.Draft = "First prompt";
                await workbench.Draft.SendAsync();
                await ApplyInitialProjectionAsync();
                Assert.Equal(mode, Assert.Single(runtime.Created).PermissionMode);
                workbench.ShowNewTask();
                Assert.Equal(mode, workbench.Draft!.Composer.Mode);
            }
            using var restarted = new WorkbenchViewModel(new DraftRuntime(), _scheduler, WorkbenchConfig.LoadOrDefault(path));
            Assert.Equal(mode, restarted.Draft!.Composer.Mode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DirectSessionCreation_UsesRememberedMode()
    {
        var runtime = new DraftRuntime();
        using var workbench = new WorkbenchViewModel(runtime, _scheduler,
            new WorkbenchConfig([], Composer: new WorkbenchComposerConfig(PermissionMode.Plan)));

        await workbench.NewSessionAsync(Environment.CurrentDirectory);
        await ApplyInitialProjectionAsync();

        Assert.Equal(PermissionMode.Plan, Assert.Single(runtime.Created).PermissionMode);
        Assert.Equal(PermissionMode.Plan, workbench.Active!.Composer.Mode);
    }

    [Fact]
    public async Task UnsentSessionModes_SurviveRestartIndependently_WithoutChangingRuntimePolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alcoler-composer-{Guid.NewGuid():N}.json");
        try
        {
            var first = StrongId.NewSessionId();
            var second = StrongId.NewSessionId();
            var model = new ModelSelection("provider", "restored-model", new ModelCallOptions(ReasoningLevel: "high"));
            var runtime = new FakeAgentRuntime();
            runtime.RegisterSession(first, new ConversationSnapshot(0, 1, [], TestSnapshots.State(modelSelection: model)));
            runtime.RegisterSession(second, new ConversationSnapshot(0, 1, [], TestSnapshots.State() with { Mode = PermissionMode.Edit }));
            var config = new WorkbenchConfig([]);
            config.Save(path);
            using (var workbench = new WorkbenchViewModel(runtime, _scheduler, config,
                saveComposer: composer => (WorkbenchConfig.LoadOrDefault(path) with { Composer = composer }).Save(path)))
            {
                await workbench.OpenSessionAsync(first);
                await ApplyInitialProjectionAsync();
                workbench.Active!.Composer.Mode = PermissionMode.Plan;
                await workbench.OpenSessionAsync(second);
                await ApplyInitialProjectionAsync();
                Assert.Equal(PermissionMode.Edit, workbench.Active!.Composer.Mode);
                Assert.Equal(PermissionMode.Plan, WorkbenchConfig.LoadOrDefault(path).Composer!.DefaultMode);
                workbench.Active.Composer.Mode = PermissionMode.Yolo;
                Assert.Empty(runtime.Submitted);
            }
            using var restarted = new WorkbenchViewModel(runtime, _scheduler, WorkbenchConfig.LoadOrDefault(path));
            await restarted.OpenSessionAsync(first);
            await ApplyInitialProjectionAsync();
            Assert.Equal(PermissionMode.Plan, restarted.Active!.Composer.Mode);
            Assert.Equal(PermissionMode.Build, restarted.Active.Composer.SessionMode);
            Assert.Equal(model, restarted.Active.CurrentSelection);
            Assert.Equal("high", restarted.Active.ThoughtLevel);
            await restarted.OpenSessionAsync(second);
            await ApplyInitialProjectionAsync();
            Assert.Equal(PermissionMode.Yolo, restarted.Active!.Composer.Mode);
            Assert.Equal(PermissionMode.Edit, restarted.Active.Composer.SessionMode);
            Assert.Empty(runtime.Submitted);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmittedMode_ClearsPendingOverrideOnlyAfterAcceptance(bool rejected)
    {
        var runtime = new FakeAgentRuntime { RejectNextCommand = rejected };
        var id = StrongId.NewSessionId();
        runtime.RegisterSession(id, new ConversationSnapshot(0, 1, [], TestSnapshots.State()));
        WorkbenchComposerConfig? saved = null;
        using var workbench = new WorkbenchViewModel(runtime, _scheduler, new WorkbenchConfig([]),
            saveComposer: composer => saved = composer);
        await workbench.OpenSessionAsync(id);
        await ApplyInitialProjectionAsync();
        workbench.Active!.Composer.Mode = PermissionMode.Plan;
        workbench.Active.Composer.Draft = "Use the selected mode";

        await workbench.Active.SendAsync();

        Assert.Equal(PermissionMode.Plan, Assert.IsType<SendTextCommand>(Assert.Single(runtime.Submitted)).Mode);
        Assert.Equal(rejected, saved!.PendingSessionModes!.ContainsKey(id.Value));
        Assert.Equal(PermissionMode.Plan, saved.DefaultMode);
    }

    [Fact]
    public async Task FirstSend_PreservesNewerModeSelectionUntilItIsSubmitted()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new DraftRuntime { CreateGate = gate.Task };
        WorkbenchComposerConfig? saved = null;
        using var workbench = new WorkbenchViewModel(runtime, _scheduler, new WorkbenchConfig([]),
            saveComposer: composer => saved = composer);
        var draft = workbench.Draft!;
        draft.Composer.Mode = PermissionMode.Plan;
        draft.Composer.Draft = "Captured prompt";
        var sending = draft.SendAsync();
        draft.Composer.Mode = PermissionMode.Edit;
        gate.SetResult();
        await sending;
        await ApplyInitialProjectionAsync();

        Assert.Equal(PermissionMode.Edit, saved!.DefaultMode);
        Assert.Equal(PermissionMode.Edit, saved.PendingSessionModes![runtime.LastSessionId.Value]);
        Assert.Equal(PermissionMode.Edit, workbench.Active!.Composer.Mode);
        Assert.Equal(PermissionMode.Plan, Assert.IsType<SendTextCommand>(Assert.Single(runtime.Submitted)).Mode);
    }

    [Fact]
    public void InvalidSavedModes_DefaultSafelyToBuild()
    {
        using var workbench = new WorkbenchViewModel(new DraftRuntime(), _scheduler,
            new WorkbenchConfig([], Composer: new WorkbenchComposerConfig((PermissionMode)999)));

        Assert.Equal(PermissionMode.Build, workbench.Draft!.Composer.Mode);
    }

    [Fact]
    public void ModeSaveFailure_KeepsSelectionAndShowsError()
    {
        using var workbench = new WorkbenchViewModel(new DraftRuntime(), _scheduler, new WorkbenchConfig([]),
            saveComposer: _ => throw new IOException("Storage unavailable"));

        workbench.Draft!.Composer.Mode = PermissionMode.Plan;

        Assert.Equal(PermissionMode.Plan, workbench.Draft.Composer.Mode);
        Assert.Contains("Storage unavailable", workbench.Banner);
    }

    private WorkbenchViewModel Create(DraftRuntime runtime) => new(runtime, _scheduler,
        new WorkbenchConfig([
            new WorkbenchProviderConfig("openai-compatible", "https://example.test/v1", "key", [
                new WorkbenchModelConfig("test-model", ReasoningLevels: ["off", "low", "high"]),
            ], Id: "custom"),
        ], new WorkbenchDefaultModelConfig("custom", "test-model")));

    private async Task ApplyInitialProjectionAsync()
    {
        await ProjectionStoreTests.WaitUntil(() => _scheduler.HasPending);
        _scheduler.RunPending();
    }

    private sealed class QueuedUiScheduler : IUiScheduler
    {
        private readonly ConcurrentQueue<Action> _pending = new();

        public bool HasPending => !_pending.IsEmpty;

        public void Post(Action action) => _pending.Enqueue(action);

        public void RunPending()
        {
            while (_pending.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private sealed class DraftRuntime : IAgentRuntime
    {
        private readonly Dictionary<SessionId, FakeConversationProjection> _projections = [];
        private readonly List<SessionSummary> _summaries = [];

        public List<SessionOptions> Created { get; } = [];
        public List<ConversationCommand> Submitted { get; } = [];
        public Task? CreateGate { get; init; }
        public Exception? CreateFailure { get; init; }
        public Func<ConversationCommand, ValueTask<CommandAck>>? SubmitHandler { get; init; }
        public SessionId LastSessionId { get; private set; }
        public int SubscribeCount { get; private set; }

        public async ValueTask<SessionHandle> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken)
        {
            Created.Add(options);
            if (CreateGate is not null)
            {
                await CreateGate;
            }
            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }
            LastSessionId = StrongId.NewSessionId();
            var state = TestSnapshots.State(modelSelection: options.ModelSelection) with { Mode = options.PermissionMode };
            _projections[LastSessionId] = new FakeConversationProjection(new ConversationSnapshot(0, 1, [], state));
            _summaries.Add(new SessionSummary(LastSessionId, options.Title ?? "", options.WorkspaceRoot,
                options.PermissionMode, options.ModelSelection, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            return new SessionHandle(LastSessionId, options.WorkspaceRoot, options.Title ?? "");
        }

        public ValueTask<SessionHandle> ResumeSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(string? workspaceRoot, int limit, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SessionSummary>>(_summaries.ToArray());

        public ValueTask<CommandAck> SubmitAsync(ConversationCommand command, CancellationToken cancellationToken)
        {
            Submitted.Add(command);
            return SubmitHandler?.Invoke(command) ?? ValueTask.FromResult(CommandAck.AcceptedAt(command.CommandId, 1));
        }

        public IConversationProjection Subscribe(SessionId sessionId)
        {
            SubscribeCount++;
            return _projections[sessionId];
        }

        public IConversationProjection? TryObserve(SessionId sessionId) => _projections.GetValueOrDefault(sessionId);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
