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

    // Only tool names that exist (or are about to exist) in the registry; unmatched names are never hit.
    private static readonly HashSet<string> EligibleToolNames =
    [
        "Read", "Write", "Edit", "Bash", "Grep", "Glob", "WebFetch", "WebSearch", "TaskOutput",
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

        var groups = CompactionSelector.GroupByAssistantLedRounds(messages);
        var protectedGroups = new HashSet<int>();
        var kept = 0;
        for (var i = groups.Count - 1; i >= 0 && kept < CompactPolicy.MicroCompactKeepRecentGroups; i--)
        {
            if (groups[i].Messages.Any(ContainsToolResult))
            {
                protectedGroups.Add(i);
                kept++;
            }
        }

        var clearedParts = new List<PartId>();
        var savedTokens = 0;
        var result = new List<ConversationMessage>(messages.Count);
        for (var gi = 0; gi < groups.Count; gi++)
        {
            if (protectedGroups.Contains(gi))
            {
                result.AddRange(groups[gi].Messages);
                continue;
            }

            foreach (var message in groups[gi].Messages)
            {
                var modified = false;
                var newParts = new List<MessagePart>(message.Parts.Count);
                foreach (var part in message.Parts)
                {
                    if (part is ToolResultPart toolResult && IsClearable(toolResult, toolNames))
                    {
                        savedTokens += ResultTextTokens(toolResult) - TokenEstimator.EstimateTokens(ClearedContentText);
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

        if (clearedParts.Count == 0 || savedTokens < CompactPolicy.MicroCompactMinSavingsTokens)
            return new MicrocompactResult(messages, [], 0);

        return new MicrocompactResult(result, clearedParts, savedTokens);
    }

    private static bool IsClearable(ToolResultPart part, Dictionary<ToolCallId, string> toolNames) =>
        !part.IsError
        && !part.Content.Any(b => b is ToolResultMedia)
        && toolNames.TryGetValue(part.ToolCallId, out var toolName)
        && EligibleToolNames.Contains(toolName);

    private static bool ContainsToolResult(ConversationMessage message) =>
        message.Parts.Any(p => p is ToolResultPart);

    private static int ResultTextTokens(ToolResultPart part)
    {
        var total = 0;
        foreach (var block in part.Content)
            if (block is ToolResultText text)
                total += TokenEstimator.EstimateTokens(text.Text);
        return total;
    }
}
