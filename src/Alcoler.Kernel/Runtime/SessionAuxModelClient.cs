using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Models;

namespace Alcoler.Kernel.Runtime;

/// <summary>
/// Runs one-shot requests on the session model with the lowest reasoning level and a
/// capped output budget. Native search is the only request that carries a tool.
/// </summary>
internal sealed class SessionAuxModelClient(
    Func<ModelSelection?> resolveSelection,
    Func<ModelSelection, Task<IChatModel>> resolveModel,
    ModelCacheAffinity? cacheAffinity = null) : IAuxModelClient, IProviderWebSearch
{
    private const int MaxOutputTokensCap = 5_000;
    private const int WebSearchMaxOutputTokens = 4_096;
    private static readonly JsonElement EmptyObjectSchema =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public bool IsAvailable
    {
        get
        {
            try
            {
                var selection = resolveSelection();
                // IAuxModelClient exposes availability as a sync property; the bound model
                // is cached after first use, so this only blocks on a cache miss.
                return selection is not null
                    && resolveModel(selection).GetAwaiter().GetResult().Descriptor.Properties.SupportsNativeWebSearch;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public async ValueTask<ProviderWebSearchResponse> SearchAsync(
        ProviderWebSearchRequest request,
        CancellationToken cancellationToken)
    {
        var selection = resolveSelection()
            ?? throw new InvalidOperationException("No model configured for the session.");
        var model = await resolveModel(selection).ConfigureAwait(false);
        if (!model.Descriptor.Properties.SupportsNativeWebSearch)
            throw new InvalidOperationException("Current model does not support native WebSearch.");

        var baseOptions = selection.Options ?? new ModelCallOptions();
        var maxOutput = Math.Min(
            WebSearchMaxOutputTokens,
            model.Descriptor.Properties.DefaultMaxOutputTokens ?? WebSearchMaxOutputTokens);
        var options = new ModelCallOptions(
            ReasoningLevel: model.Descriptor.Properties.ReasoningLevels is { Count: > 0 } levels ? levels[0] : baseOptions.ReasoningLevel,
            MaxOutputTokens: Math.Max(1, maxOutput));

        var args = new Dictionary<string, object?>
        {
            ["max_uses"] = request.MaxUses,
        };
        if (request.AllowedDomains.Count > 0)
            args["allowed_domains"] = request.AllowedDomains.Cast<object?>().ToArray();
        if (request.BlockedDomains.Count > 0)
            args["blocked_domains"] = request.BlockedDomains.Cast<object?>().ToArray();

        var tool = new ToolContract(
            ProviderNativeWebSearch.ToolName,
            "Provider-native web search used internally by the WebSearch tool",
            EmptyObjectSchema,
            ProviderNative: new ProviderNativeToolSpec(
                ProviderNativeWebSearch.ToolType,
                ProviderNativeWebSearch.ToolName,
                args));
        ModelInputMessage[] messages =
        [
            new(MessageRole.System,
                [new InputTextBlock("You are an assistant for performing a web search tool use.")]),
            new(MessageRole.User, [new InputTextBlock($"Perform a web search for the query: {request.Query}")]),
        ];
        var modelRequest = new ModelRequest(messages, [tool], options, cacheAffinity);

        var text = new StringBuilder();
        var sources = new List<ProviderWebSearchSource>();
        int? webSearchRequests = null;
        await foreach (var streamEvent in ModelStreamRunner.RunAsync(
                           model,
                           modelRequest,
                           new ModelRunnerOptions(),
                           cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (streamEvent)
            {
                case ModelTextDelta delta:
                    text.Append(delta.Delta);
                    break;
                case ModelSourceFound source:
                    sources.Add(new ProviderWebSearchSource(source.Url, source.Title, source.PageAge));
                    break;
                case ModelStreamFinished finished:
                    webSearchRequests = finished.Usage?.ServerWebSearchRequests;
                    break;
                case ModelStreamFailed failed:
                    throw new InvalidOperationException($"Web search request failed: {failed.Failure.Message}");
            }
        }

        return new ProviderWebSearchResponse(text.ToString().Trim(), sources, webSearchRequests);
    }

    public ValueTask<string> CompleteAsync(AuxModelRequest request, CancellationToken cancellationToken) =>
        CompleteAsync(request, cancellationToken, separateSystemPrompt: false);

    internal async ValueTask<string> CompleteAsync(AuxModelRequest request, CancellationToken cancellationToken, bool separateSystemPrompt)
    {
        var selection = resolveSelection()
            ?? throw new InvalidOperationException("No model configured for the session.");
        var model = await resolveModel(selection).ConfigureAwait(false);
        var baseOptions = selection.Options ?? new ModelCallOptions();
        var maxOutput = Math.Min(
            Math.Min(request.MaxOutputTokens, MaxOutputTokensCap),
            model.Descriptor.Properties.DefaultMaxOutputTokens ?? int.MaxValue);
        var options = new ModelCallOptions(
            ReasoningLevel: model.Descriptor.Properties.ReasoningLevels is { Count: > 0 } levels ? levels[0] : baseOptions.ReasoningLevel,
            MaxOutputTokens: Math.Max(1, maxOutput));

        ModelInputMessage[] messages = separateSystemPrompt
            ? [
                new(MessageRole.System, [new InputTextBlock(request.SystemPrompt)]),
                new(MessageRole.User, [new InputTextBlock(request.UserPrompt)]),
            ]
            : [
                new(MessageRole.User,
                [
                    new InputTextBlock(string.IsNullOrEmpty(request.SystemPrompt)
                        ? request.UserPrompt
                        : $"{request.SystemPrompt}\n\n{request.UserPrompt}"),
                ]),
            ];
        var modelRequest = new ModelRequest(messages, [], options, cacheAffinity);

        var text = new StringBuilder();
        await foreach (var streamEvent in ModelStreamRunner.RunAsync(
                           model,
                           modelRequest,
                           new ModelRunnerOptions(),
                           cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (streamEvent is ModelTextDelta delta)
            {
                text.Append(delta.Delta);
            }
            else if (streamEvent is ModelToolCallReady && separateSystemPrompt)
            {
                throw new InvalidOperationException("The auxiliary title request returned a tool call.");
            }
            else if (streamEvent is ModelStreamFailed)
            {
                throw new InvalidOperationException("The auxiliary model request failed.");
            }
        }

        return text.ToString();
    }
}
