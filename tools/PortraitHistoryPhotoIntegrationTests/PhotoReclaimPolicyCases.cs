using FireGaze.PortraitPreview;

internal static class PhotoReclaimPolicyCases
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        var attempts = 0;
        var reclaims = 0;
        var remaining = 2;
        Check(PortraitPhotoReclaimPolicy.TryWithReclaim(
            attempt: () => { attempts++; return remaining == 0; },
            capacity: () => remaining != 0,
            reclaim: () => { reclaims++; remaining--; return true; },
            state: () => (remaining, (long)remaining),
            maxReclaims: 4),
            "capacity writes reclaim successive ordinary photos until the candidate fits");
        Check(attempts == 3 && reclaims == 2,
            "successive reclamation attempts are retried and stop immediately after publication");

        attempts = 0;
        reclaims = 0;
        Check(!PortraitPhotoReclaimPolicy.TryWithReclaim(
            attempt: () => { attempts++; return false; },
            capacity: () => true,
            reclaim: () => { reclaims++; return false; },
            state: () => (1, 1L),
            maxReclaims: 4),
            "a failed atomic reclaim stops without pretending that capacity was freed");
        Check(attempts == 1 && reclaims == 1,
            "a no-progress reclaim is bounded to one physical attempt");

        attempts = 0;
        reclaims = 0;
        Check(!PortraitPhotoReclaimPolicy.TryWithReclaim(
            attempt: () => { attempts++; return false; },
            capacity: () => false,
            reclaim: () => { reclaims++; return true; },
            state: () => (1, 1L),
            maxReclaims: 4),
            "non-capacity storage errors do not trigger reclamation");
        Check(attempts == 1 && reclaims == 0,
            "non-capacity failures remain bounded and diagnosable");

        // Real shared-budget contract: a full candidate keeps its current bin until
        // File.Replace, so each reclaimed ordinary image must first publish a smaller
        // complete bin. One reclaim is deliberately insufficient for this 12000-byte
        // candidate; the policy must observe cache progress and continue to the second.
        var root = Path.Combine(Path.GetTempPath(), "FireGazePhotoReclaimPolicy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const long limit = 32768;
            const int slack = 10000;
            var budget = new PortraitStorageBudget(root, limit);
            var cachePath = Path.Combine(root, "portrait-history-thumbnails-v1.bin");
            var cache = new PortraitThumbnailCache(cachePath, PortraitThumbnailCache.MaximumBytes, budget);
            var oldest = PortraitThumbnailCache.KeyForSnapshot("policy-oldest-ordinary");
            var newest = PortraitThumbnailCache.KeyForSnapshot("policy-newest-ordinary");
            var favorite = PortraitThumbnailCache.KeyForSnapshot("policy-only-favorite");
            var image = new byte[1024];
            Array.Fill(image, (byte)71);
            Check(cache.TryPut(oldest, image) && cache.TryPut(newest, image) && cache.TryPut(favorite, image),
                "real budget fixture stores ordered ordinary photos and one favorite");
            cache.SetProtectedKeys(new HashSet<string>([favorite], StringComparer.Ordinal));
            var filler = Path.Combine(root, "portrait-owned-cache-v1.json");
            using (var output = new FileStream(filler, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.SetLength(limit - new FileInfo(cachePath).Length - slack);
                output.Flush(flushToDisk: true);
            }
            budget.RefreshObservation();
            var target = Path.Combine(root, "portrait-favorites-v1.json");
            var attemptsReal = 0;
            var reclaimsReal = 0;
            var last = new PortraitStorageResult(PortraitStorageCode.StorageError, "not attempted");
            bool AttemptReal()
            {
                attemptsReal++;
                return budget.TryPublish(target, 12000, stream => stream.Write(new byte[12000]),
                    keepBackup: false, out last, requestReclaim: false);
            }
            bool CapacityReal() => last.Code == PortraitStorageCode.Capacity;
            bool ReclaimReal()
            {
                reclaimsReal++;
                return cache.ReclaimUnprotected();
            }
            (int Count, long Bytes) StateReal() => (cache.Count, cache.Bytes);

            // This is the regression guard for the old single-reclaim path.
            var oneReclaim = PortraitPhotoReclaimPolicy.TryWithReclaim(
                AttemptReal, CapacityReal, ReclaimReal, StateReal, maxReclaims: 1);
            Check(!oneReclaim && attemptsReal == 2 && reclaimsReal == 1,
                "one bounded reclaim cannot falsely pass when a second ordinary photo is required");
            Check(cache.Contains(newest) && cache.Contains(favorite),
                "single-reclaim refusal retains the newer ordinary and favorite photos");

            // Restart with both ordinary photos. Reusing the partly reclaimed fixture
            // would only need one more deletion and could let the old helper pass.
            File.Delete(filler);
            budget.RefreshObservation();
            Check(cache.TryPut(oldest, image) && cache.TryPut(newest, image),
                "multi-reclaim regression starts with both ordinary photos still on disk");
            using (var output = new FileStream(filler, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.SetLength(limit - new FileInfo(cachePath).Length - slack);
                output.Flush(flushToDisk: true);
            }
            budget.RefreshObservation();

            var multiReclaim = PortraitPhotoReclaimPolicy.TryWithReclaim(
                AttemptReal, CapacityReal, ReclaimReal, StateReal, maxReclaims: 4);
            Check(multiReclaim && attemptsReal == 5 && reclaimsReal == 3,
                "real directory policy retries after each progressing reclaim until the 12000-byte candidate fits");
            Check(!cache.Contains(oldest) && !cache.Contains(newest) && cache.Contains(favorite),
                "real multi-reclaim keeps the sole favorite while evicting ordinary photos oldest first");
            Check(File.Exists(target) && new FileInfo(target).Length == 12000,
                "real candidate is atomically published after bounded successive reclamation");
        }
        finally { Directory.Delete(root, recursive: true); }

        return checks;
    }
}
