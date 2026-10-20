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
    public void PlanCompaction_SummaryRequestCarriesZeroToolsAndOutputCap()
    {
        var plan = CompactionPlanner.PlanCompaction(PlanInput(CompactionSelectorTests.BuildHistory(), toolCount: 5));
        var manyTools = CompactionPlanner.PlanCompaction(PlanInput(CompactionSelectorTests.BuildHistory(), toolCount: 150));

        Assert.Empty(plan.SummaryModelRequest.Tools);
        Assert.Empty(manyTools.SummaryModelRequest.Tools);
        Assert.Equal(CompactPolicy.SummaryMaxOutputTokens, plan.SummaryModelRequest.Options.MaxOutputTokens);
        Assert.Equal(20_000, CompactPolicy.SummaryMaxOutputTokens);
    }

    [Fact]
    public void PlanCompaction_RequestContainsOnlySystemInstructionAndSummaryPrompt()
    {
        var plan = CompactionPlanner.PlanCompaction(PlanInput(CompactionSelectorTests.BuildHistory()));

        Assert.Equal(2, plan.SummaryModelRequest.Messages.Count);
        Assert.Equal(MessageRole.System, plan.SummaryModelRequest.Messages[0].Role);
        Assert.Equal(MessageRole.User, plan.SummaryModelRequest.Messages[1].Role);
        Assert.Equal(
            CompactionPlanner.SummarySystemInstruction,
            Assert.IsType<InputTextBlock>(plan.SummaryModelRequest.Messages[0].Content[0]).Text);
        var promptText = Assert.IsType<InputTextBlock>(plan.SummaryModelRequest.Messages[1].Content[0]).Text;
        Assert.StartsWith(SummarizationPrompt.TextOnlyDirective, promptText);
        Assert.EndsWith(SummarizationPrompt.TextOnlyDirective, promptText.TrimEnd());
        Assert.Contains("[tool call ReadFile]", promptText);
        Assert.Contains("## Optional Next Step", promptText);
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

        var tail = ContextBuilder.Assemble(new ContextAssemblyInput("", "", "", null, null,
            history.Skip(3).ToList(), [], new ModelCallOptions()));
        var tailRequest = tail.Request with { Messages = tail.Request.Messages.Skip(tail.PrefixMessageCount).ToArray() };
        Assert.Equal(120_000 + TokenEstimator.EstimateRequestTokens(tailRequest), plan.PreCompactTokens);
    }

    [Fact]
    public void BuildRecentlyReadFilesAttachments_AppliesCountAndTokenCaps()
    {
        var files = new List<(string Path, string Content)>();
        for (var i = 0; i < 7; i++)
            files.Add(($"file{i}.cs", new string('a', 100) + i));

        var messages = CompactionPlanner.BuildRecentlyReadFilesAttachments(files);

        var message = Assert.Single(messages);
        Assert.Equal(MessageRole.User, message.Role);
        var parts = message.Parts.Cast<AttachmentPart>().ToList();
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
        var parts = Assert.Single(messages).Parts.Cast<AttachmentPart>().ToList();

        Assert.Equal(2, parts.Count);
        Assert.Contains("namespace T {}", parts[0].Content);
        Assert.Equal(
            "File big.cs was read before compaction; use the ReadFile tool if you need it again",
            parts[1].Content);
    }

    [Fact]
    public void BuildRecentlyReadFilesAttachments_EmptyInputYieldsNoMessage()
    {
        Assert.Empty(CompactionPlanner.BuildRecentlyReadFilesAttachments([]));
    }

    [Fact]
    public void BuildPostCompactHistory_OrdersPrefixSummaryAttachmentsThenTail()
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
        Assert.Equal("SUMMARY", summary.Content);
        Assert.True(result.Messages[2].Parts.Cast<AttachmentPart>().All(p => p.Kind == AttachmentKind.ResumeReferencedContext));
        Assert.Equal(plan.Selection.PreservedTail[0].Id, result.Messages[3].Id);
        Assert.Equal(plan.Selection.PreservedTail[^1].Id, result.Messages[^1].Id);
        Assert.Equal(plan.ContextPrefixMessages.Count + 1 + recent.Count + plan.Selection.PreservedTail.Count, result.Messages.Count);
    }

    [Fact]
    public void BuildPostCompactHistory_TruncationMarkerPrefixesSummary()
    {
        var history = CompactionSelectorTests.BuildHistory();
        var plan = CompactionPlanner.PlanCompaction(PlanInput(history));
        var reselected = CompactionSelector.ReselectForPromptTooLong(history, plan.Selection, declaredTokenGap: null);
        var retryPlan = plan with { Selection = reselected };

        var result = CompactionPlanner.BuildPostCompactHistory(
            retryPlan, "SUMMARY", CompactionPlanner.CreateCompactSummaryMessage("SUMMARY"), retryPlan.Selection.PreservedTail, []);

        var summary = Assert.IsType<AttachmentPart>(result.Messages[1].Parts[0]);
        Assert.StartsWith(CompactionSelector.TruncationMarkerText, summary.Content);
        Assert.EndsWith("SUMMARY", summary.Content);
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
        Assert.Equal(plan.Selection.ToSummarize[^1].Id, boundary.LastSummarizedMessageId);
        Assert.Equal(plan.Selection.PreservedTail[0].Id, boundary.PreservedHeadId);
        Assert.Equal(plan.Selection.PreservedTail[^1].Id, boundary.PreservedTailId);
        Assert.True(boundary.AppliedAt <= DateTimeOffset.UtcNow);
        Assert.True(boundary.AppliedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
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
