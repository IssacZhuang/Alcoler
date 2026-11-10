using System.Collections;
using System.Reflection;
using Alcoler.Controls;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Avalonia.Threading;
using Xunit;

namespace Alcoler.Tests;

public sealed class ConversationImageCacheTests
{
    [AvaloniaFact]
    public async Task Cache_ReusesLoadedThumbnailAndKeepsReleasedEntryAlive()
    {
        using var scope = UseSkia();
        var loads = 0;
        var cache = new ConversationImageCache((uri, mime, token) =>
        {
            loads++;
            return Task.FromResult<Bitmap?>(CreateBitmap(4, 4));
        });
        var first = await cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        var bitmap = Assert.IsType<WriteableBitmap>(first!.Bitmap);
        first.Release();
        Assert.False(IsDisposed(bitmap));

        var second = await cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        Assert.Same(bitmap, second!.Bitmap);
        second.Release();
        Assert.Equal(1, loads);
        Assert.False(IsDisposed(bitmap));
    }

    [AvaloniaFact]
    public async Task Cache_EvictsLeastRecentlyUsedEntryWhenCountExceedsCap()
    {
        using var scope = UseSkia();
        var loads = new Dictionary<string, int>();
        var cache = new ConversationImageCache((uri, mime, token) =>
        {
            loads[uri] = loads.TryGetValue(uri, out var count) ? count + 1 : 1;
            return Task.FromResult<Bitmap?>(CreateBitmap(4, 4));
        }, maxEntries: 2);
        var leaseA = await cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        var bitmapA = Assert.IsType<WriteableBitmap>(leaseA!.Bitmap);
        var leaseB = await cache.AcquireAsync("u://b", "image/png", CancellationToken.None);
        var bitmapB = Assert.IsType<WriteableBitmap>(leaseB!.Bitmap);
        leaseA.Release();
        leaseB.Release();

        var touchA = await cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        Assert.Same(bitmapA, touchA!.Bitmap);
        touchA.Release();

        var leaseC = await cache.AcquireAsync("u://c", "image/png", CancellationToken.None);
        var bitmapC = Assert.IsType<WriteableBitmap>(leaseC!.Bitmap);
        leaseC.Release();
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsDisposed(bitmapB));
        Assert.False(IsDisposed(bitmapA));
        Assert.False(IsDisposed(bitmapC));
        Assert.Equal(1, loads["u://a"]);
        Assert.Equal(1, loads["u://b"]);
        Assert.Equal(1, loads["u://c"]);

        var leaseB2 = await cache.AcquireAsync("u://b", "image/png", CancellationToken.None);
        Assert.NotSame(bitmapB, leaseB2!.Bitmap);
        Assert.Equal(2, loads["u://b"]);
        leaseB2.Release();
    }

    [AvaloniaFact]
    public async Task Cache_EvictsLeastRecentlyUsedEntryWhenByteBudgetExceeded()
    {
        using var scope = UseSkia();
        var loads = new Dictionary<string, int>();
        var cache = new ConversationImageCache((uri, mime, token) =>
        {
            loads[uri] = loads.TryGetValue(uri, out var count) ? count + 1 : 1;
            return Task.FromResult<Bitmap?>(CreateBitmap(10, 10));
        }, maxBytes: 10 * 10 * 4 * 3 - 1); // holds two 10x10 thumbnails, never three
        var leaseA = await cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        var bitmapA = Assert.IsType<WriteableBitmap>(leaseA!.Bitmap);
        var leaseB = await cache.AcquireAsync("u://b", "image/png", CancellationToken.None);
        var bitmapB = Assert.IsType<WriteableBitmap>(leaseB!.Bitmap);
        leaseA.Release();
        leaseB.Release();

        var leaseC = await cache.AcquireAsync("u://c", "image/png", CancellationToken.None);
        var bitmapC = Assert.IsType<WriteableBitmap>(leaseC!.Bitmap);
        leaseC.Release();
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsDisposed(bitmapA));
        Assert.False(IsDisposed(bitmapB));
        Assert.False(IsDisposed(bitmapC));
        Assert.Equal(1, loads["u://a"]);
        Assert.Equal(1, loads["u://b"]);
        Assert.Equal(1, loads["u://c"]);
    }

    [AvaloniaFact]
    public async Task Cache_EvictedEntryWithActiveLeaseSurvivesUntilFinalRelease()
    {
        using var scope = UseSkia();
        var cache = new ConversationImageCache((uri, mime, token) =>
            Task.FromResult<Bitmap?>(CreateBitmap(4, 4)), maxEntries: 1);
        var leaseA = await cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        var bitmapA = Assert.IsType<WriteableBitmap>(leaseA!.Bitmap);
        var leaseB = await cache.AcquireAsync("u://b", "image/png", CancellationToken.None);
        var bitmapB = Assert.IsType<WriteableBitmap>(leaseB!.Bitmap);

        Assert.False(IsDisposed(bitmapA));
        Assert.False(IsDisposed(bitmapB));
        leaseA.Release();
        Assert.True(IsDisposed(bitmapA));

        Assert.False(IsDisposed(bitmapB));
        leaseB.Release();
        Assert.False(IsDisposed(bitmapB));
    }

    [AvaloniaFact]
    public async Task Cache_CompletedWaitersSurviveEvictionBeforeAcquiringLeases()
    {
        using var scope = UseSkia();
        var firstLoad = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoad = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reload = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bitmapA = CreateBitmap(4, 4);
        var bitmapB = CreateBitmap(4, 4);
        var replacementA = CreateBitmap(4, 4);
        var loadsA = 0;
        var cache = new ConversationImageCache((uri, mime, token) =>
        {
            if (uri == "u://a")
                return Interlocked.Increment(ref loadsA) == 1 ? firstLoad.Task : reload.Task;
            return secondLoad.Task;
        }, maxEntries: 1);
        var first = cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        var second = cache.AcquireAsync("u://b", "image/png", CancellationToken.None);
        Task<ConversationImageCache.Lease?> reloaded;
        lock (CacheGate(cache))
        {
            CompleteLoad(cache, "u://a", firstLoad, bitmapA);
            CompleteLoad(cache, "u://b", secondLoad, bitmapB);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(IsDisposed(bitmapA));

            reloaded = cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
            Assert.False(reloaded.IsCompleted);
            CompleteLoad(cache, "u://a", reload, replacementA);
            Assert.False(IsDisposed(bitmapB));
        }

        var firstLease = Assert.IsType<ConversationImageCache.Lease>(await first);
        var secondLease = Assert.IsType<ConversationImageCache.Lease>(await second);
        var replacementLease = Assert.IsType<ConversationImageCache.Lease>(await reloaded);
        Assert.Same(bitmapA, firstLease.Bitmap);
        Assert.Same(bitmapB, secondLease.Bitmap);
        Assert.Same(replacementA, replacementLease.Bitmap);
        Assert.Equal(2, loadsA);
        firstLease.Release();
        secondLease.Release();
        replacementLease.Release();
        Assert.True(IsDisposed(bitmapA));
        Assert.True(IsDisposed(bitmapB));
        Assert.False(IsDisposed(replacementA));
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Cache_EvictedCompletedLoadReleasesEachCancelledWaiterAndLease(int successfulWaiters)
    {
        using var scope = UseSkia();
        using var cancellation = new CancellationTokenSource();
        var firstLoad = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoad = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bitmapA = CreateBitmap(4, 4);
        var bitmapB = CreateBitmap(4, 4);
        var loads = 0;
        var cache = new ConversationImageCache((uri, mime, token) =>
        {
            Interlocked.Increment(ref loads);
            return uri == "u://a" ? firstLoad.Task : secondLoad.Task;
        }, maxEntries: 1);
        var successful = Enumerable.Range(0, successfulWaiters)
            .Select(_ => cache.AcquireAsync("u://a", "image/png", CancellationToken.None)).ToArray();
        var firstCancelled = cache.AcquireAsync("u://a", "image/png", cancellation.Token);
        var secondCancelled = cache.AcquireAsync("u://a", "image/png", cancellation.Token);
        var next = cache.AcquireAsync("u://b", "image/png", CancellationToken.None);
        ConversationImageCache.Entry entry;
        lock (CacheGate(cache))
        {
            CancelWithoutInliningWaiters(cancellation);
            Assert.False(firstCancelled.IsCompleted);
            Assert.False(secondCancelled.IsCompleted);
            entry = CompleteLoad(cache, "u://a", firstLoad, bitmapA);
            Assert.Equal(successfulWaiters + 2, entry.RefCount);
            CompleteLoad(cache, "u://b", secondLoad, bitmapB);
            Assert.All(successful, waiter => Assert.False(waiter.IsCompleted));
            Assert.False(entry.Cached);
            Assert.False(IsDisposed(bitmapA));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondCancelled);
        var leases = await Task.WhenAll(successful);
        var nextLease = Assert.IsType<ConversationImageCache.Lease>(await next);
        Assert.Equal(2, loads);
        Assert.Equal(successfulWaiters, entry.RefCount);
        for (var i = 0; i < leases.Length; i++)
        {
            var lease = Assert.IsType<ConversationImageCache.Lease>(leases[i]);
            Assert.Same(bitmapA, lease.Bitmap);
            lease.Release();
            lease.Release();
            Assert.Equal(leases.Length - i - 1, entry.RefCount);
            if (entry.RefCount > 0)
                Assert.False(IsDisposed(bitmapA));
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, entry.RefCount);
        Assert.True(IsDisposed(bitmapA));
        nextLease.Release();
        Assert.False(IsDisposed(bitmapB));
    }

    [AvaloniaFact]
    public async Task Cache_CoalescesConcurrentLoadsOfSameArtifact()
    {
        using var scope = UseSkia();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var bitmap = CreateBitmap(8, 8);
        var cache = new ConversationImageCache(async (uri, mime, token) =>
        {
            loads++;
            await gate.Task;
            return bitmap;
        });
        var first = cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        var second = cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        gate.SetResult(true);
        var leases = await Task.WhenAll(first, second);
        Assert.Equal(1, loads);
        Assert.Same(bitmap, leases[0]!.Bitmap);
        Assert.Same(bitmap, leases[1]!.Bitmap);
        leases[0]!.Release();
        Assert.False(IsDisposed(bitmap));
        leases[1]!.Release();
        Assert.False(IsDisposed(bitmap));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cache_RetriesLoadAfterFailure(bool throwFailure)
    {
        using var scope = UseSkia();
        var loads = 0;
        var failures = 1;
        var cache = new ConversationImageCache((uri, mime, token) =>
        {
            loads++;
            if (failures > 0)
            {
                failures--;
                if (throwFailure)
                    throw new InvalidDataException("decode failed");
                return Task.FromResult<Bitmap?>(null);
            }
            return Task.FromResult<Bitmap?>(CreateBitmap(6, 6));
        });
        if (throwFailure)
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.AcquireAsync("u://a", "image/png", CancellationToken.None));
        else
            Assert.Null(await cache.AcquireAsync("u://a", "image/png", CancellationToken.None));

        var lease = await cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        var bitmap = Assert.IsType<WriteableBitmap>(lease!.Bitmap);
        lease.Release();
        Assert.Equal(2, loads);
        Assert.False(IsDisposed(bitmap));
    }

    [AvaloniaFact]
    public async Task Cache_DisposesOnDispatcherWhenReleasedOffUiThread()
    {
        using var scope = UseSkia();
        var cache = new ConversationImageCache((uri, mime, token) =>
            Task.FromResult<Bitmap?>(CreateBitmap(4, 4)), maxEntries: 1);
        var leaseA = await cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        var bitmapA = Assert.IsType<WriteableBitmap>(leaseA!.Bitmap);
        var leaseB = await cache.AcquireAsync("u://b", "image/png", CancellationToken.None);
        leaseB!.Release();

        await Task.Run(leaseA.Release);
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsDisposed(bitmapA));
    }

    [AvaloniaFact]
    public async Task Cache_AbandonsUnwantedLoadAndStartsFreshOnNextAcquire()
    {
        using var scope = UseSkia();
        var loads = 0;
        var cache = new ConversationImageCache(async (uri, mime, token) =>
        {
            loads++;
            if (loads == 1)
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }
            return CreateBitmap(4, 4);
        });
        using var cancellation = new CancellationTokenSource();
        var cancelled = cache.AcquireAsync("u://a", "image/png", cancellation.Token);
        await WaitForAsync(() => loads == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var lease = await cache.AcquireAsync("u://a", "image/png", CancellationToken.None);
        var bitmap = Assert.IsType<WriteableBitmap>(lease!.Bitmap);
        lease.Release();
        Assert.Equal(2, loads);
        Assert.False(IsDisposed(bitmap));
    }

    private static void CancelWithoutInliningWaiters(CancellationTokenSource cancellation)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NonInliningSynchronizationContext());
        try
        {
            cancellation.Cancel();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    // A non-default context keeps cancellation from inlining waiters through the reentrant gate.
    private sealed class NonInliningSynchronizationContext : SynchronizationContext
    {
    }

    private static object CacheGate(ConversationImageCache cache) =>
        typeof(ConversationImageCache).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    private static ConversationImageCache.Entry CompleteLoad(ConversationImageCache cache, string uri,
        TaskCompletionSource<Bitmap?> completion, Bitmap bitmap)
    {
        Assert.True(Monitor.IsEntered(CacheGate(cache)));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var flights = (IDictionary)typeof(ConversationImageCache).GetField("_inFlight", flags)!.GetValue(cache)!;
        var flight = flights[uri]!;
        var load = (Task<Bitmap?>)flight.GetType().GetField("Load")!.GetValue(flight)!;
        completion.SetResult(bitmap);
        Assert.True(load.Wait(TimeSpan.FromSeconds(5)));
        typeof(ConversationImageCache).GetMethod("InsertLoaded", flags)!.Invoke(cache, [flight]);
        return Assert.IsType<ConversationImageCache.Entry>(flight.GetType().GetField("Entry")!.GetValue(flight));
    }

    private static WriteableBitmap CreateBitmap(int width, int height) =>
        new(new PixelSize(width, height), new Vector(96, 96));

    private static bool IsDisposed(WriteableBitmap bitmap)
    {
        try
        {
            using var framebuffer = bitmap.Lock();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static IDisposable UseSkia()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Avalonia.Media.GlyphRun).TypeHandle);
        var scope = AvaloniaLocator.EnterScope();
        SkiaPlatform.Initialize();
        return scope;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }
}
