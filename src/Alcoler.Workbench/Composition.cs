using Alcoler.Adapters.Mcp;
using Alcoler.Adapters.Models;
using Alcoler.Adapters.Storage;
using Alcoler.Adapters.Tools;
using Alcoler.Browser;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Runtime;
using Alcoler.Kernel.Subagents;
using Alcoler.Kernel.Tools;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;

namespace Alcoler.Workbench;

public static class Composition
{
    public static async Task<WorkbenchViewModel> BuildWorkbenchAsync(WorkbenchConfig config, CancellationToken ct)
    {
        var mcp = config.McpServers is { Count: > 0 } servers
            ? new McpServerManager([.. servers.Select(s => new McpServerConfig(s.Name, s.Command, s.Args, s.Env))])
            : null;
        var browser = new PlaywrightBrowserController();
        var extraTools = new List<ITool> { new BrowserTool(browser) };
        var runtime = await BuildRuntimeAsync(config, ct, mcp, extraTools);
        var workbench = new WorkbenchViewModel(
            runtime,
            AvaloniaUiScheduler.Instance,
            config,
            CombineDisposables(mcp, new AsyncActionDisposable(browser.Dispose)),
            browserPane: new BrowserPaneViewModel(browser, AvaloniaUiScheduler.Instance, path =>
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 1280);
            }));
        await workbench.RefreshFromRuntimeAsync();
        return workbench;
    }

    public static async Task<AgentRuntime> BuildRuntimeAsync(
        WorkbenchConfig config,
        CancellationToken ct,
        McpServerManager? mcp = null,
        IReadOnlyList<ITool>? extraTools = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WorkbenchDataPaths.DatabasePath)!);
        Directory.CreateDirectory(WorkbenchDataPaths.ArtifactsPath);

        var store = new SqliteSessionStore(WorkbenchDataPaths.DatabasePath);
        await store.InitializeAsync(ct);
        var artifacts = new FileArtifactStore(WorkbenchDataPaths.ArtifactsPath);

        var todoStore = new InMemoryTodoStore();
        AgentRuntime? runtime = null;
        var tools = new List<ITool>(DefaultToolSet.Create())
        {
            new TodoWriteTool(todoStore),
            new TodoReadTool(todoStore),
            new ExitPlanModeTool(),
            SubagentWiring.CreateTool(() =>
                runtime ?? throw new InvalidOperationException("runtime is not initialized")),
        };
        if (mcp is not null)
        {
            tools.AddRange(await mcp.GetToolsAsync(ct));
        }
        if (extraTools is not null)
        {
            tools.AddRange(extraTools);
        }

        runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = BuildProviders(config),
            Tools = tools,
            WorkspaceFactory = root => new WorkspaceToolEnvironment(root, artifacts),
            DefaultModel = config.DefaultModel is { } d
                ? new ModelSelection(d.ProviderId, d.ModelId)
                : null,
        });
        return runtime;
    }

    private static IAsyncDisposable? CombineDisposables(IAsyncDisposable? first, IAsyncDisposable? second) =>
        (first, second) switch
        {
            (null, null) => null,
            ({ } a, null) => a,
            (null, { } b) => b,
            ({ } a, { } b) => new CompositeAsyncDisposable(a, b),
        };

    private sealed class CompositeAsyncDisposable(IAsyncDisposable first, IAsyncDisposable second) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    private sealed class AsyncActionDisposable(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static IReadOnlyList<IModelProvider> BuildProviders(WorkbenchConfig config) =>
        [.. config.Providers.Select(BuildProvider)];

    private static IModelProvider BuildProvider(WorkbenchProviderConfig provider) =>
        provider.Kind switch
        {
            "anthropic" => new AnthropicModelProvider(
                provider.ApiKey ?? string.Empty,
                string.IsNullOrWhiteSpace(provider.BaseUrl) ? "https://api.anthropic.com" : provider.BaseUrl,
                modelProperties: ToProperties(provider.Models)),
            "openai-compatible" => new OpenAiCompatibleModelProvider(
                provider.BaseUrl,
                provider.ApiKey ?? string.Empty,
                modelProperties: ToProperties(provider.Models)),
            _ => throw new InvalidOperationException($"Unknown provider kind '{provider.Kind}'."),
        };

    private static IReadOnlyDictionary<string, ModelProperties> ToProperties(IReadOnlyList<WorkbenchModelConfig> models) =>
        models
            .Where(m => m.ContextWindowTokens is not null)
            .ToDictionary(m => m.ModelId, m => new ModelProperties(m.ContextWindowTokens));
}
