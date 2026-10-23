using FireGaze.PortraitPreview;

internal static class FavoriteSharedBudgetTests
{
    internal static async Task<int> Run(string root, PortraitEditorSnapshot snapshot)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("FAIL " + name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        var directory = Path.Combine(root, "shared-production-budget");
        Directory.CreateDirectory(directory);
        var favoritePath = Path.Combine(directory, "portrait-favorites-v1.json");
        var photoPath = Path.Combine(directory, "portrait-favorite-photos-v1.json");
        var store = new PortraitFavoritesStore(favoritePath);
        var entry = store.Add(snapshot, 1, "source", "original", []).Entry!;
        Check(entry is not null && store.Rename(entry.ID, "retained").Success,
            "shared capacity fixture starts with a durable library and rotating backup");
        var a = new string('A', 64);
        var b = new string('B', 64);
        var c = new string('C', 64);
        var photos = new PortraitFavoritePhotos(photoPath);
        Check(photos.Synchronize(new HashSet<string> { a }, _ => Png())
            && photos.Synchronize(new HashSet<string> { a, b }, _ => Png()),
            "shared capacity fixture starts with durable favorite PNGs and their backup");
        var originalLibrary = File.ReadAllBytes(favoritePath);
        var originalLibraryBackup = File.ReadAllBytes(favoritePath + ".bak");
        var originalPhotos = File.ReadAllBytes(photoPath);
        var originalPhotosBackup = File.ReadAllBytes(photoPath + ".bak");
        var beforeEntries = store.Export();
        const long productionLimit = 176160768; // Approved 168 MiB, independent of the product constant.
        var currentBytes = Directory.EnumerateFiles(directory).Sum(file => new FileInfo(file).Length);
        var fillerPath = Path.Combine(directory, "portrait-edit-history-v1.json");
        var budget = PortraitStorageBudget.ForDirectory(directory);
        Check(budget.Snapshot.LimitBytes == productionLimit,
            "shared production authority uses the approved 168 MiB default");
        // A legacy history file occupies the remaining logical bytes. SetLength avoids a
        // giant managed fixture buffer; no saved favorite or photo can be evicted to fit.
        using (var filler = new FileStream(fillerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            filler.SetLength(productionLimit - currentBytes);
        budget.RegisterPath(fillerPath);
        Check(budget.Snapshot.UsedBytes == productionLimit,
            "real production authority accounts history, favorite content and both regular backups");
        Check(Directory.EnumerateFiles(directory).Sum(file => new FileInfo(file).Length) == productionLimit,
            "shared production directory reaches exactly 168 MiB before new admissions");

        var refused = store.Add(snapshot, 2, "second", "must-not-publish", []);
        Check(refused.Status == PortraitFavoritesWriteStatus.TooLarge && refused.Reason.Contains("168 MiB", StringComparison.Ordinal),
            "production-default library rejects a shared 168 MiB capacity exhaustion explicitly");
        Check(store.Export() == beforeEntries && originalLibrary.SequenceEqual(File.ReadAllBytes(favoritePath))
            && originalLibraryBackup.SequenceEqual(File.ReadAllBytes(favoritePath + ".bak")),
            "shared capacity rejection preserves the library memory, primary and exact rotating backup");
        Check(!photos.Synchronize(new HashSet<string> { a, b, c }, _ => Png())
            && photos.Status(c) == PortraitFavoritePhotoStatus.Capacity
            && photos.LastError?.Contains("168 MiB", StringComparison.Ordinal) == true,
            "production-default favorite PNG archive reports shared capacity instead of a generic storage failure");
        Check(photos.Count == 2 && photos.TryGet(a, out _) && photos.TryGet(b, out _) && !photos.TryGet(c, out _)
            && originalPhotos.SequenceEqual(File.ReadAllBytes(photoPath))
            && originalPhotosBackup.SequenceEqual(File.ReadAllBytes(photoPath + ".bak")),
            "shared capacity rejection cannot publish a phantom PNG or evict any saved favorite image");
        Check(Directory.EnumerateFiles(directory).Sum(file => new FileInfo(file).Length) == productionLimit,
            "both refused stores leave the actual total and all previously saved files unchanged");

        // Measure only the fixture's serialized documents so each operation fits by itself,
        // while their combined result cannot. No production accounting computes the limit.
        var probeDirectory = Path.Combine(root, "shared-size-fixture");
        var probeBudget = new PortraitStorageBudget(probeDirectory, 1024 * 1024);
        var probeLibraryPath = Path.Combine(probeDirectory, "library.json");
        var probePhotoPath = Path.Combine(probeDirectory, "photos.json");
        var fixtureTime = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        Check(new PortraitFavoritesStore(probeLibraryPath, () => fixtureTime, storage: probeBudget).Add(snapshot, 1, "s", "n", []).Success
            && new PortraitFavoritePhotos(probePhotoPath, storage: probeBudget).Synchronize(new HashSet<string> { a }, _ => Png()),
            "small shared-budget concurrency fixtures serialize real library and photo documents");
        var libraryLength = new FileInfo(probeLibraryPath).Length;
        var photoLength = new FileInfo(probePhotoPath).Length;
        var jointLimit = libraryLength + photoLength - 1;

        foreach (var libraryFirst in new[] { true, false })
        {
            var concurrentDirectory = Path.Combine(root, "shared-concurrent-" + libraryFirst);
            using var physical = new BlockingPublicationFiles();
            var shared = new PortraitStorageBudget(concurrentDirectory, jointLimit, physical);
            var concurrentLibraryPath = Path.Combine(concurrentDirectory, "library.json");
            var concurrentPhotoPath = Path.Combine(concurrentDirectory, "photos.json");
            var concurrentLibrary = new PortraitFavoritesStore(concurrentLibraryPath, () => fixtureTime, storage: shared);
            var concurrentPhotos = new PortraitFavoritePhotos(concurrentPhotoPath, storage: shared);
            var publish = Task.Run(() => libraryFirst
                ? concurrentLibrary.Add(snapshot, 1, "s", "n", []).Success
                : concurrentPhotos.Synchronize(new HashSet<string> { a }, _ => Png()));
            try
            {
                Check(physical.Entered.Wait(TimeSpan.FromSeconds(5)),
                    $"{(libraryFirst ? "library" : "photo")} publication reaches the real reserved temporary-file boundary");
                var competingSucceeded = libraryFirst
                    ? concurrentPhotos.Synchronize(new HashSet<string> { a }, _ => Png())
                    : concurrentLibrary.Add(snapshot, 1, "s", "n", []).Success;
                Check(!competingSucceeded && shared.Snapshot.ReservedBytes > 0
                    && (libraryFirst ? concurrentPhotos.Count == 0 : concurrentLibrary.Entries.Count == 0),
                    "a concurrent cross-store publication cannot spend an active reservation or advance the competing store");
            }
            finally { physical.Release.Set(); }
            Check(await publish.WaitAsync(TimeSpan.FromSeconds(5)) && shared.Snapshot.ReservedBytes == 0,
                "the reserved first store publishes and releases its physical reservation");
            if (libraryFirst)
            {
                Check(!concurrentPhotos.Synchronize(new HashSet<string> { a }, _ => Png())
                    && concurrentPhotos.Status(a) == PortraitFavoritePhotoStatus.Capacity
                    && concurrentPhotos.Count == 0 && !File.Exists(concurrentPhotoPath),
                    "a photo retry sees the committed library bytes and cannot publish beyond their shared quota");
            }
            else
            {
                var failedAdd = concurrentLibrary.Add(snapshot, 1, "s", "n", []);
                Check(failedAdd.Status == PortraitFavoritesWriteStatus.TooLarge && concurrentLibrary.Entries.Count == 0
                    && !File.Exists(concurrentLibraryPath),
                    "a library retry sees committed photo bytes and cannot publish a phantom favorite beyond the shared quota");
            }
            Check(Directory.EnumerateFiles(concurrentDirectory).Sum(file => new FileInfo(file).Length) <= jointLimit,
                "cross-class concurrent admissions keep actual files within the same small directory budget");
        }

        var failureDirectory = Path.Combine(root, "shared-physical-failure");
        var faultFiles = new FailingPublicationFiles();
        var failureBudget = new PortraitStorageBudget(failureDirectory, 1024 * 1024, faultFiles);
        var failureLibraryPath = Path.Combine(failureDirectory, "library.json");
        var failurePhotoPath = Path.Combine(failureDirectory, "photos.json");
        var failureLibrary = new PortraitFavoritesStore(failureLibraryPath, storage: failureBudget);
        var failurePhotos = new PortraitFavoritePhotos(failurePhotoPath, storage: failureBudget);
        var saved = failureLibrary.Add(snapshot, 1, "s", "n", []).Entry!;
        Check(failureLibrary.Rename(saved.ID, "saved").Success
            && failurePhotos.Synchronize(new HashSet<string> { a }, _ => Png())
            && failurePhotos.Synchronize(new HashSet<string> { a, b }, _ => Png()),
            "shared physical-failure fixtures contain valid current and backup files for both store classes");
        var retained = Directory.EnumerateFiles(failureDirectory)
            .Where(file => new FileInfo(file).Length != 0).ToDictionary(file => file, File.ReadAllBytes);
        faultFiles.FailReplace = true;
        var failedRename = failureLibrary.Rename(saved.ID, "failed");
        Check(failedRename.Status == PortraitFavoritesWriteStatus.PersistenceFailed
            && failureLibrary.Entries.Single().Name == "saved" && failureBudget.Snapshot.ReservedBytes == 0,
            "a shared-authority physical library failure reports failure and never advances memory or retains its reservation");
        Check(!failurePhotos.Synchronize(new HashSet<string> { a, b, c }, _ => Png())
            && failurePhotos.Status(c) == PortraitFavoritePhotoStatus.StorageError && failurePhotos.Count == 2
            && failureBudget.Snapshot.ReservedBytes == 0,
            "a shared-authority physical photo failure cannot publish an image or retain its reservation");
        Check(retained.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)))
            && !Directory.EnumerateFiles(failureDirectory).Any(file => file.EndsWith(".tmp", StringComparison.Ordinal)),
            "both failed store classes preserve all primary and backup bytes and leave no temporary candidates");
        faultFiles.FailReplace = false;
        Check(failureLibrary.Rename(saved.ID, "recovered").Success
            && failurePhotos.Synchronize(new HashSet<string> { a, b, c }, _ => Png()),
            "both shared-budget store classes can retry a transient physical publication failure");
        return checks;
    }

    private static byte[] Png()
    {
        var bytes = new byte[64];
        new byte[] { 137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,160,0,0,1,0,8,6 }.CopyTo(bytes, 0);
        return bytes;
    }

    private sealed class BlockingPublicationFiles : PortraitStorageFiles, IDisposable
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public override void MoveNew(string candidate, string target)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test publication was not released");
            base.MoveNew(candidate, target);
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class FailingPublicationFiles : PortraitStorageFiles
    {
        public bool FailReplace;
        public override void Replace(string candidate, string target, string? backup)
        {
            if (FailReplace) throw new IOException("test publication failure");
            base.Replace(candidate, target, backup);
        }
    }
}
