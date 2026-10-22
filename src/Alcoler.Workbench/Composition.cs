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
using Alcoler.Workbench.Auth;
using Alcoler.Workbench.Browser;
using Alcoler.Workbench.Git;
using Alcoler.Workbench.Ui;
using Alcoler.Workbench.ViewModel;

namespace Alcoler.Workbench;

public static class Composition
{
    public static async Task<WorkbenchViewModel> BuildWorkbenchAsync(WorkbenchConfig config, CancellationToken ct)
    {
        var mcpTools = new McpToolSource(config.McpServers);
        var browserHost = new WebViewBrowserHost(Path.Combine(WorkbenchDataPaths.Root, "browser"));
        var browser = new WebViewBrowserController(browserHost);
        browserHost.NewTabRequested += async (_, request) =>
        {
            try
            {
                await browser.OpenPopupAsync(request.SessionId, request.OpenerTabId, request.Url,
                    CancellationToken.None, request.IsUserInitiated);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        };
        var webFetch = new WebFetchTool();
        WebSearchTool? webSearch = null;
        var extraTools = new List<ITool> { new BrowserTool(browser), webFetch };
        var braveKey = config.BraveApiKey ?? Environment.GetEnvironmentVariable("ALCOLER_BRAVE_API_KEY");
        if (!string.IsNullOrWhiteSpace(braveKey))
        {
            webSearch = new WebSearchTool(braveKey);
            extraTools.Add(webSearch);
        }
        var currentConfig = config;
        var chatGptAccount = new ChatGptAccountService(Path.Combine(WorkbenchDataPaths.Root, "auth.json"));
        AgentRuntime runtime;
        try
        {
            runtime = await BuildRuntimeAsync(config, ct, mcpTools.GetToolsAsync, extraTools,
                () => Volatile.Read(ref currentConfig).IntegratedTerminalShell, chatGptAccount,
                () => Volatile.Read(ref currentConfig).DisabledSkillPaths ?? []);
        }
        catch
        {
            browser.Dispose();
            webFetch.Dispose();
            webSearch?.Dispose();
            await mcpTools.DisposeAsync();
            throw;
        }
        var workbench = new WorkbenchViewModel(
            runtime,
            AvaloniaUiScheduler.Instance,
            config,
            CombineDisposables(
                mcpTools,
                new AsyncActionDisposable(() =>
                {
                    browser.Dispose();
                    webFetch.Dispose();
                    webSearch?.Dispose();
                })),
            chatGptAccount: chatGptAccount,
            browserPane: new BrowserPaneViewModel(browser, AvaloniaUiScheduler.Instance,
                pagePresenterFactory: browserHost.CreatePagePresenter),
            saveComposer: composer => (WorkbenchConfig.LoadOrDefault() with { Composer = composer }).Save(),
            applySettings: saved =>
            {
                Volatile.Write(ref currentConfig, saved);
                runtime.ApplyProviderConfiguration(
                    BuildProviders(saved, chatGptAccount),
                    WorkbenchModelCatalog.DefaultSelection(saved));
                mcpTools.Update(saved.McpServers);
            });
        await workbench.RefreshFromRuntimeAsync();
        return workbench;
    }

    public static async Task<AgentRuntime> BuildRuntimeAsync(
        WorkbenchConfig config,
        CancellationToken ct,
        Func<CancellationToken, ValueTask<IReadOnlyList<ITool>>>? sessionTools = null,
        IReadOnlyList<ITool>? extraTools = null,
        Func<ShellEnvironment?>? shellPreference = null,
        ChatGptAccountService? chatGptAccount = null,
        Func<IReadOnlyList<string>>? disabledSkillPathsProvider = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(WorkbenchDataPaths.DatabasePath)!);
        Directory.CreateDirectory(WorkbenchDataPaths.ArtifactsPath);
        Directory.CreateDirectory(WorkbenchDataPaths.ExecPath);

        var store = new SqliteSessionStore(WorkbenchDataPaths.DatabasePath);
        await store.InitializeAsync(ct);
        var artifacts = new FileArtifactStore(WorkbenchDataPaths.ArtifactsPath);

        var todoStore = new InMemoryTodoStore();
        var messenger = new SubagentMessenger();
        AgentRuntime? runtime = null;
        var tools = new List<ITool>(DefaultToolSet.Create())
        {
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
        if (extraTools is not null)
        {
            tools.AddRange(extraTools);
        }

        runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Store = store,
            Artifacts = artifacts,
            Providers = BuildProviders(config, chatGptAccount),
            Tools = tools,
            SessionToolsProvider = sessionTools,
            WorkspaceFactory = root => new WorkspaceToolEnvironment(root, artifacts),
            DefaultModel = WorkbenchModelCatalog.DefaultSelection(config),
            SessionTitleGenerationEnabled = true,
            TodoStore = todoStore,
            GitSnapshotProvider = GitSnapshotCollector.Collect,
            SubagentOutputRoot = WorkbenchDataPaths.ExecPath,
            ResolveShell = snapshot => ShellResolver.ResolveEnvironment(
                shellPreference is null ? config.IntegratedTerminalShell : shellPreference(), snapshot),
            DisabledSkillPathsProvider = disabledSkillPathsProvider,
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

    internal static IReadOnlyList<IModelProvider> BuildProviders(WorkbenchConfig config, ChatGptAccountService? chatGptAccount = null) =>
        [.. config.Providers.Where(provider => provider.Enabled).Select(provider => BuildProvider(provider, chatGptAccount))];

    private static IModelProvider BuildProvider(WorkbenchProviderConfig provider, ChatGptAccountService? chatGptAccount) =>
        provider.Kind.ToLowerInvariant() switch
        {
            "anthropic" => new AnthropicModelProvider(
                provider.ApiKey ?? string.Empty,
                string.IsNullOrWhiteSpace(provider.BaseUrl) ? "https://api.anthropic.com" : provider.BaseUrl,
                modelProperties: ToProperties(provider),
                providerId: provider.EffectiveId),
            "openai-compatible" => new OpenAiCompatibleModelProvider(
                provider.BaseUrl,
                provider.ApiKey ?? string.Empty,
                modelProperties: ToProperties(provider),
                providerId: provider.EffectiveId),
            "openai-responses" when provider.AccessType == ProviderTemplate.ChatGptAccount => new OpenAiResponsesModelProvider(
                provider.BaseUrl,
                // The account channel resolves its bearer per request; the static
                // key stays empty so nothing but the OAuth token is ever sent.
                string.Empty,
                modelProperties: ToProperties(provider),
                providerId: provider.EffectiveId,
                accessTokenSource: chatGptAccount is null
                    ? null
                    : ct => chatGptAccount.GetAccessTokenAsync(ct)),
            "openai-responses" => new OpenAiResponsesModelProvider(
                provider.BaseUrl,
                provider.ApiKey ?? string.Empty,
                modelProperties: ToProperties(provider),
                providerId: provider.EffectiveId),
            _ => throw new InvalidOperationException($"Unknown provider kind '{provider.Kind}'."),
        };

    private static IReadOnlyDictionary<string, ModelProperties> ToProperties(WorkbenchProviderConfig provider) =>
        provider.Models
            .Where(model => model.Enabled)
            .ToDictionary(model => model.ModelId, model => WorkbenchModelCatalog.Resolve(provider, model));
}
