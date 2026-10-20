using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Context;
using Xunit;

namespace Alcoler.Kernel.Tests.Context;

public class ContextBuilderTests
{
    private static ContextAssemblyInput Input(
        IReadOnlyList<ConversationMessage>? history = null,
        string? agentInstructions = "# workspace rules",
        string? skillsListing = "## available skills") =>
        new(
            CliPrefix: "cli prefix",
            SystemStable: "stable system",
            SystemDynamic: "dynamic system",
            AgentInstructions: agentInstructions,
            SkillsListing: skillsListing,
            History: history ?? [],
            Tools: [new ToolContract("ReadFile", "reads", TestJson.Element(new { type = "object" }))],
            Options: new ModelCallOptions(MaxOutputTokens: 8_000));

    [Fact]
    public void Assemble_EmitsThreePrefixMessagesPlusAgentsSkillsMessage()
    {
        var assembled = ContextBuilder.Assemble(Input());

        var messages = assembled.Request.Messages;
        Assert.Equal(4, messages.Count);
        Assert.Equal(4, assembled.PrefixMessageCount);
        Assert.Equal("cli prefix", Assert.IsType<InputTextBlock>(messages[0].Content[0]).Text);
        Assert.Equal("stable system", Assert.IsType<InputTextBlock>(messages[1].Content[0]).Text);
        Assert.Equal("dynamic system", Assert.IsType<InputTextBlock>(messages[2].Content[0]).Text);

        var agents = Assert.IsType<InputTextBlock>(messages[3].Content[0]).Text;
        Assert.Contains("Contents of AGENTS.md (workspace instructions):", agents);
        Assert.Contains("# workspace rules", agents);
        Assert.Contains("IMPORTANT: These instructions OVERRIDE any default behavior", agents);
        Assert.Contains("## available skills", agents);
    }

    [Fact]
    public void Assemble_WithoutAgentsOrSkills_PrefixIsThreeMessages()
    {
        var assembled = ContextBuilder.Assemble(Input(agentInstructions: null, skillsListing: null));

        Assert.Equal(3, assembled.Request.Messages.Count);
        Assert.Equal(3, assembled.PrefixMessageCount);
    }

    [Fact]
    public void Assemble_CacheAnchorsOnlyOnPrefixAndFinalMessage()
    {
        var history = new List<ConversationMessage>
        {
            TestMessages.UserText("hello"),
            TestMessages.AssistantText("hi"),
            TestMessages.UserText("again"),
        };

        var assembled = ContextBuilder.Assemble(Input(history));
        var messages = assembled.Request.Messages;

        Assert.Equal(7, messages.Count);
        for (var i = 0; i < 4; i++)
            Assert.NotNull(messages[i].CacheControl);
        Assert.Null(messages[4].CacheControl);
        Assert.Null(messages[5].CacheControl);
        Assert.NotNull(messages[6].CacheControl);
    }

    [Fact]
    public void Assemble_EmptyHistory_Tolerated()
    {
        var assembled = ContextBuilder.Assemble(Input([]));

        Assert.Equal(4, assembled.Request.Messages.Count);
        Assert.All(assembled.Request.Messages, m => Assert.NotNull(m.CacheControl));
    }

    [Fact]
    public void Assemble_StripsReasoningFromAssistantMessages()
    {
        var callId = StrongId.NewToolCallId();
        var history = new List<ConversationMessage>
        {
            new(
                StrongId.NewMessageId(),
                MessageRole.Assistant,
            [
                new ReasoningPart(StrongId.NewPartId(), "secret chain of thought"),
                    new TextPart(StrongId.NewPartId(), "answer"),
                    new ToolCallPart(StrongId.NewPartId(), callId, "ReadFile", TestJson.Element(new { path = "a.cs" })),
            ],
                DateTimeOffset.UtcNow),
        };

        var assembled = ContextBuilder.Assemble(Input(history));

        var projected = assembled.Request.Messages[^1];
        Assert.Equal(MessageRole.Assistant, projected.Role);
        Assert.Equal(2, projected.Content.Count);
        Assert.Equal("answer", Assert.IsType<InputTextBlock>(projected.Content[0]).Text);
        var toolCall = Assert.IsType<InputToolCallBlock>(projected.Content[1]);
        Assert.Equal(callId, toolCall.Id);
        Assert.Equal("ReadFile", toolCall.Name);
        Assert.DoesNotContain("secret chain of thought", projected.Content.OfType<InputTextBlock>().Select(b => b.Text));
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
        Assert.Equal("line 1\nline 2", result.Content);
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
                MessageRole.Assistant,
                [new ReasoningPart(StrongId.NewPartId(), "only thoughts")],
                DateTimeOffset.UtcNow),
        };

        var assembled = ContextBuilder.Assemble(Input(history));

        Assert.Equal(5, assembled.Request.Messages.Count);
        Assert.NotNull(assembled.Request.Messages[^1].CacheControl);
        Assert.Equal("visible", Assert.IsType<InputTextBlock>(assembled.Request.Messages[^1].Content[0]).Text);
    }

    [Fact]
    public void Assemble_BreakdownMeasuresInjectedSectionsWithoutDoubleCounting()
    {
        var input = Input([TestMessages.UserText("hello")]);
        var assembled = ContextBuilder.Assemble(input);
        var categories = assembled.Breakdown.ToDictionary(item => item.Source, item => item.Characters);
        var meta = Assert.IsType<InputTextBlock>(assembled.Request.Messages[3].Content[0]).Text;

        Assert.Equal(input.CliPrefix.Length + input.SystemStable.Length + input.SystemDynamic.Length,
            categories[ContextUsageSources.SystemPrompt]);
        Assert.Equal(input.SkillsListing!.Length, categories[ContextUsageSources.Skills]);
        Assert.Equal(meta.Length - input.SkillsListing.Length, categories[ContextUsageSources.MetaUserContext]);
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
        Assert.Equal(5, assembled.PrefixMessageCount);
    }

    [Fact]
    public void Assemble_HiddenAndReasoningPartsDoNotAffectRequestAccounting()
    {
        var visible = TestMessages.AssistantText("answer");
        var hidden = visible with
        {
            Parts = [.. visible.Parts,
                new ReasoningPart(StrongId.NewPartId(), new string('r', 10_000)),
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
        var tools = new List<ToolContract> { new("ReadFile", "reads", TestJson.Element(new { type = "object" })) };
        var options = new ModelCallOptions(ReasoningLevel: "high", MaxOutputTokens: 4_000);
        var input = Input() with { Tools = tools, Options = options };

        var assembled = ContextBuilder.Assemble(input);

        Assert.Same(tools, assembled.Request.Tools);
        Assert.Equal(options, assembled.Request.Options);
    }
}
