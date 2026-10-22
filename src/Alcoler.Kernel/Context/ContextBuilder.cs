using System.Text;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Projections;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Prompts;
using Alcoler.Kernel.Runtime;

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
    IReadOnlyDictionary<string, MediaPayload>? Media = null,
    bool InlineToolResultImages = false,
    bool UseMidConversationSystem = false);

/// <summary>An artifact hydrated for provider replay, keyed by artifact URI.</summary>
public sealed record MediaPayload(string MimeType, string Base64Data, string? FileName = null);

public sealed record AssembledContext(
    ModelRequest Request,
    int PrefixMessageCount,
    IReadOnlyList<ContextUsageCategory> Breakdown)
{
    internal int EstimatedRequestTokens { get; init; }
}

public static class ContextBuilder
{
    public static AssembledContext Assemble(ContextAssemblyInput input) =>
        AssembleCore(input, out _);

    private static AssembledContext AssembleCore(
        ContextAssemblyInput input, out int requestTokens)
    {
        using var meter = new TokenEstimator.JsonLengthMeter();
        long totalTokens = 0;
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
        foreach (var message in messages)
            totalTokens += meter.MeasureMessage(message).Tokens;

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
            var metaMessage = new ModelInputMessage(MessageRole.User, [new InputTextBlock(body)]);
            messages.Add(metaMessage);
            totalTokens += meter.MeasureMessage(metaMessage).Tokens;
            AddCategory(isSkills ? ContextUsageSources.Skills : ContextUsageSources.MetaUserContext, body.Length);
        }

        if (!string.IsNullOrEmpty(input.ToolPrompt))
        {
            var toolPromptMessage = new ModelInputMessage(MessageRole.System, [new InputTextBlock(input.ToolPrompt)]);
            messages.Add(toolPromptMessage);
            totalTokens += meter.MeasureMessage(toolPromptMessage).Tokens;
            AddCategory(ContextUsageSources.ToolPrompt, input.ToolPrompt.Length);
        }
        var prefixCount = messages.Count;

        foreach (var tools in input.Tools.GroupBy(tool => tool.Source))
        {
            long characters = 0;
            foreach (var tool in tools)
            {
                var measured = meter.MeasureTool(tool);
                characters += measured.Characters;
                totalTokens += measured.Tokens;
            }
            AddCategory(tools.Key == ToolSource.Mcp ? ContextUsageSources.McpToolSchemas : ContextUsageSources.SystemToolSchemas,
                characters);
        }

        var lastNonSystemIndex = -1;
        long historyCharacters = 0;
        foreach (var message in ProjectHistory(input.History, input.UseMidConversationSystem))
        {
            foreach (var projected in Project(message, input.Media, input.InlineToolResultImages))
            {
                messages.Add(projected);
                var measured = meter.MeasureMessage(projected);
                historyCharacters += measured.Characters;
                totalTokens += measured.Tokens;
                if (projected.Role != MessageRole.System)
                    lastNonSystemIndex = messages.Count - 1;
            }
        }

        if (lastNonSystemIndex >= 0)
            messages[lastNonSystemIndex] = messages[lastNonSystemIndex] with { CacheControl = new CacheControl() };

        AddCategory(ContextUsageSources.Messages, historyCharacters);
        requestTokens = (int)Math.Min(int.MaxValue, totalTokens);
        return new AssembledContext(new ModelRequest(messages, input.Tools, input.Options), prefixCount, breakdown)
        {
            EstimatedRequestTokens = requestTokens,
        };

        void AddCategory(string source, long characters)
        {
            if (characters > 0)
                breakdown.Add(new ContextUsageCategory(source, characters));
        }
    }

    private static IReadOnlyList<ConversationMessage> ProjectHistory(
        IReadOnlyList<ConversationMessage> history, bool useMidConversationSystem)
    {
        var hasRuntimeReminder = false;
        var hasAttachmentLike = false;
        foreach (var message in history)
        {
            if (!IsRuntimeReminder(message))
                continue;
            hasRuntimeReminder = true;
            if (IsAttachmentLike(message))
            {
                hasAttachmentLike = true;
                break;
            }
        }
        if (!useMidConversationSystem && !hasRuntimeReminder)
            return history;

        IReadOnlyList<ConversationMessage> reordered = history;
        if (hasAttachmentLike)
        {
            var attachments = new List<ConversationMessage>();
            var ordered = new List<ConversationMessage>(history.Count);
            for (var index = history.Count - 1; index >= 0; index--)
            {
                var message = history[index];
                if (IsAttachmentLike(message))
                {
                    attachments.Add(message);
                    continue;
                }
                if ((message.Presentation is not null || message.Role is MessageRole.Assistant or MessageRole.System || HasToolResults(message)) && attachments.Count > 0)
                {
                    ordered.AddRange(attachments);
                    attachments.Clear();
                }
                ordered.Add(message);
            }
            ordered.AddRange(attachments);
            ordered.Reverse();
            reordered = ordered;
        }

        var projected = new List<ConversationMessage>(reordered.Count);
        var pending = new List<ConversationMessage>();
        var systems = new List<ConversationMessage>();
        foreach (var message in reordered)
        {
            if (message.Presentation is not null ||
                PendingHasPresentation(pending) && message.Role == MessageRole.User && !IsRuntimeReminder(message))
                FlushPending(projected, pending, systems);
            if (useMidConversationSystem && IsMidConversationSource(message) && CanProjectReminder(message))
            {
                pending.Add(message);
                continue;
            }
            if (message.Role is MessageRole.Assistant or MessageRole.System)
                FlushPending(projected, pending, systems);
            projected.Add(message);
        }
        FlushPending(projected, pending, systems);
        for (var index = 0; index < projected.Count; index++)
        {
            var message = projected[index];
            if (!systems.Contains(message))
                continue;
            if (index > 0 && projected[index - 1].Role == MessageRole.User &&
                (index == projected.Count - 1 || projected[index + 1].Role == MessageRole.Assistant))
                continue;
            projected[index] = message with
            {
                Role = MessageRole.User,
                Parts = [new TextPart(message.Parts[0].Id, SystemReminder.Wrap(
                    System.Text.RegularExpressions.Regex.Replace(ReminderBody(message), @"</?system-reminder\b", match => "&lt;" + match.Value[1..],
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)))],
                Source = MessageSource.LegacySynthetic,
                Presentation = null,
            };
        }

        if (!hasRuntimeReminder)
            return projected;

        var result = new List<ConversationMessage>(projected.Count);
        pending.Clear();
        foreach (var message in projected)
        {
            if (IsRuntimeReminder(message) &&
                (pending.Count > 0 || result.Count > 0 && HasToolResults(result[^1])))
            {
                pending.Add(message);
                continue;
            }
            if (!HasToolResults(message))
            {
                result.AddRange(pending);
                pending.Clear();
            }
            result.Add(message);
        }
        result.AddRange(pending);
        return result;
    }

    private static bool PendingHasPresentation(List<ConversationMessage> pending)
    {
        foreach (var message in pending)
        {
            if (message.Presentation is not null)
                return true;
        }
        return false;
    }

    private static void FlushPending(
        List<ConversationMessage> projected,
        List<ConversationMessage> pending,
        List<ConversationMessage> systems)
    {
        if (pending.Count == 0)
            return;
        if (projected.Count > 0 && projected[^1].Role == MessageRole.User)
        {
            var first = pending[0];
            var system = first with
            {
                Role = MessageRole.System,
                Parts = [new TextPart(first.Parts[0].Id, string.Join("\n\n", pending.Select(ReminderBody)))],
                Presentation = null,
            };
            projected.Add(system);
            systems.Add(system);
        }
        else
            projected.AddRange(pending);
        pending.Clear();
    }

    private static bool HasToolResults(ConversationMessage message) =>
        message.Role == MessageRole.User && message.Parts.Any(part => part is ToolResultPart);

    private static bool IsRuntimeReminder(ConversationMessage message) =>
        message.Role == MessageRole.User && message.Source is not null and not MessageSource.RealUser && !HasToolResults(message);

    private static bool IsAttachmentLike(ConversationMessage message) =>
        IsRuntimeReminder(message) && message.Source is not (MessageSource.ToolWarning or MessageSource.ResumeReferencedContext);

    private static bool IsMidConversationSource(ConversationMessage message) =>
        message.Presentation == MessagePresentation.UserSteer ||
        IsAttachmentLike(message) && message.Source != MessageSource.LegacySynthetic;

    private static bool CanProjectReminder(ConversationMessage message) =>
        message.Parts.Count > 0 && message.Parts.All(part => part is TextPart or AttachmentPart { ProviderVisible: true });

    private static string ReminderBody(ConversationMessage message)
    {
        var body = string.Join("\n", message.Parts.Select(part => part switch
        {
            TextPart text => text.Text,
            AttachmentPart attachment => attachment.Content,
            _ => "",
        }));
        if (message.Presentation == MessagePresentation.UserSteer)
            body = Steering.FormatUserSteer(body);
        const string opening = "<system-reminder>\n";
        const string closing = "\n</system-reminder>";
        return body.StartsWith(opening, StringComparison.Ordinal) && body.EndsWith(closing, StringComparison.Ordinal)
            ? body[opening.Length..^closing.Length]
            : body;
    }

    internal static IEnumerable<ModelInputMessage> Project(
        ConversationMessage message, IReadOnlyDictionary<string, MediaPayload>? media = null,
        bool inlineToolResultImages = false)
    {
        var blocks = new List<ModelContentBlock>();
        var images = new List<InputImageBlock>();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart text:
                    blocks.Add(new InputTextBlock(message.Presentation == MessagePresentation.UserSteer
                        ? Steering.FormatUserSteer(text.Text)
                        : text.Text));
                    break;
                case ReasoningPart reasoning:
                    blocks.Add(new InputReasoningBlock(reasoning.Text, reasoning.ProviderSignature,
                        reasoning.RedactedData, reasoning.EncryptedContent, reasoning.ProviderItemId));
                    break;
                case ToolCallPart call:
                    blocks.Add(new InputToolCallBlock(call.ToolCallId, call.ToolName, call.Input));
                    break;
                case ToolResultPart result:
                    var resultImages = inlineToolResultImages ? [] : images;
                    var projectedResult = ProjectToolResult(result, media, resultImages);
                    blocks.Add(inlineToolResultImages && resultImages.Count > 0
                        ? projectedResult with { Images = resultImages }
                        : projectedResult);
                    break;
                case AttachmentPart attachment when attachment.ProviderVisible:
                    blocks.Add(new InputTextBlock(attachment.Content));
                    break;
                case FilePart file when file.ProviderReady:
                    if (TryHydrateImage(file.ArtifactUri, file.MimeType, media) is { } fileImage)
                        blocks.Add(fileImage);
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
                yield return new ModelInputMessage(message.Role, [new InputTextBlock("(no content)")],
                    ProviderId: message.ProviderId, ModelId: message.ModelId);
            }
            yield break;
        }
        if (blocks.Count > 0)
            yield return new ModelInputMessage(message.Role, blocks,
                ProviderId: message.ProviderId, ModelId: message.ModelId);
        if (images.Count > 0)
            yield return new ModelInputMessage(MessageRole.User, [.. images]);
    }

    private static InputToolResultBlock ProjectToolResult(
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
