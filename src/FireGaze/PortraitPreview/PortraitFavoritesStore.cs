using System.Text;
using System.Text.Json;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitFavoriteEntry(Guid ID, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ulong SourceContentID, string SourceName, string Name, string[] Tags, PortraitEditorSnapshot Snapshot)
{
    public PortraitFavoriteEntry Copy() => this with { Tags = (string[])Tags.Clone(), Snapshot = Snapshot.Copy() };
}

internal enum PortraitFavoritesWriteStatus
{
    Saved, Unchanged, InvalidInput, NotFound, ReadOnlyCorrupt, TooLarge, Conflict, PersistenceFailed,
}

internal sealed record PortraitFavoritesWriteResult(PortraitFavoritesWriteStatus Status, string Reason = "",
    PortraitFavoriteEntry? Entry = null)
{
    public bool Success => Status is PortraitFavoritesWriteStatus.Saved or PortraitFavoritesWriteStatus.Unchanged;
}

/// <summary>All characters use one library path. Source identity is provenance, never an access restriction.</summary>
internal sealed class PortraitFavoritesStore
{
    // Favorites are intentional saves and never participate in the history retention/eviction policy.
    public const int MaximumBytes = 64 * 1024 * 1024;
    public const int MaximumEntries = 5000;
    public const int MaximumNameLength = 120;
    public const int MaximumTagLength = 32;
    public const int MaximumTags = 32;
    private const int MaximumSnapshotBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JSONOptions = new() { MaxDepth = 32 };
    private readonly object gate = new();
    private readonly string path;
    private readonly Func<DateTimeOffset> clock;
    private readonly IPortraitFavoritesFiles files;
    private List<PortraitFavoriteEntry> entries = [];
    public bool IsReadOnly { get; private set; }
    public string? LoadError { get; private set; }
    public string? PersistenceError { get; private set; }
    public IReadOnlyList<PortraitFavoriteEntry> Entries
    {
        get { lock (gate) return entries.OrderByDescending(entry => entry.UpdatedAt).Select(entry => entry.Copy()).ToArray(); }
    }

    public PortraitFavoritesStore(string filePath, Func<DateTimeOffset>? clock = null, IPortraitFavoritesFiles? files = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        path = Path.GetFullPath(filePath);
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.files = files ?? new PortraitFavoritesFiles();
        try
        {
            var bytes = this.files.Read(path, MaximumBytes);
            if (bytes is null) return;
            if (bytes.Length > MaximumBytes || !TryReadDocument(bytes, out var loaded, out _))
                throw new InvalidDataException("收藏文件无法读取，已保留原文件。" );
            entries = loaded;
        }
        catch (Exception exception) when (IsStorageException(exception) || exception is JsonException)
        {
            IsReadOnly = true;
            LoadError = exception.Message;
        }
    }

    public PortraitFavoritesWriteResult Add(PortraitEditorSnapshot snapshot, ulong sourceContentID, string sourceName,
        string name, IEnumerable<string>? tags = null)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            if (!TryName(name, out var normalizedName) || !TrySourceName(sourceName, out var normalizedSource)
                || !TryTags(tags, out var normalizedTags) || !ValidSnapshot(snapshot))
                return Invalid();
            var now = clock();
            if (now < DateTimeOffset.UnixEpoch) return Invalid();
            var added = new PortraitFavoriteEntry(Guid.NewGuid(), now, now, sourceContentID, normalizedSource,
                normalizedName, normalizedTags, snapshot.Copy());
            return Persist(entries.Append(added).ToList(), added);
        }
    }

    public PortraitFavoritesWriteResult Rename(Guid id, string name)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            if (!TryName(name, out var normalized)) return Invalid();
            var index = entries.FindIndex(entry => entry.ID == id);
            if (index < 0) return Missing();
            var previous = entries[index];
            if (previous.Name == normalized) return new(PortraitFavoritesWriteStatus.Unchanged, Entry: previous.Copy());
            var updated = previous with { Name = normalized, UpdatedAt = EditTime(previous) };
            var candidate = entries.ToList(); candidate[index] = updated;
            return Persist(candidate, updated);
        }
    }

    public PortraitFavoritesWriteResult SetTags(Guid id, IEnumerable<string>? tags)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            if (!TryTags(tags, out var normalized)) return Invalid();
            var index = entries.FindIndex(entry => entry.ID == id);
            if (index < 0) return Missing();
            var previous = entries[index];
            if (previous.Tags.SequenceEqual(normalized)) return new(PortraitFavoritesWriteStatus.Unchanged, Entry: previous.Copy());
            var updated = previous with { Tags = normalized, UpdatedAt = EditTime(previous) };
            var candidate = entries.ToList(); candidate[index] = updated;
            return Persist(candidate, updated);
        }
    }

    public PortraitFavoritesWriteResult Remove(Guid id)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            var candidate = entries.Where(entry => entry.ID != id).ToList();
            if (candidate.Count == entries.Count) return Missing();
            return Persist(candidate);
        }
    }

    /// <summary>Exports detached values, never a process pointer or a dependency on an evictable history ID.</summary>
    public string Export(IEnumerable<Guid>? ids = null)
    {
        lock (gate)
        {
            var requested = ids?.Take(MaximumEntries + 1).ToHashSet();
            var selected = requested is null ? entries : entries.Where(entry => requested.Contains(entry.ID)).ToList();
            return JsonSerializer.Serialize(new SavedDocument(1, selected), JSONOptions);
        }
    }

    public PortraitFavoritesWriteResult Import(string json)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            if (string.IsNullOrWhiteSpace(json)) return Invalid();
            if (json.Length > MaximumBytes || Encoding.UTF8.GetByteCount(json) > MaximumBytes) return TooLarge();
            if (!TryReadDocument(Encoding.UTF8.GetBytes(json), out var imported, out var error))
                return new(PortraitFavoritesWriteStatus.InvalidInput, error);
            var candidate = entries.ToList();
            foreach (var entry in imported)
            {
                var previous = entries.FirstOrDefault(value => value.ID == entry.ID);
                if (previous is not null)
                {
                    if (!SameEntry(previous, entry))
                        return new(PortraitFavoritesWriteStatus.Conflict, "同一收藏已有不同内容，请保留原件后再导入。" );
                    continue;
                }
                candidate.Add(entry);
            }
            return candidate.Count == entries.Count ? new(PortraitFavoritesWriteStatus.Unchanged) : Persist(candidate);
        }
    }

    private DateTimeOffset EditTime(PortraitFavoriteEntry previous)
    {
        var now = clock();
        return now < previous.UpdatedAt ? previous.UpdatedAt : now;
    }

    private PortraitFavoritesWriteResult Persist(List<PortraitFavoriteEntry> candidate, PortraitFavoriteEntry? entry = null)
    {
        if (candidate.Count > MaximumEntries) return TooLarge();
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedDocument(1, candidate), JSONOptions); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        { return Invalid(); }
        if (bytes.Length > MaximumBytes) return TooLarge();
        try { files.WriteAtomically(path, bytes); }
        catch (Exception exception) when (IsStorageException(exception))
        {
            PersistenceError = exception.Message;
            return new(PortraitFavoritesWriteStatus.PersistenceFailed, "收藏未能保存，请检查本地存储后重试。" );
        }
        // Publish the candidate only after the complete replacement was flushed to stable storage.
        entries = candidate;
        PersistenceError = null;
        return new(PortraitFavoritesWriteStatus.Saved, Entry: entry?.Copy());
    }

    private PortraitFavoritesWriteResult? Unavailable() => IsReadOnly
        ? new(PortraitFavoritesWriteStatus.ReadOnlyCorrupt, "收藏文件暂时无法读取，已保留原文件。" ) : null;
    private static PortraitFavoritesWriteResult Invalid() => new(PortraitFavoritesWriteStatus.InvalidInput, "收藏内容无效，请检查名称、标签和肖像。" );
    private static PortraitFavoritesWriteResult Missing() => new(PortraitFavoritesWriteStatus.NotFound, "这条收藏已不存在。" );
    private static PortraitFavoritesWriteResult TooLarge() => new(PortraitFavoritesWriteStatus.TooLarge, "收藏容量已满，请先导出或移除部分收藏。" );
    private static bool IsStorageException(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;

    private static bool TryName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? "";
        return normalized.Length is > 0 and <= MaximumNameLength && !normalized.Any(char.IsControl);
    }

    private static bool TrySourceName(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? "";
        return value is not null && normalized.Length <= MaximumNameLength && !normalized.Any(char.IsControl);
    }

    private static bool TryTags(IEnumerable<string>? values, out string[] normalized)
    {
        var result = new List<string>();
        if (values is not null)
        {
            var count = 0;
            foreach (var tag in values)
            {
                // Bound enumeration even for a repeated or untrusted enumerable.
                if (++count > MaximumTags || tag is null) { normalized = []; return false; }
                var value = tag.Trim();
                if (value.Length == 0) continue;
                if (value.Length > MaximumTagLength || value.Any(char.IsControl)) { normalized = []; return false; }
                if (!result.Contains(value, StringComparer.OrdinalIgnoreCase)) result.Add(value);
            }
        }
        normalized = result.ToArray();
        return true;
    }

    private static bool ValidSnapshot(PortraitEditorSnapshot? snapshot)
    {
        // AgentBannerEditorState has three native entry points: portrait, gearset, and
        // adventurer plate. Favorites keep the source entry point as provenance, while
        // the feature rebinds it to the currently open editor during a restore.
        if (snapshot?.IsValid != true || snapshot.EntryPoint > 2) return false;
        try { return JsonSerializer.SerializeToUtf8Bytes(snapshot, JSONOptions).Length <= MaximumSnapshotBytes; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException) { return false; }
    }

    private static bool TryReadDocument(byte[] bytes, out List<PortraitFavoriteEntry> loaded, out string error)
    {
        loaded = [];
        error = "收藏内容无效，未导入任何内容。";
        if (bytes.Length > MaximumBytes) return false;
        try
        {
            var document = JsonSerializer.Deserialize<SavedDocument>(bytes, JSONOptions);
            if (document?.Version != 1 || document.Entries is null || document.Entries.Count > MaximumEntries) return false;
            var ids = new HashSet<Guid>();
            foreach (var entry in document.Entries)
            {
                if (entry is null || entry.ID == Guid.Empty || !ids.Add(entry.ID)
                    || entry.CreatedAt < DateTimeOffset.UnixEpoch || entry.UpdatedAt < entry.CreatedAt
                    || !TryName(entry.Name, out var name) || name != entry.Name
                    || !TrySourceName(entry.SourceName, out var sourceName) || sourceName != entry.SourceName
                    || entry.Tags is null || !TryTags(entry.Tags, out var tags) || !entry.Tags.SequenceEqual(tags)
                    || !ValidSnapshot(entry.Snapshot)) return false;
                loaded.Add(entry.Copy());
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        { loaded = []; return false; }
    }

    private static bool SameEntry(PortraitFavoriteEntry a, PortraitFavoriteEntry b) => a.ID == b.ID
        && a.CreatedAt == b.CreatedAt && a.UpdatedAt == b.UpdatedAt && a.SourceContentID == b.SourceContentID
        && a.SourceName == b.SourceName && a.Name == b.Name && a.Tags.SequenceEqual(b.Tags)
        && a.Snapshot.ToJSON() == b.Snapshot.ToJSON();

    private sealed record SavedDocument(int Version, List<PortraitFavoriteEntry> Entries);
}

internal interface IPortraitFavoritesFiles
{
    byte[]? Read(string path, int maximumBytes);
    void WriteAtomically(string path, byte[] document);
}

internal sealed class PortraitFavoritesFiles : IPortraitFavoritesFiles
{
    public byte[]? Read(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes) throw new InvalidDataException("收藏文件超过可读取的容量。" );
            var buffer = new byte[checked((int)stream.Length)];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void WriteAtomically(string path, byte[] document)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                stream.Write(document);
                stream.Flush(flushToDisk: true);
            }
            // Keep the previous complete document even when the game exits immediately after
            // this edit. File.Replace rotates the single backup with the main publication.
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }
}
