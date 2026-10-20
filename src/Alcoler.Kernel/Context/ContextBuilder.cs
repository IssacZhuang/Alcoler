using System.Text;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;

namespace Alcoler.Kernel.Context;

public sealed record ContextAssemblyInput(
    string CliPrefix,
    string SystemStable,
    string SystemDynamic,
    string? AgentInstructions,
    string? SkillsListing,
    IReadOnlyList<ConversationMessage> History,
    IReadOnlyList<ToolContract> Tools,
    ModelCallOptions Options,
    string AgentInstructionsPath = "AGENTS.md",
    string? ToolPrompt = null);

public sealed record AssembledContext(
    ModelRequest Request,
    int PrefixMessageCount,
    IReadOnlyList<ContextUsageCategory> Breakdown);

public static class ContextBuilder
{
    private const string InstructionsFooter =
        "IMPORTANT: These instructions OVERRIDE any default behavior and you MUST follow them exactly as written.";

    // The three system messages plus the agents/skills user message form the immutable prefix
    // (PrefixMessageCount). Cache breakpoints exist only on the prefix and the final history message.
    public static AssembledContext Assemble(ContextAssemblyInput input)
    {
        var ephemeral = new CacheControl();
        var messages = new List<ModelInputMessage>
        {
            new(MessageRole.System, [new InputTextBlock(input.CliPrefix)], ephemeral),
            new(MessageRole.System, [new InputTextBlock(input.SystemStable)], ephemeral),
            new(MessageRole.System, [new InputTextBlock(input.SystemDynamic)], ephemeral),
        };

        var breakdown = new List<ContextUsageCategory>();
        AddCategory(ContextUsageSources.SystemPrompt,
            (long)input.CliPrefix.Length + input.SystemStable.Length + input.SystemDynamic.Length);
        var prefixCount = 3;
        if (BuildAgentAndSkillsText(input) is { } meta)
        {
            messages.Add(new ModelInputMessage(MessageRole.User, [new InputTextBlock(meta)], ephemeral));
            var skillsChars = input.SkillsListing?.Length ?? 0;
            AddCategory(ContextUsageSources.Skills, skillsChars);
            AddCategory(ContextUsageSources.MetaUserContext, meta.Length - skillsChars);
            prefixCount++;
        }
        if (!string.IsNullOrEmpty(input.ToolPrompt))
        {
            messages.Add(new ModelInputMessage(MessageRole.System, [new InputTextBlock(input.ToolPrompt)], ephemeral));
            AddCategory(ContextUsageSources.ToolPrompt, input.ToolPrompt.Length);
            prefixCount++;
        }
        foreach (var tools in input.Tools.GroupBy(tool => tool.Source))
        {
            AddCategory(tools.Key == ToolSource.Mcp ? ContextUsageSources.McpToolSchemas : ContextUsageSources.SystemToolSchemas,
                tools.Sum(tool => (long)TokenEstimator.SerializeTool(tool).Length));
        }

        ModelInputMessage? last = null;
        foreach (var message in input.History)
        {
            if (Project(message) is not { } projected)
                continue;
            messages.Add(projected);
            last = projected;
        }

        if (last is not null)
            messages[^1] = last with { CacheControl = new CacheControl() };

        AddCategory(ContextUsageSources.Messages,
            messages.Skip(prefixCount).Sum(message => (long)TokenEstimator.SerializeMessage(message).Length));
        return new AssembledContext(new ModelRequest(messages, input.Tools, input.Options), prefixCount, breakdown);

        void AddCategory(string source, long characters)
        {
            if (characters > 0)
                breakdown.Add(new ContextUsageCategory(source, characters));
        }
    }

    private static string? BuildAgentAndSkillsText(ContextAssemblyInput input)
    {
        var hasInstructions = !string.IsNullOrEmpty(input.AgentInstructions);
        var hasSkills = !string.IsNullOrEmpty(input.SkillsListing);
        if (!hasInstructions && !hasSkills)
            return null;

        var sb = new StringBuilder();
        if (hasInstructions)
        {
            sb.AppendLine($"Contents of {input.AgentInstructionsPath} (workspace instructions):");
            sb.AppendLine();
            sb.AppendLine(input.AgentInstructions);
            sb.AppendLine();
            sb.Append(InstructionsFooter);
        }
        if (hasSkills)
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();
            sb.Append(input.SkillsListing);
        }
        return sb.ToString();
    }

    internal static ModelInputMessage? Project(ConversationMessage message)
    {
        var blocks = new List<ModelContentBlock>();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart text:
                    blocks.Add(new InputTextBlock(text.Text));
                    break;
                case ToolCallPart call:
                    blocks.Add(new InputToolCallBlock(call.ToolCallId, call.ToolName, call.Input));
                    break;
                case ToolResultPart result:
                    blocks.Add(new InputToolResultBlock(
                        result.ToolCallId,
                        ConcatenateResultText(result),
                        result.IsError));
                    break;
                case AttachmentPart attachment when attachment.ProviderVisible:
                    blocks.Add(new InputTextBlock(attachment.Content));
                    break;
                case FilePart file when file.ProviderReady:
                    blocks.Add(new InputTextBlock($"[media file: {file.FileName ?? file.ArtifactUri}]"));
                    break;
            }
        }

        if (blocks.Count == 0)
            return null;
        return new ModelInputMessage(message.Role, blocks);
    }

    private static string ConcatenateResultText(ToolResultPart result) =>
        string.Join("\n", result.Content.OfType<ToolResultText>().Select(b => b.Text));
}
