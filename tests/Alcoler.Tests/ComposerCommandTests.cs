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
    [InlineData("/orchestrator", "")]
    [InlineData("/ORCHESTRATOR", "")]
    [InlineData("  /orchestrator  ", "")]
    [InlineData("/orchestrator refactor it", "refactor it")]
    [InlineData("/orchestrator\nrefactor it", "refactor it")]
    public void TryParse_RecognizesOrchestratorCommandWithOptionalPrompt(string text, string expectedPrompt)
    {
        Assert.True(ComposerCommands.TryParse(text, out var command));
        Assert.Equal(ComposerCommandKind.Orchestrator, command.Kind);
        Assert.Equal(expectedPrompt, command.Prompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/planning")]
    [InlineData("/planning design the feature")]
    [InlineData("/plan/feature")]
    [InlineData("/orch")]
    [InlineData("/orchestratorman")]
    [InlineData("/orchestrator/feature")]
    [InlineData("plan design the feature")]
    [InlineData("please /plan design the feature")]
    public void TryParse_RejectsTextThatIsNotABuiltInCommand(string text)
    {
        Assert.False(ComposerCommands.TryParse(text, out _));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/p", true)]
    [InlineData("/PL", true)]
    [InlineData("/plan", true)]
    [InlineData("/planx", false)]
    [InlineData("/o", true)]
    [InlineData("/ORCHESTRATOR", true)]
    [InlineData("/orchestratorx", false)]
    [InlineData("plan", false)]
    [InlineData("", false)]
    public void MatchesSuggestion_ShowsBuiltInsForTheirPrefixes(string token, bool expected) =>
        Assert.Equal(expected, ComposerCommands.MatchesSuggestion(token));

    [Fact]
    public void SuggestionsFor_ReturnsMatchingBuiltIns()
    {
        Assert.Equal([("plan", ComposerCommands.PlanDescription), ("orchestrator", ComposerCommands.OrchestratorDescription)],
            ComposerCommands.SuggestionsFor("/").ToArray());
        var plan = ComposerCommands.SuggestionsFor("/p").ToArray();
        Assert.Equal(["plan"], plan.Select(entry => entry.Name));
        var orchestrator = ComposerCommands.SuggestionsFor("/or").ToArray();
        Assert.Equal(["orchestrator"], orchestrator.Select(entry => entry.Name));
        Assert.Empty(ComposerCommands.SuggestionsFor("/x"));
    }

    [Fact]
    public void IsBuiltIn_MatchesPlanAndOrchestrator()
    {
        Assert.True(ComposerCommands.IsBuiltIn("plan"));
        Assert.True(ComposerCommands.IsBuiltIn("ORCHESTRATOR"));
        Assert.False(ComposerCommands.IsBuiltIn("report"));
    }
}
