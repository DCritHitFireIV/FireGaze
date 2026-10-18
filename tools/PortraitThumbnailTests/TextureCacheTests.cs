using System.Collections.Concurrent;
#if HAS_TEXTURE_CACHE
using FireGaze.PortraitPreview;
#endif

internal static class TextureCacheTests
{
    public static async Task<int> Run()
    {
#if !HAS_TEXTURE_CACHE
        throw new InvalidOperationException("FAIL thumbnails have no bounded asynchronous disposable texture cache");
#else
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS " + name); }
        var loads = new ConcurrentDictionary<string, TaskCompletionSource<Texture>>();
        var calls = 0;
        using (var cache = new PortraitThumbnailTextureCache<Texture>(key =>
        {
            Interlocked.Increment(ref calls);
            return loads.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task!;
        }, capacity: 3))
        {
            Check(cache.Get("a") is null && cache.Get("b") is null && cache.Get("c") is null, "visible cache misses return immediately without awaiting decode");
            await Until(() => loads.Count == 2);
            for (var index = 0; index < 1000; index++) { cache.Get("a"); cache.Get("b"); cache.Get("c"); }
            Check(calls == 2 && cache.InFlight == 2 && cache.Count == 0 && cache.TotalCount == 2, "repeated frames neither duplicate decodes nor queue unlimited work");
            var a = new Texture(); var b = new Texture();
            loads["a"].SetResult(a); loads["b"].SetResult(b);
            await Until(() => cache.Count == 2);
            Check(ReferenceEquals(cache.Get("a"), a), "completed decode returns the owned texture directly");
            cache.Get("c"); cache.Get("d");
            await Until(() => loads.Count == 4);
            Check(cache.Count == 1 && cache.InFlight == 2 && cache.TotalCount == 3 && b.Disposals == 1 && a.Disposals == 0, "resident and inflight share capacity and evict the least recently used resident once");
            var c = new Texture(); var d = new Texture();
            loads["c"].SetResult(c); loads["d"].SetResult(d);
            await Until(() => cache.Count == 3);
            cache.Dispose(); cache.Dispose();
            Check(a.Disposals == 1 && b.Disposals == 1 && c.Disposals == 1 && d.Disposals == 1 && cache.Get("new") is null, "dispose releases every resident exactly once and prohibits future loads");
        }
        var late = new TaskCompletionSource<Texture?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new PortraitThumbnailTextureCache<Texture>(_ => late.Task);
        disposed.Get("late"); disposed.Dispose();
        Check(disposed.Count == 0 && disposed.InFlight == 1, "disposing returns without waiting for an unfinished decoder");
        var lateTexture = new Texture(); late.SetResult(lateTexture);
        await Until(() => lateTexture.Disposals == 1 && disposed.InFlight == 0);
        Check(disposed.Count == 0 && lateTexture.Disposals == 1, "an asynchronous result arriving after disposal is released once");

        var invalidatedLoad = new TaskCompletionSource<Texture?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryCalls = 0;
        var replacement = new Texture();
        using (var cache = new PortraitThumbnailTextureCache<Texture>(_ => Interlocked.Increment(ref retryCalls) == 1
            ? invalidatedLoad.Task : Task.FromResult<Texture?>(replacement)))
        {
            cache.Get("same"); await Until(() => retryCalls == 1);
            cache.Invalidate("same");
            for (var index = 0; index < 1000; index++) cache.Get("same");
            Check(retryCalls == 1 && cache.InFlight == 1, "invalidating a pending key retains its reservation until actual completion");
            var stale = new Texture(); invalidatedLoad.SetResult(stale);
            await Until(() => stale.Disposals == 1 && cache.InFlight == 0);
            Check(cache.Count == 0, "invalidated late result is disposed instead of becoming visible");
            cache.Get("same"); await Until(() => cache.Count == 1);
            Check(retryCalls == 2 && ReferenceEquals(cache.Get("same"), replacement), "newly captured content can be decoded after invalidation");
            cache.Invalidate("same");
            Check(cache.Count == 0 && replacement.Disposals == 1, "invalidating a resident releases it once");
        }
        var failedCalls = 0;
        var releasingLoad = new TaskCompletionSource<Texture?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var releaseGate = new ManualResetEventSlim(false))
        using (var cache = new PortraitThumbnailTextureCache<Texture>(_ => releasingLoad.Task, capacity: 1))
        {
            cache.Get("old"); cache.Invalidate("old");
            var releasing = new Texture { OnDispose = () => releaseGate.Wait(TimeSpan.FromSeconds(5)) };
            releasingLoad.SetResult(releasing);
            await Until(() => releasing.Disposals == 1);
            var heldSlot = cache.InFlight == 1 && cache.TotalCount == 1;
            releaseGate.Set();
            await Until(() => cache.InFlight == 0);
            Check(heldSlot, "a stale texture keeps its resource reservation until disposal actually finishes");
        }
        using (var cache = new PortraitThumbnailTextureCache<Texture>(_ =>
        {
            Interlocked.Increment(ref failedCalls);
            throw new InvalidDataException("broken image");
        }))
        {
            cache.Get("bad"); await Until(() => cache.InFlight == 0 && failedCalls == 1);
            for (var index = 0; index < 1000; index++) cache.Get("bad");
            Check(failedCalls == 1 && cache.Count == 0, "failed images do not retry on every frame");
            cache.Invalidate("bad"); cache.Get("bad");
            await Until(() => failedCalls == 2 && cache.InFlight == 0);
            Check(failedCalls == 2, "explicit invalidation permits one retry after a failed decode");
        }
        var nullCalls = 0;
        using (var cache = new PortraitThumbnailTextureCache<Texture>(_ => { Interlocked.Increment(ref nullCalls); return Task.FromResult<Texture?>(null); }))
        {
            cache.Get("missing"); await Until(() => nullCalls == 1 && cache.InFlight == 0);
            cache.Get("missing");
            Check(nullCalls == 1, "missing encoded images are negatively cached");
        }
        CheckLimit(() => new PortraitThumbnailTextureCache<Texture>(_ => Task.FromResult<Texture?>(null), 33));
        CheckLimit(() => new PortraitThumbnailTextureCache<Texture>(_ => Task.FromResult<Texture?>(null), 0));
        CheckLimit(() => new PortraitThumbnailTextureCache<Texture>(_ => Task.FromResult<Texture?>(null), 32, 3));
        Check(true, "constructor cannot expand the hard resident and decoder bounds");
        return checks;
#endif
    }

#if HAS_TEXTURE_CACHE
    private static void CheckLimit(Action action)
    {
        try { action(); } catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("unsafe texture limit accepted");
    }
    private static async Task Until(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, cancellation.Token);
    }
    private sealed class Texture : IDisposable
    {
        private int disposals;
        public Action? OnDispose;
        public int Disposals => Volatile.Read(ref disposals);
        public void Dispose() { Interlocked.Increment(ref disposals); OnDispose?.Invoke(); }
    }
#endif
}
