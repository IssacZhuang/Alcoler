using System.Runtime.CompilerServices;
using Alcoler.Adapters.Storage;
using Alcoler.ViewModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Alcoler.Controls;

internal sealed class ConversationImageCache
{
    internal const int MaxCachedEntries = 128;
    internal const long MaxCachedThumbnailBytes = 24 * 1024 * 1024;
    private const int ThumbnailDecodeSize = 192;

    private static readonly ConditionalWeakTable<FileArtifactStore, ConversationImageCache> CachesByStore = new();

    private readonly Func<string, string, CancellationToken, Task<Bitmap?>> _loadThumbnail;
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    internal ConversationImageCache(Func<string, string, CancellationToken, Task<Bitmap?>> loadThumbnail,
        int maxEntries = MaxCachedEntries, long maxBytes = MaxCachedThumbnailBytes)
    {
        _loadThumbnail = loadThumbnail;
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    internal static ConversationImageCache For(FileArtifactStore artifacts) =>
        CachesByStore.GetValue(artifacts, store =>
            new ConversationImageCache((uri, mime, token) => LoadArtifactThumbnail(store, uri, mime, token)));

    private static async Task<Bitmap?> LoadArtifactThumbnail(
        FileArtifactStore artifacts, string artifactUri, string mimeType, CancellationToken cancellationToken)
    {
        var data = await artifacts.TryLoadAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        if (data is null || cancellationToken.IsCancellationRequested)
            return null;
        return ComposerImageAttachment.DecodeThumbnail(data.Value.ToArray(), mimeType.ToLowerInvariant(), ThumbnailDecodeSize);
    }

    internal async Task<Lease?> AcquireAsync(string artifactUri, string mimeType, CancellationToken cancellationToken)
    {
        InFlight flight;
        lock (_gate)
        {
            if (_entries.TryGetValue(artifactUri, out var hit))
            {
                hit.RefCount++;
                Touch(hit);
                return new Lease(this, hit);
            }
            if (!_inFlight.TryGetValue(artifactUri, out flight!))
            {
                flight = new InFlight { Uri = artifactUri };
                _inFlight.Add(artifactUri, flight);
                flight.Load = Task.Run(() => _loadThumbnail(artifactUri, mimeType, flight.Cancellation.Token), flight.Cancellation.Token);
                flight.Load.ContinueWith(load => LoadCompleted(flight, load), TaskScheduler.Default);
            }
            flight.Waiters++;
        }

        Lease? lease = null;
        var loaded = false;
        try
        {
            await flight.Load.WaitAsync(cancellationToken).ConfigureAwait(false);
            loaded = true;
        }
        finally
        {
            lock (_gate)
            {
                InsertLoaded(flight);
                flight.Waiters--;
                if (flight.Entry is { } entry)
                {
                    if (loaded)
                    {
                        Touch(entry);
                        lease = new Lease(this, entry);
                    }
                    else
                        ReleaseEntry(entry);
                }
                if (flight.Waiters == 0 && !flight.Abandoned && !flight.Load.IsCompleted)
                {
                    // The last waiter left before the shared decode finished; nobody will accept its result.
                    flight.Abandoned = true;
                    if (_inFlight.TryGetValue(flight.Uri, out var registered) && ReferenceEquals(registered, flight))
                        _inFlight.Remove(flight.Uri);
                    flight.Cancellation.Cancel();
                }
            }
        }
        return lease;
    }

    private void LoadCompleted(InFlight flight, Task<Bitmap?> load)
    {
        _ = load.Exception;
        lock (_gate)
        {
            if (flight.Abandoned)
            {
                if (load.Status == TaskStatus.RanToCompletion && load.Result is { } stray)
                    Dispose(stray);
                flight.Cancellation.Dispose();
                return;
            }
            InsertLoaded(flight);
            flight.Cancellation.Dispose();
        }
    }

    private void InsertLoaded(InFlight flight)
    {
        if (flight.Inserted || flight.Abandoned || !flight.Load.IsCompleted)
            return;
        flight.Inserted = true;
        if (_inFlight.TryGetValue(flight.Uri, out var registered) && ReferenceEquals(registered, flight))
            _inFlight.Remove(flight.Uri);
        if (!flight.Load.IsCompletedSuccessfully || flight.Load.Result is not { } bitmap)
            return;
        // Pending waiters own references until they accept a lease or leave.
        var entry = new Entry
        {
            Uri = flight.Uri,
            Bitmap = bitmap,
            Bytes = EstimateBytes(bitmap),
            Cached = true,
            RefCount = flight.Waiters
        };
        entry.Node = _lru.AddLast(entry);
        _entries.Add(flight.Uri, entry);
        _bytes += entry.Bytes;
        flight.Entry = entry;
        EvictOverBudget(entry);
    }

    private void EvictOverBudget(Entry freshlyInserted)
    {
        while ((_entries.Count > _maxEntries || _bytes > _maxBytes) && _lru.First is { } least)
        {
            if (ReferenceEquals(least.Value, freshlyInserted))
                break; // A single entry above either cap stays resident; the next insert evicts it.
            var entry = least.Value;
            _lru.RemoveFirst();
            entry.Node = null;
            _entries.Remove(entry.Uri);
            _bytes -= entry.Bytes;
            entry.Cached = false;
            if (entry.RefCount == 0)
                Dispose(entry.Bitmap);
        }
    }

    private void Touch(Entry entry)
    {
        if (entry.Node is not { } node)
            return;
        _lru.Remove(node);
        entry.Node = _lru.AddLast(entry);
    }

    private void ReleaseEntry(Entry entry)
    {
        lock (_gate)
        {
            entry.RefCount--;
            if (entry.Cached || entry.RefCount > 0)
                return;
        }
        Dispose(entry.Bitmap);
    }

    private static long EstimateBytes(Bitmap bitmap) =>
        (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4; // 32 bits per decoded pixel.

    private static void Dispose(Bitmap bitmap)
    {
        // Bitmaps handed to Image sources are UI-thread-affine; disposal must not race the renderer.
        if (Dispatcher.UIThread.CheckAccess())
            bitmap.Dispose();
        else
            Dispatcher.UIThread.Post(bitmap.Dispose);
    }

    internal sealed class Entry
    {
        public string Uri = null!;
        public Bitmap Bitmap = null!;
        public long Bytes;
        public int RefCount;
        public bool Cached;
        public LinkedListNode<Entry>? Node;
    }

    private sealed class InFlight
    {
        public string Uri = null!;
        public readonly CancellationTokenSource Cancellation = new();
        public Task<Bitmap?> Load = null!;
        public int Waiters;
        public bool Abandoned;
        public bool Inserted;
        public Entry? Entry;
    }

    internal sealed class Lease
    {
        private ConversationImageCache? _owner;
        private Entry? _entry;

        internal Lease(ConversationImageCache owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        internal Bitmap? Bitmap => _entry?.Bitmap;

        internal void Release()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            var owner = Interlocked.Exchange(ref _owner, null);
            if (entry is not null && owner is not null)
                owner.ReleaseEntry(entry);
        }
    }
}
