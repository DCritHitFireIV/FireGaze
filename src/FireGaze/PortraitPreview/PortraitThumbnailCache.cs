using System.Security.Cryptography;
using System.Text;

namespace FireGaze.PortraitPreview;

/// <summary>Disposable encoded images. History snapshots remain the sole restoration data.</summary>
internal sealed partial class PortraitThumbnailCache
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public const int MaximumImageBytes = 256 * 1024;
    private const int MaximumEntries = 4096;
    internal const int MaximumEntriesForReclaim = MaximumEntries;
    private const int HeaderBytes = 16;
    // Each entry stores a 32-byte key, 32-byte SHA-256, a 4-byte length, and
    // the payload. Keep the fixed portion aligned with the writer below so
    // shared-budget reservations match the bytes actually published.
    private const int EntryBytes = 68;
    private static readonly byte[] Magic = "FGTHUMB1"u8.ToArray();
    private readonly string path;
    private readonly object gate = new();
    private readonly PortraitStorageBudget storage;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> admissionPins = new(StringComparer.Ordinal);
    private volatile PortraitStorageCode lastStorageCode;
    private List<Entry> entries = [];
    private int availableBytes;
    private volatile string? lastError;
    private bool loadFailed;
    // Null means that the durable favorite library could not supply trusted references.
    // Preserve every completed image until that reference set becomes known.
    private HashSet<string>? protectedKeys = new(StringComparer.Ordinal);
    private HashSet<string> preferredKeys = new(StringComparer.Ordinal);
    private sealed record Entry(string Key, byte[] Image);

    public PortraitThumbnailCache(string path, int availableBytes, PortraitStorageBudget? storage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateBudget(availableBytes);
        this.path = Path.GetFullPath(path);
        this.storage = storage ?? PortraitStorageBudget.ForDirectory(Path.GetDirectoryName(this.path)!);
        this.storage.RegisterPath(this.path);
        this.availableBytes = availableBytes;
        Load();
    }

    // Published entry lists are immutable after assignment. Framework observations can read
    // the committed generation without joining a slow candidate write under gate.
    public int Count => Volatile.Read(ref entries).Count;
    public string[] Keys => Volatile.Read(ref entries).Select(entry => entry.Key).ToArray();
    public int Bytes => Size(Volatile.Read(ref entries));
    public int AvailableBytes => Volatile.Read(ref availableBytes);
    public string? LastError => lastError;
    public PortraitStorageCode LastStorageCode => lastStorageCode;

    public static string KeyForSnapshot(string snapshotJSON)
    {
        ArgumentNullException.ThrowIfNull(snapshotJSON);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("portrait-photo-v3|" + snapshotJSON)));
    }

    public bool TryGet(string key, out byte[] image)
    {
        var entry = Volatile.Read(ref entries).FirstOrDefault(entry => entry.Key == key);
        image = entry is null ? [] : (byte[])entry.Image.Clone();
        return entry is not null;
    }

    // Admission never joins a disk operation. The immutable committed generation supplies a
    // bounded rescue copy if an already executing replacement won its race with this new pin.
    public Admission ProtectAdmission(string key)
    {
        var committed = Volatile.Read(ref entries);
        admissionPins.AddOrUpdate(key, 1, (_, count) => checked(count + 1));
        // Preserve the generation observed before installing the pin. A candidate that had
        // already passed Fit may commit between those two operations and remove this key.
        var entry = committed.FirstOrDefault(entry => entry.Key == key)
            ?? Volatile.Read(ref entries).FirstOrDefault(entry => entry.Key == key);
        var image = entry is null ? [] : (byte[])entry.Image.Clone();
        return new(this, key, image);
    }

    internal sealed class Admission(PortraitThumbnailCache owner, string key, byte[] image) : IDisposable
    {
        private PortraitThumbnailCache? value = owner;
        public byte[] Image { get; } = image;
        public void Dispose()
        {
            var cache = Interlocked.Exchange(ref value, null);
            if (cache is null) return;
            while (cache.admissionPins.TryGetValue(key, out var count))
            {
                if (count == 1)
                {
                    if (cache.admissionPins.TryRemove(new KeyValuePair<string, int>(key, count))) return;
                }
                else if (cache.admissionPins.TryUpdate(key, count - 1, count)) return;
            }
        }
    }

    public bool Contains(string key)
    {
        return Volatile.Read(ref entries).Any(entry => entry.Key == key);
    }

    public bool TryPut(string key, ReadOnlySpan<byte> image)
    {
        lock (gate)
        {
            if (!ValidKey(key) || image.Length is <= 0 or > MaximumImageBytes
                || HeaderBytes + EntryBytes + image.Length > availableBytes)
            { lastStorageCode = PortraitStorageCode.Capacity; lastError = "照片容量不足或图片数据无效。"; return false; }
            if (!EnsureLoaded()) return false;
            var previous = entries.FirstOrDefault(entry => entry.Key == key);
            if (previous is not null && image.SequenceEqual(previous.Image)) return true;
            var candidate = entries.Where(entry => entry.Key != key).ToList();
            candidate.Add(new(key, image.ToArray()));
            if (!Fit(candidate, availableBytes, key))
            { lastStorageCode = PortraitStorageCode.Capacity; lastError = "照片容量不足，现有收藏照片已保留。"; return false; }
            return Publish(candidate);
        }
    }

    /// <summary>Pass the space remaining after the history file; negative budgets are invalid.</summary>
    public bool SetAvailableBytes(int value, bool requestReclaim = true)
    {
        ValidateBudget(value);
        lock (gate)
        {
            // A failed shrink must never leave the old, larger allowance available to writes.
            availableBytes = value;
            if (!EnsureLoaded()) return false;
            var candidate = entries.ToList();
            var fits = Fit(candidate, value);
            if (candidate.Count != entries.Count && !Publish(candidate, requestReclaim)) return false;
            lastError = fits ? null : "照片容量不足，尚未归档的收藏照片已保留。";
            lastStorageCode = fits ? PortraitStorageCode.Success : PortraitStorageCode.Capacity;
            return fits;
        }
    }

    public void SetProtectedKeys(IReadOnlySet<string>? keys, IReadOnlySet<string>? preferred = null)
    {
        lock (gate)
        {
            protectedKeys = keys?.ToHashSet(StringComparer.Ordinal);
            preferredKeys = preferred?.ToHashSet(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        }
    }

    /// <summary>Reclaims one oldest unpinned image, preferring ordinary photos to archived favorites.</summary>
    public bool ReclaimUnprotected()
    {
        lock (gate)
        {
            if (!EnsureLoaded()) return false;
            var remove = entries.FindIndex(entry => !IsProtected(entry.Key) && !preferredKeys.Contains(entry.Key));
            if (remove < 0) remove = entries.FindIndex(entry => !IsProtected(entry.Key));
            if (remove < 0) return true;
            var candidate = entries.ToList();
            candidate.RemoveAt(remove);
            return Publish(candidate, requestReclaim: false);
        }
    }

    public bool Trim(IReadOnlySet<string> liveKeys)
    {
        ArgumentNullException.ThrowIfNull(liveKeys);
        lock (gate)
        {
            if (!EnsureLoaded()) return false;
            var candidate = entries.Where(entry => liveKeys.Contains(entry.Key) || IsProtected(entry.Key)).ToList();
            return candidate.Count == entries.Count || Publish(candidate);
        }
    }

    private static bool ValidKey(string key) => key is { Length: 64 }
        && key.All(value => value is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void ValidateBudget(int value)
    {
        if (value is < 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static int Size(List<Entry> values) => values.Count == 0 ? 0
        : HeaderBytes + values.Sum(entry => EntryBytes + entry.Image.Length);

    private bool IsProtected(string key) => admissionPins.ContainsKey(key) || protectedKeys is null || protectedKeys.Contains(key);

    private bool Fit(List<Entry> values, int limit, string? requiredKey = null)
    {
        while (values.Count > MaximumEntries || Size(values) > limit)
        {
            var remove = values.FindIndex(entry => entry.Key != requiredKey && !IsProtected(entry.Key)
                && !preferredKeys.Contains(entry.Key));
            if (remove < 0) remove = values.FindIndex(entry => entry.Key != requiredKey && !IsProtected(entry.Key));
            if (remove < 0) return false;
            values.RemoveAt(remove);
        }
        return true;
    }

    private bool EnsureLoaded()
    {
        if (loadFailed) Load();
        return !loadFailed;
    }

    private void Load()
    {
        try
        {
            var candidate = new List<Entry>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (stream.Length is < HeaderBytes or > MaximumBytes) throw new InvalidDataException("Invalid thumbnail cache size.");
                if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic) || reader.ReadInt32() != 1)
                    throw new InvalidDataException("Unsupported thumbnail cache.");
                var count = reader.ReadInt32();
                if (count is <= 0 or > MaximumEntries || (long)count * (EntryBytes + 1) > stream.Length - HeaderBytes)
                    throw new InvalidDataException("Invalid thumbnail entry count.");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < count; index++)
                {
                    var hash = reader.ReadBytes(32);
                    if (hash.Length != 32) throw new InvalidDataException("Truncated thumbnail key.");
                    var key = Convert.ToHexString(hash);
                    var checksum = reader.ReadBytes(32);
                    var length = reader.ReadInt32();
                    if (!keys.Add(key) || length is <= 0 or > MaximumImageBytes || length > stream.Length - stream.Position)
                        throw new InvalidDataException("Invalid thumbnail payload.");
                    var image = reader.ReadBytes(length);
                    if (image.Length != length || checksum.Length != 32 || !SHA256.HashData(image).SequenceEqual(checksum))
                        throw new InvalidDataException("Damaged thumbnail payload.");
                    candidate.Add(new(key, image));
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected thumbnail data.");
            }
            // Loading precedes favorite reference discovery. Applying Fit here could erase
            // the only saved PNG before the archive has had a chance to rescue it.
            Volatile.Write(ref entries, candidate);
            loadFailed = false;
            lastError = null;
            lastStorageCode = PortraitStorageCode.Success;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            Volatile.Write(ref entries, []);
            loadFailed = false;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            lastError = error.Message;
            // This file owns images only. Never open, repair or delete the history file.
            if (!storage.TryDeleteOwned(path, out var cleanup)) lastError = cleanup.Reason;
            Volatile.Write(ref entries, []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A sharing violation or denied read does not establish corruption. Keep the
            // original charged and refuse publication until its real index can be read.
            loadFailed = true;
            lastError = error.Message;
            lastStorageCode = PortraitStorageCode.StorageError;
        }
    }

    private bool Publish(List<Entry> candidate, bool requestReclaim = true)
    {
        try
        {
            var success = candidate.Count == 0
                ? storage.TryDeleteOwned(path, out var result)
                : storage.TryPublish(path, Size(candidate), stream =>
            {
                using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
                writer.Write(Magic);
                writer.Write(1);
                writer.Write(candidate.Count);
                foreach (var entry in candidate)
                {
                    writer.Write(Convert.FromHexString(entry.Key));
                    writer.Write(SHA256.HashData(entry.Image));
                    writer.Write(entry.Image.Length);
                    writer.Write(entry.Image);
                }
                writer.Flush();
            }, keepBackup: false, out result, requestReclaim: requestReclaim);
            lastStorageCode = result.Code;
            if (!success) { lastError = result.Reason; return false; }
            Volatile.Write(ref entries, candidate);
            lastError = null;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            lastError = error.Message;
            return false;
        }
    }
}
