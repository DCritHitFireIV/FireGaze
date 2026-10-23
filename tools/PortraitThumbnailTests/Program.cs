using System.Text;
#if HAS_CACHE
using FireGaze.PortraitPreview;
#endif

#if !HAS_CACHE
Console.Error.WriteLine("FAIL portrait thumbnails have no bounded persistent disposable cache");
return 1;
#else
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS " + name); }
void Throws<T>(Action action, string name) where T : Exception { try { action(); } catch (T) { Check(true, name); return; } throw new InvalidOperationException(name); }
var root = Path.Combine(Path.GetTempPath(), "FireGazeThumbnailTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var path = Path.Combine(root, "photos.bin");
    var history = Path.Combine(root, "history.json");
    File.WriteAllText(history, "untouched portrait history", Encoding.UTF8);
    var historyBefore = File.ReadAllBytes(history);
    var fullRoot = Path.Combine(root, "full-root"); Directory.CreateDirectory(fullRoot);
    using (var full = new FileStream(Path.Combine(fullRoot, "portrait-favorites-v1.json.bak"), FileMode.CreateNew)) full.SetLength(PortraitStorageBudget.DefaultLimitBytes);
    var fullPath = Path.Combine(fullRoot, "portrait-history-thumbnails-v1.bin");
    var fullCache = new PortraitThumbnailCache(fullPath, 1000);
    Check(!fullCache.TryPut(PortraitThumbnailCache.KeyForSnapshot("over-quota"), [1]) && !File.Exists(fullPath), "thumbnail publication respects the shared directory quota");
    var one = PortraitThumbnailCache.KeyForSnapshot("{\"state\":1}");
    var two = PortraitThumbnailCache.KeyForSnapshot("{\"state\":2}");
    var three = PortraitThumbnailCache.KeyForSnapshot("{\"state\":3}");
    var beforeFullPortraitFix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("{\"state\":1}")));
    Check(one != beforeFullPortraitFix, "full render target fix cannot reuse an earlier blank or cropped photo key");
    var beforeAlphaFix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("portrait-photo-v2|{\"state\":1}")));
    Check(one != beforeAlphaFix, "direct overlay composition cannot reuse earlier double-alpha image keys");
    Check(one.Length == 64 && one == PortraitThumbnailCache.KeyForSnapshot("{\"state\":1}") && one != two, "snapshot hash is stable and separates states");
    Throws<ArgumentOutOfRangeException>(() => new PortraitThumbnailCache(path, -1), "negative constructor budget is rejected");
    var cache = new PortraitThumbnailCache(path, 1000);
    Check(cache.Bytes == 0 && cache.Count == 0 && !File.Exists(path), "empty cache occupies no file bytes");
    var image = Enumerable.Repeat((byte)7, 100).ToArray();
    Check(cache.TryPut(one, image), "first encoded thumbnail persists");
    Check(cache.Count == 1 && cache.Bytes == new FileInfo(path).Length && cache.Bytes <= 1000, "disk accounting includes serialization metadata");
    var firstSize = cache.Bytes;
    var mixedRoot = Path.Combine(root, "mixed-pins"); Directory.CreateDirectory(mixedRoot);
    var mixedStorage = new PortraitStorageBudget(mixedRoot, 1000);
    var mixedPath = Path.Combine(mixedRoot, "photos.bin");
    var mixed = new PortraitThumbnailCache(mixedPath, 1000, mixedStorage);
    Check(mixed.TryPut(one, [1]) && mixed.TryPut(two, [2]), "mixed pin test begins with two real published images");
    mixed = new PortraitThumbnailCache(mixedPath, 1000, new PortraitStorageBudget(mixedRoot, 220));
    mixed.SetProtectedKeys(new HashSet<string> { one });
    var mixedBefore = File.ReadAllBytes(mixedPath);
    Check(!mixed.ReclaimUnprotected() && mixed.LastStorageCode == PortraitStorageCode.Capacity
        && File.ReadAllBytes(mixedPath).SequenceEqual(mixedBefore), "a nonempty shrink without candidate headroom safely refuses and preserves pinned pixels");
    image[0] = 99;
    Check(cache.TryGet(one, out var retrieved) && retrieved[0] == 7, "insertion detaches caller bytes");
    retrieved[0] = 88;
    Check(cache.TryGet(one, out var detached) && detached[0] == 7, "retrieval cannot mutate cached image");
    Check(cache.TryPut(one, detached) && cache.Count == 1 && cache.Bytes == firstSize, "same state and image deduplicate");
    Check(cache.TryPut(two, detached) && cache.TryPut(three, detached), "multiple states share one binary file");
    Check(Directory.GetFiles(root).Count(file=>Path.GetFileName(file)!=".portrait-storage.lock") == 2, "successful atomic writes leave only history and one cache file plus zero-byte coordination lease");
    var restored = new PortraitThumbnailCache(path, 1000);
    Check(restored.Count == 3 && restored.TryGet(two, out var afterRestart) && afterRestart.SequenceEqual(detached), "restart reloads complete thumbnails");
    restored.SetAvailableBytes((int)firstSize);
    Check(restored.Count == 1 && !restored.TryGet(one, out _) && restored.TryGet(three, out _) && restored.Bytes == firstSize, "budget reduction evicts oldest images using actual file size");
    Throws<ArgumentOutOfRangeException>(() => restored.SetAvailableBytes(-1), "negative budget update preserves cache");
    Check(restored.Count == 1, "rejected budget update keeps existing image");
    Check(!restored.TryPut(one, new byte[300]) && restored.TryGet(three, out _), "an individually unfit image cannot evict useful cached image");
    restored.Trim(new HashSet<string> { two });
    Check(restored.Count == 0 && restored.Bytes == 0 && !File.Exists(path), "orphan trim removes unreferenced images and empty file");
    restored.SetAvailableBytes(1000);
    Check(restored.TryPut(one, detached), "cache remains usable after orphan trim");
    restored.SetAvailableBytes(0);
    Check(restored.Count == 0 && restored.Bytes == 0 && !File.Exists(path) && !restored.TryPut(one, detached), "zero budget leaves no file and accepts no image");
    Throws<ArgumentOutOfRangeException>(() => restored.SetAvailableBytes(PortraitThumbnailCache.MaximumBytes + 1), "cache cannot exceed combined history maximum");
    restored.SetAvailableBytes(1000);
    Check(!restored.TryPut("invalid", detached) && !restored.TryPut(one, []) && !restored.TryPut(one, new byte[PortraitThumbnailCache.MaximumImageBytes + 1]), "invalid hash empty and oversized payloads are rejected");
    Check(restored.TryPut(one, detached), "valid image can precede damage recovery");
    var valid = File.ReadAllBytes(path);
    var corruptedPayload = (byte[])valid.Clone();
    corruptedPayload[^1] ^= 1;
    File.WriteAllBytes(path, corruptedPayload);
    var checksumFailure = new PortraitThumbnailCache(path, 1000);
    Check(checksumFailure.Count == 0 && !File.Exists(path), "a flipped encoded payload byte cannot survive as a valid thumbnail");
    foreach (var bad in new[] { new byte[] { 1, 2, 3 }, valid[..^1], valid.Concat(new byte[] { 0 }).ToArray() })
    {
        File.WriteAllBytes(path, bad);
        var damaged = new PortraitThumbnailCache(path, 1000);
        Check(damaged.Count == 0 && damaged.Bytes == 0 && !File.Exists(path), "damaged or trailing binary data is discarded as rebuildable cache");
    }
    File.WriteAllBytes(path, valid);
    var tooSmall = new PortraitThumbnailCache(path, 1);
    Check(tooSmall.Count == 1 && File.ReadAllBytes(path).SequenceEqual(valid), "loading cannot evict saved pixels before favorite references are known");
    Check(tooSmall.SetAvailableBytes(1) && tooSmall.Count == 0 && !File.Exists(path), "explicit unpinned maintenance enforces the reduced available budget");
    File.WriteAllBytes(path, valid);
    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) { stream.Position = 12; stream.Write(BitConverter.GetBytes(int.MaxValue)); }
    var hostile = new PortraitThumbnailCache(path, 1000);
    Check(hostile.Count == 0 && !File.Exists(path), "hostile entry count is rejected before allocation");
    var failurePath = Path.Combine(root, "directory-is-not-file");
    Directory.CreateDirectory(failurePath);
    var failed = new PortraitThumbnailCache(failurePath, 1000);
    Check(!failed.TryPut(one, detached) && failed.LastError is not null && failed.Count == 0, "failed atomic publish does not claim a persisted image");
    Check(Directory.GetFiles(root).All(name => !Path.GetFileName(name).Contains(".tmp-", StringComparison.Ordinal)), "failed writes clean only their own temporary files");
    Check(File.ReadAllBytes(history).SequenceEqual(historyBefore), "all cache operations preserve unrelated history byte for byte");
    checks += await CompletedImageRetentionTests.Run(root);
    checks += await TextureCacheTests.Run();
    Console.WriteLine($"PortraitThumbnailTests: {checks} passed, 0 failed");
    return 0;
}
finally { Directory.Delete(root, true); }
#endif
