using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Context;

public sealed record CompactionPlanInput(
    IReadOnlyList<ConversationMessage> Messages,
    CompactTrigger Trigger,
    int EffectiveTokens,
    int ContextWindowTokens,
    ModelDescriptor Model,
    IReadOnlyList<ToolContract> Tools,
    TokenUsage? LastProviderUsage = null,
    int AnchorIndex = -1,
    string? ManualInstructions = null,
    int? DeclaredTokenGap = null);

public sealed record CompactionPlan(
    CompactTrigger Trigger,
    int PreCompactTokens,
    CompactionSelection Selection,
    ModelRequest SummaryModelRequest,
    IReadOnlyList<ConversationMessage> ContextPrefixMessages);

public sealed record PostCompactResult(
    IReadOnlyList<ConversationMessage> Messages,
    CompactBoundary Boundary);

public static class CompactionPlanner
{
    public const string SummarySystemInstruction =
        "You are a summarization assistant. Summarize the provided coding-agent conversation exactly as " +
        "instructed by the user message. Respond with text only.";

    public const int RecentlyReadMaxFiles = 5;
    public const int RecentlyReadMaxTokensPerFile = 5_000;

    public static CompactionPlan PlanCompaction(CompactionPlanInput input)
    {
        var selection = CompactionSelector.SelectForCompaction(input.Messages, input.Trigger);

        // Summaries never carry tools, so Tools is always empty regardless of CompactToolKeepMaxCount.
        var request = BuildSummaryRequest(selection, input.ManualInstructions);

        var preCompactTokens = input.EffectiveTokens > 0
            ? input.EffectiveTokens
            : TokenEstimator.EstimateCurrentInputTokens(input.Messages, input.AnchorIndex, input.LastProviderUsage).Tokens;

        return new CompactionPlan(
            input.Trigger,
            preCompactTokens,
            selection,
            request,
            input.Messages.Take(selection.ContextPrefixCount).ToList());
    }

    public static ModelRequest BuildSummaryRequest(CompactionSelection selection, string? manualInstructions) =>
        new(
        [
            new ModelInputMessage(MessageRole.System, [new InputTextBlock(SummarySystemInstruction)]),
            new ModelInputMessage(MessageRole.User, [new InputTextBlock(SummarizationPrompt.BuildSummaryUserPrompt(selection.ToSummarize, manualInstructions))]),
        ],
            Tools: [],
            Options: new ModelCallOptions(MaxOutputTokens: CompactPolicy.SummaryMaxOutputTokens));

    public const string CompactContinuationHeader =
        "This session is being continued from a previous conversation that ran out of context. " +
        "The summary below covers the earlier portion of the conversation.";

    public static ConversationMessage CreateCompactSummaryMessage(string summaryText) =>
        new(
            StrongId.NewMessageId(),
            MessageRole.User,
            [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.CompactSummary, summaryText, ProviderVisible: true)],
            DateTimeOffset.UtcNow);

    public static IReadOnlyList<ConversationMessage> BuildRecentlyReadFilesAttachments(
        IReadOnlyList<(string Path, string Content)> recentlyRead)
    {
        if (recentlyRead.Count == 0)
            return [];

        var parts = recentlyRead
            .Take(RecentlyReadMaxFiles)
            .Select(file => TokenEstimator.EstimateTokens(file.Content) <= RecentlyReadMaxTokensPerFile
                ? new AttachmentPart(
                    StrongId.NewPartId(),
                    AttachmentKind.ResumeReferencedContext,
                    $"Contents of {file.Path} (read before compaction):\n\n{file.Content}",
                    ProviderVisible: true)
                : new AttachmentPart(
                    StrongId.NewPartId(),
                    AttachmentKind.ResumeReferencedContext,
                    $"File {file.Path} was read before compaction; use the ReadFile tool if you need it again",
                    ProviderVisible: true))
            .ToList();
        return
        [
            new ConversationMessage(StrongId.NewMessageId(), MessageRole.User, parts, DateTimeOffset.UtcNow),
        ];
    }

    public static PostCompactResult BuildPostCompactHistory(
        CompactionPlan plan,
        string summaryText,
        ConversationMessage compactSummaryMessage,
        IReadOnlyList<ConversationMessage> preservedTail,
        IReadOnlyList<ConversationMessage> recentlyReadFilesAttachments)
    {
        var summaryMessage = compactSummaryMessage;
        // Mirrors ZCode compact prompt framing: the header explains what the summary is, the
        // marker (when groups were dropped for size) and verbatim-preserved note follow it.
        var summaryContent = plan.Selection.TruncationMarker is { } marker
            ? $"{CompactContinuationHeader}\n\n{marker}\n\n{summaryText}"
            : $"{CompactContinuationHeader}\n\n{summaryText}";
        if (preservedTail.Count > 0)
            summaryContent += "\n\nRecent messages are preserved verbatim.";
        summaryMessage = summaryMessage with
        {
            Parts = [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.CompactSummary, summaryContent, ProviderVisible: true)],
        };

        var messages = new List<ConversationMessage>();
        messages.AddRange(plan.ContextPrefixMessages);
        messages.Add(summaryMessage);
        messages.AddRange(recentlyReadFilesAttachments);
        messages.AddRange(preservedTail);

        var lastSummarized = plan.Selection.ToSummarize.Count > 0
            ? plan.Selection.ToSummarize[^1].Id
            : (MessageId?)null;
        var preservedHead = preservedTail.Count > 0 ? preservedTail[0].Id : (MessageId?)null;
        var preservedTailId = preservedTail.Count > 0 ? preservedTail[^1].Id : (MessageId?)null;

        var boundary = new CompactBoundary(
            BoundaryId: $"cb_{Guid.NewGuid():N}",
            Trigger: plan.Trigger,
            PreCompactTokens: plan.PreCompactTokens,
            PostCompactTokens: TokenEstimator.EstimateHistoryTokens(messages),
            LastSummarizedMessageId: lastSummarized,
            PreservedHeadId: preservedHead,
            PreservedTailId: preservedTailId,
            AppliedAt: DateTimeOffset.UtcNow);

        return new PostCompactResult(messages, boundary);
    }
}
