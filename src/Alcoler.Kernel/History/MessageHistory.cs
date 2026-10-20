using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Identifiers;

namespace Alcoler.Kernel.History;

public sealed class RecentFileRead
{
    public RecentFileRead(string path, string content, DateTimeOffset readAt)
    {
        Path = path;
        Content = content;
        ReadAt = readAt;
    }

    public string Path { get; }

    public string Content { get; }

    public DateTimeOffset ReadAt { get; }
}

public sealed class MessageHistory
{
    private readonly object _sync = new();
    private List<ConversationMessage> _messages = [];
    private readonly List<RecentFileRead> _recentReads = [];

    public int Count { get { lock (_sync) return _messages.Count; } }

    public void Append(ConversationMessage message)
    {
        lock (_sync)
        {
            _messages.Add(message);
        }
    }

    public void AppendRange(IReadOnlyList<ConversationMessage> messages)
    {
        lock (_sync)
        {
            _messages.AddRange(messages);
        }
    }

    public IReadOnlyList<ConversationMessage> Snapshot()
    {
        lock (_sync)
        {
            return [.. _messages];
        }
    }

    public void ReplaceAll(IReadOnlyList<ConversationMessage> messages)
    {
        lock (_sync)
        {
            _messages = [.. messages];
        }
    }

    public void TrimAfter(MessageId exclusiveHead)
    {
        lock (_sync)
        {
            var index = _messages.FindIndex(m => m.Id == exclusiveHead);
            if (index >= 0)
            {
                _messages.RemoveRange(index, _messages.Count - index);
            }
        }
    }

    public int? LastIndexOf(MessageId messageId)
    {
        lock (_sync)
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Id == messageId)
                {
                    return i;
                }
            }
            return null;
        }
    }

    public MessageId? LastAssistantMessageId()
    {
        lock (_sync)
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Role == MessageRole.Assistant)
                {
                    return _messages[i].Id;
                }
            }
            return null;
        }
    }

    public void RecordFileRead(string path, string content, DateTimeOffset readAt)
    {
        lock (_sync)
        {
            _recentReads.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            _recentReads.Add(new RecentFileRead(path, content, readAt));
            while (_recentReads.Count > 5)
            {
                _recentReads.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<RecentFileRead> RecentReads()
    {
        lock (_sync)
        {
            return [.. _recentReads];
        }
    }

    public void ClearRecentReads()
    {
        lock (_sync)
        {
            _recentReads.Clear();
        }
    }
}
