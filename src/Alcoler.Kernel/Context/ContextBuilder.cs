using System.Text;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Prompts;

namespace Alcoler.Kernel.Context;

public sealed record ContextAssemblyInput(
    string CliPrefix,
    string? SystemStable,
    string? SystemDynamic,
    string? AgentInstructions,
    string? SkillsListing,
    IReadOnlyList<ConversationMessage> History,
    IReadOnlyList<ToolContract> Tools,
    ModelCallOptions Options,
    string? CurrentDate = null,
    string? ToolPrompt = null,
    IReadOnlyDictionary<string, MediaPayload>? Media = null);

/// <summary>An artifact hydrated for provider replay, keyed by artifact URI.</summary>
public sealed record MediaPayload(string MimeType, string Base64Data, string? FileName = null);

public sealed record AssembledContext(
    ModelRequest Request,
    int PrefixMessageCount,
    IReadOnlyList<ContextUsageCategory> Breakdown);

public static class ContextBuilder
{
    public static AssembledContext Assemble(ContextAssemblyInput input)
    {
        var ephemeral = new CacheControl();
        var messages = new List<ModelInputMessage>();

        if (!string.IsNullOrEmpty(input.CliPrefix))
            messages.Add(new ModelInputMessage(MessageRole.System, [new InputTextBlock(input.CliPrefix)], ephemeral));
        if (!string.IsNullOrEmpty(input.SystemStable))
            messages.Add(new ModelInputMessage(MessageRole.System, [new InputTextBlock(input.SystemStable)], ephemeral));
        if (!string.IsNullOrEmpty(input.SystemDynamic))
        {
            // The dynamic system block carries its own left boundary, mirroring the reference layout.
            messages.Add(new ModelInputMessage(MessageRole.System, [new InputTextBlock("\n\n" + input.SystemDynamic)], ephemeral));
        }

        var breakdown = new List<ContextUsageCategory>();
        AddCategory(ContextUsageSources.SystemPrompt,
            (long)(input.CliPrefix.Length + input.SystemStable?.Length ?? 0) + (input.SystemDynamic?.Length ?? 0));

        var metaSections = new List<(string Body, bool IsSkills)>();
        if (!string.IsNullOrEmpty(input.SkillsListing))
            metaSections.Add((SystemReminder.Wrap(input.SkillsListing), true));
        var contextSections = new List<string>();
        if (!string.IsNullOrEmpty(input.AgentInstructions))
            contextSections.Add(input.AgentInstructions);
        if (!string.IsNullOrEmpty(input.CurrentDate))
            contextSections.Add(EnvironmentSections.CurrentDateSection(input.CurrentDate));
        if (ContextPrefix.ReminderWrapped(contextSections) is { } contextPrefix)
            metaSections.Add((contextPrefix, false));
        foreach (var (body, isSkills) in metaSections)
        {
            messages.Add(new ModelInputMessage(MessageRole.User, [new InputTextBlock(body)]));
            AddCategory(isSkills ? ContextUsageSources.Skills : ContextUsageSources.MetaUserContext, body.Length);
        }

        if (!string.IsNullOrEmpty(input.ToolPrompt))
        {
            messages.Add(new ModelInputMessage(MessageRole.System, [new InputTextBlock(input.ToolPrompt)]));
            AddCategory(ContextUsageSources.ToolPrompt, input.ToolPrompt.Length);
        }
        var prefixCount = messages.Count;

        foreach (var tools in input.Tools.GroupBy(tool => tool.Source))
        {
            AddCategory(tools.Key == ToolSource.Mcp ? ContextUsageSources.McpToolSchemas : ContextUsageSources.SystemToolSchemas,
                tools.Sum(tool => (long)TokenEstimator.SerializeTool(tool).Length));
        }

        ModelInputMessage? last = null;
        foreach (var message in input.History)
        {
            foreach (var projected in Project(message, input.Media))
            {
                messages.Add(projected);
                last = projected;
            }
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

    internal static IEnumerable<ModelInputMessage> Project(
        ConversationMessage message, IReadOnlyDictionary<string, MediaPayload>? media = null)
    {
        var blocks = new List<ModelContentBlock>();
        var images = new List<InputImageBlock>();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart text:
                    blocks.Add(new InputTextBlock(text.Text));
                    break;
                case ReasoningPart reasoning:
                    blocks.Add(new InputReasoningBlock(reasoning.Text, reasoning.ProviderSignature));
                    break;
                case ToolCallPart call:
                    blocks.Add(new InputToolCallBlock(call.ToolCallId, call.ToolName, call.Input));
                    break;
                case ToolResultPart result:
                    blocks.Add(ProjectToolResult(result, media, images));
                    break;
                case AttachmentPart attachment when attachment.ProviderVisible:
                    blocks.Add(new InputTextBlock(attachment.Content));
                    break;
                case FilePart file when file.ProviderReady:
                    if (TryHydrateImage(file.ArtifactUri, file.MimeType, media) is { } fileImage)
                        images.Add(fileImage);
                    else
                        blocks.Add(new InputTextBlock($"[media file: {file.FileName ?? file.ArtifactUri}]"));
                    break;
            }
        }

        if (blocks.Count == 0 && images.Count == 0)
        {
            // The reference repairs empty assistant content to a visible placeholder so
            // turn boundaries survive in the replayed transcript.
            if (message.Role == MessageRole.Assistant)
            {
                yield return new ModelInputMessage(message.Role, [new InputTextBlock("(no content)")]);
            }
            yield break;
        }
        if (blocks.Count > 0)
            yield return new ModelInputMessage(message.Role, blocks);
        if (images.Count > 0)
            yield return new ModelInputMessage(MessageRole.User, [.. images]);
    }

    private static ModelContentBlock ProjectToolResult(
        ToolResultPart result,
        IReadOnlyDictionary<string, MediaPayload>? media,
        List<InputImageBlock> images)
    {
        var text = new List<string>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case ToolResultText textBlock:
                    text.Add(textBlock.Text);
                    break;
                case ToolResultMedia mediaBlock:
                    if (TryHydrateImage(mediaBlock.ArtifactUri, mediaBlock.MimeType, media) is { } image)
                        images.Add(image);
                    else
                        text.Add($"[media file: {mediaBlock.FileName ?? mediaBlock.ArtifactUri}]");
                    break;
            }
        }
        return new InputToolResultBlock(result.ToolCallId, string.Join("\n", text), result.IsError);
    }

    private static InputImageBlock? TryHydrateImage(
        string artifactUri, string mimeType, IReadOnlyDictionary<string, MediaPayload>? media)
    {
        if (media is null || !media.TryGetValue(artifactUri, out var payload))
            return null;
        if (!mimeOrPayloadStartsWithImage(mimeType, payload))
            return null;
        return new InputImageBlock(
            payload.MimeType is { Length: > 0 } ? payload.MimeType : mimeType,
            payload.Base64Data);

        static bool mimeOrPayloadStartsWithImage(string mimeType, MediaPayload payload) =>
            mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            payload.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
}
