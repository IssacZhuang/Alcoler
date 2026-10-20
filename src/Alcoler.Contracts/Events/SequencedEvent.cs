using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;

namespace Alcoler.Contracts.Events;

public sealed record SequencedEvent(
    long Sequence,
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    SessionEvent Event);

public static class SessionEventRetention
{
    public const int SealedTurnTransientGraceMs = 120_000;

    public static bool IsTransient(SessionEvent sessionEvent) => sessionEvent is
        ModelRequestStartedEvent
        or ModelRequestCompletedEvent
        or ModelRequestFailedEvent
        or ModelRetryScheduledEvent
        or ModelStreamingTextEvent
        or ModelStreamingReasoningEvent
        or ModelStreamingToolInputEvent;
}
