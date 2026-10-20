using Alcoler.Kernel.Context;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class SummarizationPromptTests
{
    [Fact]
    public void Prompt_ContainsAllNineSectionHeadings()
    {
        var prompt = SummarizationPrompt.BuildSummaryUserPrompt(CompactionSelectorTests.BuildHistory());

        Assert.Contains("## Primary Request and Intent", prompt);
        Assert.Contains("## Key Technical Concepts", prompt);
        Assert.Contains("## Files and Code Sections", prompt);
        Assert.Contains("## Errors and Fixes", prompt);
        Assert.Contains("## Problem Solving", prompt);
        Assert.Contains("## All User Messages", prompt);
        Assert.Contains("## Pending Tasks", prompt);
        Assert.Contains("## Current Work", prompt);
        Assert.Contains("## Optional Next Step", prompt);
    }

    [Fact]
    public void Prompt_ContainsUserMessagesVerbatim()
    {
        var history = new List<Alcoler.Contracts.Conversation.ConversationMessage>
        {
            TestMessages.UserText("Please fix the flaky Frobber test NOW"),
            TestMessages.AssistantText("on it"),
            TestMessages.UserText("and do not forget C:\\temp"),
        };

        var prompt = SummarizationPrompt.BuildSummaryUserPrompt(history);

        Assert.Contains("Please fix the flaky Frobber test NOW", prompt);
        Assert.Contains("and do not forget C:\\temp", prompt);
        Assert.Contains("[user]", prompt);
        Assert.Contains("[assistant]", prompt);
    }

    [Fact]
    public void Prompt_OpensAndClosesWithTextOnlyDirective()
    {
        var prompt = SummarizationPrompt.BuildSummaryUserPrompt([]);

        Assert.StartsWith(SummarizationPrompt.TextOnlyDirective, prompt);
        Assert.EndsWith(SummarizationPrompt.TextOnlyDirective, prompt.TrimEnd());
        Assert.StartsWith("CRITICAL: Respond with TEXT ONLY", SummarizationPrompt.TextOnlyDirective);
    }

    [Fact]
    public void Prompt_AppendsManualInstructions()
    {
        var prompt = SummarizationPrompt.BuildSummaryUserPrompt(
            CompactionSelectorTests.BuildHistory(),
            manualInstructions: "Focus on the database migration work.");

        Assert.Contains("Focus on the database migration work.", prompt);
        Assert.True(prompt.IndexOf("Focus on the database migration work.", StringComparison.Ordinal)
            < prompt.LastIndexOf(SummarizationPrompt.TextOnlyDirective, StringComparison.Ordinal));
    }

    [Fact]
    public void Prompt_IncludesToolCallsAndErrorsInTranscript()
    {
        var prompt = SummarizationPrompt.BuildSummaryUserPrompt(CompactionSelectorTests.BuildHistory());

        Assert.Contains("[tool call ReadFile]", prompt);
        Assert.Contains("r1", prompt);
    }
}
