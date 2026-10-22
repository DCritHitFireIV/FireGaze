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
    PortraitFavoriteEntry? Entry = null, PortraitStorageCode? StorageCode = null)
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

    public PortraitFavoritesStore(string filePath, Func<DateTimeOffset>? clock = null, IPortraitFavoritesFiles? files = null,
        PortraitStorageBudget? storage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        path = Path.GetFullPath(filePath);
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.files = files ?? new PortraitFavoritesFiles(storage ?? PortraitStorageBudget.ForDirectory(Path.GetDirectoryName(path)!));
        if (this.files is PortraitFavoritesFiles productionFiles) productionFiles.Register(path);
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

    /// <summary>Restores a complete favorite with its original stable ID and timestamps.</summary>
    public PortraitFavoritesWriteResult AddExisting(PortraitFavoriteEntry entry)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            if (!ValidEntry(entry)) return Invalid();
            var existing = entries.FirstOrDefault(value => value.ID == entry.ID);
            if (existing is not null)
                return SameEntry(existing, entry) ? new(PortraitFavoritesWriteStatus.Unchanged, Entry: existing.Copy())
                    : new(PortraitFavoritesWriteStatus.Conflict, "同一收藏已有不同内容，请保留原件后重试。");
            return Persist(entries.Append(entry.Copy()).ToList(), entry);
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

    public PortraitFavoritesWriteResult UpdateMetadata(Guid id, string name, IEnumerable<string>? tags)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            if (!TryName(name, out var normalizedName) || !TryTags(tags, out var normalizedTags)) return Invalid();
            var index = entries.FindIndex(entry => entry.ID == id);
            if (index < 0) return Missing();
            var previous = entries[index];
            if (previous.Name == normalizedName && previous.Tags.SequenceEqual(normalizedTags))
                return new(PortraitFavoritesWriteStatus.Unchanged, Entry: previous.Copy());
            var updated = previous with { Name = normalizedName, Tags = normalizedTags, UpdatedAt = EditTime(previous) };
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
        catch (PortraitFavoritesStorageException exception)
        {
            PersistenceError = exception.Result.Reason;
            return new(exception.Result.Code == PortraitStorageCode.Capacity
                ? PortraitFavoritesWriteStatus.TooLarge : PortraitFavoritesWriteStatus.PersistenceFailed,
                exception.Result.Reason, StorageCode: exception.Result.Code);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            PersistenceError = exception.Message;
            return new(PortraitFavoritesWriteStatus.PersistenceFailed, "收藏未能保存，请检查本地存储后重试。",
                StorageCode: PortraitStorageCode.StorageError);
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

    internal static bool ValidEntry(PortraitFavoriteEntry? entry) => entry is not null && entry.ID != Guid.Empty
        && entry.CreatedAt >= DateTimeOffset.UnixEpoch && entry.UpdatedAt >= entry.CreatedAt
        && TryName(entry.Name, out var name) && name == entry.Name
        && TrySourceName(entry.SourceName, out var source) && source == entry.SourceName
        && entry.Tags is not null && TryTags(entry.Tags, out var tags) && entry.Tags.SequenceEqual(tags)
        && ValidSnapshot(entry.Snapshot);

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
                if (!ValidEntry(entry) || !ids.Add(entry.ID)) return false;
                loaded.Add(entry.Copy());
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        { loaded = []; return false; }
    }

    internal static bool SameEntry(PortraitFavoriteEntry a, PortraitFavoriteEntry b) => a.ID == b.ID
        && a.CreatedAt == b.CreatedAt && a.UpdatedAt == b.UpdatedAt && a.SourceContentID == b.SourceContentID
        && a.SourceName == b.SourceName && a.Name == b.Name && a.Tags.SequenceEqual(b.Tags)
        && a.Snapshot.ToJSON() == b.Snapshot.ToJSON();

    private sealed record SavedDocument(int Version, List<PortraitFavoriteEntry> Entries);
}

internal sealed record PortraitFavoriteTrashEntry(Guid ID, DateTimeOffset DeletedAt, PortraitFavoriteEntry Favorite)
{
    public PortraitFavoriteEntry CopyFavorite() => Favorite.Copy();
}

/// <summary>Recoverable delete store for favorites. Trash is durable and shares the portrait budget.</summary>
internal sealed class PortraitFavoritesTrashStore
{
    public const int MaximumBytes = PortraitFavoritesStore.MaximumBytes;
    public const int MaximumEntries = PortraitFavoritesStore.MaximumEntries;
    private static readonly JsonSerializerOptions JSONOptions = new() { MaxDepth = 32 };
    private readonly object gate = new();
    private readonly string path;
    private readonly Func<DateTimeOffset> clock;
    private readonly IPortraitFavoritesFiles files;
    private List<PortraitFavoriteTrashEntry> entries = [];
    public bool IsReadOnly { get; private set; }
    public string? LoadError { get; private set; }
    public string? PersistenceError { get; private set; }
    public IReadOnlyList<PortraitFavoriteEntry> Entries
    {
        get { lock (gate) return entries.OrderByDescending(entry => entry.DeletedAt).Select(entry => entry.CopyFavorite()).ToArray(); }
    }
    public IReadOnlyList<PortraitFavoriteTrashEntry> Records
    {
        get { lock (gate) return entries.Select(entry => entry with { Favorite = entry.Favorite.Copy() }).ToArray(); }
    }

    public PortraitFavoritesTrashStore(string filePath, Func<DateTimeOffset>? clock = null,
        IPortraitFavoritesFiles? files = null, PortraitStorageBudget? storage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        path = Path.GetFullPath(filePath);
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.files = files ?? new PortraitFavoritesFiles(storage ?? PortraitStorageBudget.ForDirectory(Path.GetDirectoryName(path)!));
        if (this.files is PortraitFavoritesFiles productionFiles) productionFiles.Register(path);
        try
        {
            var bytes = this.files.Read(path, MaximumBytes);
            if (bytes is null) return;
            if (!TryRead(bytes, out var loaded)) throw new InvalidDataException("回收站文件无法读取，原文件已保留。");
            entries = loaded;
        }
        catch (Exception exception) when (IsStorageException(exception) || exception is JsonException)
        { IsReadOnly = true; LoadError = exception.Message; }
    }

    public PortraitFavoritesWriteResult Add(PortraitFavoriteEntry favorite)
    {
        lock (gate)
        {
            if (IsReadOnly) return new(PortraitFavoritesWriteStatus.ReadOnlyCorrupt, "回收站暂时无法读取，原文件已保留。");
            if (!PortraitFavoritesStore.ValidEntry(favorite)) return new(PortraitFavoritesWriteStatus.InvalidInput, "收藏内容无效。");
            var existing = entries.FirstOrDefault(value => value.ID == favorite.ID);
            if (existing is not null) return PortraitFavoritesStore.SameEntry(existing.Favorite, favorite)
                ? new(PortraitFavoritesWriteStatus.Unchanged, Entry: existing.CopyFavorite())
                : new(PortraitFavoritesWriteStatus.Conflict, "回收站中已有内容不同的同名记录，原件已保留。");
            if (entries.Count >= MaximumEntries) return new(PortraitFavoritesWriteStatus.TooLarge, "回收站容量已满，请先清理已删除收藏。");
            var now = clock();
            if (now < DateTimeOffset.UnixEpoch) return new(PortraitFavoritesWriteStatus.InvalidInput, "删除时间无效。");
            var candidate = entries.Append(new PortraitFavoriteTrashEntry(favorite.ID, now, favorite.Copy())).ToList();
            return Persist(candidate, favorite);
        }
    }

    public PortraitFavoritesWriteResult Remove(Guid id)
    {
        lock (gate)
        {
            if (IsReadOnly) return new(PortraitFavoritesWriteStatus.ReadOnlyCorrupt, "回收站暂时无法读取，原文件已保留。");
            var candidate = entries.Where(entry => entry.ID != id).ToList();
            if (candidate.Count == entries.Count) return new(PortraitFavoritesWriteStatus.NotFound, "这条回收站记录已不存在。");
            return Persist(candidate);
        }
    }

    public PortraitFavoriteEntry? Find(Guid id)
    {
        lock (gate) return entries.FirstOrDefault(entry => entry.ID == id)?.CopyFavorite();
    }

    private PortraitFavoritesWriteResult Persist(List<PortraitFavoriteTrashEntry> candidate, PortraitFavoriteEntry? entry = null)
    {
        if (candidate.Count > MaximumEntries) return new(PortraitFavoritesWriteStatus.TooLarge, "回收站容量已满，请先清理已删除收藏。");
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedDocument(1, candidate), JSONOptions); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        { return new(PortraitFavoritesWriteStatus.InvalidInput, "回收站内容无效。"); }
        if (bytes.Length > MaximumBytes) return new(PortraitFavoritesWriteStatus.TooLarge, "回收站容量已满，请先清理已删除收藏。");
        try { files.WriteAtomically(path, bytes); }
        catch (PortraitFavoritesStorageException exception)
        {
            PersistenceError = exception.Result.Reason;
            return new(exception.Result.Code == PortraitStorageCode.Capacity ? PortraitFavoritesWriteStatus.TooLarge : PortraitFavoritesWriteStatus.PersistenceFailed,
                exception.Result.Reason, StorageCode: exception.Result.Code);
        }
        catch (Exception exception) when (IsStorageException(exception))
        { PersistenceError = exception.Message; return new(PortraitFavoritesWriteStatus.PersistenceFailed, "回收站未能保存，请检查本地存储后重试。", StorageCode: PortraitStorageCode.StorageError); }
        entries = candidate;
        PersistenceError = null;
        return new(PortraitFavoritesWriteStatus.Saved, Entry: entry?.Copy());
    }

    private static bool TryRead(byte[] bytes, out List<PortraitFavoriteTrashEntry> loaded)
    {
        loaded = [];
        if (bytes.Length > MaximumBytes) return false;
        try
        {
            var document = JsonSerializer.Deserialize<SavedDocument>(bytes, JSONOptions);
            if (document?.Version != 1 || document.Entries is null || document.Entries.Count > MaximumEntries) return false;
            var ids = new HashSet<Guid>();
            foreach (var record in document.Entries)
            {
                if (record is null || record.ID == Guid.Empty || !ids.Add(record.ID) || record.DeletedAt < DateTimeOffset.UnixEpoch
                    || !PortraitFavoritesStore.ValidEntry(record.Favorite) || record.Favorite.ID != record.ID)
                    return false;
                loaded.Add(record with { Favorite = record.Favorite.Copy() });
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        { loaded = []; return false; }
    }
    private static bool IsStorageException(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;
    private sealed record SavedDocument(int Version, List<PortraitFavoriteTrashEntry> Entries);
}

internal interface IPortraitFavoritesFiles
{
    byte[]? Read(string path, int maximumBytes);
    void WriteAtomically(string path, byte[] document);
}

internal sealed class PortraitFavoritesFiles : IPortraitFavoritesFiles
{
    private readonly PortraitStorageBudget storage;

    public PortraitFavoritesFiles(PortraitStorageBudget storage) => this.storage = storage;
    public void Register(string path) => storage.RegisterPath(path);

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
        if (!storage.TryPublish(path, document.Length, stream => stream.Write(document), keepBackup: true, out var result))
            throw new PortraitFavoritesStorageException(result);
    }
}

internal sealed class PortraitFavoritesStorageException(PortraitStorageResult result) : IOException(result.Reason)
{
    public PortraitStorageResult Result { get; } = result;
}
