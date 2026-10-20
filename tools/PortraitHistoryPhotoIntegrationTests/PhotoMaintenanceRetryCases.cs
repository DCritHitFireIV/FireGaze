using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;
using System.Reflection;

internal static class PhotoMaintenanceRetryCases
{
    internal static async Task<int> RunAsync(string root, PortraitEditorSnapshot snapshot)
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS " + name); }
        var textures = DispatchProxy.Create<ITextureProvider, NoGpuProxy>();
        var readback = DispatchProxy.Create<ITextureReadbackProvider, NoGpuProxy>();
        var key = PortraitThumbnailCache.KeyForSnapshot("10|" + snapshot.ToJSON());
        byte[] png = new byte[64];
        new byte[] {137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,160,0,0,1,0,8,6}.CopyTo(png, 0);
        async Task Settle(PortraitHistoryPhotos photos)
        {
            for (var i = 0; i < 200; i++)
            {
                photos.Poll("", default, null, null, false);
                if (!photos.IsBusy) return;
                await Task.Delay(5);
            }
            throw new InvalidOperationException("Maintenance did not settle within the deterministic retry boundary.");
        }
        var directory = Path.Combine(root, "maintenance-retries"); Directory.CreateDirectory(directory);
        var storage = new PortraitStorageBudget(directory, 10_000);
        var path = Path.Combine(directory, "photos.bin");
        Check(new PortraitThumbnailCache(path, 1000, storage).TryPut(key, png), "maintenance retry seed is a real disk image");
        var delays = new List<double>();
        using (var photos = new PortraitHistoryPhotos(path, textures, readback, new(), 0,
            favoriteSnapshots: [], retryDelay: (duration, _) => { delays.Add(duration.TotalSeconds); return Task.CompletedTask; }, storage: storage))
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                photos.SetRecords([], PortraitHistoryStore.MaximumBytes);
                await Settle(photos);
                Check(delays.SequenceEqual(new double[] { 1, 2, 4 }) && File.Exists(path), "failed deletion retries exactly at one two four seconds and preserves original");
                for (var i = 0; i < 30; i++) photos.Poll("", default, null, null, false);
                Check(delays.Count == 3 && !photos.IsBusy, "unchanged exhausted maintenance cannot restart on every frame");
                var diagnostics = photos.Diagnostics();
                Check((bool)diagnostics.GetType().GetProperty("maintenancePending")!.GetValue(diagnostics)!, "exhausted maintenance remains pending and diagnosable");
            }
            storage.RequestReclaim();
            await Settle(photos);
            Check(!File.Exists(path), "a later reclaim request recovers failed maintenance without another history edit");
        }
        var archiveDirectory = Path.Combine(root, "archive-retries"); Directory.CreateDirectory(archiveDirectory);
        storage = new PortraitStorageBudget(archiveDirectory, 10_000);
        path = Path.Combine(archiveDirectory, "photos.bin"); var archive = Path.Combine(archiveDirectory, "favorites.json");
        Check(new PortraitThumbnailCache(path, 1000, storage).TryPut(key, png), "archive retry seed is a completed PNG");
        delays.Clear();
        using (var photos = new PortraitHistoryPhotos(path, textures, readback, new(), 0, favoritePhotoPath: archive,
            favoriteSnapshots: [], retryDelay: (duration, _) => { delays.Add(duration.TotalSeconds); return Task.CompletedTask; }, storage: storage))
        {
            Directory.CreateDirectory(archive);
            Check(!photos.SetFavoriteSnapshots([(10, snapshot)]), "real archive publication failure stays visible");
            photos.SetRecords([], PortraitHistoryStore.MaximumBytes);
            await Settle(photos);
            Check(delays.SequenceEqual(new double[] { 1, 2, 4 }) && File.Exists(path), "archive failure retries finitely and pins its only completed PNG");
            Check(photos.ReclaimStorage() && File.Exists(path), "explicit reclamation cannot discard an unarchived favorite");
            Directory.Delete(archive);
            storage.RequestReclaim();
            await Settle(photos);
            Check(photos.FavoritePhotoStatus(10, snapshot) == PortraitFavoritePhotoStatus.Available
                && File.Exists(archive) && !File.Exists(path), "archive retry later succeeds before ordinary copy is removed");
        }
        var fullDirectory = Path.Combine(root, "full-mixed-retry"); Directory.CreateDirectory(fullDirectory);
        path = Path.Combine(fullDirectory, "photos.bin");
        var seed = new PortraitThumbnailCache(path, 1000, new PortraitStorageBudget(fullDirectory, 1000));
        Check(seed.TryPut(key, png) && seed.TryPut(PortraitThumbnailCache.KeyForSnapshot("ordinary"), png), "full mixed retry starts with a pinned and a disposable PNG");
        storage = new PortraitStorageBudget(fullDirectory, 320); delays.Clear();
        using (var photos = new PortraitHistoryPhotos(path, textures, readback, new(), 0,
            favoriteSnapshots: [], retryDelay: (duration, _) => { delays.Add(duration.TotalSeconds); return Task.CompletedTask; }, storage: storage))
        {
            typeof(PortraitHistoryPhotos).GetField("favoritePhotos", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(photos, new PortraitFavoritePhotos(Path.Combine(fullDirectory, "small.json"), 128, storage));
            photos.SetFavoriteSnapshots([(10, snapshot)]);
            photos.SetRecords([], PortraitHistoryStore.MaximumBytes - 148);
            photos.Poll("", default, null, null, false);
            var pending = (Task)typeof(PortraitHistoryPhotos).GetField("maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(photos)!;
            await pending;
            for (var i = 0; i < 30; i++) photos.Poll("", default, null, null, false);
            await Task.Delay(50);
            Check(delays.Count == 3 && File.Exists(path), "capacity refusals generated by reclamation cannot restart their own retry batch");
        }
        var writeDirectory = Path.Combine(root, "png-write-retry"); Directory.CreateDirectory(writeDirectory);
        path = Path.Combine(writeDirectory, "photos.bin");
        storage = new PortraitStorageBudget(writeDirectory, 10_000); delays.Clear();
        var source = new PortraitThumbnailSource();
        var identity = new PortraitIdentity(1, 2, 3, 4, 5);
        var record = new PortraitHistoryRecord(1, new(10, PortraitHistoryEntryPoint.GearsetPortrait, 0, "fixture"),
            DateTimeOffset.UtcNow, null, PortraitHistoryRecordKind.Edit, snapshot.ToJSON());
        using (var photos = new PortraitHistoryPhotos(path, textures, readback, source, 0, favoriteSnapshots: [],
            retryDelay: (duration, _) => { delays.Add(duration.TotalSeconds); return Task.CompletedTask; }, storage: storage))
        {
            photos.SetRecords([record], 0); await Settle(photos);
            photos.Poll("png-owner", identity, 1, snapshot, true); await Task.Delay(250);
            photos.Poll("png-owner", identity, 1, snapshot, true);
            var readStatus = typeof(PortraitHistoryPhotos).GetMethod("GetHistoryPhotoStatus")
                ?? throw new InvalidOperationException("history photo status API is missing");
            source.Complete(new(new FakeTexture()));
            var capture = typeof(PortraitHistoryPhotos).GetField("capture", BindingFlags.Instance | BindingFlags.NonPublic)!;
            (await (Task<PortraitThumbnailLayers?>)capture.GetValue(photos)!)?.Dispose(); capture.SetValue(photos, null);
            typeof(PortraitHistoryPhotos).GetField("encoding", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(photos, Task.FromResult(png));
            using (var held = new FileStream(Path.Combine(writeDirectory, ".portrait-storage.lock"), FileMode.OpenOrCreate,
                       FileAccess.ReadWrite, FileShare.None))
            {
                photos.Poll("png-owner", identity, 1, snapshot, true);
                var diskWrite = (Task<bool>)typeof(PortraitHistoryPhotos).GetField("writing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(photos)!;
                Check((PortraitHistoryPhotoStatus)readStatus.Invoke(photos, [1L])! == PortraitHistoryPhotoStatus.Loading,
                    "history image being written reports loading rather than missing");
                var failedWrite = !await diskWrite;
                // Observe the failed receipt before any assertion so Dispose cannot hide the RED.
                photos.Poll("png-owner", identity, 1, snapshot, true);
                Check(failedWrite && delays.SequenceEqual(new double[] { 1, 2, 4 }),
                    "completed PNG retries Busy at one two four seconds before retaining a failed write");
                Check((PortraitHistoryPhotoStatus)readStatus.Invoke(photos, [1L])! == PortraitHistoryPhotoStatus.StorageError,
                    "failed retained PNG is visible as a write failure on its history row");
                photos.Poll("png-owner", identity, 1, snapshot, true); await Task.Delay(250);
                photos.Poll("png-owner", identity, 1, snapshot, true);
                Check(source.Calls == 1, "exhausted PNG storage failure cannot start a new GPU capture of the same image");
            }
            typeof(PortraitHistoryPhotos).GetMethod("RetryStorage")!.Invoke(photos, null);
            for (var i = 0; i < 100 && !File.Exists(path); i++)
            { photos.Poll("png-owner", identity, 1, snapshot, true); await Task.Delay(5); }
            Check(File.Exists(path) && source.Calls == 1, "explicit PNG retry uses the retained bytes without recapturing the game");
        }
        return checks;
    }
}
