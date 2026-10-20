using Alcoler.Contracts.Conversation;

namespace Alcoler.Kernel.Context;

public sealed record MessageGroup(bool IsAssistantLed, IReadOnlyList<ConversationMessage> Messages);

public sealed record CompactionSelection(
    int ContextPrefixCount,
    IReadOnlyList<ConversationMessage> ToSummarize,
    IReadOnlyList<ConversationMessage> PreservedTail,
    string? TruncationMarker = null,
    int DroppedRoundCount = 0);

public static class CompactionSelector
{
    public const string TruncationMarkerText = "[earlier conversation truncated for compaction retry]";

    public static IReadOnlyList<MessageGroup> GroupByAssistantLedRounds(IReadOnlyList<ConversationMessage> messages)
    {
        var groups = new List<MessageGroup>();
        List<ConversationMessage>? current = null;

        foreach (var message in messages)
        {
            if (message.Role == MessageRole.Assistant)
            {
                if (current is not null)
                    groups.Add(new MessageGroup(current[0].Role == MessageRole.Assistant, current));
                current = [message];
            }
            else
            {
                current ??= [];
                current.Add(message);
            }
        }

        if (current is not null)
            groups.Add(new MessageGroup(current[0].Role == MessageRole.Assistant, current));
        return groups;
    }

    // Context prefix = maximal run of leading user messages with no user-typed text that carry a
    // ContextPrefix/SkillsListing/SystemReminder attachment; these are never summarized.
    public static int CountContextPrefixMessages(IReadOnlyList<ConversationMessage> messages)
    {
        var count = 0;
        foreach (var message in messages)
        {
            if (message.Role != MessageRole.User || !IsContextPrefixMessage(message))
                break;
            count++;
        }
        return count;
    }

    public static CompactionSelection SelectForCompaction(
        IReadOnlyList<ConversationMessage> messages,
        CompactTrigger trigger,
        int? declaredTokenGap = null)
    {
        var prefixCount = CountContextPrefixMessages(messages);
        var body = messages.Skip(prefixCount).ToList();

        if (trigger == CompactTrigger.Manual)
            return new CompactionSelection(prefixCount, body, []);

        var groups = GroupByAssistantLedRounds(body);
        var lastRoundIndex = -1;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i].IsAssistantLed)
            {
                lastRoundIndex = i;
                break;
            }
        }

        if (lastRoundIndex < 0)
            return new CompactionSelection(prefixCount, body, []);

        var toSummarize = groups.Take(lastRoundIndex).SelectMany(g => g.Messages).ToList();
        var preservedTail = groups.Skip(lastRoundIndex).SelectMany(g => g.Messages).ToList();
        return new CompactionSelection(prefixCount, toSummarize, preservedTail);
    }

    // Drops whole assistant-led rounds from the front of ToSummarize until the estimated dropped
    // tokens cover declaredTokenGap; at least one round is always kept summarizable.
    public static CompactionSelection ReselectForPromptTooLong(
        IReadOnlyList<ConversationMessage> messages,
        CompactionSelection previousSelection,
        int? declaredTokenGap)
    {
        if (previousSelection.ToSummarize.Count == 0)
            return previousSelection;

        var groups = GroupByAssistantLedRounds(previousSelection.ToSummarize);
        if (groups.Count <= 1)
            return previousSelection;

        var target = declaredTokenGap ?? 0;
        var droppedTokens = 0;
        var dropCount = 0;
        for (var i = 0; i < groups.Count - 1; i++)
        {
            if (dropCount > 0 && droppedTokens >= target)
                break;
            droppedTokens += TokenEstimator.EstimateHistoryTokens(groups[i].Messages);
            dropCount++;
        }

        var remaining = groups.Skip(dropCount).SelectMany(g => g.Messages).ToList();
        return previousSelection with
        {
            ToSummarize = remaining,
            TruncationMarker = TruncationMarkerText,
            DroppedRoundCount = previousSelection.DroppedRoundCount + dropCount,
        };
    }

    public static int AttemptsRemaining(int attemptsMade) =>
        Math.Max(0, CompactPolicy.PromptTooLongMaxRetries - attemptsMade);

    private static bool IsContextPrefixMessage(ConversationMessage message)
    {
        var hasPrefixAttachment = false;
        foreach (var part in message.Parts)
        {
            if (part is TextPart)
                return false;
            if (part is AttachmentPart attachment
                && attachment.Kind is AttachmentKind.ContextPrefix
                    or AttachmentKind.SkillsListing
                    or AttachmentKind.SystemReminder)
            {
                hasPrefixAttachment = true;
            }
        }
        return hasPrefixAttachment;
    }
}
