namespace Alcoler.Contracts.Models;

public sealed record ModelProperties(
    int? ContextWindowTokens = null,
    int? DefaultMaxOutputTokens = null);

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
