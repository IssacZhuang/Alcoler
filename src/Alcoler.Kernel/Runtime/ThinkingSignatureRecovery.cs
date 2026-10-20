using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;

namespace Alcoler.Kernel.Runtime;

// Mirrors ZCode reasoning-history-normalization: a 400 that names a thinking-block signature
// problem is repaired by stripping reasoning parts from history and retrying the request once.
// Matching is deliberately narrow; other invalid requests must not trigger history edits.
public static class ThinkingSignatureRecovery
{
    public const string RemovedThinkingText = "[Thinking removed]";

    public static bool IsSignatureRejection(ModelFailure failure)
    {
        if (failure.Kind != ModelFailureKind.InvalidRequest)
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

    // Removes reasoning parts from assistant messages; an assistant message left without any
    // part gets a text placeholder so turn boundaries survive. Returns the input reference
    // unchanged when there is nothing to strip.
    public static IReadOnlyList<ConversationMessage> StripReasoningParts(IReadOnlyList<ConversationMessage> messages)
    {
        List<ConversationMessage>? result = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role != MessageRole.Assistant || message.Parts.All(p => p is not ReasoningPart))
                continue;

            result ??= [.. messages];
            var kept = message.Parts.Where(p => p is not ReasoningPart).ToList();
            if (kept.Count == 0)
                kept.Add(new TextPart(StrongId.NewPartId(), RemovedThinkingText));
            result[i] = message with { Parts = kept };
        }

        return result ?? messages;
    }
}
