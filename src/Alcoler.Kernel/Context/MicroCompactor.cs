using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Kernel.Context;

public sealed record MicrocompactResult(
    IReadOnlyList<ConversationMessage> Messages,
    IReadOnlyList<PartId> ClearedParts,
    int EstimatedTokensSaved);

public static class MicroCompactor
{
    public const string ClearedContentText = "[Old tool result content cleared]";

    private static readonly HashSet<string> EligibleToolNames =
    [
        "Read", "Write", "Edit", "Bash", "Grep", "Glob", "WebFetch", "WebSearch", "ApplyPatch",
    ];

    public static MicrocompactResult Apply(
        IReadOnlyList<ConversationMessage> messages,
        DateTimeOffset lastActivityUtc,
        DateTimeOffset nowUtc,
        int autoCompactThresholdTokens)
    {
        var idle = nowUtc - lastActivityUtc;
        var triggered = idle > TimeSpan.FromMinutes(CompactPolicy.MicroCompactIdleMinutes)
            || TokenEstimator.EstimateHistoryTokens(messages)
                >= CompactPolicy.MicroCompactTriggerTokens(autoCompactThresholdTokens);
        if (!triggered)
            return new MicrocompactResult(messages, [], 0);

        var toolNames = new Dictionary<ToolCallId, string>();
        foreach (var message in messages)
            foreach (var part in message.Parts)
                if (part is ToolCallPart call)
                    toolNames[call.ToolCallId] = call.ToolName;

        var groups = GroupByToolCallRounds(messages);
        var protectedGroups = new HashSet<int>();
        var kept = 0;
        for (var i = groups.Count - 1; i >= 0 && kept < CompactPolicy.MicroCompactKeepRecentGroups; i--)
        {
            if (groups[i].Any(message => message.Parts.OfType<ToolResultPart>().Any(part => IsClearable(part, toolNames))))
            {
                protectedGroups.Add(i);
                kept++;
            }
        }

        var clearedParts = new List<PartId>();
        var result = new List<ConversationMessage>(messages.Count);
        for (var gi = 0; gi < groups.Count; gi++)
        {
            if (protectedGroups.Contains(gi))
            {
                result.AddRange(groups[gi]);
                continue;
            }

            foreach (var message in groups[gi])
            {
                var modified = false;
                var newParts = new List<MessagePart>(message.Parts.Count);
                foreach (var part in message.Parts)
                {
                    if (part is ToolResultPart toolResult && IsClearable(toolResult, toolNames))
                    {
                        clearedParts.Add(toolResult.Id);
                        newParts.Add(toolResult with { Content = [new ToolResultText(ClearedContentText)] });
                        modified = true;
                    }
                    else
                    {
                        newParts.Add(part);
                    }
                }
                result.Add(modified ? message with { Parts = newParts } : message);
            }
        }

        var savedTokens = Math.Max(0, TokenEstimator.EstimateHistoryTokens(messages) - TokenEstimator.EstimateHistoryTokens(result));
        if (clearedParts.Count == 0 || savedTokens < CompactPolicy.MicroCompactMinSavingsTokens)
            return new MicrocompactResult(messages, [], 0);

        return new MicrocompactResult(result, clearedParts, savedTokens);
    }

    private static bool IsClearable(ToolResultPart part, Dictionary<ToolCallId, string> toolNames) =>
        !part.IsError
        && !part.Content.Any(b => b is ToolResultMedia)
        && string.Join("\n\n", part.Content.OfType<ToolResultText>().Select(block => block.Text).Where(text => text.Length > 0)) != ClearedContentText
        && toolNames.TryGetValue(part.ToolCallId, out var toolName)
        && EligibleToolNames.Contains(toolName);

    private static IReadOnlyList<List<ConversationMessage>> GroupByToolCallRounds(IReadOnlyList<ConversationMessage> messages)
    {
        var groups = new List<List<ConversationMessage>>();
        List<ConversationMessage>? current = null;
        foreach (var message in messages)
        {
            if (current is null || message.Role == MessageRole.Assistant && message.Parts.Any(part => part is ToolCallPart))
            {
                current = [];
                groups.Add(current);
            }
            current.Add(message);
        }
        return groups;
    }
}
