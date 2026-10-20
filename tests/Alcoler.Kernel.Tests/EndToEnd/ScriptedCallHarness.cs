using System.Runtime.CompilerServices;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Tools;

namespace Alcoler.Kernel.Tests.EndToEnd;

// Per-call scripted model: every StreamAsync call consumes exactly one scripted response,
// success or failure alike, and records each request. SequencedChatModel cannot express
// "fail once, then succeed with different events", which the recovery scenarios need.
// The descriptor declares a context window but no DefaultMaxOutputTokens so the 32k
// request baseline is observable.
internal sealed class ScriptedCallModel(IReadOnlyList<IReadOnlyList<ModelStreamEvent>> calls) : IChatModel
{
    private readonly object _sync = new();
    private int _index;

    public List<ModelRequest> Requests { get; } = [];

    public ModelDescriptor Descriptor { get; } = new(
        "scripted",
        "test-model",
        "Scripted Call Model",
        new ModelProperties(ContextWindowTokens: 200_000));

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        IReadOnlyList<ModelStreamEvent> response;
        lock (_sync)
        {
            Requests.Add(request);
            if (_index >= calls.Count)
                throw new InvalidOperationException($"script exhausted after {calls.Count} call(s)");
            response = calls[_index++];
        }
        foreach (var streamEvent in response)
            yield return streamEvent;
    }
}

internal sealed class ScriptedCallHarness(IReadOnlyList<IReadOnlyList<ModelStreamEvent>> calls) : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "alcoler-scripted-" + Guid.NewGuid().ToString("N"));

    public ScriptedCallModel Model { get; } = new(calls);

    public static ScriptedCallHarness Create(params IReadOnlyList<ModelStreamEvent>[] calls) => new(calls);

    public AgentRuntime BuildRuntime()
    {
        Directory.CreateDirectory(Root);
        var store = new SqliteSessionStore(Path.Combine(Root, "db.sqlite"));
        store.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
        AgentRuntime? runtime = null;
        var todoStore = new InMemoryTodoStore();
        // Deliberately minimal: only the tools these scenarios exercise, so parallel work
        // on unrelated tool adapters cannot break these runtime tests.
        var tools = new List<ITool>
        {
            new TodoWriteTool(todoStore),
            new TodoReadTool(todoStore),
        };
        runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = [new ScriptedModelProvider(Model)],
            Tools = tools,
            WorkspaceFactory = workspaceRoot => new WorkspaceToolEnvironment(workspaceRoot, artifacts),
            DefaultModel = new ModelSelection("scripted", "test-model"),
            TodoStore = todoStore,
        });
        return runtime;
    }

    public SqliteSessionStore OpenStore()
    {
        var store = new SqliteSessionStore(Path.Combine(Root, "db.sqlite"));
        store.InitializeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return store;
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
