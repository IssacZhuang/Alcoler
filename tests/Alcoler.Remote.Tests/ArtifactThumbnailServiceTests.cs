using System.Collections.Concurrent;
using System.Threading.Channels;
using Xunit;

namespace Alcoler.Remote.Tests;

public sealed class ArtifactThumbnailServiceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly byte[] SourceBytes = [1, 2, 3];
    private static readonly ArtifactThumbnail Preview = new(new byte[] { 7, 8, 9 }, "image/png");

    [Fact]
    public async Task Service_IsLazyAndCachesOnlyGeneratedBytes()
    {
        var loads = 0;
        var generations = 0;
        using var service = new ArtifactThumbnailService(
            (_, _) => { Interlocked.Increment(ref loads); return ValueTask.FromResult<ReadOnlyMemory<byte>?>(SourceBytes); },
            (_, _) => { Interlocked.Increment(ref generations); return ValueTask.FromResult<ArtifactThumbnail?>(Preview); });
        Assert.Equal(0, loads);
        Assert.Equal(0, generations);
        var first = await service.GetAsync("a", CancellationToken.None);
        var repeated = await service.GetAsync("a", CancellationToken.None);
        Assert.Equal(ArtifactThumbnailStatus.Success, first.Status);
        Assert.Equal(Preview.Bytes.ToArray(), repeated.Thumbnail!.Bytes.ToArray());
        Assert.Equal(1, loads);
        Assert.Equal(1, generations);
    }

    [Fact]
    public async Task ConcurrentSameUriRequests_ShareOneLoadAndGeneration()
    {
        var entered = Signal();
        var release = Signal();
        var loads = 0;
        var generations = 0;
        using var service = new ArtifactThumbnailService(async (_, token) =>
        {
            Interlocked.Increment(ref loads);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return SourceBytes;
        }, (_, _) => { Interlocked.Increment(ref generations); return ValueTask.FromResult<ArtifactThumbnail?>(Preview); });
        var requests = Enumerable.Range(0, 20).Select(_ => service.GetAsync("same", CancellationToken.None).AsTask()).ToArray();
        await entered.Task.WaitAsync(Deadline);
        Assert.Equal(1, loads);
        Assert.Equal(0, generations);
        release.SetResult();
        var results = await Task.WhenAll(requests).WaitAsync(Deadline);
        Assert.All(results, result => Assert.Equal(ArtifactThumbnailStatus.Success, result.Status));
        Assert.Equal(1, loads);
        Assert.Equal(1, generations);
    }

    [Fact]
    public async Task DistinctRequests_BoundActiveLoadingAndGenerationToTwo()
    {
        var entered = Channel.CreateUnbounded<bool>();
        var release = Signal();
        var loads = 0;
        var active = 0;
        var maximum = 0;
        using var service = new ArtifactThumbnailService(
            (_, _) => { Interlocked.Increment(ref loads); return ValueTask.FromResult<ReadOnlyMemory<byte>?>(SourceBytes); },
            async (_, token) =>
            {
                var count = Interlocked.Increment(ref active);
                RaiseMaximum(ref maximum, count);
                entered.Writer.TryWrite(true);
                try { await release.Task.WaitAsync(token); return Preview; }
                finally { Interlocked.Decrement(ref active); }
            });
        var requests = Enumerable.Range(0, 8).Select(index => service.GetAsync(index.ToString(), CancellationToken.None).AsTask()).ToArray();
        await entered.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        await entered.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(2, active);
        Assert.Equal(2, loads);
        release.SetResult();
        await Task.WhenAll(requests).WaitAsync(Deadline);
        Assert.Equal(8, loads);
        Assert.Equal(2, maximum);
    }

    [Fact]
    public async Task CancelingOneWaiter_DoesNotCancelSharedWork()
    {
        var entered = Signal();
        var release = Signal();
        var sharedToken = CancellationToken.None;
        var loads = 0;
        using var service = new ArtifactThumbnailService(async (_, token) =>
        {
            Interlocked.Increment(ref loads);
            sharedToken = token;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return SourceBytes;
        }, Generate);
        using var canceled = new CancellationTokenSource();
        var first = service.GetAsync("same", canceled.Token).AsTask();
        await entered.Task.WaitAsync(Deadline);
        var second = service.GetAsync("same", CancellationToken.None).AsTask();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(sharedToken.IsCancellationRequested);
        release.SetResult();
        Assert.Equal(ArtifactThumbnailStatus.Success, (await second.WaitAsync(Deadline)).Status);
        await service.GetAsync("same", CancellationToken.None);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task CancelingUnusedQueuedRequest_RemovesItWithoutReadingAndFreesQueueSlot()
    {
        var entered = Signal();
        var release = Signal();
        var loaded = new ConcurrentBag<string>();
        using var service = new ArtifactThumbnailService(async (uri, token) =>
        {
            loaded.Add(uri);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return SourceBytes;
        }, Generate, maxConcurrent: 1, maxPending: 2);
        var active = service.GetAsync("active", CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var abandoned = service.GetAsync("abandoned", cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        var next = service.GetAsync("next", CancellationToken.None).AsTask();
        release.SetResult();
        Assert.Equal(ArtifactThumbnailStatus.Success, (await next.WaitAsync(Deadline)).Status);
        await active.WaitAsync(Deadline);
        Assert.Equal(new[] { "active", "next" }, loaded.Order().ToArray());
    }

    [Fact]
    public async Task CancelingLastActiveWaiter_CancelsLoaderAndAllowsFreshRetry()
    {
        var entered = Signal();
        var canceled = Signal();
        var loads = 0;
        using var service = new ArtifactThumbnailService(async (_, token) =>
        {
            if (Interlocked.Increment(ref loads) == 1)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { canceled.TrySetResult(); }
            }
            return SourceBytes;
        }, Generate);
        using var cancellation = new CancellationTokenSource();
        var abandoned = service.GetAsync("same", cancellation.Token).AsTask();
        await entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        await canceled.Task.WaitAsync(Deadline);
        Assert.Equal(ArtifactThumbnailStatus.Success, (await service.GetAsync("same", CancellationToken.None)).Status);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task PendingWorkIsBounded_AndBusyResponsesAreNotCached()
    {
        var entered = Signal();
        var release = Signal();
        var loads = 0;
        using var service = new ArtifactThumbnailService(async (_, token) =>
        {
            Interlocked.Increment(ref loads);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return SourceBytes;
        }, Generate, maxConcurrent: 1, maxPending: 2);
        var active = service.GetAsync("a", CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(Deadline);
        var queued = service.GetAsync("b", CancellationToken.None).AsTask();
        Assert.Equal(ArtifactThumbnailStatus.Busy, (await service.GetAsync("c", CancellationToken.None)).Status);
        release.SetResult();
        await Task.WhenAll(active, queued).WaitAsync(Deadline);
        Assert.Equal(ArtifactThumbnailStatus.Success, (await service.GetAsync("c", CancellationToken.None)).Status);
        Assert.Equal(3, loads);
    }

    [Theory]
    [InlineData("loader", "Failed")]
    [InlineData("generator", "Failed")]
    [InlineData("missing", "NotFound")]
    [InlineData("unsupported", "Unsupported")]
    [InlineData("unsafe_mime", "Unsupported")]
    [InlineData("empty", "Unsupported")]
    public async Task FailuresAreNotCached(string failure, string expected)
    {
        var loads = 0;
        using var service = new ArtifactThumbnailService((_, _) =>
        {
            Interlocked.Increment(ref loads);
            if (failure == "loader") throw new IOException("source unavailable");
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(failure == "missing" ? (ReadOnlyMemory<byte>?)null : SourceBytes);
        }, (_, _) =>
        {
            if (failure == "generator") throw new InvalidDataException("invalid image");
            return ValueTask.FromResult<ArtifactThumbnail?>(failure switch
            {
                "unsupported" => null,
                "unsafe_mime" => new ArtifactThumbnail(Preview.Bytes, "image/svg+xml"),
                "empty" => new ArtifactThumbnail(ReadOnlyMemory<byte>.Empty, "image/png"),
                _ => Preview,
            });
        });
        Assert.Equal(expected, (await service.GetAsync("a", CancellationToken.None)).Status.ToString());
        Assert.Equal(expected, (await service.GetAsync("a", CancellationToken.None)).Status.ToString());
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task OversizedSource_IsNotPassedToGeneratorOrCached()
    {
        var generations = 0;
        var source = new byte[ArtifactThumbnailService.MaxSourceBytes + 1];
        using var service = new ArtifactThumbnailService(
            (_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>?>(source),
            (_, _) => { generations++; return ValueTask.FromResult<ArtifactThumbnail?>(Preview); });
        Assert.Equal(ArtifactThumbnailStatus.TooLarge, (await service.GetAsync("large", CancellationToken.None)).Status);
        Assert.Equal(0, generations);
    }

    [Fact]
    public async Task CacheEvictsLeastRecentlyUsedEntries()
    {
        var loads = new ConcurrentDictionary<string, int>();
        using var service = new ArtifactThumbnailService((uri, _) =>
        {
            loads.AddOrUpdate(uri, 1, (_, count) => count + 1);
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(SourceBytes);
        }, Generate, maxCacheEntries: 2);
        foreach (var uri in new[] { "a", "b", "a", "c", "a", "b" })
            await service.GetAsync(uri, CancellationToken.None);
        Assert.Equal(1, loads["a"]);
        Assert.Equal(2, loads["b"]);
        Assert.Equal(1, loads["c"]);
    }

    [Fact]
    public async Task CacheIsBoundedByEncodedBytesEvenBelowEntryLimit()
    {
        var loads = 0;
        using var service = new ArtifactThumbnailService(
            (_, _) => { loads++; return ValueTask.FromResult<ReadOnlyMemory<byte>?>(SourceBytes); },
            Generate, maxCacheBytes: Preview.Bytes.Length);
        foreach (var uri in new[] { "a", "b", "a" })
            await service.GetAsync(uri, CancellationToken.None);
        Assert.Equal(3, loads);
    }

    [Fact]
    public async Task OversizedCacheEntry_IsReturnedButNeverCached()
    {
        var loads = 0;
        using var service = new ArtifactThumbnailService(
            (_, _) => { loads++; return ValueTask.FromResult<ReadOnlyMemory<byte>?>(SourceBytes); },
            Generate, maxCacheBytes: Preview.Bytes.Length - 1);
        Assert.Equal(ArtifactThumbnailStatus.Success, (await service.GetAsync("a", CancellationToken.None)).Status);
        Assert.Equal(ArtifactThumbnailStatus.Success, (await service.GetAsync("a", CancellationToken.None)).Status);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task CacheCopiesOnlyEncodedSliceIntoOwnedMemory()
    {
        byte[] storage = [0, 7, 8, 9, 0];
        using var service = new ArtifactThumbnailService(Load,
            (_, _) => ValueTask.FromResult<ArtifactThumbnail?>(new ArtifactThumbnail(storage.AsMemory(1, 3), "image/png")));
        await service.GetAsync("a", CancellationToken.None);
        Array.Fill(storage, (byte)0);
        Assert.Equal(new byte[] { 7, 8, 9 }, (await service.GetAsync("a", CancellationToken.None)).Thumbnail!.Bytes.ToArray());
    }

    [Fact]
    public async Task Dispose_CancelsActiveAndQueuedWorkAndRejectsNewRequests()
    {
        var entered = Signal();
        var loads = 0;
        using var service = new ArtifactThumbnailService(async (_, token) =>
        {
            Interlocked.Increment(ref loads);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return SourceBytes;
        }, Generate, maxConcurrent: 1);
        var active = service.GetAsync("a", CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(Deadline);
        var queued = service.GetAsync("b", CancellationToken.None).AsTask();
        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(Deadline));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
        Assert.Equal(1, loads);
        Assert.Throws<ObjectDisposedException>(() => service.GetAsync("c", CancellationToken.None));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ValueTask<ReadOnlyMemory<byte>?> Load(string uri, CancellationToken token) =>
        ValueTask.FromResult<ReadOnlyMemory<byte>?>(SourceBytes);

    private static ValueTask<ArtifactThumbnail?> Generate(ReadOnlyMemory<byte> bytes, CancellationToken token) =>
        ValueTask.FromResult<ArtifactThumbnail?>(Preview);

    private static void RaiseMaximum(ref int maximum, int value)
    {
        int previous;
        do { previous = Volatile.Read(ref maximum); }
        while (value > previous && Interlocked.CompareExchange(ref maximum, value, previous) != previous);
    }
}
