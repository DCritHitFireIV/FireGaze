using System.Text;
using FireGaze.PortraitPreview;

internal static class CachePressureTests
{
    internal static async Task<(int Passed, int Failed)> Run()
    {
        var passed = 0; var failed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            passed++; Console.WriteLine("PASS glamour-pressure " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "firegaze-glamour-pressure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task Case(string name, Func<string, Task> run)
        {
            try { await run(Path.Combine(root, name)); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL glamour-pressure " + name + ": " + error.Message); }
        }
        try
        {
            await Case("transient-exhaustion", async directory =>
            {
                var faults = new FaultFiles();
                var budget = new PortraitStorageBudget(directory, 1024 * 1024, faults);
                var waits = new List<double>();
                var catalog = Catalog();
                using var cache = new PortraitGlamourPlateCache(directory, budget,
                    (duration, _) => { waits.Add(duration.TotalSeconds); return Task.CompletedTask; });
                Check(cache.QueueIfNeeded(catalog), "final twenty-plate snapshot reaches the production retry writer");
                var first = cache.FlushAsync();
                Check(!await first.WaitAsync(TimeSpan.FromSeconds(5)) && faults.Attempts == 4 && waits.SequenceEqual([1d, 2d, 4d])
                    && cache.LastError is not null && cache.SavedRevision == 0,
                    "template StorageError has an exact bounded retry schedule and retains its undurable final revision");
                var restarts = 0;
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 100_000; index++) if (cache.QueueIfNeeded(catalog)) restarts++;
                Check(restarts == 0 && GC.GetAllocatedBytesForCurrentThread() - before == 0 && faults.Attempts == 4,
                    "100000 unchanged template observations create zero writes and zero allocation after exhaustion");
                faults.Fail = false;
                Check(await cache.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5)) && !await first
                    && cache.SavedRevision == catalog.ContentRevision && cache.LastError is null,
                    "explicit template retry persists the retained complete set without changing its earlier failed boundary");
                var loaded = new PortraitGlamourPlateCatalog();
                using var reader = new PortraitGlamourPlateCache(directory, budget);
                Check(reader.Load(loaded, out _) && loaded.ExportJSON() == catalog.ExportJSON(),
                    "recovered template data reloads exactly as the last accepted complete document");
            });

            await Case("capacity-real-reclaim", async directory =>
            {
                Directory.CreateDirectory(directory);
                var catalog = Catalog();
                var limit = Encoding.UTF8.GetByteCount(catalog.ExportJSON()) * 2 + 100;
                var filler = Path.Combine(directory, "portrait-favorites-v1.json.bak");
                File.WriteAllBytes(filler, new byte[limit - 1]);
                var budget = new PortraitStorageBudget(directory, limit);
                using var cache = new PortraitGlamourPlateCache(directory, budget,
                    (_, _) => throw new InvalidOperationException("template capacity cannot poll or sleep"));
                cache.QueueIfNeeded(catalog);
                var boundary = cache.FlushAsync();
                Check(SpinWait.SpinUntil(() => cache.LastError is not null, TimeSpan.FromSeconds(5)) && !boundary.IsCompleted,
                    "template capacity keeps its accepted document pending on one reclamation signal");
                Check(budget.TryDeleteOwned(filler, out _) && await boundary.WaitAsync(TimeSpan.FromSeconds(5))
                    && cache.SavedRevision == catalog.ContentRevision,
                    "real quota reclamation persists the same twenty templates without another source read");
            });

            await Case("capacity-close", async directory =>
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "portrait-favorites-v1.json.bak"), new byte[99]);
                using var cache = new PortraitGlamourPlateCache(directory, new PortraitStorageBudget(directory, 100));
                cache.QueueIfNeeded(Catalog());
                var boundary = cache.FlushAsync();
                Check(SpinWait.SpinUntil(() => cache.LastError is not null, TimeSpan.FromSeconds(5)), "template close reaches its capacity signal wait");
                Check(!await cache.BeginDispose().WaitAsync(TimeSpan.FromSeconds(1)) && !await boundary && !await cache.RetryAsync()
                    && cache.SavedRevision == 0 && cache.LastError is not null,
                    "template close cancels the reclamation wait and truthfully keeps the final revision undurable");
            });
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("firegaze-glamour-pressure-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside this test's exact temporary root.");
            Directory.Delete(resolved, true);
        }
        return (passed, failed);
    }

    private static PortraitGlamourPlateCatalog Catalog()
    {
        var owner = new OwnedCharacterKey(1, 20);
        var catalog = new PortraitGlamourPlateCatalog();
        catalog.ActivateCharacter(owner);
        catalog.ApplyRead(owner, DateTimeOffset.UtcNow, Enumerable.Range(0, 20).Select(index =>
        {
            var pieces = new PortraitPiece[12];
            pieces[2] = new((uint)index + 100, 4, 5);
            return new PortraitGlamourPlate(index, pieces);
        }).ToArray());
        return catalog;
    }

    private sealed class FaultFiles : PortraitStorageFiles
    {
        public bool Fail = true;
        public int Attempts;
        public override FileStream CreateNew(string path)
        {
            Interlocked.Increment(ref Attempts);
            if (Fail) throw new IOException("temporary template publication is unavailable");
            return base.CreateNew(path);
        }
    }
}
