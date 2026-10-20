using Alcoler.Kernel.Prompts;
using Xunit;

namespace Alcoler.Kernel.Tests.Prompts;

public class PromptTextsTests
{
    [Fact]
    public void CliPrefix_MatchesReference() =>
        Assert.Equal(PromptGolden.CliPrefix, PromptTexts.CliPrefix);

    [Fact]
    public void StableIdentity_MatchesReference()
    {
        Assert.Equal(PromptGolden.Identity, PromptTexts.StableIdentity);
        Assert.True(PromptTexts.StableIdentity.StartsWith('\n'), "Identity section must keep its leading blank line.");
    }

    [Fact]
    public void HarnessBlock_IsContainedInStableIdentity() =>
        Assert.Contains("\n\n" + PromptTexts.HarnessBlock, PromptTexts.StableIdentity);

    [Fact]
    public void DynamicBehavior_MatchesReference() =>
        Assert.Equal(PromptGolden.DynamicBehavior, PromptTexts.DynamicBehavior);

    [Fact]
    public void ContextManagementAndAutonomy_MatchesReference() =>
        Assert.Equal(PromptGolden.ContextManagementAndAutonomy, PromptTexts.ContextManagementAndAutonomy);

    [Fact]
    public void SkillSlashGuidance_UsesVerbatimText() =>
        Assert.StartsWith("- When the user types `/<skill-name>`, invoke it via Skill.", PromptTexts.SkillSlashGuidance);

    [Fact]
    public void SessionSpecificGuidance_IncludesSkillLine_WhenSkillToolAndSkillsPresent()
    {
        var guidance = PromptTexts.SessionSpecificGuidance(skillToolAvailable: true, hasSkills: true);

        Assert.Equal("# Session-specific guidance\n" + PromptTexts.SkillSlashGuidance, guidance);
    }

    [Fact]
    public void SessionSpecificGuidance_Null_WhenSkillToolUnavailable()
    {
        Assert.Null(PromptTexts.SessionSpecificGuidance(skillToolAvailable: false, hasSkills: true));
        Assert.Null(PromptTexts.SessionSpecificGuidance(skillToolAvailable: true, hasSkills: false));
    }
}
