using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;

namespace Alcoler.Kernel.Runtime;

public static class ThinkingSignatureRecovery
{
    public const string RemovedThinkingText = "[Thinking removed]";

    public static bool IsSignatureRejection(ModelFailure failure, int statusCode = 400)
    {
        if (statusCode != 400 || failure.Kind != ModelFailureKind.InvalidRequest)
            return false;

        var message = failure.Message.ToLowerInvariant();
        if (message.Contains("signature in thinking block"))
            return true;

        var namesThinkingBlock = message.Contains("thinking block")
            || message.Contains("`thinking`")
            || message.Contains("redacted_thinking");
        var namesSignatureFailure = message.Contains("cannot be modified")
            || message.Contains("invalid signature");
        return namesThinkingBlock && namesSignatureFailure;
    }

    public static bool IsSignedOrRedacted(InputReasoningBlock block) =>
        !string.IsNullOrEmpty(block.Signature) || block.RedactedData is not null;

    public static IReadOnlyList<ModelInputMessage> RepairRequestMessages(IReadOnlyList<ModelInputMessage> messages)
    {
        List<ModelInputMessage>? result = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role != MessageRole.Assistant ||
                !message.Content.Any(block => block is InputReasoningBlock reasoning && IsSignedOrRedacted(reasoning)))
                continue;

            result ??= [.. messages];
            var kept = message.Content.Where(block =>
                    block is not InputReasoningBlock reasoning || !IsSignedOrRedacted(reasoning))
                .Where(block => block is not InputTextBlock text || text.Text.Trim().Length > 0)
                .ToList();
            if (kept.All(block => block is InputReasoningBlock))
                kept.Add(new InputTextBlock(RemovedThinkingText));
            result[i] = message with { Content = kept };
        }
        return result ?? messages;
    }
}
