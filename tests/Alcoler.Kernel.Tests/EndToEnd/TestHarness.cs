using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Tests.EndToEnd;

public sealed class TestHarness : IAsyncDisposable
{
    public string Root { get; }

    public string DatabasePath { get; }

    public SequencedChatModel ScriptedModel { get; }

    private TestHarness(string root, string databasePath, SequencedChatModel scriptedModel)
    {
        Root = root;
        DatabasePath = databasePath;
        ScriptedModel = scriptedModel;
    }

    public static TestHarness Create(params ScriptStep[] steps)
    {
        var root = Path.Combine(Path.GetTempPath(), "alcoler-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "db.sqlite");
        var scripted = new SequencedChatModel(
            steps,
            new ModelDescriptor("scripted", "test-model", "Scripted Test Model", new ModelProperties(ContextWindowTokens: 200_000)));
        return new TestHarness(root, databasePath, scripted);
    }

    /// <summary>Full toolset runtime around a caller-supplied model and workspace root.</summary>
    public static AgentRuntime BuildRuntimeFor(IChatModel model, string root, Contracts.Storage.ISessionStore? store = null)
    {
        store ??= new SqliteSessionStore(Path.Combine(root, "db.sqlite"));
        store.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var artifacts = new FileArtifactStore(Path.Combine(root, "artifacts"));
        AgentRuntime? runtime = null;
        var todoStore = new InMemoryTodoStore();
        var messenger = new SubagentMessenger();
        var tools = new List<ITool>
        {
            new ReadTool(),
            new WriteTool(),
            new EditTool(),
            new GlobTool(),
            new GrepTool(),
            new BashTool(),
            new TodoWriteTool(todoStore),
            new TodoReadTool(todoStore),
            new ExitPlanModeTool(),
            new AskUserQuestionTool(),
            new SkillTool(),
            new TaskOutputTool(),
            new TaskStopTool(),
            new SendMessageTool(messenger),
            SubagentWiring.CreateTool(
                () => runtime ?? throw new InvalidOperationException("runtime is not initialized"),
                messenger),
        };
        runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(model)],
            Tools = tools,
            WorkspaceFactory = workspaceRoot => new WorkspaceToolEnvironment(workspaceRoot, artifacts),
            DefaultModel = new ModelSelection("scripted", "test-model"),
            TodoStore = todoStore,
            PlanFileStore = new FilePlanFileStore(Path.Combine(root, "plans")),
            // Isolates the user skills level from the developer's real home directory.
            UserSkillsBase = Path.Combine(root, "user-skills-base"),
            SubagentOutputRoot = Path.Combine(root, "agent-output"),
        });
        return runtime;
    }

    public AgentRuntime BuildRuntime(params ITool[] extraTools) => BuildRuntime(null, extraTools);

    public AgentRuntime BuildRuntime(
        Func<AgentRuntimeOptions, AgentRuntimeOptions>? transform, params ITool[] extraTools)
    {
        var store = new SqliteSessionStore(DatabasePath);
        store.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
        AgentRuntime? runtime = null;
        var todoStore = new InMemoryTodoStore();
        var messenger = new SubagentMessenger();
        var tools = new List<ITool>
        {
            new ReadTool(),
            new WriteTool(),
            new EditTool(),
            new GlobTool(),
            new GrepTool(),
            new BashTool(),
            new TodoWriteTool(todoStore),
            new TodoReadTool(todoStore),
            new ExitPlanModeTool(),
            new AskUserQuestionTool(),
            new SkillTool(),
            new TaskOutputTool(),
            new TaskStopTool(),
            new ReadSessionContextTool(store),
            new SendMessageTool(messenger),
            SubagentWiring.CreateTool(
                () => runtime ?? throw new InvalidOperationException("runtime is not initialized"),
                messenger),
        };
        tools.AddRange(extraTools);
        var options = new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(ScriptedModel)],
            Tools = tools,
            WorkspaceFactory = workspaceRoot => new WorkspaceToolEnvironment(workspaceRoot, artifacts),
            DefaultModel = new ModelSelection("scripted", "test-model"),
            TodoStore = todoStore,
            PlanFileStore = new FilePlanFileStore(Path.Combine(Root, "plans")),
            // Isolates the user skills level from the developer's real home directory.
            UserSkillsBase = Path.Combine(Root, "user-skills-base"),
            SubagentOutputRoot = Path.Combine(Root, "agent-output"),
        };
        runtime = new AgentRuntime(transform?.Invoke(options) ?? options);
        return runtime;
    }

    /// <summary>Records every request served by the inner model so prompt assembly can be asserted.</summary>
    public sealed class RequestCapturingModel(IChatModel inner) : IChatModel
    {
        private readonly List<ModelRequest> _requests = [];

        public IReadOnlyList<ModelRequest> Requests => _requests;

        public ModelDescriptor Descriptor => inner.Descriptor;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _requests.Add(request);
            await foreach (var streamEvent in inner.StreamAsync(request, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return streamEvent;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
