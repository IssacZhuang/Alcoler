using System.Text.Json;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Tools;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.WorkspaceTools;

public class AskUserQuestionToolTests
{
    private const string LibraryQuestionJson =
        """
        {"questions": [{"question": "Which library should we use for date formatting?", "header": "Library", "options": [{"label": "NodaTime (Recommended)", "description": "Most precise"}, {"label": "DateTime", "description": "Built in"}]}]}
        """;

    private const string TwoQuestionsJson =
        """
        {"questions": [{"question": "Which library should we use for date formatting?", "header": "Library", "options": [{"label": "NodaTime (Recommended)", "description": "Most precise"}, {"label": "DateTime", "description": "Built in"}]}, {"question": "Which features do you want to enable?", "header": "Features", "multiSelect": true, "options": [{"label": "Diagrams", "description": "Render diagrams"}, {"label": "Previews", "description": "Render previews"}]}]}
        """;

    [Fact]
    public async Task ValidateInput_RejectsInvalidShapes()
    {
        var tool = new AskUserQuestionTool();

        foreach (var json in new[]
        {
            "{}",
            """{"questions": "help"}""",
            """{"questions": []}""",
            """{"questions": [{"question": "Q1?", "header": "H1", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}, {"question": "Q2?", "header": "H2", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}, {"question": "Q3?", "header": "H3", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}, {"question": "Q4?", "header": "H4", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}, {"question": "Q5?", "header": "H5", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}]}""",
            """{"questions": [{"question": "", "header": "Library", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}]}""",
            """{"questions": [{"question": "Q?", "header": "Too long header", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}]}""",
            """{"questions": [{"question": "Q?", "header": "Library", "options": [{"label": "A", "description": "a"}]}]}""",
            """{"questions": [{"question": "Q?", "header": "Library", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}, {"label": "C", "description": "c"}, {"label": "D", "description": "d"}, {"label": "E", "description": "e"}]}]}""",
            """{"questions": [{"question": "Q?", "header": "Library", "options": [{"label": "A", "description": ""}, {"label": "B", "description": "b"}]}]}""",
            """{"questions": [{"question": "Q?", "header": "Library", "options": [{"label": "A", "description": "a"}, {"label": "A", "description": "b"}]}]}""",
            """{"questions": [{"question": "Q?", "header": "Library", "multiSelect": "yes", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}]}""",
            """{"questions": [{"question": "Same?", "header": "H1", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}, {"question": "Same?", "header": "H2", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}]}""",
            """{"questions": [{"question": "Q1?", "header": "Same", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}, {"question": "Q2?", "header": "Same", "options": [{"label": "A", "description": "a"}, {"label": "B", "description": "b"}]}]}"""
        })
        {
            var issue = await tool.ValidateInputAsync(
                JsonDocument.Parse(json).RootElement, CancellationToken.None);
            Assert.NotNull(issue);
        }
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("other")]
    [InlineData("  OTHER  ")]
    public async Task ValidateInput_RejectsOtherOption(string forbiddenLabel)
    {
        var json = $$"""
            {"questions": [{"question": "Q?", "header": "Library", "options": [{"label": "{{forbiddenLabel}}", "description": "custom"}, {"label": "Choice", "description": "pick me"}]}]}
            """;

        var issue = await new AskUserQuestionTool().ValidateInputAsync(
            JsonDocument.Parse(json).RootElement, CancellationToken.None);

        Assert.NotNull(issue);
        Assert.Equal("other_option_forbidden", issue!.Code);
        Assert.Contains("Do not include an Other option", issue!.Message);
    }

    [Fact]
    public async Task ValidateInput_AcceptsValidInputAndNonExactOtherLabels()
    {
        var json =
            """
            {"questions": [{"question": "Q?", "header": "Approach", "options": [{"label": "Other approach", "description": "different"}, {"label": "Choice", "description": "pick me"}]}]}
            """;

        var issue = await new AskUserQuestionTool().ValidateInputAsync(
            JsonDocument.Parse(json).RootElement, CancellationToken.None);

        Assert.Null(issue);
    }

    [Fact]
    public async Task Execute_WithoutInteractionPort_ReportsUnavailable()
    {
        var result = await new AskUserQuestionTool().ExecuteAsync(
            Context(LibraryQuestionJson, interactions: null), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("AskUserQuestion is not available in this session", result.SingleText());
    }

    [Fact]
    public async Task Execute_WithInvalidInput_ReportsIssue()
    {
        var port = new FakeInteractionPort();

        var result = await new AskUserQuestionTool().ExecuteAsync(
            Context("""{"questions": []}""", port), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(port.Requests);
    }

    [Fact]
    public async Task Execute_MapsQuestionsOntoInteractionRequest()
    {
        var port = new FakeInteractionPort
        {
            Answers = new Dictionary<string, string> { ["Library"] = "NodaTime (Recommended)" },
        };

        await new AskUserQuestionTool().ExecuteAsync(
            Context(TwoQuestionsJson, port), CancellationToken.None);

        var request = Assert.Single(port.Requests);
        Assert.NotEqual(default, request.InteractionId);
        Assert.False(string.IsNullOrWhiteSpace(request.Prompt));
        Assert.Equal(2, request.Questions.Count);

        var library = request.Questions[0];
        Assert.Equal("Library", library.Header);
        Assert.Equal("Which library should we use for date formatting?", library.Text);
        Assert.False(library.MultiSelect);
        Assert.Equal(2, library.Options.Count);
        Assert.Equal("NodaTime (Recommended)", library.Options[0].Label);
        Assert.Equal("Most precise", library.Options[0].Description);
        Assert.Equal("DateTime", library.Options[1].Label);

        var features = request.Questions[1];
        Assert.Equal("Features", features.Header);
        Assert.True(features.MultiSelect);
    }

    [Fact]
    public async Task Execute_AllQuestionsAnswered_RendersEveryAnswer()
    {
        var port = new FakeInteractionPort
        {
            Answers = new Dictionary<string, string>
            {
                ["Library"] = "NodaTime (Recommended)",
                ["Features"] = "Diagrams, Previews",
            },
        };

        var result = await new AskUserQuestionTool().ExecuteAsync(
            Context(TwoQuestionsJson, port), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "User has answered your questions: "
            + "\"Which library should we use for date formatting?\"=\"NodaTime (Recommended)\", "
            + "\"Which features do you want to enable?\"=\"Diagrams, Previews\""
            + ". You can now continue with the user's answers in mind.",
            result.SingleText());
    }

    [Fact]
    public async Task Execute_PartiallyAnswered_CountsSkippedQuestions()
    {
        var port = new FakeInteractionPort
        {
            Answers = new Dictionary<string, string> { ["Library"] = "NodaTime (Recommended)" },
        };

        var result = await new AskUserQuestionTool().ExecuteAsync(
            Context(TwoQuestionsJson, port), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "The user answered some questions and skipped 1. Provided answers: "
            + "\"Which library should we use for date formatting?\"=\"NodaTime (Recommended)\""
            + ". Continue with the provided answers and use your best judgment for the unanswered questions; "
            + "do not invent user preferences.",
            result.SingleText());
    }

    [Fact]
    public async Task Execute_BlankAnswerCountsAsSkipped()
    {
        var port = new FakeInteractionPort
        {
            Answers = new Dictionary<string, string> { ["Library"] = "   ", ["Features"] = "Diagrams" },
        };

        var result = await new AskUserQuestionTool().ExecuteAsync(
            Context(TwoQuestionsJson, port), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.StartsWith("The user answered some questions and skipped 1.", result.SingleText());
        Assert.DoesNotContain("Library", result.SingleText());
    }

    [Fact]
    public async Task Execute_NoAnswers_AdvisesBestJudgment()
    {
        var port = new FakeInteractionPort
        {
            Answers = new Dictionary<string, string>(),
        };

        var result = await new AskUserQuestionTool().ExecuteAsync(
            Context(TwoQuestionsJson, port), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "The user did not provide answers to these questions. Continue using your best judgment; "
            + "do not treat this as a rejection or invent a user preference.",
            result.SingleText());
    }

    [Fact]
    public void Metadata_UsesInteractionPortInsteadOfApproval()
    {
        var metadata = new AskUserQuestionTool().Metadata;

        Assert.True(metadata.RequiresUserInteraction);
        Assert.False(metadata.NeedsApproval);
        Assert.True(metadata.AllowedInPlanMode);
        Assert.False(metadata.ConcurrentSafe);
        Assert.Equal(SideEffectScope.UserInteraction, metadata.SideEffectScope);
    }

    private static ToolExecutionContext Context(string json, IUserInteractionPort? interactions) =>
        new(
            StrongId.NewSessionId(),
            @"C:\alcoler-test-workspace",
            JsonDocument.Parse(json).RootElement.Clone(),
            NoArtifactWorkspace.Instance,
            TimeProvider.System,
            Interactions: interactions);

    private sealed class FakeInteractionPort : IUserInteractionPort
    {
        public List<InteractionRequest> Requests { get; } = [];

        public IReadOnlyDictionary<string, string> Answers { get; init; } =
            new Dictionary<string, string>();

        public ValueTask<InteractionAnswer> AskAsync(InteractionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new InteractionAnswer(Answers));
        }
    }
}
