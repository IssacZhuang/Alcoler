using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

/// <summary>
/// Ports ZCode normalizeReasoningHistory: structural cleanup of replayed assistant
/// history before serialization. Providers that cannot replay thinking blocks pass
/// <c>keepReasoning: false</c> to drop them first; every provider then runs the same
/// structural passes. Cross-model signature compatibility is handled reactively by
/// the kernel's signature-rejection recovery instead of proactively here, because
/// Alcoler history does not record which model produced a reasoning block.
/// </summary>
internal static class ReasoningHistoryNormalizer
{
    private const string EmptyAssistantContentFallback = "(no content)";
    private const string UnsupportedImageText =
        "[Attached media]\n[Media omitted from provider request because the selected model does not support image input.]";

    public static IReadOnlyList<ModelInputMessage> Normalize(
        IReadOnlyList<ModelInputMessage> messages, bool keepReasoning, bool supportsImages = true)
    {
        var withoutReasoning = keepReasoning ? messages : DropReasoningBlocks(messages);
        var withoutUnsupportedImages = supportsImages
            ? withoutReasoning
            : DowngradeUnsupportedImages(withoutReasoning);
        var withoutOrphans = RemoveReasoningOnlyAssistants(withoutUnsupportedImages);
        var withoutTrailing = RemoveTrailingReasoning(withoutOrphans);
        var withoutWhitespaceOnly = RemoveWhitespaceOnlyAssistants(withoutTrailing);
        return RepairEmptyAssistantContent(withoutWhitespaceOnly);
    }

    private static IReadOnlyList<ModelInputMessage> DowngradeUnsupportedImages(
        IReadOnlyList<ModelInputMessage> messages)
    {
        List<ModelInputMessage>? result = null;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (!message.Content.Any(block => block is InputImageBlock))
                continue;
            result ??= [.. messages];
            result[index] = message with
            {
                Content = message.Content.Select(block =>
                        block is InputImageBlock ? new InputTextBlock(UnsupportedImageText) : block)
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
        var removed = false;
        var result = new List<ModelInputMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (ShouldDrop(message, block =>
                    block is InputTextBlock text &&
                    (text.Text.Trim().Length == 0 || text.Text.Trim() == EmptyAssistantContentFallback)))
            {
                removed = true;
                continue;
            }
            result.Add(message);
        }
        return removed ? MergeAdjacentUserMessages(result) : messages;
    }

    private static IReadOnlyList<ModelInputMessage> RemoveWhere(
        IReadOnlyList<ModelInputMessage> messages, Predicate<ModelInputMessage> shouldRemove)
    {
        var removed = false;
        var result = new List<ModelInputMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (shouldRemove(message))
            {
                removed = true;
                continue;
            }
            result.Add(message);
        }
        return removed ? result : messages;
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
                message.Content[0] is InputTextBlock)
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
