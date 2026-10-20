using Alcoler.Kernel.Prompts;
using Xunit;

namespace Alcoler.Kernel.Tests.Prompts;

public class ContextPrefixTests
{
    [Fact]
    public void WrapSections_MatchesReferenceComposition()
    {
        var agentsMd = AgentsMdSection.Render(
        [
            new AgentInstructionSource(
                "C:/Users/u/.alcoler/AGENTS.md",
                "AGENTS.md",
                "user body",
                Truncated: true,
                AgentInstructionScope.User),
            new AgentInstructionSource(
                "C:/ws/AGENTS.md",
                "AGENTS.md",
                "ws body",
                Truncated: false,
                AgentInstructionScope.Workspace),
        ]);
        var currentDate = EnvironmentSections.CurrentDateSection("2026-10-20");

        Assert.Equal(PromptGolden.ContextWrapper, ContextPrefix.WrapSections([agentsMd!, currentDate]));
    }

    [Fact]
    public void WrapSections_JoinsSectionsWithBlankLine()
    {
        var wrapped = ContextPrefix.WrapSections(["first", "second"]);

        Assert.Equal(
            "As you answer the user's questions, you can use the following context:\nfirst\n\nsecond\n\n      IMPORTANT: this context may or may not be relevant to your tasks. You should not respond to this context unless it is highly relevant to your task.",
            wrapped);
    }

    [Fact]
    public void WrapSections_NoSections_ReturnsNull() =>
        Assert.Null(ContextPrefix.WrapSections([]));

    [Fact]
    public void ReminderWrapped_WrapsInSystemReminderWithTrailingNewline() =>
        Assert.Equal(
            "<system-reminder>\nAs you answer the user's questions, you can use the following context:\nbody\n\n      IMPORTANT: this context may or may not be relevant to your tasks. You should not respond to this context unless it is highly relevant to your task.\n</system-reminder>\n",
            ContextPrefix.ReminderWrapped(["body"]));

    [Fact]
    public void ReminderWrapped_NoSections_ReturnsNull() =>
        Assert.Null(ContextPrefix.ReminderWrapped([]));
}
