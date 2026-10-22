using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class ComposerCommandTests
{
    [Theory]
    [InlineData("/plan", "")]
    [InlineData("/plan ", "")]
    [InlineData("  /plan  ", "")]
    [InlineData("/plan design the feature", "design the feature")]
    [InlineData("/PLAN design the feature", "design the feature")]
    [InlineData("/plan\ndesign the feature", "design the feature")]
    [InlineData("/plan\tdo it", "do it")]
    [InlineData("/plan\u3000design the feature", "design the feature")]
    public void TryParse_RecognizesPlanCommandWithOptionalPrompt(string text, string expectedPrompt)
    {
        Assert.True(ComposerCommands.TryParse(text, out var command));
        Assert.Equal(ComposerCommandKind.Plan, command.Kind);
        Assert.Equal(expectedPrompt, command.Prompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/planning")]
    [InlineData("/planning design the feature")]
    [InlineData("/plan/feature")]
    [InlineData("plan design the feature")]
    [InlineData("please /plan design the feature")]
    public void TryParse_RejectsTextThatIsNotThePlanCommand(string text)
    {
        Assert.False(ComposerCommands.TryParse(text, out _));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/p", true)]
    [InlineData("/PL", true)]
    [InlineData("/plan", true)]
    [InlineData("/planx", false)]
    [InlineData("plan", false)]
    [InlineData("", false)]
    public void MatchesSuggestion_ShowsPlanOnlyForItsPrefix(string token, bool expected) =>
        Assert.Equal(expected, ComposerCommands.MatchesSuggestion(token));
}
