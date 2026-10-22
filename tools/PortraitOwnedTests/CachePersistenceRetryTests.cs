using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using FireGaze.PortraitPreview;

internal static class CachePersistenceRetryTests
{
    internal static async Task<(int Passed, int Failed)> Run()
    {
        var passed = 0; var failed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            passed++; Console.WriteLine("PASS cache-retry " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "firegaze-owned-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        async Task Case(string name, Func<string, Task> run)
        {
            try { await run(Path.Combine(root, name)); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL cache-retry " + name + ": " + error.Message); }
        }
        try
        {
            await Case("real-busy", async directory =>
            {
                Directory.CreateDirectory(directory);
                var catalog = Catalog();
                var budget = new PortraitStorageBudget(directory, 1024 * 1024);
                var cache = new OwnedEquipmentCacheFile(directory, storage: budget);
                cache.Load(catalog, out _);
                var delay = new ControlledDelay();
                using var writer = new OwnedCacheWriter(cache.SaveJSONResult, budget, delay.Wait);
                Task<bool> boundary;
                (TimeSpan Duration, TaskCompletionSource Completion) step;
                using (var peer = new FileStream(Path.Combine(directory, ".portrait-storage.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    Check(writer.Queue(catalog.ContentRevision, catalog.ExportJSON()), "last source snapshot enters the production worker");
                    boundary = writer.FlushAsync();
                    step = await delay.Next();
                    Check(step.Duration == TimeSpan.FromSeconds(1) && !boundary.IsCompleted && writer.LastError is not null,
                        "real peer OS lease returns Busy and keeps the same durability boundary pending");
                }
                step.Completion.SetResult();
                Check(await boundary.WaitAsync(TimeSpan.FromSeconds(5)) && writer.SavedRevision == catalog.ContentRevision
                    && writer.LastError is null && cache.Load(new(id => id), out _),
                    "releasing the real peer lease recovers the last source snapshot without another observation");
            });

            await Case("storage-error-exhaustion", async directory =>
            {
                var catalog = Catalog();
                var faults = new FaultFiles { Remaining = int.MaxValue };
                var budget = new PortraitStorageBudget(directory, 1024 * 1024, faults);
                var cache = new OwnedEquipmentCacheFile(directory, storage: budget);
                var waits = new List<double>();
                using var writer = new OwnedCacheWriter(cache.SaveJSONResult, budget,
                    (duration, _) => { waits.Add(duration.TotalSeconds); return Task.CompletedTask; });
                var policy = new OwnedCacheSavePolicy(writer);
                Check(policy.QueueIfNeeded(catalog), "typed transient failure fixture admits one real snapshot");
                var original = writer.FlushAsync();
                Check(!await original.WaitAsync(TimeSpan.FromSeconds(5)) && faults.Attempts == 4 && waits.SequenceEqual([1d, 2d, 4d])
                    && writer.SavedRevision == 0 && writer.FailedWrites == 1 && writer.LastError is not null,
                    "StorageError exhausts only the initial attempt plus three exact 1/2/4-second backoffs");
                var repeated = 0;
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 100_000; index++) if (policy.QueueIfNeeded(catalog)) repeated++;
                Check(repeated == 0 && GC.GetAllocatedBytesForCurrentThread() - before == 0 && faults.Attempts == 4
                    && writer.NeedsRevision(catalog.ContentRevision),
                    "100000 unchanged failed observations perform zero writes and zero caller allocation while retaining dirty status");
                faults.Remaining = 0;
                Task<bool> recovery;
                var changed = 0;
                long allocation;
                var gate = typeof(OwnedCacheWriter).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;
                lock (gate)
                {
                    recovery = writer.RetryAsync();
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    for (var index = 0; index < 100_000; index++)
                        if (!ReferenceEquals(recovery, writer.RetryAsync()) || !ReferenceEquals(recovery, writer.FlushAsync())) changed++;
                    allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                }
                Check(changed == 0 && allocation == 0, "100000 explicit Retry/Flush calls share one retained-snapshot task with zero metadata");
                Check(await recovery.WaitAsync(TimeSpan.FromSeconds(5)) && !await original && writer.SavedRevision == catalog.ContentRevision
                    && writer.LastError is null && writer.CompletedWrites == 2 && writer.FailedWrites == 1,
                    "explicit retry persists the exact retained snapshot and cannot rewrite the earlier failed result");
                Check(await writer.BeginDispose() && await writer.RetryAsync() && faults.Attempts == 5,
                    "successful close returns durability and refuses any additional retry publication");
            });

            await Case("capacity-one-reclaim", async directory =>
            {
                Directory.CreateDirectory(directory);
                var catalog = Catalog();
                var limit = Encoding.UTF8.GetByteCount(catalog.ExportJSON()) * 2 + 100;
                var filler = Path.Combine(directory, "portrait-favorites-v1.json.bak");
                File.WriteAllBytes(filler, new byte[limit - 1]);
                var budget = new PortraitStorageBudget(directory, limit);
                var cache = new OwnedEquipmentCacheFile(directory, storage: budget);
                var attempts = 0;
                using var writer = new OwnedCacheWriter(snapshot => { Interlocked.Increment(ref attempts); return cache.SaveJSONResult(snapshot); },
                    budget, (_, _) => throw new InvalidOperationException("capacity cannot use timed backoff"));
                writer.Queue(catalog.ContentRevision, catalog.ExportJSON());
                var boundary = writer.FlushAsync();
                Check(SpinWait.SpinUntil(() => writer.LastError is not null, TimeSpan.FromSeconds(5)) && !boundary.IsCompleted && budget.NeedsReclaim,
                    "capacity requests one reclamation and waits on the authority signal");
                budget.CompleteReclaim(budget.ReclaimRequestVersion);
                Check(!await boundary.WaitAsync(TimeSpan.FromSeconds(5)) && attempts == 2 && writer.LastError is not null,
                    "a reclaim acknowledgment freeing no bytes receives one retry and then a truthful final failure");
                var automaticRestarts = 0;
                for (var index = 0; index < 1000; index++)
                {
                    budget.CompleteReclaim(budget.ReclaimRequestVersion);
                    if (writer.NeedsAutomaticRevision(catalog.ContentRevision)) automaticRestarts++;
                }
                Check(automaticRestarts == 0 && attempts == 2, "1000 reclaim acknowledgments cannot restart an unchanged capacity revision");
                Check(budget.TryDeleteOwned(filler, out _) && await writer.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5)),
                    "genuine later space release plus explicit retry recovers the retained snapshot without a new source read");
            });

            await Case("capacity-real-release", async directory =>
            {
                Directory.CreateDirectory(directory);
                var catalog = Catalog();
                var limit = Encoding.UTF8.GetByteCount(catalog.ExportJSON()) * 2 + 100;
                var filler = Path.Combine(directory, "portrait-favorites-v1.json.bak");
                File.WriteAllBytes(filler, new byte[limit - 1]);
                var budget = new PortraitStorageBudget(directory, limit);
                var cache = new OwnedEquipmentCacheFile(directory, storage: budget);
                using var writer = new OwnedCacheWriter(cache.SaveJSONResult, budget,
                    (_, _) => throw new InvalidOperationException("capacity cannot poll"));
                writer.Queue(catalog.ContentRevision, catalog.ExportJSON());
                var boundary = writer.FlushAsync();
                Check(SpinWait.SpinUntil(() => writer.LastError is not null, TimeSpan.FromSeconds(5)), "real reclamation fixture reaches capacity refusal");
                Check(budget.TryDeleteOwned(filler, out _) && await boundary.WaitAsync(TimeSpan.FromSeconds(5))
                    && cache.Load(new(id => id), out _), "real space release wakes and persists the same final snapshot automatically once");
            });

            await Case("capacity-close", async directory =>
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "portrait-favorites-v1.json.bak"), new byte[99]);
                var budget = new PortraitStorageBudget(directory, 100);
                var cache = new OwnedEquipmentCacheFile(directory, storage: budget);
                using var writer = new OwnedCacheWriter(cache.SaveJSONResult, budget);
                writer.Queue(1, Catalog().ExportJSON());
                var boundary = writer.FlushAsync();
                Check(SpinWait.SpinUntil(() => writer.LastError is not null, TimeSpan.FromSeconds(5)), "capacity close fixture reaches the bounded wait");
                var drain = writer.BeginDispose();
                Check(!await drain.WaitAsync(TimeSpan.FromSeconds(1)) && !await boundary && !await writer.RetryAsync()
                    && ReferenceEquals(drain, writer.BeginDispose()) && writer.SavedRevision == 0,
                    "close cancels reclamation promptly, preserves false durability and never reopens the final failed snapshot");
            });

            await Case("exported-drain-lifetime", async directory =>
            {
                var retained = BeginCollectibleDrain(directory);
                Collect();
                Check(!retained.Drain.IsCompleted && retained.Owner.IsAlive && retained.Snapshot.IsAlive,
                    "an exported close task roots its writer and final source snapshot while the accepted retry is blocked");
                retained.Release.SetResult();
                Check(await retained.Drain.WaitAsync(TimeSpan.FromSeconds(5)) && File.ReadAllText(retained.Path).Contains("123"),
                    "the exported cache drain survives forced collection and finishes a real atomic publication");
                for (var attempt = 0; attempt < 20 && retained.Snapshot.IsAlive; attempt++)
                { Collect(); await Task.Delay(10); }
                Check(!retained.Snapshot.IsAlive, "completed cache drain releases the successful source snapshot while its task remains rooted");
                GC.KeepAlive(retained.Drain);
            });
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("firegaze-owned-retry-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside this test's exact temporary root.");
            Directory.Delete(resolved, true);
        }
        return (passed, failed);
    }

    private static OwnedEquipmentCatalog Catalog()
    {
        var catalog = new OwnedEquipmentCatalog(id => id);
        catalog.ActivateCharacter(new(1, 20));
        catalog.ApplyRead(new(1, 20), new(OwnedSourceKind.Inventory), DateTimeOffset.UtcNow, [new(123, 1, 0, 1, 4, 5)]);
        return catalog;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<bool> Drain, WeakReference Owner, WeakReference Snapshot, TaskCompletionSource Release, string Path)
        BeginCollectibleDrain(string directory)
    {
        var faults = new FaultFiles { Remaining = 1 };
        var budget = new PortraitStorageBudget(directory, 1024 * 1024, faults);
        var cache = new OwnedEquipmentCacheFile(directory, storage: budget);
        var delay = new ControlledDelay();
        var writer = new OwnedCacheWriter(cache.SaveJSONResult, budget, delay.Wait);
        var snapshot = Catalog().ExportJSON();
        writer.Queue(1, snapshot);
        var step = delay.Next().GetAwaiter().GetResult();
        return (writer.BeginDispose(), new(writer), new(snapshot), step.Completion,
            Path.Combine(directory, "portrait-owned-cache-v1.json"));
    }

    private static void Collect()
    {
        for (var attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    }

    private sealed class FaultFiles : PortraitStorageFiles
    {
        public int Remaining;
        public int Attempts;
        public override FileStream CreateNew(string path)
        {
            Interlocked.Increment(ref Attempts);
            if (Interlocked.Decrement(ref Remaining) >= 0) throw new IOException("actual temporary publication is unavailable");
            return base.CreateNew(path);
        }
    }

    private sealed class ControlledDelay
    {
        private readonly Channel<(TimeSpan Duration, TaskCompletionSource Completion)> steps =
            Channel.CreateBounded<(TimeSpan, TaskCompletionSource)>(3);
        public Task Wait(TimeSpan duration, CancellationToken _)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!steps.Writer.TryWrite((duration, completion))) throw new InvalidOperationException("unexpected retry backlog");
            return completion.Task;
        }
        public Task<(TimeSpan Duration, TaskCompletionSource Completion)> Next() =>
            steps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
