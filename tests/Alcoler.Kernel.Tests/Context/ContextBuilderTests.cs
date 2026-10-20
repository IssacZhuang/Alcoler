using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Context;
using Alcoler.Kernel.Prompts;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class ContextBuilderTests
{
    private const string CurrentDate = "2026-10-20";

    private static ContextAssemblyInput Input(
        IReadOnlyList<ConversationMessage>? history = null,
        string? agentInstructions = "# workspace rules",
        string? skillsListing = "The following skills are available for use with the Skill tool:",
        string? currentDate = CurrentDate) =>
        new(
            CliPrefix: "cli prefix",
            SystemStable: "stable system",
            SystemDynamic: "dynamic system",
            AgentInstructions: agentInstructions,
            SkillsListing: skillsListing,
            History: history ?? [],
            Tools: [new ToolContract("Read", "reads", TestJson.Element(new { type = "object" }))],
            Options: new ModelCallOptions(MaxOutputTokens: 8_000),
            CurrentDate: currentDate);

    [Fact]
    public void Assemble_EmitsSystemStackThenSkillsAndContextMetaMessages()
    {
        var assembled = ContextBuilder.Assemble(Input());

        var messages = assembled.Request.Messages;
        Assert.Equal(5, messages.Count);
        Assert.Equal(5, assembled.PrefixMessageCount);
        Assert.Equal(MessageRole.System, messages[0].Role);
        Assert.Equal("cli prefix", Assert.IsType<InputTextBlock>(messages[0].Content[0]).Text);
        Assert.Equal("stable system", Assert.IsType<InputTextBlock>(messages[1].Content[0]).Text);
        Assert.Equal("\n\ndynamic system", Assert.IsType<InputTextBlock>(messages[2].Content[0]).Text);

        var skills = Assert.IsType<InputTextBlock>(messages[3].Content[0]).Text;
        Assert.Equal(
            "<system-reminder>\nThe following skills are available for use with the Skill tool:\n</system-reminder>",
            skills);
        Assert.Equal(MessageRole.User, messages[3].Role);

        var context = Assert.IsType<InputTextBlock>(messages[4].Content[0]).Text;
        Assert.StartsWith("<system-reminder>\nAs you answer the user's questions, you can use the following context:", context);
        Assert.Contains("# workspace rules", context);
        Assert.Contains($"# currentDate\nToday's date is {CurrentDate}.", context);
        Assert.Contains("      IMPORTANT: this context may or may not be relevant", context);
        Assert.EndsWith("</system-reminder>\n", context);
        Assert.Equal(MessageRole.User, messages[4].Role);
    }

    [Fact]
    public void Assemble_WithoutAgentsSkillsOrDate_PrefixIsThreeMessages()
    {
        var assembled = ContextBuilder.Assemble(Input(agentInstructions: null, skillsListing: null, currentDate: null));

        Assert.Equal(3, assembled.Request.Messages.Count);
        Assert.Equal(3, assembled.PrefixMessageCount);
    }

    [Fact]
    public void Project_AssistantReasoningBecomesReasoningBlockWithSignature()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserText("think"),
            new ConversationMessage(
                TestMessages.NextMessageId(),
                MessageRole.Assistant,
                [new ReasoningPart(TestMessages.NextPartId(), "chain of thought", "sig-1")],
                DateTimeOffset.UtcNow),
        };

        var assembled = ContextBuilder.Assemble(Input(history));
        var projected = assembled.Request.Messages[^1];

        Assert.Equal(MessageRole.Assistant, projected.Role);
        var reasoning = Assert.IsType<InputReasoningBlock>(projected.Content[0]);
        Assert.Equal("chain of thought", reasoning.Text);
        Assert.Equal("sig-1", reasoning.Signature);
    }

    [Fact]
    public void Project_EmptyAssistantMessage_BecomesNoContentPlaceholder()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserText("hi"),
            new ConversationMessage(
                TestMessages.NextMessageId(),
                MessageRole.Assistant,
                [],
                DateTimeOffset.UtcNow),
        };

        var assembled = ContextBuilder.Assemble(Input(history));
        var projected = assembled.Request.Messages[^1];

        Assert.Equal(MessageRole.Assistant, projected.Role);
        var text = Assert.IsType<InputTextBlock>(projected.Content.Single());
        Assert.Equal("(no content)", text.Text);
    }

    [Fact]
    public void Project_HydratedToolResultMedia_SplitsIntoFollowupImageMessage()
    {
        var callId = TestMessages.NextToolCallId("Read");
        var history = new List<ConversationMessage>
        {
            TestMessages.AssistantToolCall(callId, "Read", new { file_path = "shot.png" }),
            new ConversationMessage(
                TestMessages.NextMessageId(),
                MessageRole.User,
                [new ToolResultPart(
                    TestMessages.NextPartId(),
                    callId,
                    [new ToolResultMedia("alcoler-artifact://sess/shot.png", "image/png", "shot.png")])],
                DateTimeOffset.UtcNow),
        };
        var media = new Dictionary<string, MediaPayload>
        {
            ["alcoler-artifact://sess/shot.png"] = new("image/png", "aW1hZ2U=", "shot.png"),
        };

        var assembled = ContextBuilder.Assemble(Input(history) with { Media = media });
        var messages = assembled.Request.Messages;

        var toolResult = Assert.IsType<InputToolResultBlock>(messages[^2].Content[0]);
        Assert.Equal("", toolResult.Content);
        var imageMessage = messages[^1];
        Assert.Equal(MessageRole.User, imageMessage.Role);
        var image = Assert.IsType<InputImageBlock>(imageMessage.Content.Single());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("aW1hZ2U=", image.Base64Data);
    }

    [Fact]
    public void Project_UnhydratedToolResultMedia_FallsBackToPlaceholderText()
    {
        var callId = TestMessages.NextToolCallId("Read");
        var history = new List<ConversationMessage>
        {
            TestMessages.AssistantToolCall(callId, "Read", new { file_path = "shot.png" }),
            new ConversationMessage(
                TestMessages.NextMessageId(),
                MessageRole.User,
                [new ToolResultPart(
                    TestMessages.NextPartId(),
                    callId,
                    [new ToolResultMedia("alcoler-artifact://sess/shot.png", "image/png", "shot.png")])],
                DateTimeOffset.UtcNow),
        };

        var assembled = ContextBuilder.Assemble(Input(history));
        var messages = assembled.Request.Messages;

        var toolResult = Assert.IsType<InputToolResultBlock>(messages[^1].Content[0]);
        Assert.Equal("[media file: shot.png]", toolResult.Content);
    }

    [Fact]
    public void Assemble_CacheAnchorsOnSystemStackAndFinalHistoryMessage()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserText("hello"),
            TestMessages.AssistantText("hi"),
            TestMessages.UserText("again"),
        };

        var assembled = ContextBuilder.Assemble(Input(history));
        var messages = assembled.Request.Messages;

        Assert.Equal(8, messages.Count);
        for (var i = 0; i < 3; i++)
            Assert.NotNull(messages[i].CacheControl);
        Assert.Null(messages[3].CacheControl);
        Assert.Null(messages[4].CacheControl);
        Assert.Null(messages[5].CacheControl);
        Assert.Null(messages[6].CacheControl);
        Assert.NotNull(messages[7].CacheControl);
    }

    [Fact]
    public void Assemble_EmptyHistory_MetaMessagesStayUncached()
    {
        var assembled = ContextBuilder.Assemble(Input([]));

        Assert.Equal(5, assembled.Request.Messages.Count);
        for (var i = 0; i < 3; i++)
            Assert.NotNull(assembled.Request.Messages[i].CacheControl);
        Assert.Null(assembled.Request.Messages[3].CacheControl);
        Assert.Null(assembled.Request.Messages[4].CacheControl);
    }

    [Fact]
    public void Assemble_ProjectsReasoningFromAssistantMessages()
    {
        var callId = StrongId.NewToolCallId();
        var history = new List<ConversationMessage>
        {
            new(
                StrongId.NewMessageId(),
                MessageRole.Assistant,
            [
                new ReasoningPart(StrongId.NewPartId(), "chain of thought", "sig-1"),
                    new TextPart(StrongId.NewPartId(), "answer"),
                    new ToolCallPart(StrongId.NewPartId(), callId, "Read", TestJson.Element(new { path = "a.cs" })),
            ],
                DateTimeOffset.UtcNow),
        };

        var assembled = ContextBuilder.Assemble(Input(history));

        var projected = assembled.Request.Messages[^1];
        Assert.Equal(MessageRole.Assistant, projected.Role);
        Assert.Equal(3, projected.Content.Count);
        var reasoning = Assert.IsType<InputReasoningBlock>(projected.Content[0]);
        Assert.Equal("chain of thought", reasoning.Text);
        Assert.Equal("sig-1", reasoning.Signature);
        Assert.Equal("answer", Assert.IsType<InputTextBlock>(projected.Content[1]).Text);
        var toolCall = Assert.IsType<InputToolCallBlock>(projected.Content[2]);
        Assert.Equal(callId, toolCall.Id);
        Assert.Equal("Read", toolCall.Name);
    }

    [Fact]
    public void Assemble_MapsToolResultsWithConcatenatedTextAndErrorFlag()
    {
        var callId = StrongId.NewToolCallId();
        var history = new List<ConversationMessage>
        {
            new(
                StrongId.NewMessageId(),
                MessageRole.User,
            [
                new ToolResultPart(
                    StrongId.NewPartId(),
                    callId,
                    [new ToolResultText("line 1"), new ToolResultText("line 2"), new ToolResultMedia("alcoler-artifact://s/1", "image/png")],
                    IsError: true),
            ],
                DateTimeOffset.UtcNow),
        };

        var assembled = ContextBuilder.Assemble(Input(history));

        var projected = assembled.Request.Messages[^1];
        var result = Assert.IsType<InputToolResultBlock>(Assert.Single(projected.Content));
        Assert.Equal(callId, result.Id);
        Assert.Equal("line 1\nline 2\n[media file: alcoler-artifact://s/1]", result.Content);
        Assert.True(result.IsError);
    }

    [Fact]
    public void Assemble_ProjectsAttachmentsAndFileParts()
    {
        var history = new List<ConversationMessage>
        {
            new(
                StrongId.NewMessageId(),
                MessageRole.User,
            [
                new AttachmentPart(StrongId.NewPartId(), AttachmentKind.TaskNotification, "task done", ProviderVisible: true),
                    new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, "hidden", ProviderVisible: false),
                    new FilePart(StrongId.NewPartId(), "alcoler-artifact://s/a.png", "image/png", "a.png"),
            ],
                DateTimeOffset.UtcNow),
        };

        var assembled = ContextBuilder.Assemble(Input(history));

        var projected = assembled.Request.Messages[^1];
        Assert.Equal(2, projected.Content.Count);
        Assert.Equal("task done", Assert.IsType<InputTextBlock>(projected.Content[0]).Text);
        Assert.Equal("[media file: a.png]", Assert.IsType<InputTextBlock>(projected.Content[1]).Text);
    }

    [Fact]
    public void Assemble_SkipsMessagesThatProjectToNoBlocks()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.AssistantText("visible"),
            new ConversationMessage(
                StrongId.NewMessageId(),
                MessageRole.User,
                [new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, "hidden", ProviderVisible: false)],
                DateTimeOffset.UtcNow),
        };

        var assembled = ContextBuilder.Assemble(Input(history));

        Assert.Equal(6, assembled.Request.Messages.Count);
        Assert.NotNull(assembled.Request.Messages[^1].CacheControl);
        Assert.Equal("visible", Assert.IsType<InputTextBlock>(assembled.Request.Messages[^1].Content[0]).Text);
    }

    [Fact]
    public void Assemble_BreakdownMeasuresInjectedSectionsWithoutDoubleCounting()
    {
        var input = Input([TestMessages.UserText("hello")]);
        var assembled = ContextBuilder.Assemble(input);
        var categories = assembled.Breakdown.ToDictionary(item => item.Source, item => item.Characters);
        var skillsMeta = Assert.IsType<InputTextBlock>(assembled.Request.Messages[3].Content[0]).Text;
        var contextMeta = Assert.IsType<InputTextBlock>(assembled.Request.Messages[4].Content[0]).Text;

        Assert.Equal(input.CliPrefix.Length + input.SystemStable!.Length + input.SystemDynamic!.Length,
            categories[ContextUsageSources.SystemPrompt]);
        Assert.Equal(skillsMeta.Length, categories[ContextUsageSources.Skills]);
        Assert.Equal(contextMeta.Length, categories[ContextUsageSources.MetaUserContext]);
        Assert.True(categories[ContextUsageSources.Messages] > "hello".Length);
        Assert.DoesNotContain(ContextUsageSources.ToolPrompt, categories.Keys);
        Assert.DoesNotContain(ContextUsageSources.McpToolSchemas, categories.Keys);
    }

    [Fact]
    public void Assemble_UsesExplicitToolProvenanceRatherThanToolNames()
    {
        var schema = TestJson.Element(new { type = "object" });
        var assembled = ContextBuilder.Assemble(Input() with
        {
            Tools = [new ToolContract("mcp_fake", "same", schema), new ToolContract("ordinary", "remote", schema, ToolSource.Mcp)],
            ToolPrompt = "tool instructions",
        });
        var categories = assembled.Breakdown.ToDictionary(item => item.Source, item => item.Characters);

        Assert.True(categories[ContextUsageSources.SystemToolSchemas] > 0);
        Assert.True(categories[ContextUsageSources.McpToolSchemas] > 0);
        Assert.Equal("tool instructions".Length, categories[ContextUsageSources.ToolPrompt]);
        Assert.Equal(6, assembled.PrefixMessageCount);
    }

    [Fact]
    public void Assemble_HiddenPartsDoNotAffectRequestAccounting()
    {
        var visible = TestMessages.AssistantText("answer");
        var hidden = visible with
        {
            Parts = [.. visible.Parts,
                new AttachmentPart(StrongId.NewPartId(), AttachmentKind.SystemReminder, "not sent", ProviderVisible: false)],
        };
        var first = ContextBuilder.Assemble(Input([visible]));
        var second = ContextBuilder.Assemble(Input([hidden]));

        Assert.Equal(first.Breakdown, second.Breakdown);
        Assert.Equal(TokenEstimator.EstimateRequestTokens(first.Request), TokenEstimator.EstimateRequestTokens(second.Request));
    }

    [Fact]
    public void Assemble_PassesToolsAndOptionsThrough()
    {
        var tools = new List<ToolContract> { new("Read", "reads", TestJson.Element(new { type = "object" })) };
        var options = new ModelCallOptions(ReasoningLevel: "high", MaxOutputTokens: 4_000);
        var input = Input() with { Tools = tools, Options = options };

        var assembled = ContextBuilder.Assemble(input);

        Assert.Same(tools, assembled.Request.Tools);
        Assert.Equal(options, assembled.Request.Options);
    }
}
