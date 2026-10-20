namespace Alcoler.Contracts.Models;

public sealed record ModelProperties(
    int? ContextWindowTokens = null,
    int? DefaultMaxOutputTokens = null,
    bool SupportsImage = false,
    bool SupportsVideo = false,
    bool SupportsAudio = false,
    bool SupportsPdf = false,
    bool SupportsJsonSchemaOutput = false,
    bool SupportsNativeWebSearch = false,
    bool SupportsMidConversationSystem = false,
    bool SupportsToolCall = true,
    bool RequiresMfjsToolSchema = false,
    IReadOnlyList<string>? ReasoningLevels = null,
    string? ReasoningMapping = null,
    string? MaxOutputTokensMapping = null)
{
    public void ValidateOptions(ModelCallOptions options)
    {
        if (ContextWindowTokens is <= 0)
            throw new ArgumentException("Context window tokens must be greater than zero.");
        if (DefaultMaxOutputTokens is <= 0 || options.MaxOutputTokens is <= 0)
            throw new ArgumentException("Max output tokens must be greater than zero.");
        if (options.ReasoningLevel is { } level &&
            (string.IsNullOrWhiteSpace(level) || ReasoningLevels is not null && !ReasoningLevels.Contains(level, StringComparer.Ordinal)))
            throw new ArgumentException($"Reasoning level '{level}' is not supported. Choose from: {string.Join(", ", ReasoningLevels ?? [])}.");
    }
}

public sealed record ModelDescriptor(
    string ProviderId,
    string ModelId,
    string DisplayName,
    ModelProperties Properties) : IEquatable<ModelDescriptor>
{
    public string FullId => $"{ProviderId}/{ModelId}";
}

public sealed record ModelCallOptions(
    string? ReasoningLevel = null,
    int? MaxOutputTokens = null);

public sealed record ModelSelection(
    string ProviderId,
    string ModelId,
    ModelCallOptions? Options = null);
