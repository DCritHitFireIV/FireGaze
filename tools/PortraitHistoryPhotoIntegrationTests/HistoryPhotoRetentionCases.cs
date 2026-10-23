using System.Reflection;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

// Exercises the actual service/cache/directory quota with real files. Only the GPU
// boundary is absent; no policy stub supplies the preservation result.
internal static class HistoryPhotoRetentionCases
{
    internal static async Task<int> RunAsync(string root, PortraitEditorSnapshot initial)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL " + name);
            checks++; Console.WriteLine("PASS " + name);
        }
        var textures = DispatchProxy.Create<ITextureProvider, NoGpuProxy>();
        var readback = DispatchProxy.Create<ITextureReadbackProvider, NoGpuProxy>();
        var png = new byte[64];
        new byte[] {137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,160,0,0,1,0,8,6}.CopyTo(png, 0);
        var one = Record(1, 10, initial);
        var twoSnapshot = initial with { Composition = initial.Composition with { CameraYaw = .2f } };
        var two = Record(2, 20, twoSnapshot);
        var oneKey = PortraitThumbnailCache.KeyForSnapshot(10, initial);
        var twoKey = PortraitThumbnailCache.KeyForSnapshot(20, twoSnapshot);
        var orphan = PortraitThumbnailCache.KeyForSnapshot("not referenced by any history");
        var newImage = PortraitThumbnailCache.KeyForSnapshot("new image after capacity is exhausted");
        (string, PortraitStorageBudget) Fixture(string name, params string[] keys)
        {
            var dir = Path.Combine(root, name); Directory.CreateDirectory(dir);
            var storage = new PortraitStorageBudget(dir, 100_000);
            var path = Path.Combine(dir, "photos.bin");
            var cache = new PortraitThumbnailCache(path, 1000, storage);
            foreach (var key in keys) Check(cache.TryPut(key, png), name + " seeds a real completed cache entry");
            return (path, storage);
        }
        PortraitHistoryPhotos Service(string path, PortraitStorageBudget storage, int historyBytes = 0)
            => new(path, textures, readback, new(), historyBytes, favoriteSnapshots: [], storage: storage,
                retryDelay: (_, _) => Task.CompletedTask);

        var (path, storage) = Fixture("history-retain-shrink", oneKey, orphan);
        using (var photos = Service(path, storage))
        {
            photos.SetRecords([one], PortraitThumbnailCache.MaximumBytes - 148);
            await Maintain(photos);
            var fresh = new PortraitThumbnailCache(path, 1000, storage);
            Check(fresh.Contains(oneKey) && fresh.Contains(orphan),
                "history-byte growth preserves every completed product PNG, including the referenced oldest image");
            var before = File.ReadAllBytes(path);
            Check(!Cache(photos).TryPut(newImage, png) && File.ReadAllBytes(path).SequenceEqual(before),
                "small-budget saturation refuses a new PNG without replacing an existing referenced history photo");
            Check(photos.ReclaimStorage() && new PortraitThumbnailCache(path, 1000, storage).Contains(oneKey),
                "shared-directory reclamation cannot remove the only still-referenced history photo");
        }

        (path, storage) = Fixture("history-retain-capacity", oneKey);
        using (var photos = Service(path, storage))
        {
            photos.SetRecords([one], PortraitThumbnailCache.MaximumBytes);
            var before = File.ReadAllBytes(path);
            await Maintain(photos);
            Check(File.ReadAllBytes(path).SequenceEqual(before) && !Cache(photos).TryPut(orphan, png),
                "an impossible budget shrink reports capacity and leaves the prior history PNG byte-for-byte intact");
        }

        (path, storage) = Fixture("history-retain-startup", oneKey);
        var startup = File.ReadAllBytes(path);
        using (var photos = Service(path, storage, PortraitThumbnailCache.MaximumBytes))
        {
            Check(File.ReadAllBytes(path).SequenceEqual(startup),
                "known empty favorites cannot shrink an old cache before durable history references are known");
            Check(photos.ReclaimStorage() && File.ReadAllBytes(path).SequenceEqual(startup),
                "unknown startup history conservatively protects all completed images");
        }

        (path, storage) = Fixture("history-retain-cross-character", oneKey, twoKey);
        using (var photos = Service(path, storage))
        {
            photos.SetRecords([one], 0);
            photos.SetRecords([two], PortraitThumbnailCache.MaximumBytes - 280);
            await Maintain(photos);
            Check(!Cache(photos).TryPut(orphan, png) && Cache(photos).Contains(oneKey) && Cache(photos).Contains(twoKey),
                "switching the current history subset never unpins previously seen photos from another character");
            photos.SetRecords([], PortraitThumbnailCache.MaximumBytes - 148);
            await Maintain(photos);
            Check(Cache(photos).Contains(oneKey) && Cache(photos).Contains(twoKey),
                "in-memory history retirement does not release a PNG that may still be in the durable writer generation");
        }

        var playing = initial with { Composition = initial.Composition with { Paused = false, AnimationProgress = 2.5f } };
        var canonical = PortraitThumbnailCache.KeyForSnapshot(10, playing);
        var legacy = PortraitThumbnailCache.KeyForSnapshot("10|" + playing.ToJSON());
        Check(canonical != legacy, "playing-frame fixture exercises distinct canonical and legacy photo keys");
        (path, storage) = Fixture("history-retain-legacy", canonical, legacy);
        using (var photos = Service(path, storage))
        {
            photos.SetRecords([Record(3, 10, playing)], PortraitThumbnailCache.MaximumBytes - 280);
            await Maintain(photos);
            Check(photos.ReclaimStorage() && Cache(photos).Contains(canonical) && Cache(photos).Contains(legacy),
                "all still-referenced canonical and legacy keys survive shared reclamation together");
        }

        (path, storage) = Fixture("history-retain-damaged-metadata", oneKey, orphan);
        var historyPath = Path.Combine(Path.GetDirectoryName(path)!, "damaged-history.json");
        File.WriteAllText(historyPath, "unreadable history metadata");
        using (var history = new PortraitHistoryStore(historyPath, storage: storage))
        using (var photos = Service(path, storage))
        {
            Check(history.IsReadOnly, "damaged durable history is observed as read-only rather than a known empty library");
            SetKnownRecords(photos, history.GetAllRecords(), PortraitThumbnailCache.MaximumBytes - 148, !history.IsReadOnly);
            await Maintain(photos);
            Check(Cache(photos).Contains(oneKey) && Cache(photos).Contains(orphan),
                "read-only or unknown durable history cannot turn an empty projection into permission to delete images");
        }

        (path, storage) = Fixture("history-retain-invalid-snapshot", oneKey, orphan);
        using (var photos = Service(path, storage))
        {
            photos.SetRecords([one with { SnapshotJSON = "broken snapshot" }], PortraitThumbnailCache.MaximumBytes - 148);
            await Maintain(photos);
            Check(Cache(photos).Contains(oneKey) && Cache(photos).Contains(orphan),
                "an unparseable history snapshot keeps reference discovery conservative instead of freeing unknown images");
        }

        (path, storage) = Fixture("history-retain-restart", oneKey, orphan);
        historyPath = Path.Combine(Path.GetDirectoryName(path)!, "durable-history.json");
        using (var history = new PortraitHistoryStore(historyPath, storage: storage))
        {
            history.Record(one.Key, initial.ToJSON(), "durable history fixture");
            Check(await history.FlushAsync(), "restart fixture flushes the actual history writer");
        }
        using (var history = new PortraitHistoryStore(historyPath, storage: storage))
        using (var photos = Service(path, storage))
        {
            photos.SetRecords(history.GetAllRecords(), PortraitThumbnailCache.MaximumBytes - 148);
            await Maintain(photos);
            Check(Cache(photos).Contains(oneKey) && Cache(photos).Contains(orphan),
                "a restarted service reconstructs durable history without deleting any pre-existing completed product PNG");
        }

        var slowDir = Path.Combine(root, "history-retain-slow-cache"); Directory.CreateDirectory(slowDir);
        var files = new HeldHistoryPhotoFiles();
        storage = new(slowDir, 100_000, files);
        path = Path.Combine(slowDir, "photos.bin");
        Check(new PortraitThumbnailCache(path, 1000, storage).TryPut(oneKey, png), "slow cache fixture contains a real history image");
        using (var photos = Service(path, storage))
        {
            photos.SetRecords([one], 0);
            files.HoldNext();
            var writer = Task.Run(() => Cache(photos).TryPut(orphan, png));
            await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var policy = Task.Run(() => photos.SetRecords([one, two], 0));
            var prompt = await Task.WhenAny(policy, Task.Delay(250)) == policy;
            files.Release();
            Check(await writer, "held cache publication eventually completes its real atomic write");
            await policy;
            Check(prompt, "SetRecords publishes managed protection without waiting for a slow cache file or cache IO lock");
        }
        var archiveDir = Path.Combine(root, "history-retain-slow-favorite"); Directory.CreateDirectory(archiveDir);
        files = new HeldHistoryPhotoFiles();
        storage = new(archiveDir, 100_000, files);
        path = Path.Combine(archiveDir, "photos.bin");
        Check(new PortraitThumbnailCache(path, 1000, storage).TryPut(oneKey, png),
            "slow favorite fixture begins with a completed photo before archive publication");
        using (var photos = Service(path, storage))
        {
            photos.SetRecords([one], 0);
            files.HoldNext();
            var archive = photos.SetFavoriteSnapshotsAsync([(10, initial)]);
            await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var policy = Task.Run(() => photos.SetRecords([one, two], 0));
            var prompt = await Task.WhenAny(policy, Task.Delay(250)) == policy;
            files.Release();
            Check(await archive, "held favorite archive completes its real durable publication");
            await policy;
            Check(prompt, "SetRecords does not wait on favoriteSynchronization or a slow favorite archive write");
        }
        return checks;
    }

    private static PortraitHistoryRecord Record(long id, ulong content, PortraitEditorSnapshot value)
        => new(id, new(content, PortraitHistoryEntryPoint.GearsetPortrait, 0, "fixture"), DateTimeOffset.UtcNow,
            null, PortraitHistoryRecordKind.Edit, value.ToJSON());
    private static PortraitThumbnailCache Cache(PortraitHistoryPhotos photos)
        => (PortraitThumbnailCache)typeof(PortraitHistoryPhotos).GetField("cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(photos)!;
    private static async Task Maintain(PortraitHistoryPhotos photos)
    {
        photos.Poll("", default, null, null, false);
        var task = (Task?)typeof(PortraitHistoryPhotos).GetField("maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(photos);
        if (task is not null) await task.WaitAsync(TimeSpan.FromSeconds(5));
        photos.Poll("", default, null, null, false);
    }
    private static void SetKnownRecords(PortraitHistoryPhotos photos, IReadOnlyList<PortraitHistoryRecord> records, int bytes, bool known)
    {
        var method = typeof(PortraitHistoryPhotos).GetMethod("SetRecords", [typeof(IReadOnlyList<PortraitHistoryRecord>), typeof(int), typeof(bool)]);
        if (method is null) throw new InvalidOperationException("History reference publication must distinguish read-only/unknown from known-empty metadata.");
        method.Invoke(photos, [records, bytes, known]);
    }
    private sealed class HeldHistoryPhotoFiles : PortraitStorageFiles
    {
        private ManualResetEventSlim? release;
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void HoldNext() { release = new(false); Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void Release() => release?.Set();
        public override FileStream CreateNew(string path)
        {
            var stream = base.CreateNew(path);
            if (release is { } held)
            {
                Entered.TrySetResult();
                if (!held.Wait(TimeSpan.FromSeconds(5))) { stream.Dispose(); throw new IOException("Controlled slow cache write timed out."); }
                Interlocked.CompareExchange(ref release, null, held);
            }
            return stream;
        }
    }
}
