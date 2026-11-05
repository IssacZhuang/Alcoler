using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Storage;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.History;

namespace Alcoler.Kernel.Runtime;

internal static class SessionRestore
{
    internal static bool IsRealUserTurn(ConversationMessage message)
    {
        if (message.Role != MessageRole.User)
            return false;
        if (message.Source == MessageSource.RealUser)
            return true;
        if (message.Source is not null)
            return false;
        var hasText = false;
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case ToolResultPart:
                case AttachmentPart:
                    return false;
                case TextPart:
                    hasText = true;
                    break;
            }
        }
        return hasText;
    }

    internal static (TokenUsage Usage, int AnchorIndex)? RestoreProviderUsage(
        IReadOnlyList<SequencedEvent> durable,
        IReadOnlyList<SequencedEvent> replayDurable,
        ModelDescriptor descriptor,
        ContextUsageState? contextUsage,
        MessageHistory history)
    {
        if (contextUsage is not { TokenSource: TokenEstimator.ProviderUsageSource, LastUsage: { } usage } saved
            || (usage.ContextUsageTokens ?? usage.InputTokens) is not > 0)
        {
            return null;
        }

        // Only successful usage samples advance cache input totals; pre-request snapshots retain them.
        var sample = replayDurable.FirstOrDefault(entry => entry.Event is ContextUsageUpdatedEvent context
            && context.Usage.TokenSource == TokenEstimator.ProviderUsageSource
            && context.Usage.CacheInputTokens == saved.CacheInputTokens
            && context.Usage.LastUsage == usage);
        // Microcompaction is not materialized in stored history until a full compaction.
        var lastRewrite = durable.LastOrDefault(entry => entry.Event is CompactBoundaryAppliedEvent or MicrocompactAppliedEvent);
        if (sample is null || lastRewrite?.Event is MicrocompactAppliedEvent
            || replayDurable.Any(entry => entry.Sequence > sample.Sequence
                && entry.Event is SessionModelChangedEvent or CompactBoundaryAppliedEvent
                    or MicrocompactAppliedEvent or ConversationRewoundEvent))
        {
            return null;
        }

        var assistant = replayDurable.Where(entry => entry.Sequence > sample.Sequence)
            .TakeWhile(entry => entry.Event is not (ContextUsageUpdatedEvent or TurnStartedEvent or TurnCompletedEvent))
            .Select(entry => entry.Event).OfType<AssistantMessageCommittedEvent>().FirstOrDefault()?.Message;
        if (assistant is null || assistant.ProviderId != descriptor.ProviderId || assistant.ModelId != descriptor.ModelId
            || history.LastIndexOf(assistant.Id) is not { } anchorIndex)
        {
            return null;
        }

        return (usage, anchorIndex);
    }

    // Committed messages survive the cut as anchors for the kept prefix; every other
    // pre-cut event belongs to the discarded branch and would resurrect rejected state.
    internal static IReadOnlyList<SequencedEvent> FilterDurableForBranch(
        IReadOnlyList<SequencedEvent> durable, SessionBranch? branch)
    {
        if (branch?.CutEventSequence is not { } cutEventSequence)
        {
            return durable;
        }

        return durable
            .Where(entry => entry.Sequence > cutEventSequence
                || entry.Event is UserMessageCommittedEvent or AssistantMessageCommittedEvent)
            .ToList();
    }

    internal static IReadOnlyList<SequencedEvent> FilterDurableForMissingRewindTargets(IReadOnlyList<SequencedEvent> durable)
    {
        var replay = new List<SequencedEvent>(durable.Count);
        foreach (var entry in durable.OrderBy(entry => entry.Sequence))
        {
            if (entry.Event is ConversationRewoundEvent rewound
                && !replay.Any(previous => CommittedMessage(previous.Event)?.Id == rewound.TargetMessageId))
            {
                var kept = rewound.KeptMessageIds.ToHashSet();
                var prefixCount = 0;
                TurnId? turn = null;
                var keepTurn = false;
                for (var index = 0; index < replay.Count && replay[index].Sequence <= rewound.CutEventSequence; index++)
                {
                    var previous = replay[index].Event;
                    if (CommittedMessage(previous) is { } message)
                    {
                        keepTurn = kept.Contains(message.Id);
                        if (keepTurn)
                            prefixCount = index + 1;
                    }
                    else if (previous is TurnStartedEvent started)
                    {
                        turn = started.TurnId;
                        keepTurn = false;
                    }
                    else if (previous is TurnCompletedEvent completed)
                    {
                        if (keepTurn && turn == completed.TurnId)
                            prefixCount = index + 1;
                        turn = null;
                        keepTurn = false;
                    }
                    else if (previous is TurnFailedEvent failed)
                    {
                        if (keepTurn && turn == failed.TurnId)
                            prefixCount = index + 1;
                    }
                    else if (previous is CompactBoundaryAppliedEvent compact)
                    {
                        // Archived display history is outside the rewind's compacted model prefix.
                        var compactPrefix = compact.Boundary.PreservedHeadId is { } head
                            ? replay.FindIndex(0, index, candidate => CommittedMessage(candidate.Event)?.Id == head)
                            : index + 1;
                        if (compactPrefix < 0 && compact.Boundary.LastSummarizedMessageId is { } summarizedTail)
                        {
                            var tailIndex = replay.FindIndex(0, index,
                                candidate => CommittedMessage(candidate.Event)?.Id == summarizedTail);
                            if (tailIndex >= 0)
                            {
                                compactPrefix = tailIndex + 1;
                                for (var terminal = compactPrefix; terminal < index; terminal++)
                                {
                                    var next = replay[terminal].Event;
                                    if (CommittedMessage(next) is not null
                                        || next is TurnStartedEvent or ConversationRewoundEvent or CompactBoundaryAppliedEvent)
                                        break;
                                    if (next is TurnCompletedEvent or TurnFailedEvent)
                                        compactPrefix = terminal + 1;
                                }
                            }
                        }
                        if (compactPrefix >= 0)
                            prefixCount = Math.Max(prefixCount, compactPrefix);
                    }
                }

                // Legacy targets can be absent from both the active messages and committed envelopes.
                for (var index = replay.Count - 1; index >= prefixCount; index--)
                {
                    if (replay[index].Sequence <= rewound.CutEventSequence)
                        replay.RemoveAt(index);
                }
            }
            replay.Add(entry);
        }
        return replay;
    }

    internal static ConversationMessage? CommittedMessage(SessionEvent sessionEvent) => sessionEvent switch
    {
        UserMessageCommittedEvent user => user.Message,
        AssistantMessageCommittedEvent assistant => assistant.Message,
        _ => null,
    };

    internal static TurnId? FindInterruptedTurn(IReadOnlyList<SequencedEvent> durable)
    {
        TurnId? activeTurn = null;
        foreach (var entry in durable)
        {
            switch (entry.Event)
            {
                case TurnStartedEvent started:
                    activeTurn = started.TurnId;
                    break;
                case TurnCompletedEvent:
                case TurnFailedEvent:
                    activeTurn = null;
                    break;
            }
        }
        return activeTurn;
    }

    internal static IEnumerable<(SessionEvent Event, DateTimeOffset OccurredAt)> ReconstructTimeline(
        IReadOnlyList<StoredMessage> messages,
        IReadOnlyList<SequencedEvent> durable,
        IReadOnlyDictionary<MessageId, TurnId> completedTails)
    {
        var canonical = messages.ToDictionary(stored => stored.Message.Id, stored => stored.Message);
        var representedCompletions = durable.Select(entry => entry.Event).OfType<TurnCompletedEvent>()
            .Select(completed => completed.TurnId).ToHashSet();
        var timeline = new List<(SessionEvent Event, DateTimeOffset OccurredAt)>();
        var anchors = new Dictionary<MessageId, int>();
        foreach (var entry in durable.OrderBy(entry => entry.Sequence))
        {
            var committed = CommittedMessage(entry.Event);
            if (committed is not null)
            {
                // Compaction replaces model context, not the visible conversation retained in committed envelopes.
                var message = canonical.GetValueOrDefault(committed.Id) ?? committed;
                if (anchors.TryAdd(message.Id, timeline.Count))
                {
                    timeline.Add((MessageCommitted(message), entry.OccurredAt));
                    if (completedTails.TryGetValue(message.Id, out var turnId)
                        && !representedCompletions.Contains(turnId))
                    {
                        timeline.Add((new TurnCompletedEvent(turnId, TurnOutcome.Completed), entry.OccurredAt));
                    }
                }
            }
            else if (entry.Event is SubagentStartedEvent or SubagentStatusChangedEvent
                or TurnStartedEvent or TurnFailedEvent or TurnCompletedEvent
                or ToolCallStartedEvent or ToolCallResultEvent
                or ConversationRewoundEvent or CompactBoundaryAppliedEvent)
            {
                timeline.Add((entry.Event, entry.OccurredAt));
            }
        }

        var nextAnchors = new int[messages.Count];
        var nextAnchor = timeline.Count;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (anchors.TryGetValue(messages[index].Message.Id, out var anchor))
                nextAnchor = anchor;
            nextAnchors[index] = nextAnchor;
        }

        var eventIndex = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index].Message;
            if (anchors.TryGetValue(message.Id, out var anchor))
            {
                while (eventIndex <= anchor)
                    yield return timeline[eventIndex++];
            }
            else
            {
                // Legacy messages have no shared sequence; ties favor messages, bounded by exact anchors.
                while (eventIndex < nextAnchors[index] && timeline[eventIndex].OccurredAt < message.CreatedAt)
                    yield return timeline[eventIndex++];
                yield return (MessageCommitted(message), message.CreatedAt);
            }
        }
        while (eventIndex < timeline.Count)
            yield return timeline[eventIndex++];
    }

    internal static SessionEvent MessageCommitted(ConversationMessage message) => message.Role == MessageRole.User
        ? new UserMessageCommittedEvent(message)
        : new AssistantMessageCommittedEvent(message);
}
