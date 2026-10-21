using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

public sealed class AnthropicModelProvider(
    string apiKey,
    string baseUrl = "https://api.anthropic.com",
    IReadOnlyDictionary<string, ModelProperties>? modelProperties = null,
    string? providerId = null) : IModelProvider
{
    private static readonly HttpClient SharedClient = ModelProviderCatalog.CreateSharedClient();

    public string Kind => "anthropic";
    public string Id => providerId ?? Kind;

    public ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IChatModel>(new AnthropicChatModel(SharedClient,
            new AnthropicModelOptions(selection.ModelId, apiKey, baseUrl),
            ModelProviderCatalog.Resolve(Id, selection, modelProperties), Id));
}

public sealed class OpenAiCompatibleModelProvider(
    string baseUrl,
    string apiKey,
    bool useLegacyMaxTokens = false,
    IReadOnlyDictionary<string, ModelProperties>? modelProperties = null,
    string? providerId = null) : IModelProvider
{
    private static readonly HttpClient SharedClient = ModelProviderCatalog.CreateSharedClient();

    public string Kind => "openai-compatible";
    public string Id => providerId ?? Kind;

    public ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IChatModel>(new OpenAiCompatibleChatModel(SharedClient,
            new OpenAiCompatibleModelOptions(selection.ModelId, apiKey, baseUrl, useLegacyMaxTokens),
            ModelProviderCatalog.Resolve(Id, selection, modelProperties), Id));
}

public sealed class OpenAiResponsesModelProvider(
    string baseUrl,
    string apiKey,
    IReadOnlyDictionary<string, ModelProperties>? modelProperties = null,
    string? providerId = null,
    Func<CancellationToken, ValueTask<string>>? accessTokenSource = null) : IModelProvider
{
    private static readonly HttpClient SharedClient = ModelProviderCatalog.CreateSharedClient();

    public string Kind => "openai-responses";
    public string Id => providerId ?? Kind;

    public ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IChatModel>(new OpenAiResponsesChatModel(SharedClient,
            new OpenAiResponsesModelOptions(selection.ModelId, apiKey, baseUrl),
            ModelProviderCatalog.Resolve(Id, selection, modelProperties), Id, accessTokenSource));
}

public sealed class ScriptedModelProvider(IChatModel model, string? providerId = null) : IModelProvider
{
    public string Kind => "scripted";
    public string Id => providerId ?? Kind;

    public ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IChatModel>(providerId is null ? model : new IdentifiedModel(model, Id));

    private sealed class IdentifiedModel(IChatModel inner, string id) : IChatModel
    {
        public ModelDescriptor Descriptor { get; } = inner.Descriptor with { ProviderId = id };
        public void ValidateOptions(ModelCallOptions options) => inner.ValidateOptions(options);
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken) => inner.StreamAsync(request, cancellationToken);
    }
}

internal static class ModelProviderCatalog
{
    // The stream runner's idle timer is the only stream deadline; HttpClient's default
    // 100-second timeout would abort silent-but-healthy SSE streams.
    internal static HttpClient CreateSharedClient() =>
        new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    internal static ModelProperties? Resolve(string providerId, ModelSelection selection, IReadOnlyDictionary<string, ModelProperties>? catalog)
    {
        if (!string.Equals(providerId, selection.ProviderId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Model selection belongs to provider '{selection.ProviderId}', not '{providerId}'.");
        if (string.IsNullOrWhiteSpace(selection.ModelId))
            throw new ArgumentException("Choose a nonempty model ID.");
        if (catalog is null) return null;
        if (!catalog.TryGetValue(selection.ModelId, out var properties))
            throw new ArgumentException($"Model '{selection.ModelId}' is not available in provider '{providerId}'. Choose an enabled model from its catalog.");
        return properties;
    }
}
