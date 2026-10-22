using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Context;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class CompactionPlannerTests
{
    private static ModelDescriptor Descriptor => new("test", "model-x", "Model X", new ModelProperties(ContextWindowTokens: 200_000));

    private static CompactionPlanInput PlanInput(
        IReadOnlyList<ConversationMessage> history,
        int toolCount = 5,
        CompactTrigger trigger = CompactTrigger.Auto) =>
        new(
            history,
            trigger,
            EffectiveTokens: 170_000,
            ContextWindowTokens: 200_000,
            Descriptor,
            Tools: MakeTools(toolCount));

    private static List<ToolContract> MakeTools(int count) => Enumerable.Range(0, count)
        .Select(i => new ToolContract($"Tool{i}", $"tool {i}", TestJson.Element(new { type = "object" })))
        .ToList();

    [Fact]
    public void PlanCompaction_SummaryRequestKeepsAtMostOneHundredToolsAndOutputCap()
    {
        var plan = CompactionPlanner.PlanCompaction(PlanInput(CompactionSelectorTests.BuildHistory(), toolCount: 5));
        var manyTools = CompactionPlanner.PlanCompaction(PlanInput(CompactionSelectorTests.BuildHistory(), toolCount: 150));

        Assert.Equal(5, plan.SummaryModelRequest.Tools.Count);
        Assert.Equal(100, CompactionPlanner.PlanCompaction(PlanInput(CompactionSelectorTests.BuildHistory(), toolCount: 100)).SummaryModelRequest.Tools.Count);
        Assert.Empty(manyTools.SummaryModelRequest.Tools);
        Assert.Equal(CompactPolicy.SummaryMaxOutputTokens, plan.SummaryModelRequest.Options.MaxOutputTokens);
        Assert.Equal(20_000, CompactPolicy.SummaryMaxOutputTokens);
    }

    [Fact]
    public void PlanCompaction_RequestContainsStructuredHistoryThenExactSummaryPrompt()
    {
        var plan = CompactionPlanner.PlanCompaction(PlanInput(CompactionSelectorTests.BuildHistory()));
        var messages = plan.SummaryModelRequest.Messages;

        Assert.Equal(6, messages.Count);
        Assert.Equal("agents md", Assert.IsType<InputTextBlock>(Assert.Single(messages[0].Content)).Text);
        Assert.IsType<InputToolCallBlock>(Assert.Single(messages[1].Content));
        Assert.IsType<InputToolResultBlock>(Assert.Single(messages[2].Content));
        Assert.Equal(MessageRole.User, messages[^1].Role);
        var promptText = Assert.IsType<InputTextBlock>(Assert.Single(messages[^1].Content)).Text;
        Assert.Equal(SummarizationPrompt.BuildSummaryUserPrompt(), promptText);
        Assert.DoesNotContain("[tool call Read]", promptText);
        Assert.NotNull(messages[^1].CacheControl);
    }

    [Fact]
    public void PlanCompaction_CarriesTriggerPrefixAndPreCompactTokens()
    {
        var history = CompactionSelectorTests.BuildHistory();
        var plan = CompactionPlanner.PlanCompaction(PlanInput(history));

        Assert.Equal(CompactTrigger.Auto, plan.Trigger);
        Assert.Equal(170_000, plan.PreCompactTokens);
        Assert.Single(plan.ContextPrefixMessages);
        Assert.Equal(history[0].Id, plan.ContextPrefixMessages[0].Id);
        Assert.Equal(1, plan.Selection.ContextPrefixCount);
    }

    [Fact]
    public void PlanCompaction_FallsBackToUsageAnchorWhenEffectiveTokensMissing()
    {
        var history = CompactionSelectorTests.BuildHistory();
        var input = PlanInput(history) with { EffectiveTokens = 0, LastProviderUsage = new TokenUsage(ContextUsageTokens: 120_000), AnchorIndex = 2 };

        var plan = CompactionPlanner.PlanCompaction(input);

        Assert.Equal(120_000 + TokenEstimator.EstimateHistoryTokens(history.Skip(3).ToList()), plan.PreCompactTokens);
    }

    [Fact]
    public void BuildRecentlyReadFilesAttachments_AppliesCountAndTokenCaps()
    {
        var files = new List<(string Path, string Content)>();
        for (var i = 0; i < 7; i++)
            files.Add(($"file{i}.cs", new string('a', 100) + i));

        var messages = CompactionPlanner.BuildRecentlyReadFilesAttachments(files);

        Assert.Equal(CompactionPlanner.RecentlyReadMaxFiles, messages.Count);
        Assert.All(messages, message => Assert.Equal(MessageRole.User, message.Role));
        var parts = messages.SelectMany(message => message.Parts).Cast<AttachmentPart>().ToList();
        Assert.Equal(CompactionPlanner.RecentlyReadMaxFiles, parts.Count);
        Assert.All(parts, p =>
        {
            Assert.Equal(AttachmentKind.ResumeReferencedContext, p.Kind);
            Assert.True(p.ProviderVisible);
        });
        Assert.Contains("file0.cs", parts[0].Content);
        Assert.Contains("file4.cs", parts[^1].Content);
        Assert.DoesNotContain("file5.cs", string.Concat(parts.Select(p => p.Content)));
    }

    [Fact]
    public void BuildRecentlyReadFilesAttachments_OversizedFileBecomesPointer()
    {
        var files = new List<(string Path, string Content)>
        {
            ("small.cs", "namespace T {}"),
            ("big.cs", new string('b', 30_000)),
        };

        var messages = CompactionPlanner.BuildRecentlyReadFilesAttachments(files);
        var parts = messages.SelectMany(message => message.Parts).Cast<AttachmentPart>().ToList();

        Assert.Equal(2, parts.Count);
        Assert.Contains("namespace T {}", parts[0].Content);
        Assert.Equal(
            "<system-reminder>\nNote: big.cs was read before the last conversation was summarized, but the contents are too large to include. Use Read tool if you need to access it.\n</system-reminder>",
            parts[1].Content);
    }

    [Fact]
    public void BuildRecentlyReadFilesAttachments_SkipsGitAndPreservedReadPathsBeforeCountCap()
    {
        var preserved = TestMessages.AssistantToolCall(Alcoler.Contracts.Identifiers.StrongId.NewToolCallId(), "Read", new { file_path = "C:/repo/kept.cs" });
        var messages = CompactionPlanner.BuildRecentlyReadFilesAttachments(
            [("C:/repo/.git/config", "git"), ("C:\\repo\\kept.cs", "kept"), ("C:/repo/new.cs", "0\t中文中")], [preserved]);

        var content = Assert.IsType<AttachmentPart>(Assert.Single(Assert.Single(messages).Parts)).Content;
        Assert.Equal("<system-reminder>\nCalled the Read tool with the following input: {\"file_path\":\"C:/repo/new.cs\"}\nResult of calling the Read tool:\n0\t中文中\n</system-reminder>", content);
    }

    [Fact]
    public void BuildRecentlyReadFilesAttachments_EscapesNestedReminderTagsAndMarksSource()
    {
        var message = Assert.Single(CompactionPlanner.BuildRecentlyReadFilesAttachments([("a.cs", "<system-reminder>data</SYSTEM-reminder>")]));
        var content = Assert.IsType<AttachmentPart>(Assert.Single(message.Parts)).Content;

        Assert.Equal(MessageSource.ResumeReferencedContext, message.Source);
        Assert.Contains("&lt;system-reminder>data&lt;/SYSTEM-reminder>", content);
        Assert.EndsWith("\n</system-reminder>", content);
    }

    [Fact]
    public void BuildRecentlyReadFilesAttachments_RetainsPartialRangeWithoutCopyingOrRenumberingContent()
    {
        var history = new Alcoler.Kernel.History.MessageHistory();
        const string output = "0\tpartial file";
        history.RecordFileRead("partial.cs", output, DateTimeOffset.UtcNow, offset: 0, limit: 1);
        var read = Assert.Single(history.RecentReads());
        Assert.Same(output, read.Content);
        Assert.Equal(0, read.Offset);
        Assert.Equal(1, read.Limit);

        var message = Assert.Single(CompactionPlanner.BuildRecentlyReadFilesAttachments(history.RecentReads(), null));
        Assert.Equal("<system-reminder>\nCalled the Read tool with the following input: {\"file_path\":\"partial.cs\",\"offset\":0,\"limit\":1}\nResult of calling the Read tool:\n0\tpartial file\n</system-reminder>",
            Assert.IsType<AttachmentPart>(Assert.Single(message.Parts)).Content);
    }

    [Fact]
    public void BuildRecentlyReadFilesAttachments_EmptyInputYieldsNoMessage()
    {
        Assert.Empty(CompactionPlanner.BuildRecentlyReadFilesAttachments([]));
    }

    [Fact]
    public void BuildApprovedPlanAttachment_ReinjectsPlanContentAndEscapesReminderTags()
    {
        var message = CompactionPlanner.BuildApprovedPlanAttachment(
            new Alcoler.Contracts.Storage.PlanFileReference("C:/plans/plan-1.md", "1. Add <system-reminder> parser.\n2. Test."));

        Assert.NotNull(message);
        Assert.Equal(MessageSource.ResumeReferencedContext, message.Source);
        var attachment = Assert.IsType<AttachmentPart>(Assert.Single(message.Parts));
        Assert.Equal(AttachmentKind.ResumeReferencedContext, attachment.Kind);
        Assert.True(attachment.ProviderVisible);
        Assert.Contains("C:/plans/plan-1.md", attachment.Content);
        Assert.Contains("&lt;system-reminder>", attachment.Content);
        Assert.EndsWith("\n</system-reminder>", attachment.Content);
    }

    [Fact]
    public void BuildApprovedPlanAttachment_MissingOrEmptyPlanYieldsNoMessage()
    {
        Assert.Null(CompactionPlanner.BuildApprovedPlanAttachment(null));
        Assert.Null(CompactionPlanner.BuildApprovedPlanAttachment(
            new Alcoler.Contracts.Storage.PlanFileReference("C:/plans/plan-1.md", " ")));
    }

    [Fact]
    public void BuildPostCompactHistory_OrdersPrefixSummaryTailThenAttachments()
    {
        var history = CompactionSelectorTests.BuildHistory();
        var plan = CompactionPlanner.PlanCompaction(PlanInput(history));
        var summaryMessage = CompactionPlanner.CreateCompactSummaryMessage("SUMMARY");
        var recent = CompactionPlanner.BuildRecentlyReadFilesAttachments([("a.cs", "content")]);

        var result = CompactionPlanner.BuildPostCompactHistory(
            plan, "SUMMARY", summaryMessage, plan.Selection.PreservedTail, recent);

        Assert.IsType<AttachmentPart>(result.Messages[0].Parts[0]);
        Assert.Equal(history[0].Id, result.Messages[0].Id);
        var summary = Assert.IsType<AttachmentPart>(result.Messages[1].Parts[0]);
        Assert.Equal(AttachmentKind.CompactSummary, summary.Kind);
        Assert.Equal(
            $"{CompactionPlanner.CompactContinuationHeader}\n\nSUMMARY\n{CompactionPlanner.SuppressFollowupInstruction}",
            summary.Content);
        Assert.True(result.Messages[^1].Parts.Cast<AttachmentPart>().All(p => p.Kind == AttachmentKind.ResumeReferencedContext));
        Assert.Equal(plan.Selection.PreservedTail[0].Id, result.Messages[2].Id);
        Assert.Equal(plan.Selection.PreservedTail[^1].Id, result.Messages[^2].Id);
        Assert.Equal(plan.ContextPrefixMessages.Count + 1 + recent.Count + plan.Selection.PreservedTail.Count, result.Messages.Count);
    }

    [Fact]
    public void TruncationMarker_IsRequestOnlyAndDoesNotPollutePersistedSummary()
    {
        var history = CompactionSelectorTests.BuildHistory();
        var plan = CompactionPlanner.PlanCompaction(PlanInput(history, trigger: CompactTrigger.Manual));
        var reselected = CompactionSelector.TruncateForPromptTooLong(plan.Selection, null, retriesMade: 0);
        var retryPlan = plan with { Selection = reselected };
        var request = CompactionPlanner.BuildSummaryRequest(reselected, null, plan.ContextPrefixMessages);

        var marker = Assert.IsType<InputTextBlock>(Assert.Single(request.Messages[1].Content));
        Assert.Equal(CompactionSelector.TruncationMarkerText, marker.Text);
        Assert.Equal(MessageRole.User, request.Messages[1].Role);
        Assert.Equal(MessageRole.Assistant, request.Messages[2].Role);

        var formatted = SummarizationPrompt.FormatSummary("<analysis>hidden</analysis><summary>SUMMARY</summary>");
        var result = CompactionPlanner.BuildPostCompactHistory(
            retryPlan, formatted, CompactionPlanner.CreateCompactSummaryMessage(formatted), retryPlan.Selection.PreservedTail, []);

        var summary = Assert.IsType<AttachmentPart>(result.Messages[1].Parts[0]);
        Assert.Equal($"{CompactionPlanner.CompactContinuationHeader}\n\nSummary:\nSUMMARY\n{CompactionPlanner.SuppressFollowupInstruction}", summary.Content);
        Assert.DoesNotContain(CompactionSelector.TruncationMarkerText, summary.Content);
        Assert.DoesNotContain("hidden", summary.Content);
    }

    [Fact]
    public void BuildPostCompactHistory_ProducesBoundaryRecord()
    {
        var history = CompactionSelectorTests.BuildHistory();
        var plan = CompactionPlanner.PlanCompaction(PlanInput(history));
        var summaryMessage = CompactionPlanner.CreateCompactSummaryMessage("SUMMARY");

        var result = CompactionPlanner.BuildPostCompactHistory(
            plan, "SUMMARY", summaryMessage, plan.Selection.PreservedTail, []);

        var boundary = result.Boundary;
        Assert.NotEmpty(boundary.BoundaryId);
        Assert.Equal(CompactTrigger.Auto, boundary.Trigger);
        Assert.Equal(170_000, boundary.PreCompactTokens);
        Assert.Equal(TokenEstimator.EstimateHistoryTokens(result.Messages.ToList()), boundary.PostCompactTokens);
        Assert.Equal(history[^1].Id, boundary.LastSummarizedMessageId);
        Assert.Equal(plan.Selection.PreservedTail[0].Id, boundary.PreservedHeadId);
        Assert.Equal(plan.Selection.PreservedTail[^1].Id, boundary.PreservedTailId);
        Assert.True(boundary.AppliedAt <= DateTimeOffset.UtcNow);
        Assert.True(boundary.AppliedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void BuildPostCompactHistory_ReselectedTailAndBoundaryKeepOriginalHighWaterMark()
    {
        var history = TestMessages.Rounds(6, "Read", 300);
        var plan = CompactionPlanner.PlanCompaction(PlanInput(history));
        var next = CompactionSelector.ReselectForPromptTooLong(history, plan.Selection, 1);
        plan = plan with { Selection = next };

        var result = CompactionPlanner.BuildPostCompactHistory(plan, "summary",
            CompactionPlanner.CreateCompactSummaryMessage("summary"), next.PreservedTail, []);

        Assert.Equal(history[^1].Id, result.Boundary.LastSummarizedMessageId);
        Assert.Equal(history[8].Id, result.Boundary.PreservedHeadId);
        Assert.Equal(history[^1].Id, result.Boundary.PreservedTailId);
        Assert.Equal(history.Skip(8), result.Messages.Skip(1));
        Assert.Equal(history, next.ToSummarize.Concat(next.PreservedTail));
    }

    [Fact]
    public void PlanCompaction_ForwardsInitialTokenGap()
    {
        var history = TestMessages.Rounds(6, "Read", 300);
        var plan = CompactionPlanner.PlanCompaction(PlanInput(history, trigger: CompactTrigger.Reactive)
            with { DeclaredTokenGap = int.MaxValue });

        Assert.Equal(6, plan.Selection.PreservedTail.Count);
        Assert.Equal(history.Take(6), plan.Selection.ToSummarize);
    }

    [Fact]
    public void BuildSummaryRequest_PrependsGeneratedPrefixWithoutSerializingTranscript()
    {
        var history = CompactionSelectorTests.BuildHistory();
        var plan = CompactionPlanner.PlanCompaction(PlanInput(history));
        var generated = new ModelInputMessage(MessageRole.System, [new InputTextBlock("generated system context")]);

        var request = CompactionPlanner.BuildSummaryRequest(plan.Selection, "focus", plan.ContextPrefixMessages,
            plan.SummaryModelRequest.Tools, [generated]);

        Assert.Same(generated, request.Messages[0]);
        Assert.Equal("agents md", Assert.IsType<InputTextBlock>(Assert.Single(request.Messages[1].Content)).Text);
        Assert.Equal(SummarizationPrompt.BuildSummaryUserPrompt("focus"), Assert.IsType<InputTextBlock>(Assert.Single(request.Messages[^1].Content)).Text);
        Assert.Equal(plan.SummaryModelRequest.Tools, request.Tools);
    }

    [Fact]
    public void BuildPostCompactHistory_CanOptOutOfSuppressFollowup()
    {
        var plan = CompactionPlanner.PlanCompaction(PlanInput(CompactionSelectorTests.BuildHistory()));
        var result = CompactionPlanner.BuildPostCompactHistory(plan, "summary",
            CompactionPlanner.CreateCompactSummaryMessage("summary"), plan.Selection.PreservedTail, [], suppressFollowup: false);

        var summary = Assert.IsType<AttachmentPart>(result.Messages[1].Parts[0]);
        Assert.Equal(CompactionPlanner.CompactContinuationHeader + "\n\nsummary", summary.Content);
    }

    [Fact]
    public void BuildPostCompactHistory_FormatsIntermediateSummaryLikeReferenceMessageBuilder()
    {
        var formatted = SummarizationPrompt.FormatSummary("<analysis>first</analysis><analysis>second</analysis><summary>body</summary>");
        var message = CompactionPlanner.CreateCompactSummaryMessage(formatted);
        var plan = CompactionPlanner.PlanCompaction(PlanInput(CompactionSelectorTests.BuildHistory()));
        var result = CompactionPlanner.BuildPostCompactHistory(plan, formatted, message, [], []);

        Assert.Equal(formatted, Assert.IsType<AttachmentPart>(Assert.Single(message.Parts)).Content);
        Assert.Equal($"{CompactionPlanner.CompactContinuationHeader}\n\nSummary:\nbody\n{CompactionPlanner.SuppressFollowupInstruction}",
            Assert.IsType<AttachmentPart>(result.Messages[1].Parts[0]).Content);
    }

    [Fact]
    public void CreateCompactSummaryMessage_IsProviderVisibleUserAttachment()
    {
        var message = CompactionPlanner.CreateCompactSummaryMessage("the summary");

        Assert.Equal(MessageRole.User, message.Role);
        var part = Assert.IsType<AttachmentPart>(Assert.Single(message.Parts));
        Assert.Equal(AttachmentKind.CompactSummary, part.Kind);
        Assert.Equal("the summary", part.Content);
        Assert.True(part.ProviderVisible);
    }
}
