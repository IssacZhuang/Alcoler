using Alcoler.Contracts.Identifiers;

namespace Alcoler.Contracts.Conversation;

public enum CompactTrigger
{
    Auto,
    Manual,
    Reactive,
}

public sealed record CompactBoundary(
    string BoundaryId,
    CompactTrigger Trigger,
    int PreCompactTokens,
    int PostCompactTokens,
    MessageId? LastSummarizedMessageId,
    MessageId? PreservedHeadId,
    MessageId? PreservedTailId,
    DateTimeOffset AppliedAt);
