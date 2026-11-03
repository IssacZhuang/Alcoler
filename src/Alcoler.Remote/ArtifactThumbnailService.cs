namespace Alcoler.Remote;

internal enum ArtifactThumbnailStatus
{
    Success,
    NotFound,
    Unsupported,
    TooLarge,
    Failed,
    Busy,
}

internal readonly record struct ArtifactThumbnailResult(
    ArtifactThumbnailStatus Status, ArtifactThumbnail? Thumbnail = null);

internal sealed class ArtifactThumbnailService(
    Func<string, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>>? loader,
    Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ArtifactThumbnail?>>? generator,
    int maxCacheBytes = 16 * 1024 * 1024,
    int maxCacheEntries = 64,
    int maxConcurrent = 2,
    int maxPending = 64) : IDisposable
{
    internal const int MaxSourceBytes = 32 * 1024 * 1024;
    private const int MaxThumbnailBytes = 1024 * 1024;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _parallel = new(maxConcurrent, maxConcurrent);
    private readonly Dictionary<string, Work> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private long _cacheBytes;
    private bool _disposed;

    public bool IsConfigured => generator is not null;

    public ValueTask<ArtifactThumbnailResult> GetAsync(string uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Work work;
        var start = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(uri, out var cached))
            {
                _recency.Remove(cached.Node);
                _recency.AddLast(cached.Node);
                return ValueTask.FromResult(new ArtifactThumbnailResult(ArtifactThumbnailStatus.Success, cached.Thumbnail));
            }
            if (!_pending.TryGetValue(uri, out work!))
            {
                if (_pending.Count >= maxPending)
                    return ValueTask.FromResult(new ArtifactThumbnailResult(ArtifactThumbnailStatus.Busy));
                work = new Work();
                _pending.Add(uri, work);
                start = true;
            }
            work.Waiters++;
        }
        if (start)
            _ = Task.Run(() => GenerateAsync(uri, work));
        return new ValueTask<ArtifactThumbnailResult>(AwaitAsync(uri, work, cancellationToken));
    }

    public void Dispose()
    {
        Work[] pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cache.Clear();
            _recency.Clear();
            _cacheBytes = 0;
            pending = _pending.Values.ToArray();
            _pending.Clear();
        }
        foreach (var work in pending)
            work.Cancel();
    }

    private async Task<ArtifactThumbnailResult> AwaitAsync(string uri, Work work, CancellationToken cancellationToken)
    {
        try
        {
            return await work.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var cancel = false;
            lock (_gate)
            {
                if (--work.Waiters == 0 && !work.Completion.Task.IsCompleted)
                {
                    if (_pending.TryGetValue(uri, out var current) && ReferenceEquals(current, work))
                        _pending.Remove(uri);
                    cancel = true;
                }
            }
            if (cancel)
                work.Cancel();
        }
    }

    private async Task GenerateAsync(string uri, Work work)
    {
        var entered = false;
        try
        {
            await _parallel.WaitAsync(work.Token).ConfigureAwait(false);
            entered = true;
            work.Token.ThrowIfCancellationRequested();
            var result = await LoadAndGenerateAsync(uri, work.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!work.Token.IsCancellationRequested && !_disposed && work.Waiters > 0)
                {
                    if (result.Thumbnail is { } thumbnail)
                        Cache(uri, thumbnail);
                    work.Completion.TrySetResult(result);
                }
                else
                    work.Completion.TrySetCanceled(work.Token);
                RemovePending(uri, work);
            }
        }
        catch (OperationCanceledException) when (work.Token.IsCancellationRequested)
        {
            Finish(new ArtifactThumbnailResult(ArtifactThumbnailStatus.Failed), canceled: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Finish(new ArtifactThumbnailResult(ArtifactThumbnailStatus.Failed), canceled: false);
        }
        finally
        {
            if (entered)
                _parallel.Release();
            work.Dispose();
        }
        return;

        void Finish(ArtifactThumbnailResult result, bool canceled)
        {
            lock (_gate)
            {
                if (canceled)
                    work.Completion.TrySetCanceled(work.Token);
                else
                    work.Completion.TrySetResult(result);
                RemovePending(uri, work);
            }
        }
    }

    private async ValueTask<ArtifactThumbnailResult> LoadAndGenerateAsync(string uri, CancellationToken token)
    {
        if (loader is null || generator is null)
            return new ArtifactThumbnailResult(ArtifactThumbnailStatus.Unsupported);
        var bytes = await loader(uri, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (bytes is null)
            return new ArtifactThumbnailResult(ArtifactThumbnailStatus.NotFound);
        if (bytes.Value.Length > MaxSourceBytes)
            return new ArtifactThumbnailResult(ArtifactThumbnailStatus.TooLarge);
        var thumbnail = await generator(bytes.Value, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (thumbnail is null || thumbnail.Bytes.Length is 0 or > MaxThumbnailBytes
            || thumbnail.MimeType is not ("image/png" or "image/jpeg"))
            return new ArtifactThumbnailResult(ArtifactThumbnailStatus.Unsupported);
        return new ArtifactThumbnailResult(ArtifactThumbnailStatus.Success, thumbnail);
    }

    private void Cache(string uri, ArtifactThumbnail thumbnail)
    {
        if (thumbnail.Bytes.Length > maxCacheBytes || maxCacheEntries <= 0)
            return;
        while (_cache.Count >= maxCacheEntries || _cacheBytes + thumbnail.Bytes.Length > maxCacheBytes)
        {
            var oldest = _recency.First!;
            _cacheBytes -= _cache[oldest.Value].Thumbnail.Bytes.Length;
            _cache.Remove(oldest.Value);
            _recency.RemoveFirst();
        }
        // Cache owned encoded bytes, not a slice retaining a larger source buffer.
        var owned = thumbnail with { Bytes = thumbnail.Bytes.ToArray() };
        _cache.Add(uri, new CacheEntry(owned, _recency.AddLast(uri)));
        _cacheBytes += owned.Bytes.Length;
    }

    private void RemovePending(string uri, Work work)
    {
        if (_pending.TryGetValue(uri, out var current) && ReferenceEquals(current, work))
            _pending.Remove(uri);
    }

    private sealed record CacheEntry(ArtifactThumbnail Thumbnail, LinkedListNode<string> Node);

    private sealed class Work : IDisposable
    {
        private readonly object _lifetime = new();
        private readonly CancellationTokenSource _cancellation = new();
        private bool _disposed;

        public Work() => Token = _cancellation.Token;

        public CancellationToken Token { get; }
        public int Waiters { get; set; }
        public TaskCompletionSource<ArtifactThumbnailResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Cancel()
        {
            lock (_lifetime)
            {
                if (!_disposed)
                    _cancellation.Cancel();
            }
        }

        public void Dispose()
        {
            lock (_lifetime)
            {
                _disposed = true;
                _cancellation.Dispose();
            }
        }
    }
}
