using System.Security.Cryptography;
using System.Text;

namespace FireGaze.PortraitPreview;

/// <summary>Disposable encoded images. History snapshots remain the sole restoration data.</summary>
internal sealed class PortraitThumbnailCache
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public const int MaximumImageBytes = 256 * 1024;
    private const int MaximumEntries = 4096;
    private const int HeaderBytes = 16;
    private const int EntryBytes = 68;
    private static readonly byte[] Magic = "FGTHUMB1"u8.ToArray();
    private readonly string path;
    private readonly object gate = new();
    private List<Entry> entries = [];
    private int availableBytes;
    private string? lastError;
    private sealed record Entry(string Key, byte[] Image);

    public PortraitThumbnailCache(string path, int availableBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateBudget(availableBytes);
        this.path = Path.GetFullPath(path);
        this.availableBytes = availableBytes;
        Load();
    }

    public int Count { get { lock (gate) return entries.Count; } }
    public string[] Keys { get { lock (gate) return entries.Select(entry => entry.Key).ToArray(); } }
    public int Bytes { get { lock (gate) return Size(entries); } }
    public int AvailableBytes { get { lock (gate) return availableBytes; } }
    public string? LastError { get { lock (gate) return lastError; } }

    public static string KeyForSnapshot(string snapshotJSON)
    {
        ArgumentNullException.ThrowIfNull(snapshotJSON);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("portrait-photo-v3|" + snapshotJSON)));
    }

    public bool TryGet(string key, out byte[] image)
    {
        lock (gate)
        {
            var entry = entries.FirstOrDefault(entry => entry.Key == key);
            image = entry is null ? [] : (byte[])entry.Image.Clone();
            return entry is not null;
        }
    }

    public bool TryPut(string key, ReadOnlySpan<byte> image)
    {
        lock (gate)
        {
            if (!ValidKey(key) || image.Length is <= 0 or > MaximumImageBytes
                || HeaderBytes + EntryBytes + image.Length > availableBytes) return false;
            var previous = entries.FirstOrDefault(entry => entry.Key == key);
            if (previous is not null && image.SequenceEqual(previous.Image)) return true;
            var candidate = entries.Where(entry => entry.Key != key).ToList();
            candidate.Add(new(key, image.ToArray()));
            Fit(candidate, availableBytes);
            return Publish(candidate);
        }
    }

    /// <summary>Pass the space remaining after the history file; negative budgets are invalid.</summary>
    public bool SetAvailableBytes(int value)
    {
        ValidateBudget(value);
        lock (gate)
        {
            var candidate = entries.ToList();
            Fit(candidate, value);
            if (candidate.Count != entries.Count && !Publish(candidate)) return false;
            availableBytes = value;
            return true;
        }
    }

    public bool Trim(IReadOnlySet<string> liveKeys)
    {
        ArgumentNullException.ThrowIfNull(liveKeys);
        lock (gate)
        {
            var candidate = entries.Where(entry => liveKeys.Contains(entry.Key)).ToList();
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

    private static void Fit(List<Entry> values, int limit)
    {
        while (values.Count > MaximumEntries || Size(values) > limit) values.RemoveAt(0);
    }

    private void Load()
    {
        if (!File.Exists(path)) return;
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
            var originalCount = candidate.Count;
            Fit(candidate, availableBytes);
            if (candidate.Count == originalCount) entries = candidate;
            else Publish(candidate);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            lastError = error.Message;
            // This file owns images only. Never open, repair or delete the history file.
            try { File.Delete(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { lastError = cleanup.Message; }
            entries = [];
        }
    }

    private bool Publish(List<Entry> candidate)
    {
        string? temporary = null;
        try
        {
            if (candidate.Count == 0) File.Delete(path);
            else
            {
                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    {
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
                    }
                    if (stream.Length != Size(candidate)) throw new InvalidDataException("Thumbnail size accounting mismatch.");
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                temporary = null;
            }
            entries = candidate;
            lastError = null;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            lastError = error.Message;
            return false;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { lastError = cleanup.Message; }
        }
    }
}
