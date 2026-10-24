using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Tools;

/// <summary>
/// Blocks the turn on IUserInteractionPort until the user answers 1-4 multiple-choice
/// questions. Answers come back keyed by question header (UI convention) and are rendered
/// keyed by question text. NeedsApproval stays false because the interaction itself is the
/// only user prompt; routing through the permission broker as well would double-ask.
/// </summary>
public sealed class AskUserQuestionTool : ITool
{
    private const int MaxHeaderLength = 12;

    private const string NoAnswerMessage =
        "The user did not provide answers to these questions. Continue using your best judgment; do not treat this as a rejection or invent a user preference.";

    private const string PartialAnswerSuffix =
        ". Continue with the provided answers and use your best judgment for the unanswered questions; do not invent user preferences.";

    private const string AllAnswersSuffix = ". You can now continue with the user's answers in mind.";

    public ToolContract Contract { get; } = new(
        "AskUserQuestion",
        """
        Use this tool only when you are blocked on a decision that is genuinely the user's to make: one you cannot resolve from the request, the code, or sensible defaults.

        Usage notes:
        - Users will always be able to select "Other" to provide custom text input
        - Use multiSelect: true to allow multiple answers to be selected for a question
        - If you recommend a specific option, make that the first option in the list and add "(Recommended)" at the end of the label

        Plan mode note: plan mode is entered manually by the user — do not treat it as something you can switch into. While in plan mode, use this tool to clarify requirements or choose between approaches BEFORE finalizing your plan. Do NOT use this tool to ask "Is my plan ready?", "Should I proceed?", or otherwise reference "the plan" in questions — the user cannot see the plan until you call ExitPlanMode for approval.

        Reserve this for decisions where the user's answer changes what you do next — not for choices with a conventional default or facts you can verify in the codebase yourself. In those cases pick the obvious option, mention it in your response, and proceed.

        Preview feature:
        Use the optional `preview` field on options when presenting concrete artifacts that users need to visually compare:
        - ASCII mockups of UI layouts or components
        - Code snippets showing different implementations
        - Diagram variations
        - Configuration examples

        Preview content is rendered as markdown in a monospace box. Multi-line text with newlines is supported. When any option has a preview, the UI switches to a side-by-side layout with a vertical option list on the left and preview on the right. Do not use previews for simple preference questions where labels and descriptions suffice. Note: previews are only supported for single-select questions (not multiSelect).
        """,
        ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "questions": {
              "type": "array",
              "minItems": 1,
              "maxItems": 4,
              "description": "Questions to ask the user (1-4 questions)",
              "items": {
                "type": "object",
                "properties": {
                  "question": { "type": "string", "description": "The complete question to ask the user. Should be clear, specific, and end with a question mark. Example: \"Which library should we use for date formatting?\" If multiSelect is true, phrase it accordingly, e.g. \"Which features do you want to enable?\"" },
                  "header": { "type": "string", "description": "Very short label displayed as a chip/tag (max 12 chars). Examples: \"Auth method\", \"Library\", \"Approach\"." },
                  "options": {
                    "type": "array",
                    "minItems": 2,
                    "maxItems": 4,
                    "description": "The available choices for this question. Must have 2-4 options. Each option should be a distinct, mutually exclusive choice (unless multiSelect is enabled). There should be no 'Other' option, that will be provided automatically.",
                    "items": {
                      "type": "object",
                      "properties": {
                        "label": { "type": "string", "description": "The display text for this option that the user will see and select. Should be concise (1-5 words) and clearly describe the choice." },
                        "description": { "type": "string", "description": "Explanation of what this option means or what will happen if chosen. Useful for providing context about trade-offs or implications." },
                        "preview": { "type": "string", "description": "Optional preview content rendered when this option is focused. Use for mockups, code snippets, or visual comparisons that help users compare options. See the tool description for the expected content format." }
                      },
                      "required": ["label", "description"],
                      "additionalProperties": false
                    }
                  },
                  "multiSelect": { "type": "boolean", "default": false, "description": "Set to true to allow the user to select multiple options instead of just one. Use when choices are not mutually exclusive." }
                },
                "required": ["question", "header", "options", "multiSelect"],
                "additionalProperties": false
              }
            },
            "answers": {
              "type": "object",
              "additionalProperties": { "type": "string" },
              "description": "User answers collected by the permission component"
            },
            "annotations": {
              "type": "object",
              "description": "Optional per-question annotations from the user (e.g., notes on preview selections). Keyed by question text.",
              "additionalProperties": {
                "type": "object",
                "properties": {
                  "preview": { "type": "string", "description": "The preview content of the selected option, if the question used previews." },
                  "notes": { "type": "string", "description": "Free-text notes the user added to their selection." }
                },
                "additionalProperties": false
              }
            },
            "metadata": {
              "type": "object",
              "description": "Optional metadata for tracking and analytics purposes. Not displayed to user.",
              "properties": {
                "source": { "type": "string", "description": "Optional identifier for the source of this question (e.g., \"remember\" for /remember command). Used for analytics tracking." }
              },
              "additionalProperties": false
            }
          },
          "required": ["questions"],
          "additionalProperties": false
        }
        """));

    private static readonly object RuntimeInputSchemaSync = new();
    private static JsonElement? _runtimeInputSchemaCache;

    public JsonElement RuntimeInputSchema
    {
        get
        {
            lock (RuntimeInputSchemaSync)
            {
                if (_runtimeInputSchemaCache is null)
                {
                    var schema = JsonNode.Parse(Contract.InputSchema.GetRawText())!;
                    schema["properties"]!["questions"]!["items"]!["required"] =
                        new JsonArray("question", "header", "options");
                    _runtimeInputSchemaCache = JsonSerializer.SerializeToElement(schema);
                }
                return _runtimeInputSchemaCache.Value;
            }
        }
    }

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: true,
        Destructive: false,
        ConcurrentSafe: false,
        RequiresUserInteraction: true,
        SideEffectScope: SideEffectScope.UserInteraction,
        RiskLevel: RiskLevel.Low,
        NeedsApproval: false,
        AllowedInPlanMode: true);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        TryParseQuestions(input, out _, out var issue);
        return ValueTask.FromResult(issue);
    }

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Interactions is null)
            return ToolExecutionResult.Text("AskUserQuestion is not available in this session", isError: true);

        if (!TryParseQuestions(context.Input, out var questions, out var issue) || issue is not null)
            return ToolExecutionResult.Text(issue!.Message, isError: true);

        var request = new InteractionRequest(
            StrongId.NewInteractionId(),
            "AskUserQuestion pauses execution to collect answers from the user",
            [.. questions.Select(question => new InteractionQuestion(question.Header, question.Text, question.Options, question.MultiSelect))]);
        var answer = await context.Interactions.AskAsync(request, cancellationToken).ConfigureAwait(false);
        return ToolExecutionResult.Text(FormatAnswers(questions, answer));
    }

    private static string FormatAnswers(IReadOnlyList<QuestionSpec> questions, InteractionAnswer answer)
    {
        var answers = answer.Answers;
        var answered = questions
            .Where(question => answers.TryGetValue(question.Header, out var value) && !string.IsNullOrWhiteSpace(value))
            .Select(question => FormatAnswer(question, answers[question.Header], answer.Annotations))
            .ToList();

        if (answered.Count == 0)
            return NoAnswerMessage;

        var answersText = string.Join(", ", answered);
        if (answered.Count < questions.Count)
            return $"The user answered some questions and skipped {questions.Count - answered.Count}. Provided answers: {answersText}{PartialAnswerSuffix}";

        return $"User has answered your questions: {answersText}{AllAnswersSuffix}";
    }

    private static string FormatAnswer(
        QuestionSpec question, string value, IReadOnlyDictionary<string, InteractionAnnotation>? annotations)
    {
        InteractionAnnotation? annotation = null;
        if (annotations is not null)
            annotations.TryGetValue(question.Header, out annotation);
        var preview = annotation?.Preview ?? (question.MultiSelect
            ? null
            : question.Options.FirstOrDefault(option => option.Label == value)?.Preview);
        var text = $"\"{question.Text}\"=\"{value}\"";
        if (!string.IsNullOrEmpty(preview))
            text += $" selected preview:\n{preview}";
        if (!string.IsNullOrEmpty(annotation?.Notes))
            text += $" user notes: {annotation.Notes}";
        return text;
    }

    private static bool TryParseQuestions(
        JsonElement input,
        out IReadOnlyList<QuestionSpec> questions,
        out ToolInputIssue? issue)
    {
        questions = [];
        issue = null;
        if (input.ValueKind is not JsonValueKind.Object
            || !input.TryGetProperty("questions", out var questionsElement)
            || questionsElement.ValueKind is not JsonValueKind.Array
            || questionsElement.GetArrayLength() is < 1 or > 4)
        {
            issue = new ToolInputIssue("invalid_questions", "'questions' must be an array of 1-4 questions");
            return false;
        }

        var specs = new List<QuestionSpec>(questionsElement.GetArrayLength());
        var seenTexts = new HashSet<string>(StringComparer.Ordinal);
        var seenHeaders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questionsElement.EnumerateArray())
        {
            if (question.ValueKind is not JsonValueKind.Object
                || !question.TryGetProperty("question", out var questionElement)
                || questionElement.ValueKind is not JsonValueKind.String
                || string.IsNullOrWhiteSpace(questionElement.GetString()))
            {
                issue = new ToolInputIssue("invalid_question", "'question' must be a non-empty string");
                return false;
            }
            if (!question.TryGetProperty("header", out var headerElement)
                || headerElement.ValueKind is not JsonValueKind.String
                || string.IsNullOrWhiteSpace(headerElement.GetString()))
            {
                issue = new ToolInputIssue("invalid_header", "'header' must be a non-empty string");
                return false;
            }
            var header = headerElement.GetString()!;
            if (header.Length > MaxHeaderLength)
            {
                issue = new ToolInputIssue("header_too_long", $"'header' must be at most {MaxHeaderLength} characters");
                return false;
            }
            if (!question.TryGetProperty("options", out var optionsElement)
                || optionsElement.ValueKind is not JsonValueKind.Array
                || optionsElement.GetArrayLength() is < 2 or > 4)
            {
                issue = new ToolInputIssue("options_out_of_range", "each question must have 2-4 options");
                return false;
            }

            var options = new List<InteractionOption>(optionsElement.GetArrayLength());
            var seenLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in optionsElement.EnumerateArray())
            {
                if (option.ValueKind is not JsonValueKind.Object
                    || !option.TryGetProperty("label", out var labelElement)
                    || labelElement.ValueKind is not JsonValueKind.String
                    || string.IsNullOrWhiteSpace(labelElement.GetString())
                    || !option.TryGetProperty("description", out var descriptionElement)
                    || descriptionElement.ValueKind is not JsonValueKind.String
                    || string.IsNullOrWhiteSpace(descriptionElement.GetString()))
                {
                    issue = new ToolInputIssue(
                        "invalid_option",
                        "each option needs non-empty 'label' and 'description' strings");
                    return false;
                }
                var label = labelElement.GetString()!;
                if (label.Trim().Equals("Other", StringComparison.OrdinalIgnoreCase))
                {
                    issue = new ToolInputIssue(
                        "other_option_forbidden",
                        "Do not include an Other option; clients provide it automatically");
                    return false;
                }
                if (!seenLabels.Add(label))
                {
                    issue = new ToolInputIssue("duplicate_labels", "Option labels must be unique within each question");
                    return false;
                }
                string? preview = null;
                if (option.TryGetProperty("preview", out var previewElement))
                {
                    if (previewElement.ValueKind is not JsonValueKind.String)
                    {
                        issue = new ToolInputIssue("invalid_preview", "'preview' must be a string");
                        return false;
                    }
                    preview = previewElement.GetString()!;
                    if (ValidatePreview(preview) is { } previewIssue)
                    {
                        issue = previewIssue;
                        return false;
                    }
                }
                options.Add(new InteractionOption(label, descriptionElement.GetString(), preview));
            }

            var multiSelect = false;
            if (question.TryGetProperty("multiSelect", out var multiSelectElement))
            {
                if (multiSelectElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    issue = new ToolInputIssue("invalid_multi_select", "'multiSelect' must be a boolean");
                    return false;
                }
                multiSelect = multiSelectElement.GetBoolean();
            }

            if (!seenTexts.Add(questionElement.GetString()!))
            {
                issue = new ToolInputIssue("duplicate_questions", "Question texts must be unique");
                return false;
            }
            if (!seenHeaders.Add(header))
            {
                issue = new ToolInputIssue("duplicate_headers", "Question headers must be unique");
                return false;
            }
            specs.Add(new QuestionSpec(header, questionElement.GetString()!, options, multiSelect));
        }

        questions = specs;
        return true;
    }

    private static ToolInputIssue? ValidatePreview(string preview)
    {
        const RegexOptions flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        const string tag = @"</?\s*[a-z][a-z0-9:-]*(?:\s[^<>]*)?>";
        if (!Regex.IsMatch(preview, @"<!doctype\b|<!--|" + tag, flags))
            return null;
        if (Regex.IsMatch(preview, @"<!doctype\b|</?\s*(?:html|body)\b", flags))
            return new ToolInputIssue("invalid_preview", "HTML preview must be a fragment without html, body, or doctype");
        if (Regex.IsMatch(preview, @"</?\s*(?:script|style)\b", flags))
            return new ToolInputIssue("invalid_preview", "HTML preview cannot contain script or style tags");
        if (!Regex.IsMatch(preview, tag, flags))
            return new ToolInputIssue("invalid_preview", "HTML preview must contain an HTML tag");
        return null;
    }

    private sealed record QuestionSpec(
        string Header,
        string Text,
        IReadOnlyList<InteractionOption> Options,
        bool MultiSelect);

    private static JsonElement ParseSchema(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();
}
