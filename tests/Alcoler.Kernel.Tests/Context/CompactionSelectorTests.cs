using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Kernel.Context;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class CompactionSelectorTests
{
    [Fact]
    public void GroupByAssistantLedRounds_LeadingGroupThenOneGroupPerAssistantMessage()
    {
        var u1 = TestMessages.UserText("u1");
        var a1 = TestMessages.AssistantText("a1");
        var u2 = TestMessages.UserText("u2");
        var a2 = TestMessages.AssistantText("a2");
        var u3 = TestMessages.UserText("u3");

        var groups = CompactionSelector.GroupByAssistantLedRounds([u1, a1, u2, a2, u3]);

        Assert.Equal(3, groups.Count);
        Assert.False(groups[0].IsAssistantLed);
        Assert.Equal([u1], groups[0].Messages);
        Assert.True(groups[1].IsAssistantLed);
        Assert.Equal([a1, u2], groups[1].Messages);
        Assert.True(groups[2].IsAssistantLed);
        Assert.Equal([a2, u3], groups[2].Messages);
    }

    [Fact]
    public void GroupByAssistantLedRounds_ConsecutiveAssistantsSplitAndEmptyTolerated()
    {
        var a1 = TestMessages.AssistantText("a1");
        var a2 = TestMessages.AssistantText("a2");

        var groups = CompactionSelector.GroupByAssistantLedRounds([a1, a2]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.True(g.IsAssistantLed));
        Assert.Empty(CompactionSelector.GroupByAssistantLedRounds([]));
    }

    [Fact]
    public void CountContextPrefixMessages_CountsLeadingAttachmentBearingUserMessages()
    {
        var p1 = TestMessages.UserAttachment(AttachmentKind.ContextPrefix, "agents");
        var p2 = TestMessages.UserAttachment(AttachmentKind.SkillsListing, "skills");
        var reminder = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.User,
            [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, "note")],
            DateTimeOffset.UtcNow);
        var prompt = TestMessages.UserText("real prompt");
        var assistant = TestMessages.AssistantText("hi");

        Assert.Equal(3, CompactionSelector.CountContextPrefixMessages([p1, p2, reminder, prompt, assistant]));
        Assert.Equal(0, CompactionSelector.CountContextPrefixMessages([prompt, p1]));
        Assert.Equal(0, CompactionSelector.CountContextPrefixMessages([assistant, p1]));
        Assert.Equal(0, CompactionSelector.CountContextPrefixMessages([]));
    }

    [Fact]
    public void CountContextPrefixMessages_UserTextWithReminderAttachmentIsNotPrefix()
    {
        var message = new ConversationMessage(
            StrongId.NewMessageId(),
            MessageRole.User,
        [
            new TextPart(StrongId.NewPartId(), "do work"),
            new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, "note"),
        ],
            DateTimeOffset.UtcNow);

        Assert.Equal(0, CompactionSelector.CountContextPrefixMessages([message]));
    }

    [Fact]
    public void SelectForCompaction_Auto_PreservesContextPrefixAndLastRound()
    {
        var history = BuildHistory();

        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Auto);

        Assert.Equal(1, selection.ContextPrefixCount);
        Assert.Equal(4, selection.ToSummarize.Count);
        Assert.Equal(2, selection.PreservedTail.Count);
        Assert.Equal(history[1].Id, selection.ToSummarize[0].Id);
        Assert.Equal(history[4].Id, selection.ToSummarize[^1].Id);
        Assert.Equal(history[5].Id, selection.PreservedTail[0].Id);
        Assert.Equal(history[6].Id, selection.PreservedTail[^1].Id);
    }

    [Fact]
    public void SelectForCompaction_Reactive_PreservesContextPrefixAndLastRound()
    {
        var history = BuildHistory();

        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Reactive, declaredTokenGap: 5_000);

        Assert.Equal(1, selection.ContextPrefixCount);
        Assert.Equal(2, selection.PreservedTail.Count);
        Assert.Equal(4, selection.ToSummarize.Count);
    }

    [Fact]
    public void SelectForCompaction_Manual_SummarizesEverythingExceptPrefix()
    {
        var history = BuildHistory();

        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Manual);

        Assert.Equal(1, selection.ContextPrefixCount);
        Assert.Empty(selection.PreservedTail);
        Assert.Equal(6, selection.ToSummarize.Count);
        Assert.Equal(history[1].Id, selection.ToSummarize[0].Id);
        Assert.Equal(history[6].Id, selection.ToSummarize[^1].Id);
    }

    [Fact]
    public void TruncateForPromptTooLong_WithoutGap_DropsSingleOldestRoundBehindMarker()
    {
        var history = BuildHistory();
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Manual);

        var reselected = CompactionSelector.TruncateForPromptTooLong(selection, declaredTokenGap: null, retriesMade: 0);

        Assert.Equal(CompactionSelector.TruncationMarkerText, reselected.TruncationMarker);
        Assert.Equal(1, reselected.DroppedRoundCount);
        Assert.Equal(4, reselected.ToSummarize.Count);
        Assert.Equal(history[3].Id, reselected.ToSummarize[0].Id);
        Assert.Empty(reselected.PreservedTail);
    }

    [Fact]
    public void TruncateForPromptTooLong_WithLargeGap_DropsRoundsCoveringGapButKeepsOne()
    {
        var history = BuildHistory();
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Manual);

        var reselected = CompactionSelector.TruncateForPromptTooLong(selection, declaredTokenGap: 10_000_000, retriesMade: 0);

        Assert.Equal(2, reselected.DroppedRoundCount);
        Assert.Equal(2, reselected.ToSummarize.Count);
        Assert.Equal(history[5].Id, reselected.ToSummarize[0].Id);
        Assert.Equal(history[6].Id, reselected.ToSummarize[1].Id);
    }

    [Fact]
    public void TruncateForPromptTooLong_RepeatedRetriesKeepDropping()
    {
        var history = BuildHistory();
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Manual);

        var first = CompactionSelector.TruncateForPromptTooLong(selection, null, retriesMade: 0);
        var second = CompactionSelector.TruncateForPromptTooLong(first, null, retriesMade: 1);

        Assert.Equal(2, second.DroppedRoundCount);
        Assert.Equal(2, second.ToSummarize.Count);
    }

    [Theory]
    [InlineData(CompactTrigger.Auto)]
    [InlineData(CompactTrigger.Reactive)]
    public void ReselectForPromptTooLong_MovesRecentRoundsToVerbatimTail(CompactTrigger trigger)
    {
        var history = TestMessages.Rounds(6, "Read", 300);
        var selection = CompactionSelector.SelectForCompaction(history, trigger);
        var groups = CompactionSelector.GroupByAssistantLedRounds(selection.ToSummarize);
        var gap = TokenEstimator.EstimateHistoryTokens(groups[^1].Messages) + 1;

        var next = CompactionSelector.ReselectForPromptTooLong(history, selection, gap, trigger);

        Assert.Equal(history.Take(6), next.ToSummarize);
        Assert.Equal(history.Skip(6), next.PreservedTail);
        Assert.Null(next.TruncationMarker);
        Assert.Equal(0, next.DroppedRoundCount);
        Assert.Equal(history, next.ToSummarize.Concat(next.PreservedTail));
    }

    [Fact]
    public void ReselectForPromptTooLong_HugeGapMovesHalfInsteadOfAllButOne()
    {
        var history = TestMessages.Rounds(8, "Read", 30);
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Auto);

        var next = CompactionSelector.ReselectForPromptTooLong(history, selection, int.MaxValue);

        Assert.Equal(8, next.ToSummarize.Count);
        Assert.Equal(8, next.PreservedTail.Count);
        Assert.Equal(history, next.ToSummarize.Concat(next.PreservedTail));
    }

    [Fact]
    public void ReselectForPromptTooLong_IsNaturallyBoundedAndNotCappedAtThreeRetries()
    {
        var history = TestMessages.Rounds(8, "Read", 30);
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Auto);
        for (var retry = 0; retry < 5; retry++)
        {
            var next = CompactionSelector.ReselectForPromptTooLong(history, selection, null);
            Assert.NotSame(selection, next);
            Assert.Equal(history, next.ToSummarize.Concat(next.PreservedTail));
            selection = next;
        }

        Assert.Equal(4, selection.ToSummarize.Count);
        Assert.Same(selection, CompactionSelector.ReselectForPromptTooLong(history, selection, null));
    }

    [Fact]
    public void ReselectForPromptTooLong_ManualDoesNotUseRecentPreservation()
    {
        var history = TestMessages.Rounds(6, "Read", 30);
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Manual);

        Assert.Same(selection, CompactionSelector.ReselectForPromptTooLong(history, selection, 500, CompactTrigger.Manual));
    }

    [Fact]
    public void SelectForCompaction_InitialGapSubtractsAlreadyPreservedTailTokens()
    {
        var history = TestMessages.Rounds(6, "Read", 300);
        var lastGroupTokens = TokenEstimator.EstimateHistoryTokens(history.TakeLast(2).ToList());

        var unchanged = CompactionSelector.SelectForCompaction(history, CompactTrigger.Reactive, lastGroupTokens);
        var enlarged = CompactionSelector.SelectForCompaction(history, CompactTrigger.Reactive, lastGroupTokens + 1);

        Assert.Equal(2, unchanged.PreservedTail.Count);
        Assert.Equal(4, enlarged.PreservedTail.Count);
        Assert.Equal(history.Take(8), enlarged.ToSummarize);
        Assert.Equal(history.Skip(8), enlarged.PreservedTail);
    }

    [Fact]
    public void SelectForCompaction_InitialHugeGapUsesHalfFallbackAndKeepsSummaryEligible()
    {
        var history = TestMessages.Rounds(6, "Read", 30);

        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Reactive, int.MaxValue);

        Assert.Equal(6, selection.ToSummarize.Count);
        Assert.Equal(6, selection.PreservedTail.Count);
        Assert.True(CompactionSelector.HasEnoughMessagesToCompact(selection.ToSummarize));
    }

    [Fact]
    public void SelectForCompaction_OnlyRoundIsNotPreservedAway()
    {
        var history = TestMessages.Round("Read", "result").Messages;

        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Auto);

        Assert.Equal(history, selection.ToSummarize);
        Assert.Empty(selection.PreservedTail);
        Assert.False(CompactionSelector.HasEnoughMessagesToCompact(selection.ToSummarize));
    }

    [Fact]
    public void HasEnoughMessagesToCompact_RequiresTwoNonPrefixGroupsAndAssistant()
    {
        var prefix = TestMessages.UserAttachment(AttachmentKind.ContextPrefix, "agents");
        var summary = TestMessages.UserAttachment(AttachmentKind.CompactSummary, "previous summary");
        var assistant = TestMessages.AssistantText("work");
        Assert.False(CompactionSelector.HasEnoughMessagesToCompact([]));
        Assert.False(CompactionSelector.HasEnoughMessagesToCompact([prefix, summary]));
        Assert.False(CompactionSelector.HasEnoughMessagesToCompact([prefix, assistant]));
        Assert.True(CompactionSelector.HasEnoughMessagesToCompact([prefix, summary, assistant]));
        Assert.True(CompactionSelector.HasEnoughMessagesToCompact([TestMessages.UserText("task"), assistant]));
    }

    [Fact]
    public void TruncateForPromptTooLong_NoGapDropsTwentyPercentAndStopsAfterThreeRetries()
    {
        var history = TestMessages.Rounds(10, "Read", 30);
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Manual);
        var first = CompactionSelector.TruncateForPromptTooLong(selection, null, retriesMade: 0);
        var second = CompactionSelector.TruncateForPromptTooLong(first, null, retriesMade: 1);
        var third = CompactionSelector.TruncateForPromptTooLong(second, null, retriesMade: 2);

        Assert.Equal(2, first.DroppedRoundCount);
        Assert.Equal(history.Skip(4), first.ToSummarize);
        Assert.Same(third, CompactionSelector.TruncateForPromptTooLong(third, null, retriesMade: 3));
    }

    [Fact]
    public void ContextPrefixes_IncludeSystemAndLegacyReminderTextButNotRealPrompt()
    {
        var system = TestMessages.UserText("system") with { Role = MessageRole.System };
        var legacy = TestMessages.UserText("  <system-reminder>context</system-reminder>");
        var prompt = TestMessages.UserText("real task");

        Assert.Equal(2, CompactionSelector.CountContextPrefixMessages([system, legacy, prompt]));
        Assert.Equal(0, CompactionSelector.CountContextPrefixMessages([legacy with { Source = MessageSource.RealUser }]));
        Assert.Equal(0, CompactionSelector.CountContextPrefixMessages([legacy with { Source = MessageSource.TodoReminder }]));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    [InlineData(3, 0)]
    [InlineData(5, 0)]
    public void AttemptsRemaining_TracksPromptTooLongBudget(int attemptsMade, int expected)
    {
        Assert.Equal(expected, CompactionSelector.AttemptsRemaining(attemptsMade));
    }

    internal static List<ConversationMessage> BuildHistory()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserAttachment(AttachmentKind.ContextPrefix, "agents md"),
        };
        history.AddRange(TestMessages.Round("Read", "r1").Messages);
        history.AddRange(TestMessages.Round("Grep", "r2").Messages);
        history.AddRange(TestMessages.Round("Bash", "r3").Messages);
        return history;
    }
}
