using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Kernel.Runtime;

internal sealed class TurnRequestEntries
{
    private readonly Dictionary<MessageId, List<ConversationMessage>> _entries = [];

    public void AppendAfter(MessageId anchor, ConversationMessage message)
    {
        if (!_entries.TryGetValue(anchor, out var entries))
            _entries[anchor] = entries = [];
        entries.Add(message);
    }

    public IReadOnlyList<ConversationMessage> Merge(IReadOnlyList<ConversationMessage> history)
    {
        if (_entries.Count == 0)
            return history;

        var merged = new List<ConversationMessage>(history.Count + _entries.Values.Sum(entries => entries.Count));
        foreach (var message in history)
        {
            merged.Add(message);
            if (_entries.TryGetValue(message.Id, out var entries))
                merged.AddRange(entries);
        }
        return merged;
    }

    public void Clear() => _entries.Clear();
}
