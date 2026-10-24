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
    public async Task Executor_DefaultsOmittedMultiSelectDespiteModelSchemaRequirement()
    {
        var port = new FakeInteractionPort();
        var tool = new AskUserQuestionTool();
        var executor = new ToolCallExecutor(new ToolRegistry([tool]), new ScriptedPermissionGate());
        var outcome = await executor.ExecuteAsync(
            new Alcoler.Contracts.Models.ToolCall(StrongId.NewToolCallId(), "AskUserQuestion",
                JsonDocument.Parse(LibraryQuestionJson).RootElement.Clone()),
            (call, _) => ValueTask.FromResult(Context(call.Input.GetRawText(), port)),
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.False(outcome.Result.IsError);
        Assert.False(Assert.Single(port.Requests).Questions[0].MultiSelect);
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
    public async Task Execute_PreviewAndNotesReachUserAndModel()
    {
        const string json = """
            {"questions":[{"question":"Which layout?","header":"Layout","options":[{"label":"Split","description":"Two panes","preview":"```text\n[left][right]\n```"},{"label":"Stack","description":"One column"}]}]}
            """;
        var port = new FakeInteractionPort
        {
            Answers = new Dictionary<string, string> { ["Layout"] = "Split" },
            Annotations = new Dictionary<string, InteractionAnnotation>
            {
                ["Layout"] = new(Notes: "Keep the right pane wider"),
            },
        };

        var result = await new AskUserQuestionTool().ExecuteAsync(Context(json, port), CancellationToken.None);

        Assert.Equal("```text\n[left][right]\n```", Assert.Single(port.Requests).Questions[0].Options[0].Preview);
        Assert.Equal("User has answered your questions: \"Which layout?\"=\"Split\" selected preview:\n```text\n[left][right]\n``` user notes: Keep the right pane wider. You can now continue with the user's answers in mind.", result.SingleText());
    }

    [Theory]
    [InlineData("<!doctype html><div>x</div>", "HTML preview must be a fragment without html, body, or doctype")]
    [InlineData("<body>x</body>", "HTML preview must be a fragment without html, body, or doctype")]
    [InlineData("<script>x</script>", "HTML preview cannot contain script or style tags")]
    [InlineData("<STYLE>x</STYLE>", "HTML preview cannot contain script or style tags")]
    [InlineData("<!-- only a comment -->", "HTML preview must contain an HTML tag")]
    public async Task ValidateInput_RejectsUnsupportedHtmlPreview(string preview, string expected)
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            questions = new[] { new { question = "Which?", header = "Layout", options = new[]
            {
                new { label = "A", description = "a", preview },
                new { label = "B", description = "b", preview = "plain text" },
            } } },
        });

        var issue = await new AskUserQuestionTool().ValidateInputAsync(input, CancellationToken.None);

        Assert.Equal(expected, issue?.Message);
    }

    [Fact]
    public void Contract_ExposesPreviewAnnotationsMetadataAndDefaultedRequiredMultiSelect()
    {
        var contract = new AskUserQuestionTool().Contract;
        var properties = contract.InputSchema.GetProperty("properties");
        var question = properties.GetProperty("questions").GetProperty("items");
        Assert.Contains("multiSelect", question.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.True(question.GetProperty("properties").GetProperty("options").GetProperty("items").GetProperty("properties").TryGetProperty("preview", out _));
        Assert.True(properties.TryGetProperty("annotations", out _));
        Assert.True(properties.TryGetProperty("metadata", out _));
        Assert.Contains("Preview feature:", contract.Description);
    }

    [Fact]
    public void RuntimeSchema_ConcurrentReaders_SeeConsistentRequiredProperties()
    {
        var tools = Enumerable.Range(0, 16).Select(_ => new AskUserQuestionTool()).ToArray();

        Parallel.For(0, 10_000, index =>
        {
            var tool = tools[index % tools.Length];
            var question = tool.RuntimeInputSchema.GetProperty("properties")
                .GetProperty("questions").GetProperty("items");
            Assert.Equal(new[] { "question", "header", "options" },
                question.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            Assert.Contains("multiSelect", tool.Contract.InputSchema.GetProperty("properties")
                .GetProperty("questions").GetProperty("items").GetProperty("required")
                .EnumerateArray().Select(value => value.GetString()));
        });
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

        public IReadOnlyDictionary<string, InteractionAnnotation>? Annotations { get; init; }

        public ValueTask<InteractionAnswer> AskAsync(InteractionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new InteractionAnswer(Answers, Annotations));
        }
    }
}
