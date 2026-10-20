using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

/// <summary>
/// Envelope for passing provider-agnostic system blocks through <see cref="ModelRequest"/>,
/// whose message roles only cover User/Assistant. Each system block becomes one
/// <see cref="ModelInputMessage"/> of role User whose text blocks carry <see cref="BlockPrefix"/>;
/// a message counts as a system-block message only when every block is prefixed.
/// Adapters extract these messages before building provider payloads
/// (Anthropic: system array; OpenAI-compatible: role "system" messages).
/// </summary>
public static class ModelRequestSystemBlocks
{
    public const string BlockPrefix = "alcoler-system-block:";

    public static ModelInputMessage ToMessage(IReadOnlyList<string> blocks) =>
        new(MessageRole.User, blocks.Select(b => (ModelContentBlock)new InputTextBlock(BlockPrefix + b)).ToArray());

    public static (IReadOnlyList<ModelInputMessage> Conversation, IReadOnlyList<string> SystemBlocks) Extract(
        IReadOnlyList<ModelInputMessage> messages)
    {
        List<string>? systemBlocks = null;
        List<ModelInputMessage>? conversation = null;
        foreach (var message in messages)
        {
            if (message.Role == MessageRole.System)
            {
                (systemBlocks ??= []).AddRange(message.Content.OfType<InputTextBlock>().Select(b => b.Text));
                continue;
            }
            if (IsSystemBlockMessage(message, out var blocks))
            {
                (systemBlocks ??= []).AddRange(blocks);
                continue;
            }
            (conversation ??= []).Add(message);
        }
        return (conversation ?? (IReadOnlyList<ModelInputMessage>)[], systemBlocks ?? (IReadOnlyList<string>)[]);
    }

    internal static IReadOnlyList<ModelInputMessage> Normalize(IReadOnlyList<ModelInputMessage> messages, bool preserveMidSystem)
    {
        var result = new List<ModelInputMessage>(messages.Count);
        var leading = true;
        foreach (var message in messages)
        {
            if (message.Role == MessageRole.System || IsSystemBlockMessage(message, out _))
            {
                var blocks = message.Role == MessageRole.System
                    ? message.Content.OfType<InputTextBlock>().Select(b => b.Text)
                    : message.Content.OfType<InputTextBlock>().Select(b => b.Text[BlockPrefix.Length..]);
                result.Add(message with
                {
                    Role = leading || preserveMidSystem ? MessageRole.System : MessageRole.User,
                    Content = blocks.Select(b => (ModelContentBlock)new InputTextBlock(b)).ToArray(),
                });
            }
            else
            {
                leading = false;
                result.Add(message);
            }
        }
        return result;
    }

    internal static (IReadOnlyList<ModelInputMessage> Conversation, IReadOnlyList<string> SystemBlocks) ExtractLeading(IReadOnlyList<ModelInputMessage> messages)
    {
        var normalized = Normalize(messages, preserveMidSystem: false);
        var count = normalized.TakeWhile(m => m.Role == MessageRole.System).Count();
        return (normalized.Skip(count).ToArray(), normalized.Take(count).SelectMany(m => m.Content.OfType<InputTextBlock>().Select(b => b.Text)).ToArray());
    }

    private static bool IsSystemBlockMessage(ModelInputMessage message, out IReadOnlyList<string> blocks)
    {
        blocks = [];
        if (message.Content.Count == 0)
            return false;

        var extracted = new List<string>(message.Content.Count);
        foreach (var block in message.Content)
        {
            if (block is not InputTextBlock text ||
                !text.Text.StartsWith(BlockPrefix, StringComparison.Ordinal))
                return false;
            extracted.Add(text.Text[BlockPrefix.Length..]);
        }
        blocks = extracted;
        return true;
    }
}
