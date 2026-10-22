using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;
using System.Reflection;

internal static class PhotoNonBlockingStorageCases
{
    internal static async Task<int> RunAsync(string root, PortraitEditorSnapshot snapshot)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++; Console.WriteLine("PASS " + name);
        }
        var directory = Path.Combine(root, "nonblocking-archive"); Directory.CreateDirectory(directory);
        var files = new HeldPhotoFiles();
        var storage = new PortraitStorageBudget(directory, 100_000, files);
        var path = Path.Combine(directory, "photos.bin");
        var archivePath = Path.Combine(directory, "favorites.json");
        byte[] png = new byte[64];
        new byte[] {137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,160,0,0,1,0,8,6}.CopyTo(png, 0);
        var first = PortraitThumbnailCache.KeyForSnapshot("first");
        var second = PortraitThumbnailCache.KeyForSnapshot("second");
        var archive = new PortraitFavoritePhotos(archivePath, storage: storage);
        Check(archive.Synchronize(new HashSet<string> { first }, _ => png), "archive nonblocking fixture persists its original real PNG");
        files.HoldNext();
        var publish = Task.Run(() => archive.Synchronize(new HashSet<string> { first, second }, _ => png));
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reads = Task.Run(() => archive.Count == 1 && archive.Contains(first) && !archive.Contains(second)
            && archive.TryGet(first, out var image) && image.SequenceEqual(png)
            && archive.Status(first) == PortraitFavoritePhotoStatus.Available);
        var returned = await Task.WhenAny(reads, Task.Delay(250)) == reads;
        files.Release();
        Check(await publish, "slow real archive candidate eventually publishes after release");
        Check(returned && await reads, "archive state reads return committed data while real file publication is blocked");
        Check(new PortraitFavoritePhotos(archivePath, storage: storage).Contains(second), "released archive result is durable in a fresh reader");

        var textures = DispatchProxy.Create<ITextureProvider, NoGpuProxy>();
        var readback = DispatchProxy.Create<ITextureReadbackProvider, NoGpuProxy>();
        var snapshotKey = PortraitThumbnailCache.KeyForSnapshot("10|" + snapshot.ToJSON());
        Check(new PortraitThumbnailCache(path, 1000, storage).TryPut(snapshotKey, png), "asynchronous favorite fixture has a completed real PNG");
        var serviceArchive = Path.Combine(directory, "service-favorites.json");
        var photos = new PortraitHistoryPhotos(path, textures, readback, new(), 0,
            favoritePhotoPath: serviceArchive, favoriteSnapshots: [], storage: storage);
        var asyncSetter = typeof(PortraitHistoryPhotos).GetMethod("SetFavoriteSnapshotsAsync")
            ?? throw new InvalidOperationException("favorite reference updates need an accepted asynchronous persistence task");
        files.HoldNext();
        var queued = (Task<bool>)asyncSetter.Invoke(photos, [new (ulong, PortraitEditorSnapshot)[] { (10, snapshot) }])!;
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondAdmission = Task.Factory.StartNew(() => (Task<bool>)asyncSetter.Invoke(photos,
            [new (ulong, PortraitEditorSnapshot)[] { (10, snapshot) }])!, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        var admittedBeforeRelease = await Task.WhenAny(secondAdmission, Task.Delay(250)) == secondAdmission;
        var statusRead = Task.Run(() => photos.FavoritePhotoStatus(10, snapshot));
        var statusReturned = await Task.WhenAny(statusRead, Task.Delay(250)) == statusRead;
        var close = photos.BeginDispose();
        Check(photos.IsBusy, "pending managed favorite archive remains busy while physical publication is held");
        Check(!queued.IsCompleted && !close.IsCompleted, "photo close tracks an accepted favorite archive until its physical file write settles");
        files.Release();
        Check(await queued, "asynchronous favorite references complete only after durable archive publication");
        Check(admittedBeforeRelease && await await secondAdmission,
            "a second favorite reference update is admitted without waiting on the first real archive write");
        await close;
        Check(!photos.IsBusy, "completed managed favorite tail does not leave the photo service permanently busy");
        Check(statusReturned && await statusRead == PortraitFavoritePhotoStatus.Missing,
            "favorite status does not wait for a blocked archive write");
        Check(new PortraitFavoritePhotos(serviceArchive, storage: storage).Contains(snapshotKey),
            "closing accepted favorite work preserves its real saved PNG");
        var rejected = (Task<bool>)asyncSetter.Invoke(photos, [new (ulong, PortraitEditorSnapshot)[] { }])!;
        Check(!await rejected, "photo close seals asynchronous favorite admissions");
        await photos.DrainPhysicalAsync();

        var observedCache = new PortraitThumbnailCache(Path.Combine(directory, "observed.bin"), 1000, storage);
        Check(observedCache.TryPut(first, png), "cache observations begin from a real committed generation");
        files.HoldNext();
        var cachePublish = Task.Run(() => observedCache.TryPut(second, png));
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cacheReads = Task.Run(() => observedCache.Count == 1 && observedCache.Keys.SequenceEqual([first])
            && observedCache.Bytes == 148 && observedCache.Contains(first) && !observedCache.Contains(second)
            && observedCache.LastError is null && observedCache.LastStorageCode == PortraitStorageCode.Success);
        var cacheReturned = await Task.WhenAny(cacheReads, Task.Delay(250)) == cacheReads;
        files.Release();
        Check(await cachePublish, "held cache generation eventually reaches the real atomic replacement");
        Check(cacheReturned && await cacheReads,
            "cache count keys bytes and write outcome remain readable during a slow physical candidate");

        var protectedDirectory = Path.Combine(root, "accepted-favorite-close"); Directory.CreateDirectory(protectedDirectory);
        files = new HeldPhotoFiles();
        storage = new PortraitStorageBudget(protectedDirectory, 100_000, files);
        path = Path.Combine(protectedDirectory, "photos.bin");
        serviceArchive = Path.Combine(protectedDirectory, "favorites.json");
        Check(new PortraitThumbnailCache(path, 1000, storage).TryPut(snapshotKey, png),
            "protected favorite action begins with its only real completed PNG");
        photos = new PortraitHistoryPhotos(path, textures, readback, new(), 0,
            favoritePhotoPath: serviceArchive, favoriteSnapshots: [], storage: storage);
        var persist = typeof(PortraitHistoryPhotos).GetMethod("PersistFavoriteAsync")
            ?? throw new InvalidOperationException("favorite metadata needs one tracked protected persistence operation");
        var metadataEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task<bool>> metadataWrite = async () =>
        {
            var protectedImageSurvives = photos.ReclaimStorage() && File.Exists(path);
            metadataEntered.TrySetResult();
            await releaseMetadata.Task;
            // Reclamation occurs while metadata is becoming durable. Its provisional token
            // must already protect the only PNG, before the permanent reference set exists.
            return protectedImageSurvives;
        };
        Func<IEnumerable<(ulong ContentID, PortraitEditorSnapshot Snapshot)>> references = () => [(10, snapshot)];
        var acceptedWrite = (Task<bool>)persist.Invoke(photos, [metadataWrite, references, (10UL, snapshot)])!;
        await metadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        close = photos.BeginDispose();
        Check(!close.IsCompleted && !acceptedWrite.IsCompleted,
            "close retains an accepted metadata action before its archive has even started");
        releaseMetadata.TrySetResult();
        Check(await acceptedWrite, "an accepted metadata action publishes its photo references after close seals new work");
        await close;
        Check(new PortraitFavoritePhotos(serviceArchive, storage: storage).Contains(snapshotKey),
            "protected metadata and its sole PNG are durable before the close drain completes");
        await photos.DrainPhysicalAsync();

        var queuedDirectory = Path.Combine(root, "queued-favorite-protection"); Directory.CreateDirectory(queuedDirectory);
        storage = new PortraitStorageBudget(queuedDirectory, 100_000);
        path = Path.Combine(queuedDirectory, "photos.bin");
        serviceArchive = Path.Combine(queuedDirectory, "favorites.json");
        Check(new PortraitThumbnailCache(path, 1000, storage).TryPut(snapshotKey, png),
            "queued favorite admission starts with its only completed PNG");
        photos = new PortraitHistoryPhotos(path, textures, readback, new(), 0,
            favoritePhotoPath: serviceArchive, favoriteSnapshots: [], storage: storage);
        metadataEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseMetadata = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstMetadata = photos.PersistFavoriteAsync(async () =>
        {
            metadataEntered.TrySetResult();
            await releaseMetadata.Task;
            return true;
        }, () => []);
        await metadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedSave = photos.PersistFavoriteAsync(() => Task.FromResult(true), references, (10, snapshot));
        var queuedPngRetained = photos.ReclaimStorage() && new PortraitThumbnailCache(path, 1000, storage).Contains(snapshotKey);
        releaseMetadata.TrySetResult();
        await firstMetadata;
        await queuedSave;
        var queuedPngArchived = new PortraitFavoritePhotos(serviceArchive, storage: storage).Contains(snapshotKey);
        await photos.BeginDispose();
        await photos.DrainPhysicalAsync();
        Check(queuedPngRetained && queuedPngArchived,
            "accepted queued favorite protects its only real PNG before its metadata worker starts");

        var racingDirectory = Path.Combine(root, "inflight-favorite-protection"); Directory.CreateDirectory(racingDirectory);
        files = new HeldPhotoFiles();
        storage = new PortraitStorageBudget(racingDirectory, 100_000, files);
        path = Path.Combine(racingDirectory, "photos.bin");
        serviceArchive = Path.Combine(racingDirectory, "favorites.json");
        var seed = new PortraitThumbnailCache(path, 1000, storage);
        Check(seed.TryPut(snapshotKey, png) && seed.TryPut(second, png),
            "inflight favorite fixture has a real oldest image and replacement candidate");
        photos = new PortraitHistoryPhotos(path, textures, readback, new(), 0,
            favoritePhotoPath: serviceArchive, favoriteSnapshots: [], storage: storage);
        files.HoldNext();
        var inflightReclaim = Task.Run(photos.ReclaimStorage);
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var metadataSawDurablePng = false;
        var inflightSaveAdmission = Task.Factory.StartNew(() => photos.PersistFavoriteAsync(() =>
        {
            metadataSawDurablePng = new PortraitThumbnailCache(path, 1000, storage).Contains(snapshotKey);
            return Task.FromResult(true);
        }, references, (10, snapshot)), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var inflightAdmittedBeforeRelease = await Task.WhenAny(inflightSaveAdmission, Task.Delay(250)) == inflightSaveAdmission;
        files.Release();
        await inflightReclaim;
        var inflightSave = await inflightSaveAdmission;
        await inflightSave;
        await photos.BeginDispose();
        await photos.DrainPhysicalAsync();
        Check(inflightAdmittedBeforeRelease && metadataSawDurablePng
            && new PortraitFavoritePhotos(serviceArchive, storage: storage).Contains(snapshotKey),
            "favorite admission rescues an image removed by an already executing physical replacement before metadata writes");

        var recoveredDirectory = Path.Combine(root, "recovered-favorite-drain"); Directory.CreateDirectory(recoveredDirectory);
        storage = new PortraitStorageBudget(recoveredDirectory, 100_000);
        path = Path.Combine(recoveredDirectory, "photos.bin");
        serviceArchive = Path.Combine(recoveredDirectory, "favorites.json");
        Check(new PortraitThumbnailCache(path, 1000, storage).TryPut(snapshotKey, png),
            "favorite recovery drain starts with a real durable PNG");
        photos = new PortraitHistoryPhotos(path, textures, readback, new(), 0,
            favoritePhotoPath: serviceArchive, favoriteSnapshots: [], storage: storage,
            retryDelay: (_, _) => Task.CompletedTask);
        Directory.CreateDirectory(serviceArchive);
        Check(!await photos.PersistFavoriteAsync(() => Task.FromResult(true), references, (10, snapshot)),
            "accepted favorite archive reports its real physical publication failure");
        Directory.Delete(serviceArchive);
        photos.RetryStorage();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            photos.Poll("", default, null, null, false);
            if (!photos.IsBusy) break;
            await Task.Delay(5);
        }
        Check(photos.FavoritePhotoStatus(10, snapshot) == PortraitFavoritePhotoStatus.Available
            && new PortraitFavoritePhotos(serviceArchive, storage: storage).Contains(snapshotKey),
            "explicit favorite maintenance retry actually recovers the real archive before close");
        Exception? closeFailure = null;
        try { await photos.BeginDispose(); }
        catch (Exception error) { closeFailure = error; }
        await photos.DrainPhysicalAsync();
        Check(closeFailure is null,
            "recovered favorite archive closes successfully after every accepted task settles despite an earlier false receipt");

        var failedDirectory = Path.Combine(root, "failed-favorite-metadata-drain"); Directory.CreateDirectory(failedDirectory);
        storage = new PortraitStorageBudget(failedDirectory, 100_000);
        path = Path.Combine(failedDirectory, "photos.bin");
        serviceArchive = Path.Combine(failedDirectory, "favorites.json");
        Check(new PortraitThumbnailCache(path, 1000, storage).TryPut(snapshotKey, png),
            "failed metadata drain fixture starts from its only completed PNG");
        photos = new PortraitHistoryPhotos(path, textures, readback, new(), 0,
            favoritePhotoPath: serviceArchive, favoriteSnapshots: [], storage: storage);
        Check(!await photos.PersistFavoriteAsync(() => Task.FromResult(false), references, (10, snapshot)),
            "unsaved favorite metadata returns an unsuccessful accepted receipt");
        closeFailure = null;
        try { await photos.BeginDispose(); }
        catch (Exception error) { closeFailure = error; }
        await photos.DrainPhysicalAsync();
        Check(closeFailure is IOException && new PortraitThumbnailCache(path, 1000, storage).Contains(snapshotKey),
            "unrecovered metadata failure remains a failed close and preserves its sole completed image");
        return checks;
    }

    private sealed class HeldPhotoFiles : PortraitStorageFiles
    {
        private ManualResetEventSlim? release;
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void HoldNext()
        {
            release = new(false);
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void Release() => release?.Set();
        public override FileStream CreateNew(string path)
        {
            var stream = base.CreateNew(path);
            var held = release;
            if (held is not null)
            {
                Entered.TrySetResult();
                if (!held.Wait(TimeSpan.FromSeconds(10))) { stream.Dispose(); throw new IOException("Slow-file fixture was not released."); }
                Interlocked.CompareExchange(ref release, null, held);
            }
            return stream;
        }
    }
}
