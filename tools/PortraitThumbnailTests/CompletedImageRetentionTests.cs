using FireGaze.PortraitPreview;

internal static class CompletedImageRetentionTests
{
    internal static async Task<int> Run(string root)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++; Console.WriteLine("PASS " + name);
        }
        var directory = Path.Combine(root, "completed-image-retention");
        Directory.CreateDirectory(directory);
        var files = new HeldFiles();
        var storage = new PortraitStorageBudget(directory, 100_000, files);
        var path = Path.Combine(directory, "photos.bin");
        var first = PortraitThumbnailCache.KeyForSnapshot("first-completed");
        var second = PortraitThumbnailCache.KeyForSnapshot("second-completed");
        byte[] original = [1, 2, 3];
        var cache = new PortraitThumbnailCache(path, 1000, storage);
        Check(cache.TryPut(first, original), "retention fixture begins with a durable completed image");

        files.HoldNext();
        var write = Task.Run(() => cache.TryPut(second, [4, 5, 6]));
        await files.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var keys = new HashSet<string> { first };
        var publishProtection = Task.Run(() => cache.SetProtectedKeys(keys));
        var returned = await Task.WhenAny(publishProtection, Task.Delay(250)) == publishProtection;
        files.Release();
        Check(await write, "held real cache publication completes after release");
        await publishProtection;
        Check(returned, "protection publication never waits on a real cache file write");
        keys.Clear();
        Check(cache.ReclaimUnprotected() && cache.Contains(first) && !cache.Contains(second),
            "published protection detaches caller keys and preserves their actual pixels");

        Check(PortraitThumbnailCache.MaximumBytes == 50 * 1024 * 1024,
            "photo cache accepts the requested fifty MiB allowance");
        var retained = new PortraitThumbnailCache(path, 1000, storage, preserveCompletedImages: true);
        retained.SetProtectedKeys(new HashSet<string>());
        var before = File.ReadAllBytes(path);
        Check(!retained.SetAvailableBytes(16 + 68 + 2)
            && retained.LastStorageCode == PortraitStorageCode.Capacity
            && retained.TryGet(first, out var preserved) && preserved.SequenceEqual(original)
            && File.ReadAllBytes(path).SequenceEqual(before),
            "shrinking allowance refuses capacity while retaining an existing durable completed image");
        Check(!retained.TryPut(second, [7]) && retained.LastStorageCode == PortraitStorageCode.Capacity
            && File.ReadAllBytes(path).SequenceEqual(before),
            "new image capacity failure never replaces completed pixels");
        Check(retained.Trim(new HashSet<string>()) && retained.ReclaimUnprotected()
            && File.ReadAllBytes(path).SequenceEqual(before),
            "trim and shared reclaim cannot discard completed images in retention mode");
        retained = new PortraitThumbnailCache(path, 1000, storage, preserveCompletedImages: true);
        Check(retained.TryGet(first, out preserved) && preserved.SequenceEqual(original),
            "completed image retention survives a fresh reader without changing the file format");
        var full = new PortraitThumbnailCache(Path.Combine(directory, "above-ten.bin"), 50 * 1024 * 1024, storage);
        Check(full.AvailableBytes == 50 * 1024 * 1024, "fifty MiB is a valid real cache allowance");
        var expandedDirectory = Path.Combine(root, "above-former-cache-limit");
        Directory.CreateDirectory(expandedDirectory);
        var expandedPath = Path.Combine(expandedDirectory, "photos.bin");
        var expanded = new PortraitThumbnailCache(expandedPath, 50 * 1024 * 1024,
            preserveCompletedImages: true);
        var payload = new byte[256 * 1024];
        for (var index = 0; index < 41; index++)
        {
            payload[0] = (byte)index;
            Check(expanded.TryPut(PortraitThumbnailCache.KeyForSnapshot("expanded-" + index), payload),
                "expanded cache durably admits completed image " + index);
        }
        var expandedBytes = File.ReadAllBytes(expandedPath);
        Check(expandedBytes.Length > 10 * 1024 * 1024 && expanded.Count == 41,
            "real completed PNG payloads can exceed the former ten MiB file ceiling");
        expanded = new PortraitThumbnailCache(expandedPath, 50 * 1024 * 1024,
            preserveCompletedImages: true);
        Check(expanded.Count == 41 && expanded.TryGet(PortraitThumbnailCache.KeyForSnapshot("expanded-0"), out preserved)
            && preserved.Length == 256 * 1024 && preserved[0] == 0
            && File.ReadAllBytes(expandedPath).SequenceEqual(expandedBytes),
            "fresh reader retains the first completed image in a cache larger than the former ceiling");

        var intact = File.ReadAllBytes(path);
        var corruptedChecksum = (byte[])intact.Clone();
        corruptedChecksum[^1] ^= 1;
        var protectedCorruption = AssertCorruptionIsRetained(corruptedChecksum, "checksum damage");

        var truncated = intact[..^1];
        var protectedTruncation = AssertCorruptionIsRetained(truncated, "truncated data");

        File.WriteAllBytes(path, intact);
        Check(protectedCorruption.TryPut(second, [9])
            && protectedCorruption.LastStorageCode == PortraitStorageCode.Success
            && protectedCorruption.TryGet(first, out var recovered) && recovered.SequenceEqual(original),
            "a readable replacement file lets a retained cache reload and publish again");
        Check(protectedTruncation.TryPut(second, [9])
            && protectedTruncation.TryGet(second, out var recoveredSecond)
            && recoveredSecond.SequenceEqual(new byte[] { 9 }),
            "a second retained reader observes the recovered cache after the first writer publishes it");
        return checks;

        PortraitThumbnailCache AssertCorruptionIsRetained(byte[] damaged, string kind)
        {
            File.WriteAllBytes(path, damaged);
            var retainedDamage = new PortraitThumbnailCache(path, 1000, storage, preserveCompletedImages: true);
            var untouched = File.Exists(path) ? File.ReadAllBytes(path) : [];
            Check(File.Exists(path) && untouched.SequenceEqual(damaged), kind + " keeps the original cache bytes");
            Check(!retainedDamage.TryPut(second, [9])
                && retainedDamage.LastStorageCode == PortraitStorageCode.StorageError
                && retainedDamage.LastError is { } error
                && error.Contains("已保留原文件并暂停新增照片", StringComparison.Ordinal)
                && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(damaged),
                kind + " rejects new photos without deleting the original file");
            Check(!retainedDamage.Trim(new HashSet<string>())
                && !retainedDamage.ReclaimUnprotected()
                && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(damaged),
                kind + " blocks maintenance until the file can be read again");
            return retainedDamage;
        }
    }

    private sealed class HeldFiles : PortraitStorageFiles
    {
        private ManualResetEventSlim? release;
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void HoldNext() { release = new(false); Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void Release() => release?.Set();
        public override FileStream CreateNew(string path)
        {
            var stream = base.CreateNew(path);
            var held = release;
            if (held is not null)
            {
                Entered.TrySetResult();
                if (!held.Wait(TimeSpan.FromSeconds(10))) { stream.Dispose(); throw new IOException("Held file was not released."); }
                Interlocked.CompareExchange(ref release, null, held);
            }
            return stream;
        }
    }
}
