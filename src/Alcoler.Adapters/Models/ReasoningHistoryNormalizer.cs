using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;
using Alcoler.Kernel.Runtime;

namespace Alcoler.Adapters.Models;

internal static class ReasoningHistoryNormalizer
{
    private const string EmptyAssistantContentFallback = "(no content)";
    private const string UnsupportedImageText =
        "[Attached media]\n[Media omitted from provider request because the selected model does not support image input.]";

    public static IReadOnlyList<ModelInputMessage> Normalize(
        IReadOnlyList<ModelInputMessage> messages, bool keepReasoning, bool supportsImages = true,
        ModelDescriptor? targetModel = null)
    {
        var compatible = RemoveCrossModelReasoning(messages, targetModel);
        var withoutReasoning = keepReasoning ? compatible : DropReasoningBlocks(compatible);
        var withoutUnsupportedImages = supportsImages
            ? withoutReasoning
            : DowngradeUnsupportedImages(withoutReasoning);
        var withoutOrphans = RemoveReasoningOnlyAssistants(withoutUnsupportedImages);
        var withoutTrailing = RemoveTrailingReasoning(withoutOrphans);
        var withoutWhitespaceOnly = RemoveWhitespaceOnlyAssistants(withoutTrailing);
        return RepairEmptyAssistantContent(withoutWhitespaceOnly);
    }

    private static readonly string[][] ReasoningProviderGroups =
    [
        ["builtin:zai-coding-plan", "account:zai-individual-coding-plan", "account:zai-team-coding-plan"],
        ["builtin:bigmodel-coding-plan", "account:bigmodel-individual-coding-plan", "account:bigmodel-team-coding-plan"],
    ];

    private static IReadOnlyList<ModelInputMessage> RemoveCrossModelReasoning(
        IReadOnlyList<ModelInputMessage> messages, ModelDescriptor? targetModel)
    {
        if (targetModel is null)
            return messages;
        List<ModelInputMessage>? result = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role != MessageRole.Assistant ||
                string.IsNullOrEmpty(message.ProviderId) || string.IsNullOrEmpty(message.ModelId) ||
                message.ModelId == targetModel.ModelId && ProvidersCompatible(message.ProviderId, targetModel.ProviderId))
                continue;
            var content = message.Content.Where(block =>
                block is not InputReasoningBlock reasoning || !ThinkingSignatureRecovery.IsSignedOrRedacted(reasoning)).ToList();
            if (content.Count == message.Content.Count)
                continue;
            result ??= [.. messages];
            result[index] = message with { Content = content };
        }
        return result ?? messages;
    }

    private static bool ProvidersCompatible(string source, string target) =>
        source == target || ReasoningProviderGroups.Any(group => group.Contains(source) && group.Contains(target));

    private static IReadOnlyList<ModelInputMessage> DowngradeUnsupportedImages(
        IReadOnlyList<ModelInputMessage> messages)
    {
        List<ModelInputMessage>? result = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (!message.Content.Any(block => block is InputImageBlock || block is InputToolResultBlock { Images.Count: > 0 }))
                continue;
            result ??= [.. messages];
            result[index] = message with
            {
                Content = message.Content.Select(block =>
                        block switch
                        {
                            InputImageBlock => new InputTextBlock(UnsupportedImageText),
                            InputToolResultBlock { Images.Count: > 0 } toolResult => toolResult with
                            {
                                Content = string.Join("\n", new[] { toolResult.Content }.Concat(toolResult.Images.Select(_ => UnsupportedImageText))),
                                Images = null,
                            },
                            _ => block,
                        })
                    .ToList(),
            };
        }
        return result ?? messages;
    }

    private static IReadOnlyList<ModelInputMessage> DropReasoningBlocks(
        IReadOnlyList<ModelInputMessage> messages)
    {
        List<ModelInputMessage>? result = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (!message.Content.Any(block => block is InputReasoningBlock))
                continue;
            result ??= [.. messages];
            result[index] = message with
            {
                Content = message.Content.Where(block => block is not InputReasoningBlock).ToList(),
            };
        }
        return result ?? messages;
    }

    private static IReadOnlyList<ModelInputMessage> RemoveReasoningOnlyAssistants(
        IReadOnlyList<ModelInputMessage> messages) =>
        RemoveWhere(messages, message => ShouldDrop(message, block => block is InputReasoningBlock));

    private static IReadOnlyList<ModelInputMessage> RemoveWhitespaceOnlyAssistants(
        IReadOnlyList<ModelInputMessage> messages)
    {
        var first = -1;
        for (var index = 0; index < messages.Count; index++)
        {
            if (IsWhitespaceOnlyAssistant(messages[index]))
            {
                first = index;
                break;
            }
        }
        if (first < 0)
            return messages;

        var result = new List<ModelInputMessage>(messages.Count - 1);
        for (var index = 0; index < first; index++)
            result.Add(messages[index]);
        for (var index = first + 1; index < messages.Count; index++)
        {
            if (!IsWhitespaceOnlyAssistant(messages[index]))
                result.Add(messages[index]);
        }
        return MergeAdjacentUserMessages(result);
    }

    private static bool IsWhitespaceOnlyAssistant(ModelInputMessage message) =>
        ShouldDrop(message, static block => block is InputTextBlock text && IsEmptyOrFallback(text.Text));

    private static bool IsEmptyOrFallback(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.IsEmpty || trimmed.SequenceEqual(EmptyAssistantContentFallback.AsSpan());
    }

    private static IReadOnlyList<ModelInputMessage> RemoveWhere(
        IReadOnlyList<ModelInputMessage> messages, Predicate<ModelInputMessage> shouldRemove)
    {
        var first = -1;
        for (var index = 0; index < messages.Count; index++)
        {
            if (shouldRemove(messages[index]))
            {
                first = index;
                break;
            }
        }
        if (first < 0)
            return messages;

        var result = new List<ModelInputMessage>(messages.Count - 1);
        for (var index = 0; index < first; index++)
            result.Add(messages[index]);
        for (var index = first + 1; index < messages.Count; index++)
        {
            if (!shouldRemove(messages[index]))
                result.Add(messages[index]);
        }
        return result;
    }

    private static bool ShouldDrop(ModelInputMessage message, Func<ModelContentBlock, bool> blockMatches) =>
        message.Role == MessageRole.Assistant &&
        !HasToolCalls(message) &&
        message.Content.Count > 0 &&
        message.Content.All(blockMatches);

    private static IReadOnlyList<ModelInputMessage> RemoveTrailingReasoning(
        IReadOnlyList<ModelInputMessage> messages)
    {
        if (messages.Count == 0)
            return messages;
        var last = messages[^1];
        if (last.Role != MessageRole.Assistant)
            return messages;

        var end = last.Content.Count;
        while (end > 0 && last.Content[end - 1] is InputReasoningBlock)
            end--;
        if (end == last.Content.Count)
            return messages;

        var result = new List<ModelInputMessage>(messages);
        result[^1] = last with { Content = [.. last.Content.Take(end)] };
        return result;
    }

    private static IReadOnlyList<ModelInputMessage> RepairEmptyAssistantContent(
        IReadOnlyList<ModelInputMessage> messages)
    {
        List<ModelInputMessage>? result = null;
        for (var index = 0; index < messages.Count - 1; index++)
        {
            var message = messages[index];
            if (message.Role != MessageRole.Assistant ||
                message.Content.Count > 0 || HasToolCalls(message))
                continue;
            result ??= [.. messages];
            result[index] = message with { Content = [new InputTextBlock(EmptyAssistantContentFallback)] };
        }
        return result ?? messages;
    }

    private static IReadOnlyList<ModelInputMessage> MergeAdjacentUserMessages(
        IReadOnlyList<ModelInputMessage> messages)
    {
        var result = new List<ModelInputMessage>();
        foreach (var message in messages)
        {
            var previous = result.Count > 0 ? result[^1] : null;
            if (previous is null ||
                previous.Role != MessageRole.User ||
                message.Role != MessageRole.User ||
                ContainsToolResult(previous) || ContainsToolResult(message))
            {
                result.Add(message);
                continue;
            }

            var mergedContent = new List<ModelContentBlock>(previous.Content);
            if (previous.Content.Count > 0 &&
                previous.Content[^1] is InputTextBlock previousText &&
                message.Content.Count > 0 && message.Content[0] is InputTextBlock)
                mergedContent[^1] = new InputTextBlock(previousText.Text + "\n");
            mergedContent.AddRange(message.Content);
            result[^1] = new ModelInputMessage(
                message.Role, mergedContent, message.CacheControl ?? previous.CacheControl);
        }
        return result;
    }

    private static bool HasToolCalls(ModelInputMessage message) =>
        message.Content.Any(block => block is InputToolCallBlock);

    private static bool ContainsToolResult(ModelInputMessage message) =>
        message.Content.Any(block => block is InputToolResultBlock);
}
