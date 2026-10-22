using System.Buffers.Binary;
using System.Text.Json.Nodes;
using FireGaze.PortraitPreview;

#if !HAS_FAVORITE_PHOTOS
Console.Error.WriteLine("FAIL favorite photos must survive eviction of the disposable history cache");
return 1;
#else
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name); checks++;
}
var root = Path.Combine(Path.GetTempPath(), "FireGazeFavoritePhotoTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var sourcePath = Path.Combine(root, "history.bin");
    var archivePath = Path.Combine(root, "favorites.json");
    var history = new PortraitThumbnailCache(sourcePath, PortraitThumbnailCache.MaximumBytes);
    var a = PortraitThumbnailCache.KeyForSnapshot("10|{\"pose\":1}");
    var b = PortraitThumbnailCache.KeyForSnapshot("20|{\"pose\":1}");
    var c = PortraitThumbnailCache.KeyForSnapshot("10|{\"pose\":2}");
    var png = Png();
    Check(history.TryPut(a, png), "existing completed history image is the only image source");
    var archive = new PortraitFavoritePhotos(archivePath);
    var recoveredReferences = Enumerable.Range(1, 6000).Select(index => index.ToString("X64")).ToHashSet(StringComparer.Ordinal);
    var recoveredLast = 6000.ToString("X64");
    var recoveryPath = Path.Combine(root, "recovery-many.json");
    var recovery = new PortraitFavoritePhotos(recoveryPath);
    Check(recovery.Synchronize(recoveredReferences, key => key == recoveredLast ? png : null)
        && new PortraitFavoritePhotos(recoveryPath).TryGet(recoveredLast, out var recoveredPhoto) && recoveredPhoto.SequenceEqual(png),
        "combined favorite and trash references retain a tail photo beyond the former five-thousand limit after restart");
    byte[]? ReadCompleted(string key) => history.TryGet(key, out var value) ? value : null;
    Check(archive.Synchronize(new HashSet<string> { a }, ReadCompleted), "a favorite copies its completed PNG into durable independent storage");
    Check(archive.Status(a) == PortraitFavoritePhotoStatus.Available && archive.TryGet(a, out var copy) && copy.SequenceEqual(png),
        "retained photo bytes match the original completed PNG");
    history.SetAvailableBytes(0);
    var reopened = new PortraitFavoritePhotos(archivePath);
    var durableFound = reopened.TryGet(a, out var durable);
    Check(!File.Exists(sourcePath) && durableFound && durable.SequenceEqual(png),
        "history budget eviction and a restart cannot discard a referenced favorite photo");
    durable[0] = 0;
    Check(reopened.TryGet(a, out var detached) && detached[0] == 137, "photo retrieval cannot mutate durable bytes");
    var original = File.ReadAllBytes(archivePath);
    Check(reopened.Synchronize(new HashSet<string> { a }, _ => throw new InvalidOperationException("must reuse retained image"))
        && original.SequenceEqual(File.ReadAllBytes(archivePath)), "repeated identical favorites reuse retained data without writes or source reads");
    Check(reopened.Synchronize(new HashSet<string> { a, b }, ReadCompleted)
        && reopened.Status(b) == PortraitFavoritePhotoStatus.Missing && !reopened.TryGet(b, out _),
        "same-looking state from another character has an explicit missing photo and cannot borrow the first photo");
    Check(reopened.Synchronize(new HashSet<string> { a, b }, key => key == b ? Png(80) : null)
        && reopened.Status(b) == PortraitFavoritePhotoStatus.Available, "a later completed PNG fills a previously missing favorite without replay");
    Check(reopened.Count == 2, "different source characters retain independent rendered photos");
    Check(reopened.Synchronize(new HashSet<string> { a }, _ => null) && new PortraitFavoritePhotos(archivePath).Count == 1,
        "deleting the last reference collects the removed photo immediately without Dispose");
    Check(reopened.Synchronize(new HashSet<string>(), _ => null) && new PortraitFavoritePhotos(archivePath).Count == 0,
        "deleting all favorites durably removes every photo while preserving an empty valid archive");

    var limited = new PortraitFavoritePhotos(Path.Combine(root, "limited.json"), maximumBytes: 900);
    Check(limited.Synchronize(new HashSet<string> { a }, _ => Png(200)), "one bounded image fits the small real serialized budget");
    Check(limited.Synchronize(new HashSet<string> { a, b }, _ => Png(400))
        && limited.Status(a) == PortraitFavoritePhotoStatus.Available && limited.Status(b) == PortraitFavoritePhotoStatus.Capacity,
        "capacity exhaustion preserves every existing referenced photo and explains why a new image is missing");
    Check(new FileInfo(Path.Combine(root, "limited.json")).Length <= 900 && limited.Count == 1,
        "encoded file size including JSON and base64 never exceeds its finite budget");
    Check(limited.Synchronize(new HashSet<string> { b }, _ => Png(400)) && limited.Status(b) == PortraitFavoritePhotoStatus.Available,
        "removing a reference frees space for the remaining favorite in the same atomic update");
    Check(limited.Synchronize(new HashSet<string> { b, c }, _ => new byte[400])
        && limited.Status(c) == PortraitFavoritePhotoStatus.Missing, "invalid or non-PNG source data never becomes a durable photo");
    Check(limited.Synchronize(new HashSet<string> { b, c }, _ => Png(PortraitThumbnailCache.MaximumImageBytes + 1))
        && limited.Status(c) == PortraitFavoritePhotoStatus.Missing, "oversize source image is refused before publication");
    var wrongSize = Png(); BinaryPrimitives.WriteUInt32BigEndian(wrongSize.AsSpan(16), 4096);
    Check(limited.Synchronize(new HashSet<string> { b, c }, _ => wrongSize)
        && limited.Status(c) == PortraitFavoritePhotoStatus.Missing, "full-size images cannot be retained as thumbnails");

    var lockedPath = Path.Combine(root, "locked.json");
    var locked = new PortraitFavoritePhotos(lockedPath);
    Check(locked.Synchronize(new HashSet<string> { a }, _ => png), "failure case starts from a real durable archive");
    var beforeFailure = File.ReadAllBytes(lockedPath);
    using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        Check(!locked.Synchronize(new HashSet<string> { a, b }, _ => png) && locked.Status(b) == PortraitFavoritePhotoStatus.StorageError
            && locked.TryGet(a, out _), "failed atomic replacement cannot publish a phantom new photo or remove an existing one");
    Check(beforeFailure.SequenceEqual(File.ReadAllBytes(lockedPath)) && new PortraitFavoritePhotos(lockedPath).Count == 1,
        "a failed write preserves the complete durable archive byte for byte");
    Check(!Directory.EnumerateFiles(root).Any(file => file.Contains(".tmp-", StringComparison.Ordinal)),
        "failed writes remove only their own temporary file");
    Check(locked.Synchronize(new HashSet<string> { a, b }, _ => png) && locked.Status(b) == PortraitFavoritePhotoStatus.Available,
        "transient storage failure can be retried without losing old photos");

    var backupPath = Path.Combine(root, "backed-up.json");
    var backed = new PortraitFavoritePhotos(backupPath);
    Check(backed.Synchronize(new HashSet<string> { a }, _ => png), "backup case starts with a durable first photo");
    var firstGeneration = File.ReadAllBytes(backupPath);
    Check(backed.Synchronize(new HashSet<string> { a, b }, _ => png)
        && File.Exists(backupPath + ".bak") && firstGeneration.SequenceEqual(File.ReadAllBytes(backupPath + ".bak")),
        "successful photo replacement retains the exact previous valid generation immediately");
    var secondGeneration = File.ReadAllBytes(backupPath);
    Check(backed.Synchronize(new HashSet<string> { b }, _ => null)
        && secondGeneration.SequenceEqual(File.ReadAllBytes(backupPath + ".bak"))
        && Directory.GetFiles(root, "backed-up.json.bak*").Length == 1,
        "photo backups rotate one bounded generation rather than grow with every edit");
    var backupBytes = File.ReadAllBytes(backupPath + ".bak");
    File.WriteAllText(backupPath, "{damaged-current");
    var damagedCurrent = new PortraitFavoritePhotos(backupPath);
    Check(!damagedCurrent.Synchronize(new HashSet<string> { a }, _ => png)
        && File.ReadAllText(backupPath) == "{damaged-current"
        && backupBytes.SequenceEqual(File.ReadAllBytes(backupPath + ".bak"))
        && new PortraitFavoritePhotos(backupPath + ".bak").Count == 2,
        "a damaged current photo archive preserves both the original and its independently readable backup");

    var corruptPath = Path.Combine(root, "corrupt.json");
    File.WriteAllText(corruptPath, "{broken");
    var corrupt = new PortraitFavoritePhotos(corruptPath);
    Check(!corrupt.Synchronize(new HashSet<string> { a }, _ => png)
        && corrupt.Status(a) == PortraitFavoritePhotoStatus.StorageError && File.ReadAllText(corruptPath) == "{broken",
        "damaged durable archive is preserved rather than silently replaced as disposable history");
    var damaged = JsonNode.Parse(File.ReadAllText(lockedPath))!;
    damaged["Photos"]![a]!["Checksum"] = new string('0', 64);
    var checksumPath = Path.Combine(root, "checksum.json"); File.WriteAllText(checksumPath, damaged.ToJsonString());
    Check(!new PortraitFavoritePhotos(checksumPath).TryGet(a, out _), "a damaged checksum cannot upload corrupt photo bytes");
    Console.WriteLine($"PortraitFavoritePhotoTests: {checks} passed, 0 failed; no native/GPU/game calls.");
    return 0;
}
finally { Directory.Delete(root, true); }

static byte[] Png(int length = 64)
{
    var bytes = new byte[length];
    new byte[] { 137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,160,0,0,1,0,8,6 }.CopyTo(bytes, 0);
    return bytes;
}
#endif
