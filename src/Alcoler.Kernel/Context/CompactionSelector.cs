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

    public static int CountContextPrefixMessages(IReadOnlyList<ConversationMessage> messages)
    {
        var count = 0;
        foreach (var message in messages)
        {
            if (!IsContextPrefixMessage(message))
                break;
            count++;
        }
        return count;
    }

    public static bool HasEnoughMessagesToCompact(IReadOnlyList<ConversationMessage> messages)
    {
        var body = messages.Where(message => !IsContextPrefixMessage(message)).ToList();
        return GroupByAssistantLedRounds(body).Count >= 2
            && body.Any(message => message.Role == MessageRole.Assistant);
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
        var preservedCount = groups.Count > 1 ? 1 : 0;
        var selection = SelectGroups(prefixCount, groups, preservedCount);
        if (groups.Count <= 3 || declaredTokenGap is not > 0)
            return selection;

        var remainingGap = declaredTokenGap.Value - TokenEstimator.EstimateHistoryTokens(groups[^1].Messages);
        if (remainingGap <= 0)
            return selection;

        var additionalGroups = CountRecentGroupsToCoverTokenGap(groups.Take(groups.Count - 1).ToList(), remainingGap);
        var initialSelection = SelectGroups(prefixCount, groups, Math.Min(groups.Count - 1, 1 + additionalGroups));
        return HasEnoughMessagesToCompact(initialSelection.ToSummarize) ? initialSelection : selection;
    }

    public static CompactionSelection ReselectForPromptTooLong(
        IReadOnlyList<ConversationMessage> messages,
        CompactionSelection previousSelection,
        int? declaredTokenGap,
        CompactTrigger trigger = CompactTrigger.Auto)
    {
        if (trigger == CompactTrigger.Manual)
            return previousSelection;

        var groups = GroupByAssistantLedRounds(previousSelection.ToSummarize);
        if (groups.Count < 2)
            return previousSelection;

        var moveCount = declaredTokenGap is > 0
            ? CountRecentGroupsToCoverTokenGap(groups, declaredTokenGap.Value)
            : 1;
        var remaining = groups.Take(groups.Count - moveCount).SelectMany(group => group.Messages).ToList();
        if (!HasEnoughMessagesToCompact(remaining))
            return previousSelection;

        var preserved = groups.Skip(groups.Count - moveCount).SelectMany(group => group.Messages)
            .Concat(previousSelection.PreservedTail).ToList();
        return previousSelection with { ToSummarize = remaining, PreservedTail = preserved };
    }

    public static CompactionSelection TruncateForPromptTooLong(
        CompactionSelection previousSelection,
        int? declaredTokenGap,
        int retriesMade)
    {
        if (retriesMade >= CompactPolicy.PromptTooLongMaxRetries)
            return previousSelection;

        var groups = GroupByAssistantLedRounds(previousSelection.ToSummarize);
        if (groups.Count < 2)
            return previousSelection;

        var dropCount = Math.Max(1, groups.Count / 5);
        if (declaredTokenGap is > 0)
        {
            dropCount = 0;
            var droppedTokens = 0;
            foreach (var group in groups)
            {
                droppedTokens += TokenEstimator.EstimateHistoryTokens(group.Messages);
                dropCount++;
                if (droppedTokens >= declaredTokenGap.Value)
                    break;
            }
        }

        dropCount = Math.Min(dropCount, groups.Count - 1);
        var remaining = groups.Skip(dropCount).SelectMany(group => group.Messages).ToList();
        return previousSelection with
        {
            ToSummarize = remaining,
            TruncationMarker = remaining[0].Role == MessageRole.Assistant ? TruncationMarkerText : null,
            DroppedRoundCount = previousSelection.DroppedRoundCount + dropCount,
        };
    }

    public static int AttemptsRemaining(int attemptsMade) =>
        Math.Max(0, CompactPolicy.PromptTooLongMaxRetries - attemptsMade);

    private static CompactionSelection SelectGroups(int prefixCount, IReadOnlyList<MessageGroup> groups, int preservedCount) =>
        new(prefixCount,
            groups.Take(groups.Count - preservedCount).SelectMany(group => group.Messages).ToList(),
            groups.Skip(groups.Count - preservedCount).SelectMany(group => group.Messages).ToList());

    private static int CountRecentGroupsToCoverTokenGap(IReadOnlyList<MessageGroup> groups, int tokenGap)
    {
        var coveredTokens = 0;
        var coveredGroups = 0;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            coveredTokens += TokenEstimator.EstimateHistoryTokens(groups[i].Messages);
            coveredGroups++;
            if (coveredTokens >= tokenGap)
                break;
        }

        return coveredGroups >= groups.Count - 1
            ? Math.Max(1, groups.Count / 2)
            : Math.Max(1, coveredGroups);
    }

    private static bool IsContextPrefixMessage(ConversationMessage message)
    {
        if (message.Role == MessageRole.System)
            return true;
        if (message.Role != MessageRole.User || message.Source is not null)
            return false;

        var textParts = message.Parts.OfType<TextPart>().ToList();
        if (textParts.Count > 0)
            return string.Join("\n\n", textParts.Select(part => part.Text)).TrimStart()
                .StartsWith("<system-reminder>", StringComparison.Ordinal);

        return message.Parts.OfType<AttachmentPart>().Any(attachment =>
            attachment.Kind is AttachmentKind.ContextPrefix or AttachmentKind.SkillsListing or AttachmentKind.SystemReminder);
    }
}
