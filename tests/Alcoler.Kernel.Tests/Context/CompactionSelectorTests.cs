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
    public void ReselectForPromptTooLong_WithoutGap_DropsSingleOldestRoundBehindMarker()
    {
        var history = BuildHistory();
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Manual);

        var reselected = CompactionSelector.ReselectForPromptTooLong(history, selection, declaredTokenGap: null);

        Assert.Equal(CompactionSelector.TruncationMarkerText, reselected.TruncationMarker);
        Assert.Equal(1, reselected.DroppedRoundCount);
        Assert.Equal(4, reselected.ToSummarize.Count);
        Assert.Equal(history[3].Id, reselected.ToSummarize[0].Id);
        Assert.Empty(reselected.PreservedTail);
    }

    [Fact]
    public void ReselectForPromptTooLong_WithLargeGap_DropsRoundsCoveringGapButKeepsOne()
    {
        var history = BuildHistory();
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Manual);

        var reselected = CompactionSelector.ReselectForPromptTooLong(history, selection, declaredTokenGap: 10_000_000);

        Assert.Equal(2, reselected.DroppedRoundCount);
        Assert.Equal(2, reselected.ToSummarize.Count);
        Assert.Equal(history[5].Id, reselected.ToSummarize[0].Id);
        Assert.Equal(history[6].Id, reselected.ToSummarize[1].Id);
    }

    [Fact]
    public void ReselectForPromptTooLong_RepeatedRetriesKeepDropping()
    {
        var history = BuildHistory();
        var selection = CompactionSelector.SelectForCompaction(history, CompactTrigger.Manual);

        var first = CompactionSelector.ReselectForPromptTooLong(history, selection, null);
        var second = CompactionSelector.ReselectForPromptTooLong(history, first, null);

        Assert.Equal(2, second.DroppedRoundCount);
        Assert.Equal(2, second.ToSummarize.Count);
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
        history.AddRange(TestMessages.Round("ReadFile", "r1").Messages);
        history.AddRange(TestMessages.Round("Grep", "r2").Messages);
        history.AddRange(TestMessages.Round("Bash", "r3").Messages);
        return history;
    }
}
