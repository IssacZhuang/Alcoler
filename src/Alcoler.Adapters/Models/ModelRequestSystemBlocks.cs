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
            if (message.Role == MessageRole.System)
            {
                var blocks = message.Content.OfType<InputTextBlock>().Select(b => b.Text);
                result.Add(Normalized(message, blocks, leading, preserveMidSystem));
            }
            else if (IsSystemBlockMessage(message, out var blocks))
            {
                result.Add(Normalized(message, blocks, leading, preserveMidSystem));
            }
            else
            {
                leading = false;
                result.Add(message);
            }
        }
        return result;
    }

    internal static (IReadOnlyList<ModelInputMessage> Conversation, IReadOnlyList<string> SystemBlocks) ExtractLeading(
        IReadOnlyList<ModelInputMessage> messages, bool preserveMidSystem = false)
    {
        var conversation = new List<ModelInputMessage>(messages.Count);
        List<string>? systemBlocks = null;
        var leading = true;
        foreach (var message in messages)
        {
            if (message.Role == MessageRole.System)
            {
                AddLeadingOrConversation(message, message.Content.OfType<InputTextBlock>().Select(b => b.Text),
                    leading, preserveMidSystem, conversation, ref systemBlocks);
            }
            else if (IsSystemBlockMessage(message, out var blocks))
            {
                AddLeadingOrConversation(message, blocks, leading, preserveMidSystem, conversation, ref systemBlocks);
            }
            else
            {
                leading = false;
                conversation.Add(message);
            }
        }
        return (conversation, systemBlocks ?? []);
    }

    private static ModelInputMessage Normalized(ModelInputMessage message, IEnumerable<string> blocks, bool leading, bool preserveMidSystem) =>
        message with
        {
            Role = leading || preserveMidSystem ? MessageRole.System : MessageRole.User,
            Content = blocks.Select(b => (ModelContentBlock)new InputTextBlock(b)).ToArray(),
        };

    private static void AddLeadingOrConversation(ModelInputMessage message, IEnumerable<string> blocks, bool leading,
        bool preserveMidSystem, List<ModelInputMessage> conversation, ref List<string>? systemBlocks)
    {
        if (leading)
        {
            foreach (var block in blocks)
                (systemBlocks ??= []).Add(block);
            return;
        }
        conversation.Add(Normalized(message, blocks, leading: false, preserveMidSystem));
    }

    private static bool IsSystemBlockMessage(ModelInputMessage message, out IReadOnlyList<string> blocks)
    {
        blocks = [];
        if (message.Content.Count == 0 || !IsPrefixedTextBlock(message.Content[0]))
            return false;

        var extracted = new List<string>(message.Content.Count);
        foreach (var block in message.Content)
        {
            if (block is not InputTextBlock text || !IsPrefixed(text.Text))
                return false;
            extracted.Add(text.Text[BlockPrefix.Length..]);
        }
        blocks = extracted;
        return true;
    }

    private static bool IsPrefixedTextBlock(ModelContentBlock block) =>
        block is InputTextBlock text && IsPrefixed(text.Text);

    private static bool IsPrefixed(string text) =>
        text.AsSpan().StartsWith(BlockPrefix.AsSpan(), StringComparison.Ordinal);
}
