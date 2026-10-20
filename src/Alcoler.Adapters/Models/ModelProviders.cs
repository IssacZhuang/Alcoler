using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

public sealed class AnthropicModelProvider(
    string apiKey,
    string baseUrl = "https://api.anthropic.com",
    IReadOnlyDictionary<string, ModelProperties>? modelProperties = null) : IModelProvider
{
    private static readonly HttpClient SharedClient = new();

    public string Kind => "anthropic";

    public ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken)
    {
        var properties = modelProperties?.GetValueOrDefault(selection.ModelId);
        return ValueTask.FromResult<IChatModel>(
            new AnthropicChatModel(SharedClient, new AnthropicModelOptions(selection.ModelId, apiKey, baseUrl), properties));
    }
}

public sealed class OpenAiCompatibleModelProvider(
    string baseUrl,
    string apiKey,
    bool useLegacyMaxTokens = false,
    IReadOnlyDictionary<string, ModelProperties>? modelProperties = null) : IModelProvider
{
    private static readonly HttpClient SharedClient = new();

    public string Kind => "openai-compatible";

    public ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken)
    {
        var properties = modelProperties?.GetValueOrDefault(selection.ModelId);
        return ValueTask.FromResult<IChatModel>(
            new OpenAiCompatibleChatModel(
                SharedClient,
                new OpenAiCompatibleModelOptions(selection.ModelId, apiKey, baseUrl, useLegacyMaxTokens),
                properties));
    }
}

public sealed class ScriptedModelProvider(IChatModel model) : IModelProvider
{
    public string Kind => "scripted";

    public ValueTask<IChatModel> BindAsync(ModelSelection selection, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IChatModel>(model);
}
