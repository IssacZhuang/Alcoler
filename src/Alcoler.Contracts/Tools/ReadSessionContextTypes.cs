namespace Alcoler.Contracts.Tools;

// Field names mirror the reference ReadSessionContext contracts; partId/reason stay
// unpopulated until part-level citation exists.
public sealed record ReadSessionContextReference(
    string MessageId,
    string? PartId = null,
    int? Index = null,
    string? Role = null,
    string? Reason = null);

// Metadata-only companion of the model-visible ReadSessionContext text: content itself
// stays in the sibling text block, so the payload must not duplicate it.
public sealed record ReadSessionContextSources(
    string Status,
    string SessionId,
    string? Title,
    string? Directory,
    string? Path,
    string Strategy,
    string Query,
    string Source,
    int MessageCount,
    int? SelectedMessageCount,
    bool Truncated,
    string? Error,
    IReadOnlyList<ReadSessionContextReference>? References);
