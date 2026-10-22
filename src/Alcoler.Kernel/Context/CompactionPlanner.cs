using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.History;
using Alcoler.Kernel.Prompts;

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
    IReadOnlyList<ConversationMessage> ContextPrefixMessages,
    MessageId? LastActiveMessageId = null);

public sealed record PostCompactResult(
    IReadOnlyList<ConversationMessage> Messages,
    CompactBoundary Boundary);

public static class CompactionPlanner
{
    public const int RecentlyReadMaxFiles = 5;
    public const int RecentlyReadMaxTokensPerFile = 5_000;

    public static CompactionPlan PlanCompaction(CompactionPlanInput input)
    {
        var selection = CompactionSelector.SelectForCompaction(input.Messages, input.Trigger, input.DeclaredTokenGap);
        var contextPrefix = input.Messages.Take(selection.ContextPrefixCount).ToList();
        var request = BuildSummaryRequest(selection, input.ManualInstructions, contextPrefix, input.Tools);

        var preCompactTokens = input.EffectiveTokens > 0
            ? input.EffectiveTokens
            : TokenEstimator.EstimateCurrentInputTokens(input.Messages, input.AnchorIndex, input.LastProviderUsage).Tokens;

        return new CompactionPlan(
            input.Trigger,
            preCompactTokens,
            selection,
            request,
            contextPrefix,
            input.Messages.LastOrDefault()?.Id);
    }

    public static ModelRequest BuildSummaryRequest(
        CompactionSelection selection,
        string? manualInstructions,
        IReadOnlyList<ConversationMessage>? contextPrefixMessages = null,
        IReadOnlyList<ToolContract>? tools = null,
        IReadOnlyList<ModelInputMessage>? requestPrefix = null)
    {
        var messages = new List<ModelInputMessage>();
        if (requestPrefix is not null)
            messages.AddRange(requestPrefix);
        foreach (var message in contextPrefixMessages ?? [])
            messages.AddRange(ContextBuilder.Project(message));
        if (selection.TruncationMarker is { } marker)
            messages.Add(new ModelInputMessage(MessageRole.User, [new InputTextBlock(marker)]));
        foreach (var message in selection.ToSummarize)
            messages.AddRange(ContextBuilder.Project(message));
        messages.Add(new ModelInputMessage(MessageRole.User,
            [new InputTextBlock(SummarizationPrompt.BuildSummaryUserPrompt(manualInstructions))], new CacheControl()));

        return new ModelRequest(messages,
            tools is { Count: <= CompactPolicy.CompactToolKeepMaxCount } ? tools : [],
            new ModelCallOptions(MaxOutputTokens: CompactPolicy.SummaryMaxOutputTokens));
    }

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
        IReadOnlyList<(string Path, string Content)> recentlyRead,
        IReadOnlyList<ConversationMessage>? preservedTail = null) =>
        BuildRecentlyReadFilesAttachments(recentlyRead.Select(file => new RecentFileRead(file.Path, file.Content, default)).ToList(), preservedTail);

    public static IReadOnlyList<ConversationMessage> BuildRecentlyReadFilesAttachments(
        IReadOnlyList<RecentFileRead> recentlyRead,
        IReadOnlyList<ConversationMessage>? preservedTail)
    {
        var preservedPaths = (preservedTail ?? []).SelectMany(message => message.Parts).OfType<ToolCallPart>()
            .Where(call => call.ToolName == "Read" && call.Input.ValueKind == System.Text.Json.JsonValueKind.Object)
            .Select(call => call.Input.TryGetProperty("file_path", out var path) && path.ValueKind == System.Text.Json.JsonValueKind.String
                ? path.GetString()?.Replace('\\', '/') : null)
            .Where(path => path is not null).ToHashSet(StringComparer.Ordinal);
        var messages = new List<ConversationMessage>();
        foreach (var file in recentlyRead.Where(file => !file.Path.Replace('\\', '/').Contains("/.git/", StringComparison.Ordinal)
            && !preservedPaths.Contains(file.Path.Replace('\\', '/'))).Take(RecentlyReadMaxFiles))
        {
            var input = new Dictionary<string, object> { ["file_path"] = file.Path };
            if (file.Offset is { } offset)
                input["offset"] = offset;
            if (file.Limit is { } limit)
                input["limit"] = limit;
            var content = (file.Content.Length + 2) / 3 <= RecentlyReadMaxTokensPerFile
                ? $"Called the Read tool with the following input: {System.Text.Json.JsonSerializer.Serialize(input)}\nResult of calling the Read tool:\n{file.Content}"
                : $"Note: {file.Path} was read before the last conversation was summarized, but the contents are too large to include. Use Read tool if you need to access it.";
            messages.Add(new ConversationMessage(StrongId.NewMessageId(), MessageRole.User,
                [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.ResumeReferencedContext,
                    SystemReminder.Wrap(System.Text.RegularExpressions.Regex.Replace(content, @"</?system-reminder\b",
                        match => "&lt;" + match.Value[1..], System.Text.RegularExpressions.RegexOptions.IgnoreCase)), ProviderVisible: true)],
                DateTimeOffset.UtcNow, Source: MessageSource.ResumeReferencedContext));
        }
        return messages;
    }

    public static ConversationMessage? BuildApprovedPlanAttachment(PlanFileReference? planFile)
    {
        if (planFile is null || string.IsNullOrWhiteSpace(planFile.Content))
            return null;
        var content = $"""
            A plan file exists from plan mode at: {planFile.Path}

            Plan contents:

            {planFile.Content.Trim()}

            If this plan is relevant to the current work and not already complete, continue working on it.
            """;
        return new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.User,
            [new AttachmentPart(
                StrongId.NewPartId(),
                AttachmentKind.ResumeReferencedContext,
                SystemReminder.Wrap(System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"</?system-reminder\b",
                    match => "&lt;" + match.Value[1..],
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                ProviderVisible: true)],
            DateTimeOffset.UtcNow,
            Source: MessageSource.ResumeReferencedContext);
    }

    public const string SuppressFollowupInstruction =
        "Continue the conversation from where it left off without asking the user any further questions. " +
        "Resume directly — do not acknowledge the summary, do not recap what was happening, do not preface with \"I'll continue\" or similar. " +
        "Pick up the last task as if the break never happened.";

    public static PostCompactResult BuildPostCompactHistory(
        CompactionPlan plan,
        string summaryText,
        ConversationMessage compactSummaryMessage,
        IReadOnlyList<ConversationMessage> preservedTail,
        IReadOnlyList<ConversationMessage> recentlyReadFilesAttachments,
        bool suppressFollowup = true)
    {
        var summaryMessage = compactSummaryMessage;
        var summaryContent = $"{CompactContinuationHeader}\n\n{SummarizationPrompt.FormatSummary(summaryText)}";
        if (suppressFollowup)
            summaryContent += "\n" + SuppressFollowupInstruction;
        summaryMessage = summaryMessage with
        {
            Parts = [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.CompactSummary, summaryContent, ProviderVisible: true)],
        };

        var messages = new List<ConversationMessage>();
        messages.AddRange(plan.ContextPrefixMessages);
        messages.Add(summaryMessage);
        messages.AddRange(preservedTail);
        messages.AddRange(recentlyReadFilesAttachments);

        var lastSummarized = plan.LastActiveMessageId;
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
