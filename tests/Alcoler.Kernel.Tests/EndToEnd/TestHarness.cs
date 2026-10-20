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

    public AgentRuntime BuildRuntime(params ITool[] extraTools)
    {
        var store = new SqliteSessionStore(DatabasePath);
        store.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
        AgentRuntime? runtime = null;
        var todoStore = new InMemoryTodoStore();
        var messenger = new SubagentMessenger();
        var tools = new List<ITool>
        {
            new ReadFileTool(),
            new WriteFileTool(),
            new EditFileTool(),
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
        runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(ScriptedModel)],
            Tools = tools,
            WorkspaceFactory = workspaceRoot => new WorkspaceToolEnvironment(workspaceRoot, artifacts),
            DefaultModel = new ModelSelection("scripted", "test-model"),
            TodoStore = todoStore,
        });
        return runtime;
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
