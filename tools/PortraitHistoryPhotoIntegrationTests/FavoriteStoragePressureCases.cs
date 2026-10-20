using System.Reflection;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class FavoriteStoragePressureCases
{
    internal static async Task<int> RunAsync(string root, PortraitEditorSnapshot snapshot)
    {
        var checks = 0;
        var failures = new List<string>();
        void Check(bool value, string name)
        {
            checks++; Console.WriteLine((value ? "PASS " : "FAIL ") + name);
            if (!value) failures.Add(name);
        }
        var textures = DispatchProxy.Create<ITextureProvider, NoGpuProxy>();
        var readback = DispatchProxy.Create<ITextureReadbackProvider, NoGpuProxy>();
        var key = PortraitThumbnailCache.KeyForSnapshot("10|" + snapshot.ToJSON());
        var png = new byte[64];
        new byte[] { 137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,160,0,0,1,0,8,6 }.CopyTo(png, 0);

        // A broken archive means the bin is the only completed favorite image. A budget
        // reduction must preserve that copy even though archive rescue returned false.
        var failedPath = Path.Combine(root, "pressure-failed-archive.bin");
        var archivePath = Path.Combine(root, "pressure-failed-archive.json");
        new PortraitThumbnailCache(failedPath, PortraitThumbnailCache.MaximumBytes).TryPut(key, png);
        var before = File.ReadAllBytes(failedPath);
        File.WriteAllText(archivePath, "{broken-favorite-archive");
        using (var service = new PortraitHistoryPhotos(failedPath, textures, readback, new(),
            PortraitHistoryStore.MaximumBytes, favoritePhotoPath: archivePath, favoriteSnapshots: [(10, snapshot)]))
        {
            Check(File.Exists(failedPath) && before.SequenceEqual(File.ReadAllBytes(failedPath)),
                "startup budget pressure preserves the only favorite PNG after archive rescue fails");
            Check(service.FavoritePhotoStatus(10, snapshot) == PortraitFavoritePhotoStatus.StorageError,
                "failed rescue remains an explicit favorite storage error");
            Check(File.ReadAllText(archivePath) == "{broken-favorite-archive",
                "favorite rescue failure preserves damaged intentional content for recovery");
        }

        // Null means that favorite references could not be read, not an authoritative empty set.
        var unknownPath = Path.Combine(root, "pressure-unknown-references.bin");
        new PortraitThumbnailCache(unknownPath, PortraitThumbnailCache.MaximumBytes).TryPut(key, png);
        using (var service = new PortraitHistoryPhotos(unknownPath, textures, readback, new(), PortraitHistoryStore.MaximumBytes))
        {
            Check(File.Exists(unknownPath), "unknown favorite references cannot destructively fit a valid bin during Load");
            Check(service.SetFavoriteSnapshots([(10, snapshot)])
                && service.FavoritePhotoStatus(10, snapshot) == PortraitFavoritePhotoStatus.Available,
                "a later trustworthy favorite reference set can still rescue the startup PNG");
        }

        var capacityPath = Path.Combine(root, "pressure-photo-capacity.bin");
        new PortraitThumbnailCache(capacityPath, PortraitThumbnailCache.MaximumBytes).TryPut(key, png);
        using (var service = new PortraitHistoryPhotos(capacityPath, textures, readback, new(), 0,
            favoriteSnapshots: Array.Empty<(ulong, PortraitEditorSnapshot)>()))
        {
            // Keep the real archive implementation, but use its supported small capacity to
            // exercise the finite-capacity path without manufacturing a 16 MiB archive.
            typeof(PortraitHistoryPhotos).GetField("favoritePhotos", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, new PortraitFavoritePhotos(Path.Combine(root, "pressure-small-archive.json"), 128));
            service.SetFavoriteSnapshots([(10, snapshot)]);
            Check(service.FavoritePhotoStatus(10, snapshot) == PortraitFavoritePhotoStatus.Capacity,
                "favorite photo capacity refusal is distinct from a missing completed image");
            service.SetRecords([], PortraitHistoryStore.MaximumBytes);
            service.Poll("", default, null, null, false);
            for (var retry = 0; retry < 100 && service.IsBusy; retry++) await Task.Delay(10);
            service.Poll("", default, null, null, false);
            var retained = new PortraitThumbnailCache(capacityPath, PortraitThumbnailCache.MaximumBytes);
            Check(File.Exists(capacityPath) && retained.TryGet(key, out var bytes) && bytes.SequenceEqual(png),
                "maintenance cannot evict a favorite whose archive is full and whose only copy is the bin");
        }

        var provisionalPath = Path.Combine(root, "pressure-provisional.bin");
        new PortraitThumbnailCache(provisionalPath, PortraitThumbnailCache.MaximumBytes).TryPut(key, png);
        using (var service = new PortraitHistoryPhotos(provisionalPath, textures, readback, new(), 0,
            favoriteSnapshots: Array.Empty<(ulong, PortraitEditorSnapshot)>()))
        {
            var protect = typeof(PortraitHistoryPhotos).GetMethod("ProtectFavoriteSnapshot");
            Check(protect is not null, "saving favorite metadata can first acquire a provisional photo pin");
            if (protect is not null)
            {
                using var first = (IDisposable?)protect.Invoke(service, [10UL, snapshot]);
                var second = (IDisposable?)protect.Invoke(service, [10UL, snapshot]);
                Check(first is not null && second is not null, "overlapping saves can retain the same completed PNG independently");
                first?.Dispose();
                service.SetRecords([], PortraitHistoryStore.MaximumBytes);
                service.Poll("", default, null, null, false);
                for (var retry = 0; retry < 100 && service.IsBusy; retry++) await Task.Delay(10);
                Check(File.Exists(provisionalPath), "disposing one save token cannot release another save's provisional pin");
                Check(service.SetFavoriteSnapshots([(10, snapshot)]), "durable favorite synchronization succeeds while a provisional pin is held");
                second?.Dispose();
                Check(service.FavoritePhotoStatus(10, snapshot) == PortraitFavoritePhotoStatus.Available,
                    "releasing the last provisional token preserves the newly durable favorite photo");
            }
        }

        var reclaimPath = Path.Combine(root, "pressure-oldest-reclaim.bin");
        var oldest = PortraitThumbnailCache.KeyForSnapshot("oldest");
        var newest = PortraitThumbnailCache.KeyForSnapshot("newest");
        var favorite = PortraitThumbnailCache.KeyForSnapshot("favorite");
        var reclaimCache = new PortraitThumbnailCache(reclaimPath, PortraitThumbnailCache.MaximumBytes);
        Check(reclaimCache.TryPut(oldest, png) && reclaimCache.TryPut(newest, png)
            && reclaimCache.TryPut(favorite, png), "oldest reclaim fixture stores ordered ordinary and favorite images");
        reclaimCache.SetProtectedKeys(new HashSet<string>([favorite], StringComparer.Ordinal));
        Check(reclaimCache.ReclaimUnprotected(), "oldest reclaim operation completes");
        Check(!reclaimCache.Contains(oldest) && reclaimCache.Contains(newest) && reclaimCache.Contains(favorite),
            "reclaim removes only the oldest non-favorite image and preserves newer and favorite images");

        var unreadablePath = Path.Combine(root, "pressure-unreadable-favorite.bin");
        Check(new PortraitThumbnailCache(unreadablePath, 1000).TryPut(key, png),
            "unreadable favorite fixture contains a completed image on disk");
        var originalBytes = File.ReadAllBytes(unreadablePath);
        PortraitThumbnailCache unreadable;
        using (var held = new FileStream(unreadablePath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            unreadable = new(unreadablePath, 1000);
            unreadable.SetProtectedKeys(new HashSet<string>([key], StringComparer.Ordinal));
            Check(!unreadable.ReclaimUnprotected() && !unreadable.SetAvailableBytes(0),
                "an unreadable cache refuses reclamation and shrink instead of deleting unknown images");
            Check(!unreadable.TryPut(newest, png),
                "an unreadable cache cannot replace unknown images with a new empty index");
        }
        Check(File.Exists(unreadablePath) && File.ReadAllBytes(unreadablePath).SequenceEqual(originalBytes),
            "a temporary read denial with delete sharing preserves the only favorite PNG byte for byte");
        Check(unreadable.SetAvailableBytes(1000) && unreadable.TryPut(newest, png)
            && unreadable.Contains(key) && unreadable.Contains(newest),
            "after read access recovers the same cache reloads its old index before a new publication");
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
        return checks;
    }
}
