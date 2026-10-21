using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Commands;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tests.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Runtime;

public class SessionShellTests
{
    [Fact]
    public void ShellEnvironment_JsonRoundTripStoresOnlySnapshotAndReadsLegacyRecords()
    {
        var shell = ShellSnapshotFixture.GitBash;
        var json = AlcolerJson.Serialize(shell);
        Assert.Equal(shell, AlcolerJson.Deserialize<ShellEnvironment>(json));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        Assert.Equal("GitBash", document.RootElement.GetProperty("dialect").GetString());
        Assert.Equal("Git Bash", shell.Name);
        Assert.True(shell.IsPosix);
        Assert.Equal("-c", shell.Arguments);
        Assert.Equal("cmd.exe", ShellSnapshotFixture.Cmd.Name);
        Assert.False(ShellSnapshotFixture.Cmd.IsPosix);
        Assert.Equal("/c", ShellSnapshotFixture.Cmd.Arguments);
        var posix = new ShellEnvironment(Path.Combine("bin", "sh"), ShellDialect.Posix);
        Assert.Equal("sh", posix.Name);

        var now = DateTimeOffset.UtcNow;
        var record = new SessionRecord(StrongId.NewSessionId(), "workspace", "title", PermissionMode.Build,
            null, now, now, Shell: shell);
        var recordJson = AlcolerJson.Serialize(record);
        Assert.Equal(record, AlcolerJson.Deserialize<SessionRecord>(recordJson));
        using var recordDocument = JsonDocument.Parse(recordJson);
        var legacy = "{" + string.Join(",", recordDocument.RootElement.EnumerateObject()
            .Where(property => property.Name != "shell")
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText())) + "}";
        Assert.Null(AlcolerJson.Deserialize<SessionRecord>(legacy)!.Shell);
    }

    [Fact]
    public async Task PreferenceChanges_OnlyAffectSessionsThatHaveNotExecuted()
    {
        await using var fixture = await ShellSnapshotFixture.CreateAsync();
        var preference = ShellSnapshotFixture.Cmd;
        var resolutions = new List<ShellEnvironment?>();
        ShellEnvironment Resolve(ShellEnvironment? saved)
        {
            resolutions.Add(saved);
            return saved ?? preference;
        }
        var model = new ShellSnapshotModel();
        await using var runtime = fixture.Runtime(model, Resolve);
        var first = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        var draft = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        Assert.Null(runtime.GetSessionShell(first.Id));
        Assert.Null((await fixture.RecordAsync(draft.Id)).Shell);
        Assert.Empty(resolutions);

        var rejected = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), draft.Id, 0, "  "), CancellationToken.None);
        Assert.False(rejected.Accepted);
        var emptyCompact = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), draft.Id, 0), CancellationToken.None);
        Assert.False(emptyCompact.Accepted);
        Assert.Empty(resolutions);

        await ShellSnapshotFixture.SendAndWaitAsync(runtime, first.Id, 1);
        preference = ShellSnapshotFixture.GitBash;
        await ShellSnapshotFixture.SendAndWaitAsync(runtime, first.Id, 2);
        await ShellSnapshotFixture.SendAndWaitAsync(runtime, draft.Id, 1);
        var future = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        await ShellSnapshotFixture.SendAndWaitAsync(runtime, future.Id, 1);

        Assert.Equal(ShellSnapshotFixture.Cmd, runtime.GetSessionShell(first.Id));
        Assert.Equal(ShellSnapshotFixture.GitBash, runtime.GetSessionShell(draft.Id));
        Assert.Equal(ShellSnapshotFixture.GitBash, (await fixture.RecordAsync(future.Id)).Shell);
        Assert.Equal(3, resolutions.Count);
        Assert.All(resolutions, Assert.Null);
        Assert.Equal(new[] { "cmd.exe", "cmd.exe", "Git Bash", "Git Bash" },
            model.Requests.Select(ShellSnapshotFixture.PromptShell).ToArray());
    }

    [Fact]
    public async Task Restart_ValidatesSavedSnapshotOnceInsteadOfUsingNewPreference()
    {
        await using var fixture = await ShellSnapshotFixture.CreateAsync();
        SessionHandle session;
        await using (var runtime = fixture.Runtime(new ShellSnapshotModel(), saved => saved ?? ShellSnapshotFixture.GitBash))
        {
            session = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
            await ShellSnapshotFixture.SendAndWaitAsync(runtime, session.Id, 1);
        }
        await fixture.Store.DisposeAsync();
        await using var reopenedStore = new SqliteSessionStore(fixture.DatabasePath);
        await reopenedStore.InitializeAsync(CancellationToken.None);
        var resolutions = new List<ShellEnvironment?>();
        var model = new ShellSnapshotModel();
        await using var reopened = fixture.Runtime(model, saved =>
        {
            resolutions.Add(saved);
            return saved ?? ShellSnapshotFixture.Cmd;
        }, store: reopenedStore);
        await reopened.ResumeSessionAsync(session.Id, CancellationToken.None);
        Assert.Empty(resolutions);
        Assert.Equal(ShellSnapshotFixture.GitBash, reopened.GetSessionShell(session.Id));
        await ShellSnapshotFixture.SendAndWaitAsync(reopened, session.Id, 2);
        await ShellSnapshotFixture.SendAndWaitAsync(reopened, session.Id, 3);
        Assert.Equal(ShellSnapshotFixture.GitBash, Assert.Single(resolutions));
        Assert.Equal(ShellSnapshotFixture.GitBash, (await reopenedStore.GetSessionAsync(session.Id, CancellationToken.None))!.Shell);
        Assert.All(model.Requests, request => Assert.Equal("Git Bash", ShellSnapshotFixture.PromptShell(request)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOrStaleSnapshot_IsPersistedBeforeModelAndRemainsFixed(bool stale)
    {
        await using var fixture = await ShellSnapshotFixture.CreateAsync();
        var original = stale ? new ShellEnvironment("missing-bash", ShellDialect.GitBash) : null;
        var session = await fixture.SeedAsync(original);
        var resolutions = new List<ShellEnvironment?>();
        var tool = new FakeTool("InspectShell");
        var model = new ShellSnapshotModel(
            ShellSnapshotModel.Call("InspectShell"), ShellSnapshotModel.Reply(),
            ShellSnapshotModel.Call("InspectShell"), ShellSnapshotModel.Reply());
        model.BeforeRequest = async (_, ct) =>
        {
            var record = await fixture.Store.GetSessionAsync(session.Id, ct);
            Assert.Equal(ShellSnapshotFixture.Cmd, record!.Shell);
        };
        await using var runtime = fixture.Runtime(model, saved =>
        {
            resolutions.Add(saved);
            return ShellSnapshotFixture.Cmd;
        }, [tool]);
        await runtime.ResumeSessionAsync(session.Id, CancellationToken.None);
        Assert.Empty(resolutions);
        Assert.Equal(original, (await fixture.RecordAsync(session.Id)).Shell);
        await ShellSnapshotFixture.SendAndWaitAsync(runtime, session.Id, 1);
        await ShellSnapshotFixture.SendAndWaitAsync(runtime, session.Id, 2);

        Assert.Equal(original, Assert.Single(resolutions));
        Assert.Equal(ShellSnapshotFixture.Cmd, runtime.GetSessionShell(session.Id));
        Assert.Equal(2, tool.ExecutedContexts.Count);
        Assert.All(tool.ExecutedContexts, context => Assert.Equal(ShellSnapshotFixture.Cmd, context.Shell!.Environment));
        Assert.Same(tool.ExecutedContexts[0].Shell, tool.ExecutedContexts[1].Shell);
        Assert.All(model.Requests, request => Assert.Equal("cmd.exe", ShellSnapshotFixture.PromptShell(request)));
        var persisted = await fixture.RecordAsync(session.Id);
        Assert.Equal("Seed title", persisted.Title);
        Assert.Equal(PermissionMode.Yolo, persisted.PermissionMode);
    }

    [Fact]
    public async Task ManualCompaction_InitializesLegacyHistoryBeforeSummaryRequest()
    {
        await using var fixture = await ShellSnapshotFixture.CreateAsync();
        var session = await fixture.SeedAsync(null);
        var now = DateTimeOffset.UtcNow;
        ConversationMessage[] messages =
        [
            new(StrongId.NewMessageId(), MessageRole.User, [new TextPart(StrongId.NewPartId(), "question")], now),
            new(StrongId.NewMessageId(), MessageRole.Assistant, [new TextPart(StrongId.NewPartId(), "answer")], now),
            new(StrongId.NewMessageId(), MessageRole.User, [new TextPart(StrongId.NewPartId(), "follow-up")], now),
            new(StrongId.NewMessageId(), MessageRole.Assistant, [new TextPart(StrongId.NewPartId(), "details")], now),
        ];
        await fixture.Store.AppendMessagesAsync(session.Id, messages, 1, CancellationToken.None);
        var calls = 0;
        var model = new ShellSnapshotModel(ShellSnapshotModel.Reply("The conversation concerns shell snapshots."));
        model.BeforeRequest = async (_, ct) =>
            Assert.Equal(ShellSnapshotFixture.GitBash, (await fixture.Store.GetSessionAsync(session.Id, ct))!.Shell);
        await using var runtime = fixture.Runtime(model, saved =>
        {
            calls++;
            Assert.Null(saved);
            return ShellSnapshotFixture.GitBash;
        });
        await runtime.ResumeSessionAsync(session.Id, CancellationToken.None);
        Assert.Equal(0, calls);
        var ack = await runtime.SubmitAsync(new CompactCommand(StrongId.NewCommandId(), session.Id, 0), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        Assert.Equal(1, calls);
        Assert.Equal("Git Bash", ShellSnapshotFixture.PromptShell(Assert.Single(model.Requests)));
        Assert.Equal(ShellSnapshotFixture.GitBash, runtime.GetSessionShell(session.Id));
    }

    [Fact]
    public async Task NoResolver_PreservesLegacyPromptOverrideButSavedShellTakesPrecedence()
    {
        await using var fixture = await ShellSnapshotFixture.CreateAsync();
        var model = new ShellSnapshotModel();
        await using var runtime = fixture.Runtime(model, null);
        var legacy = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root), CancellationToken.None);
        var saved = await runtime.CreateSessionAsync(new SessionOptions(fixture.Root, Shell: ShellSnapshotFixture.Cmd), CancellationToken.None);
        await ShellSnapshotFixture.SendAndWaitAsync(runtime, legacy.Id, 1);
        await ShellSnapshotFixture.SendAndWaitAsync(runtime, saved.Id, 1);
        Assert.Null(runtime.GetSessionShell(legacy.Id));
        Assert.Equal(new[] { "legacy override", "cmd.exe" }, model.Requests.Select(ShellSnapshotFixture.PromptShell).ToArray());
    }
}

internal sealed class ShellSnapshotFixture(string root, SqliteSessionStore store) : IAsyncDisposable
{
    public static readonly ShellEnvironment Cmd = new("cmd.exe", ShellDialect.Cmd);
    public static readonly ShellEnvironment GitBash = new("git-bash-for-tests", ShellDialect.GitBash);

    public string Root { get; } = root;
    public string DatabasePath => Path.Combine(Root, "sessions.db");
    public SqliteSessionStore Store { get; } = store;

    public static async Task<ShellSnapshotFixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "alcoler-shell-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SqliteSessionStore(Path.Combine(root, "sessions.db"));
        await store.InitializeAsync(CancellationToken.None);
        return new ShellSnapshotFixture(root, store);
    }

    public AgentRuntime Runtime(ShellSnapshotModel model, Func<ShellEnvironment?, ShellEnvironment>? resolve,
        IReadOnlyList<ITool>? tools = null, SqliteSessionStore? store = null, SubagentMessenger? messenger = null)
    {
        var artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
        AgentRuntime? runtime = null;
        var allTools = new List<ITool>(tools ?? []);
        allTools.Add(SubagentWiring.CreateTool(() => runtime!, messenger));
        runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store ?? Store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(model)],
            Tools = allTools,
            WorkspaceFactory = workspace => new WorkspaceToolEnvironment(workspace, artifacts),
            DefaultModel = new ModelSelection(model.Descriptor.ProviderId, model.Descriptor.ModelId),
            ShellName = "legacy override",
            ResolveShell = resolve,
        });
        return runtime;
    }

    public async Task<SessionRecord> RecordAsync(SessionId id) =>
        Assert.IsType<SessionRecord>(await Store.GetSessionAsync(id, CancellationToken.None));

    public async Task<SessionRecord> SeedAsync(ShellEnvironment? shell)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new SessionRecord(StrongId.NewSessionId(), Root, "Seed title", PermissionMode.Yolo,
            new ModelSelection("scripted", "shell-model"), now, now, Shell: shell);
        await Store.UpsertSessionAsync(record, CancellationToken.None);
        return record;
    }

    public static async Task SendAndWaitAsync(AgentRuntime runtime, SessionId id, int replies, string text = "run")
    {
        using var projection = runtime.Subscribe(id);
        var ack = await runtime.SubmitAsync(new SendTextCommand(StrongId.NewCommandId(), id, 0, text), CancellationToken.None);
        Assert.True(ack.Accepted, ack.RejectionReason);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (projection.Snapshot() is var snapshot &&
               (snapshot.State.Phase != TurnPhase.Idle || runtime.GetSessionActivity(id)!.Phase != TurnPhase.Idle ||
                runtime.GetSessionActivity(id)!.LastOutcome is null || snapshot.Rows.OfType<AssistantMessageRow>()
                   .Count(row => !row.IsStreaming && row.Parts.OfType<TextPart>().Any()) < replies))
        {
            Assert.DoesNotContain(snapshot.Rows.OfType<SystemNoticeRow>(), row => row.Kind == "error");
            await Task.Delay(10, timeout.Token);
        }
        Assert.Equal(TurnOutcome.Completed, runtime.GetSessionActivity(id)!.LastOutcome);
    }

    public static string PromptShell(ModelRequest request)
    {
        var system = string.Join("\n", request.Messages.Where(message => message.Role == MessageRole.System)
            .SelectMany(message => message.Content).OfType<InputTextBlock>().Select(block => block.Text));
        var line = Assert.Single(system.Split('\n'), line => line.TrimStart('-', ' ').StartsWith("Shell: ", StringComparison.Ordinal));
        return line.TrimStart('-', ' ')["Shell: ".Length..].TrimEnd('\r');
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

internal sealed class ShellSnapshotModel(params ModelStreamEvent[][] steps) : IChatModel
{
    private int _requestIndex;
    private readonly ConcurrentQueue<ModelRequest> _requests = new();

    public ModelDescriptor Descriptor { get; } = new("scripted", "shell-model", "Shell model",
        new ModelProperties(ContextWindowTokens: 200_000));
    public IReadOnlyList<ModelRequest> Requests => _requests.ToArray();
    public Func<ModelRequest, CancellationToken, ValueTask>? BeforeRequest { get; set; }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _requestIndex) - 1;
        _requests.Enqueue(request);
        if (BeforeRequest is { } before)
            await before(request, cancellationToken);
        var events = steps.Length == 0 ? Reply() : steps[index];
        foreach (var streamEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public static ModelStreamEvent[] Reply(string text = "done") =>
        [new ModelTextDelta(text), new ModelTextEnded(), new ModelStreamFinished(ModelFinishReason.Stop)];

    public static ModelStreamEvent[] Call(string name, object? input = null) =>
    [
        new ModelToolCallReady(new ToolCall(StrongId.NewToolCallId(), name, JsonSerializer.SerializeToElement(input ?? new { }))),
        new ModelStreamFinished(ModelFinishReason.ToolCalls),
    ];
}
