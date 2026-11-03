namespace Alcoler.Adapters.Web;

public sealed record WebFetchContent(string Content, string ContentType);

public sealed class WebFetchCache
{
    internal const int MaxRetainedContentChars = 100_001;

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _entries = new();
    private readonly TimeSpan _ttl;
    private readonly int _capacity;

    public WebFetchCache(int capacity = 50, TimeSpan? ttl = null)
    {
        _capacity = capacity;
        _ttl = ttl ?? TimeSpan.FromMinutes(15);
    }

    public WebFetchContent? TryGet(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            RemoveExpiredCore(now);
            var node = Find(key);
            if (node is null)
                return null;
            _entries.Remove(node);
            _entries.AddLast(node);
            return node.Value.Content;
        }
    }

    public void Set(string key, WebFetchContent content, DateTimeOffset now)
    {
        if (content.Content.Length > MaxRetainedContentChars)
            content = content with { Content = content.Content[..MaxRetainedContentChars] };
        lock (_gate)
        {
            RemoveExpiredCore(now);
            var existing = Find(key);
            if (existing is not null)
                _entries.Remove(existing);
            _entries.AddLast(new Entry(key, content, now + _ttl));
            while (_entries.Count > _capacity)
                _entries.RemoveFirst();
        }
    }

    internal void RemoveExpired(DateTimeOffset now)
    {
        lock (_gate)
            RemoveExpiredCore(now);
    }

    private void RemoveExpiredCore(DateTimeOffset now)
    {
        for (var node = _entries.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.ExpiresAt <= now)
                _entries.Remove(node);
            node = next;
        }
    }

    private LinkedListNode<Entry>? Find(string key)
    {
        for (var node = _entries.First; node is not null; node = node.Next)
            if (node.Value.Key == key)
                return node;
        return null;
    }

    private sealed record Entry(string Key, WebFetchContent Content, DateTimeOffset ExpiresAt);
}
