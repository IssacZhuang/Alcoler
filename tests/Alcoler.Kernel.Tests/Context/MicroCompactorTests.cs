using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Kernel.Context;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class MicroCompactorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NotIdleAndBelowPressure_IsNoOp()
    {
        var history = TestMessages.Rounds(count: 8, "ReadFile", resultChars: 600);

        var result = MicroCompactor.Apply(history, Now.AddMinutes(-5), Now, autoCompactThresholdTokens: 166_000);

        Assert.Same(history, result.Messages);
        Assert.Empty(result.ClearedParts);
        Assert.Equal(0, result.EstimatedTokensSaved);
    }

    [Fact]
    public void IdleTrigger_ClearsResultsBeyondFiveMostRecentGroups()
    {
        var history = TestMessages.Rounds(count: 8, "ReadFile", resultChars: 600);

        var result = MicroCompactor.Apply(history, Now.AddMinutes(-61), Now, autoCompactThresholdTokens: 166_000);

        Assert.Equal(3, result.ClearedParts.Count);
        Assert.True(result.EstimatedTokensSaved >= CompactPolicy.MicroCompactMinSavingsTokens);

        var cleared = result.Messages
            .SelectMany(m => m.Parts)
            .OfType<ToolResultPart>()
            .Where(p => result.ClearedParts.Contains(p.Id))
            .ToList();
        Assert.Equal(3, cleared.Count);
        Assert.All(cleared, p =>
        {
            var block = Assert.IsType<ToolResultText>(Assert.Single(p.Content));
            Assert.Equal(MicroCompactor.ClearedContentText, block.Text);
        });

        for (var i = 1; i <= 10; i++)
            Assert.Same(history[^i], result.Messages[^i]);
    }

    [Fact]
    public void PressureTrigger_ClearsEvenWhenRecentlyActive()
    {
        var history = TestMessages.Rounds(count: 7, "ReadFile", resultChars: 5_000);
        var threshold = 10_000; // trigger = min(9_000, 8_000) = 8_000 tokens

        var result = MicroCompactor.Apply(history, Now, Now, autoCompactThresholdTokens: threshold);

        Assert.True(TokenEstimator.EstimateHistoryTokens(history) >= 8_000);
        Assert.Equal(2, result.ClearedParts.Count);
    }

    [Fact]
    public void MediaAndErrorResults_AreNeverCleared()
    {
        var history = new List<ConversationMessage>();
        var (normalRound, normalCall) = TestMessages.Round("ReadFile", new string('x', 1_000));

        var mediaCall = StrongId.NewToolCallId();
        var mediaRound = new List<ConversationMessage>
        {
            TestMessages.AssistantToolCall(mediaCall, "ReadFile", new { path = "img.png" }),
            TestMessages.ToolResult(mediaCall,
            [
                new ToolResultText(new string('x', 1_000)),
                new ToolResultMedia("alcoler-artifact://s/1.png", "image/png", "1.png"),
            ]),
        };
        var (errorRound, _) = TestMessages.Round("Bash", new string('x', 1_000), isError: true);

        history.AddRange(normalRound);
        history.AddRange(mediaRound);
        history.AddRange(errorRound);
        history.AddRange(TestMessages.Rounds(count: 5, "Grep", 600));

        var result = MicroCompactor.Apply(history, Now.AddMinutes(-90), Now, autoCompactThresholdTokens: 166_000);

        var clearedPart = Assert.Single(result.ClearedParts);
        Assert.Equal(normalRound[1].Parts[0].Id, clearedPart);

        var mediaPart = Assert.IsType<ToolResultPart>(result.Messages[3].Parts[0]);
        Assert.Equal(2, mediaPart.Content.Count);
        Assert.Contains(mediaPart.Content, b => b is ToolResultMedia);
        var errorPart = Assert.IsType<ToolResultPart>(result.Messages[5].Parts[0]);
        Assert.True(errorPart.IsError);
        Assert.Equal(new string('x', 1_000), Assert.IsType<ToolResultText>(errorPart.Content[0]).Text);
    }

    [Fact]
    public void IneligibleToolNames_AreNotCleared()
    {
        var history = new List<ConversationMessage>();
        history.AddRange(TestMessages.Round("TodoWrite", new string('x', 600)).Messages);
        history.AddRange(TestMessages.Round("ApplyPatch", new string('x', 600)).Messages);
        history.AddRange(TestMessages.Rounds(count: 1, "ReadFile", 1_000));
        history.AddRange(TestMessages.Rounds(count: 5, "Grep", 600));

        var result = MicroCompactor.Apply(history, Now.AddMinutes(-90), Now, autoCompactThresholdTokens: 166_000);

        var clearedPart = Assert.Single(result.ClearedParts);
        var remainingResults = result.Messages
            .SelectMany(m => m.Parts)
            .OfType<ToolResultPart>()
            .ToList();
        Assert.Equal(clearedPart, remainingResults.Single(p => p.Id == clearedPart).Id);
        Assert.All(
            remainingResults.Where(p => p.Id != clearedPart),
            p =>
            {
                var text = Assert.IsType<ToolResultText>(Assert.Single(p.Content)).Text;
                Assert.DoesNotContain(MicroCompactor.ClearedContentText, text);
            });
    }

    [Fact]
    public void TaskOutputResults_AreCleared()
    {
        var history = new List<ConversationMessage>();
        history.AddRange(TestMessages.Rounds(count: 8, "TaskOutput", 600));

        var result = MicroCompactor.Apply(history, Now.AddMinutes(-90), Now, autoCompactThresholdTokens: 166_000);

        Assert.Equal(3, result.ClearedParts.Count);
    }

    [Fact]
    public void SavingsBelowMinimum_SkipsCompaction()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserText("hi"),
        };
        history.AddRange(TestMessages.Round("ReadFile", "tiny").Messages);
        history.AddRange(TestMessages.Rounds(count: 5, "ReadFile", 600));

        var result = MicroCompactor.Apply(history, Now.AddMinutes(-90), Now, autoCompactThresholdTokens: 166_000);

        Assert.Same(history, result.Messages);
        Assert.Empty(result.ClearedParts);
        Assert.Equal(0, result.EstimatedTokensSaved);
    }

    [Fact]
    public void FilePartsInClearedMessages_ArePreserved()
    {
        var callId = StrongId.NewToolCallId();
        var filePart = new FilePart(StrongId.NewPartId(), "alcoler-artifact://s/a.png", "image/png", "a.png");
        var round = new List<ConversationMessage>
        {
            TestMessages.AssistantToolCall(callId, "ReadFile", new { path = "a.cs" }),
                new ConversationMessage(
                StrongId.NewMessageId(),
                MessageRole.User,
                [TestMessages.ToolResult(callId, new string('x', 1_000)).Parts[0], filePart],
                Now),
        };

        var history = new List<ConversationMessage>(round);
        history.AddRange(TestMessages.Rounds(count: 5, "ReadFile", 600));

        var result = MicroCompactor.Apply(history, Now.AddMinutes(-90), Now, autoCompactThresholdTokens: 166_000);

        Assert.Single(result.ClearedParts);
        Assert.Same(filePart, result.Messages[1].Parts[1]);
    }
}
