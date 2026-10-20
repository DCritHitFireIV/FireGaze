using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FireGaze.PortraitPreview;

internal enum PortraitHistoryEntryPoint { GearsetPortrait, AdventurerPlate }
internal readonly record struct PortraitHistoryKey(ulong ContentID, PortraitHistoryEntryPoint EntryPoint,
    int SourceID, string CompatToken)
{
    public bool IsValid => ContentID != 0 && Enum.IsDefined(EntryPoint) && SourceID >= 0
        && !string.IsNullOrWhiteSpace(CompatToken) && CompatToken.Length <= 512;
}

internal enum PortraitHistoryRecordKind { Edit, Restore, Undo, Redo }
internal enum PortraitHistoryWriteStatus { Added, Duplicate, InvalidKey, InvalidJson, TooLarge, ReadOnlyCorrupt, StaleSelection, Disposed }
internal sealed record PortraitHistoryRecord(long ID, PortraitHistoryKey Key, DateTimeOffset Timestamp,
    long? ParentID, PortraitHistoryRecordKind Kind, string SnapshotJSON, long? TargetID = null, string Label = "编辑肖像");
/// <summary>
/// Added means admission into the immutable write queue. For committed edits, Persistence
/// reports success only after the corresponding document (or a later complete history state)
/// has been atomically published, and false if that writer attempt fails. Preparation
/// results do not submit writes and have no boundary.
/// </summary>
internal sealed record PortraitHistoryWriteResult(PortraitHistoryWriteStatus Status,
    PortraitHistoryRecord? Record = null, Task<bool>? Persistence = null);
internal sealed record PortraitHistoryDay(DateOnly Date, IReadOnlyList<PortraitHistoryRecord> Records);
internal enum PortraitHistorySelectionKind { Undo, Redo, Restore }
internal sealed record PortraitHistorySelection(PortraitHistoryKey Key, PortraitHistorySelectionKind Kind,
    long ExpectedCursorID, long ExpectedRevision, PortraitHistoryRecord Record);

/// <summary>
/// Portable history of detached JSON snapshots. Keys contain stable game identities, never process addresses.
/// Snapshot producers must likewise supply data values, not native structures or pointers. Navigation is
/// two phase: obtain a selection, apply it to the native preview, then commit only after successful apply.
/// </summary>
internal sealed class PortraitHistoryStore : IDisposable
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> clock;
    private readonly int byteLimit;
    private readonly PortraitHistoryWriter? writer;
    private List<PortraitHistoryRecord> records = [];
    // Fragments are immutable once encoded and are shared safely with queued worker snapshots.
    private Dictionary<long, byte[]> encodedRecords = [];
    private Dictionary<PortraitHistoryKey, Cursor> cursors = [];
    private long nextID = 1;
    private long revision;
    private int serializedBytes;
    private bool disposed;
    private Task<bool>? disposeTask;
    public bool IsReadOnly { get; private set; }
    public string? LoadError { get; private set; }
    public string? PersistenceError => writer?.LastError;
    public int Count { get { lock (gate) return records.Count; } }
    public int Bytes { get { lock (gate) return serializedBytes; } }

    public PortraitHistoryStore(string filePath, Func<DateTimeOffset>? clock = null, int byteLimit = MaximumBytes,
        IPortraitHistoryMigrationFiles? migrationFiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (byteLimit is <= 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.byteLimit = byteLimit;
        var path = Path.GetFullPath(filePath);
        Load(path, migrationFiles ?? new PortraitHistoryMigrationFiles());
        if (!IsReadOnly) writer = new(path, byteLimit);
    }

    public IReadOnlyList<PortraitHistoryRecord> GetRecords(PortraitHistoryKey key)
    {
        lock (gate) return records.Where(record => record.Key == key).Reverse().ToArray();
    }

    public IReadOnlyList<PortraitHistoryRecord> GetRecordsForOwner(ulong contentID)
    {
        lock (gate) return records.Where(record => record.Key.ContentID == contentID).Reverse().ToArray();
    }

    public long? CurrentRecordID(PortraitHistoryKey key)
    {
        lock (gate) return cursors.GetValueOrDefault(key)?.ID;
    }

    public IReadOnlyList<PortraitHistoryDay> GetDayGroups(PortraitHistoryKey key) => GetRecords(key)
        .GroupBy(record => DateOnly.FromDateTime(record.Timestamp.Date))
        .OrderByDescending(group => group.Key).Select(group => new PortraitHistoryDay(group.Key, group.ToArray())).ToArray();

    /// <summary>Validate quota and the complete next history document before changing a native preview.</summary>
    public PortraitHistoryWriteResult PrepareRecord(PortraitHistoryKey key, string snapshotJSON, string label = "编辑肖像")
        => RecordCore(key, snapshotJSON, label, commit: false);

    public PortraitHistoryWriteResult Record(PortraitHistoryKey key, string snapshotJSON, string label = "编辑肖像")
        => RecordCore(key, snapshotJSON, label, commit: true);

    private PortraitHistoryWriteResult RecordCore(PortraitHistoryKey key, string snapshotJSON, string label, bool commit)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            if (!key.IsValid) return new(PortraitHistoryWriteStatus.InvalidKey);
            if (!TryNormalize(snapshotJSON, out var normalized)) return new(PortraitHistoryWriteStatus.InvalidJson);
            var cursor = cursors.GetValueOrDefault(key);
            var current = cursor is null ? null : Find(cursor.ID);
            if (current?.SnapshotJSON == normalized) return new(PortraitHistoryWriteStatus.Duplicate, current);
            var added = new PortraitHistoryRecord(nextID, key, clock(), current?.ID, PortraitHistoryRecordKind.Edit, normalized, Label: NormalizeLabel(label, "编辑肖像"));
            var nextCursors = CloneCursors();
            nextCursors[key] = new(added.ID, []);
            return Accept(added, nextCursors, commit);
        }
    }

    public bool TryGetUndo(PortraitHistoryKey key, out PortraitHistorySelection selection)
    {
        lock (gate)
        {
            selection = null!;
            if (disposed || IsReadOnly || !cursors.TryGetValue(key, out var cursor)
                || Find(cursor.ID)?.ParentID is not { } parent || Find(parent) is not { } record) return false;
            selection = new(key, PortraitHistorySelectionKind.Undo, cursor.ID, revision, record);
            return true;
        }
    }

    public bool TryGetRedo(PortraitHistoryKey key, out PortraitHistorySelection selection)
    {
        lock (gate)
        {
            selection = null!;
            if (disposed || IsReadOnly || !cursors.TryGetValue(key, out var cursor) || cursor.Redo.Count == 0
                || Find(cursor.Redo[^1]) is not { } record) return false;
            selection = new(key, PortraitHistorySelectionKind.Redo, cursor.ID, revision, record);
            return true;
        }
    }

    public bool TryGetRecord(PortraitHistoryKey key, long id, out PortraitHistorySelection selection)
    {
        lock (gate)
        {
            selection = null!;
            if (disposed || IsReadOnly || !cursors.TryGetValue(key, out var cursor)
                || Find(id) is not { } record || record.Key != key) return false;
            selection = new(key, PortraitHistorySelectionKind.Restore, cursor.ID, revision, record);
            return true;
        }
    }

    public PortraitHistoryWriteResult CommitNavigation(PortraitHistorySelection selection, string? label = null)
        => CommitNavigationCore(selection, label, commit: true);

    public PortraitHistoryWriteResult PrepareCommit(PortraitHistorySelection selection) => selection.Kind == PortraitHistorySelectionKind.Restore
        ? CommitRestoreCore(selection, "恢复历史", commit: false) : CommitNavigationCore(selection, null, commit: false);

    private PortraitHistoryWriteResult CommitNavigationCore(PortraitHistorySelection selection, string? label, bool commit)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            if (!IsValidSelection(selection) || selection.Kind == PortraitHistorySelectionKind.Restore)
                return new(PortraitHistoryWriteStatus.StaleSelection);
            var cursor = cursors[selection.Key];
            var redo = cursor.Redo.ToList();
            PortraitHistoryRecordKind kind;
            if (selection.Kind == PortraitHistorySelectionKind.Undo)
            {
                if (Find(cursor.ID)?.ParentID != selection.Record.ID) return new(PortraitHistoryWriteStatus.StaleSelection);
                redo.Add(cursor.ID);
                kind = PortraitHistoryRecordKind.Undo;
            }
            else
            {
                if (redo.Count == 0 || redo[^1] != selection.Record.ID) return new(PortraitHistoryWriteStatus.StaleSelection);
                redo.RemoveAt(redo.Count - 1);
                kind = PortraitHistoryRecordKind.Redo;
            }
            var nextCursors = CloneCursors();
            nextCursors[selection.Key] = new(selection.Record.ID, redo);
            return Accept(new(nextID, selection.Key, clock(), cursor.ID, kind, selection.Record.SnapshotJSON, selection.Record.ID,
                NormalizeLabel(label, kind == PortraitHistoryRecordKind.Undo ? "撤销" : "重做")), nextCursors, commit);
        }
    }

    public PortraitHistoryWriteResult CommitRestore(PortraitHistorySelection selection, string label = "恢复历史")
        => CommitRestoreCore(selection, label, commit: true);

    private PortraitHistoryWriteResult CommitRestoreCore(PortraitHistorySelection selection, string label, bool commit)
    {
        lock (gate)
        {
            if (Unavailable() is { } unavailable) return unavailable;
            if (!IsValidSelection(selection) || selection.Kind != PortraitHistorySelectionKind.Restore)
                return new(PortraitHistoryWriteStatus.StaleSelection);
            var cursor = cursors[selection.Key];
            if (cursor.ID == selection.Record.ID)
                return new(PortraitHistoryWriteStatus.Duplicate, Find(cursor.ID));
            var record = new PortraitHistoryRecord(nextID, selection.Key, clock(), cursor.ID,
                PortraitHistoryRecordKind.Restore, selection.Record.SnapshotJSON, selection.Record.ID, NormalizeLabel(label, "恢复历史"));
            var nextCursors = CloneCursors();
            nextCursors[selection.Key] = new(record.ID, []);
            return Accept(record, nextCursors, commit);
        }
    }

    private bool IsValidSelection(PortraitHistorySelection? selection) => selection is not null
        && Enum.IsDefined(selection.Kind) && selection.ExpectedRevision == revision
        && cursors.TryGetValue(selection.Key, out var cursor) && cursor.ID == selection.ExpectedCursorID
        && Find(selection.Record.ID) == selection.Record && selection.Record.Key == selection.Key;

    private PortraitHistoryWriteResult? Unavailable() => disposed ? new(PortraitHistoryWriteStatus.Disposed)
        : IsReadOnly ? new(PortraitHistoryWriteStatus.ReadOnlyCorrupt) : null;

    private PortraitHistoryRecord? Find(long id) => records.FirstOrDefault(record => record.ID == id);
    private static string NormalizeLabel(string? label, string fallback) => string.IsNullOrWhiteSpace(label)
        ? fallback : label.Trim()[..Math.Min(label.Trim().Length, 120)];
    private Dictionary<PortraitHistoryKey, Cursor> CloneCursors() => cursors.ToDictionary(pair => pair.Key, pair => new Cursor(pair.Value.ID, pair.Value.Redo.ToList()));

    private PortraitHistoryWriteResult Accept(PortraitHistoryRecord added, Dictionary<PortraitHistoryKey, Cursor> nextCursors, bool commit = true)
    {
        // Build and trim a detached candidate. Failure must leave both current memory and disk intact.
        var nextRecords = records.Append(added).ToList();
        var protectedIDs = nextCursors.Values.SelectMany(cursor => cursor.Redo.Append(cursor.ID)).Append(added.ID).ToHashSet();
        var changedEncoding = new Dictionary<long, byte[]> { [added.ID] = JsonSerializer.SerializeToUtf8Bytes(added) };
        byte[] EncodingFor(PortraitHistoryRecord record) => changedEncoding.TryGetValue(record.ID, out var changed)
            ? changed : encodedRecords[record.ID];
        var prefix = Encoding.UTF8.GetBytes("{\"Version\":2,\"NextID\":" + (nextID + 1).ToString(CultureInfo.InvariantCulture) + ",\"Records\":[");
        var suffix = Encoding.UTF8.GetBytes("],\"Cursors\":" + JsonSerializer.Serialize(nextCursors
            .Select(pair => new SavedCursor(pair.Key, pair.Value.ID, pair.Value.Redo)).ToList()) + "}");
        long byteCount = prefix.Length + suffix.Length + Math.Max(0, nextRecords.Count - 1);
        foreach (var record in nextRecords) byteCount += EncodingFor(record).Length;
        while (byteCount > byteLimit)
        {
            // Repair this removal before checking quota again: long shared IDs becoming null can
            // shrink many surviving fragments enough to retain the next-oldest record.
            var remove = nextRecords.FindIndex(record => !protectedIDs.Contains(record.ID));
            if (remove < 0) return new(PortraitHistoryWriteStatus.TooLarge);
            var removed = nextRecords[remove];
            byteCount -= EncodingFor(removed).Length + (nextRecords.Count > 1 ? 1 : 0);
            nextRecords.RemoveAt(remove);
            for (var index = 0; index < nextRecords.Count; index++)
            {
                var record = nextRecords[index];
                var parent = record.ParentID == removed.ID ? null : record.ParentID;
                var target = record.TargetID == removed.ID ? null : record.TargetID;
                if (parent == record.ParentID && target == record.TargetID) continue;
                var repaired = record with { ParentID = parent, TargetID = target };
                var encoded = JsonSerializer.SerializeToUtf8Bytes(repaired);
                byteCount += encoded.Length - EncodingFor(record).Length;
                changedEncoding[record.ID] = encoded;
                nextRecords[index] = repaired;
            }
        }
        if (!commit) return new(PortraitHistoryWriteStatus.Added, nextRecords.First(record => record.ID == added.ID));
        var nextEncoding = new Dictionary<long, byte[]>(nextRecords.Count);
        var fragments = new byte[nextRecords.Count][];
        for (var index = 0; index < nextRecords.Count; index++)
        {
            var record = nextRecords[index];
            var fragment = EncodingFor(record);
            fragments[index] = fragment;
            nextEncoding.Add(record.ID, fragment);
        }
        var document = new PortraitHistoryWriteDocument(prefix, fragments, suffix);
        if (writer is null || !writer.Queue(document)) return new(PortraitHistoryWriteStatus.Disposed);
        // Capture this submission's boundary before another store operation can enqueue.
        // The caller must never wait for disk while holding a Framework/native ownership lease.
        var persistence = writer.FlushAsync();
        records = nextRecords;
        encodedRecords = nextEncoding;
        cursors = nextCursors;
        nextID++;
        revision++;
        serializedBytes = document.Length;
        return new(PortraitHistoryWriteStatus.Added, records.First(record => record.ID == added.ID), persistence);
    }

    private static bool TryNormalize(string? json, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            normalized = JsonSerializer.Serialize(document.RootElement);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private void Load(string path, IPortraitHistoryMigrationFiles migrationFiles)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > byteLimit) throw new InvalidDataException("肖像历史文件超过容量上限，已保留原文件。");
            var fileBytes = File.ReadAllBytes(path);
            using var root = JsonDocument.Parse(fileBytes);
            if (root.RootElement.ValueKind != JsonValueKind.Object
                || !root.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber))
                throw new InvalidDataException("肖像历史文件无效，已保留原文件。");
            Document document;
            if (versionNumber == 1)
            {
                var legacy = JsonSerializer.Deserialize<LegacyDocument>(fileBytes);
                if (legacy is not { Records: not null, Cursors: not null }
                    || !ValidDocument(legacy.NextID,
                        legacy.Records.Select(row => row is null ? null : new ValidationRecord<LegacyKey>(row.ID, row.Key, row.ParentID, row.TargetID, row.Kind, row.SnapshotJSON)).ToArray(),
                        legacy.Cursors.Select(row => row is null ? null : new ValidationCursor<LegacyKey>(row.Key, row.ID, row.Redo)).ToArray(), key => key.IsValid))
                    throw new InvalidDataException("肖像历史记录关系无效，已保留原文件。");
                // Validate using the complete old key first: canonicalization must not make
                // an originally corrupt cross-account parent/target appear legitimate.
                var latestOperations = legacy.Records.GroupBy(record => record.Key)
                    .ToDictionary(group => group.Key, group => group.Max(record => record.ID));
                var selectedCursors = legacy.Cursors.GroupBy(cursor => cursor.Key.Canonical)
                    .Select(group => group.OrderByDescending(cursor => latestOperations[cursor.Key]).First())
                    .Select(cursor => new SavedCursor(cursor.Key.Canonical, cursor.ID, cursor.Redo.ToList())).ToList();
                document = new(2, legacy.NextID, legacy.Records.Select(record => new PortraitHistoryRecord(record.ID,
                    record.Key.Canonical, record.Timestamp, record.ParentID, record.Kind, record.SnapshotJSON,
                    record.TargetID, record.Label)).ToList(), selectedCursors);
                ValidateCurrent(document);
                var candidate = JsonSerializer.SerializeToUtf8Bytes(document);
                if (candidate.Length > byteLimit) throw new InvalidDataException("肖像历史升级后超过容量上限，已保留原文件。");
                PersistMigration(path, fileBytes, candidate, migrationFiles);
                fileBytes = candidate;
            }
            else if (versionNumber == 2)
            {
                document = JsonSerializer.Deserialize<Document>(fileBytes)
                    ?? throw new InvalidDataException("肖像历史文件无效，已保留原文件。");
                ValidateCurrent(document);
            }
            else throw new InvalidDataException("肖像历史版本暂不支持，已保留原文件。");
            records = document.Records;
            encodedRecords = records.ToDictionary(record => record.ID, record => JsonSerializer.SerializeToUtf8Bytes(record));
            cursors = document.Cursors.ToDictionary(cursor => cursor.Key, cursor => new Cursor(cursor.ID, cursor.Redo));
            nextID = document.NextID;
            serializedBytes = fileBytes.Length;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            records = [];
            cursors = [];
            IsReadOnly = true;
            LoadError = "肖像历史暂时无法读取，未覆盖原文件：" + exception.Message;
        }
    }

    private static void ValidateCurrent(Document document)
    {
        if (document is not { Version: 2, Records: not null, Cursors: not null }
            || !ValidDocument(document.NextID,
                document.Records.Select(row => row is null ? null : new ValidationRecord<PortraitHistoryKey>(row.ID, row.Key, row.ParentID, row.TargetID, row.Kind, row.SnapshotJSON)).ToArray(),
                document.Cursors.Select(row => row is null ? null : new ValidationCursor<PortraitHistoryKey>(row.Key, row.ID, row.Redo)).ToArray(), key => key.IsValid))
            throw new InvalidDataException("肖像历史记录关系无效，已保留原文件。");
    }

    private static bool ValidDocument<TKey>(long next, ValidationRecord<TKey>?[] rows, ValidationCursor<TKey>?[] saved,
        Func<TKey, bool> isValid) where TKey : notnull
    {
        if (next is <= 0 or >= long.MaxValue
            || rows.Any(row => row is null || !isValid(row.Key) || row.ID <= 0 || row.ID >= next
                || !Enum.IsDefined(row.Kind) || !TryNormalize(row.SnapshotJSON, out _))
            || rows.Select(row => row!.ID).Distinct().Count() != rows.Length
            || !rows.Select(row => row!.ID).SequenceEqual(rows.Select(row => row!.ID).Order())) return false;
        var byID = rows.ToDictionary(row => row!.ID, row => row!);
        var comparer = EqualityComparer<TKey>.Default;
        if (rows.Any(row => row!.ParentID is { } parent && (!byID.TryGetValue(parent, out var previous)
                || !comparer.Equals(previous.Key, row.Key) || parent >= row.ID)
            || row!.TargetID is { } target && (!byID.TryGetValue(target, out var previousTarget)
                || !comparer.Equals(previousTarget.Key, row.Key) || target >= row.ID))
            || saved.Any(cursor => cursor is null || !isValid(cursor.Key) || cursor.Redo is null
                || !byID.TryGetValue(cursor.ID, out var current) || !comparer.Equals(current.Key, cursor.Key)
                || cursor.Redo.Any(id => !byID.TryGetValue(id, out var redo) || !comparer.Equals(redo.Key, cursor.Key)))
            || saved.Select(cursor => cursor!.Key).Distinct().Count() != saved.Length) return false;
        var keys = saved.Select(cursor => cursor!.Key).ToHashSet();
        return rows.All(record => keys.Contains(record!.Key));
    }

    private static void PersistMigration(string path, byte[] original, byte[] candidate, IPortraitHistoryMigrationFiles files)
    {
        var backup = path + ".v1-" + Convert.ToHexString(SHA256.HashData(original)) + ".bak";
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp");
        var backupTemporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (!files.Exists(backup))
            {
                // A failed/partial write cannot occupy the durable backup name and poison
                // future retries. Publish only bytes already verified against the source.
                files.WriteNewAndFlush(backupTemporary, original);
                if (!files.ReadAllBytes(backupTemporary).SequenceEqual(original))
                    throw new InvalidDataException("肖像历史升级备份未能验证，已保留原文件。");
                files.MoveNew(backupTemporary, backup);
            }
            if (!files.ReadAllBytes(backup).SequenceEqual(original))
                throw new InvalidDataException("肖像历史升级备份未能验证，已保留原文件。");
            files.WriteNewAndFlush(temporary, candidate);
            if (!files.ReadAllBytes(temporary).SequenceEqual(candidate))
                throw new InvalidDataException("肖像历史升级文件未能验证，已保留原文件。");
            if (!files.ReadAllBytes(path).SequenceEqual(original))
                throw new InvalidDataException("肖像历史已被其他操作更新，本次升级未覆盖原文件。");
            files.Replace(temporary, path);
        }
        finally
        {
            foreach (var ownedTemporary in new[] { temporary, backupTemporary })
            {
                try { if (files.Exists(ownedTemporary)) files.Delete(ownedTemporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    /// <summary>Waits for the newest accepted immutable document, without native or Framework callbacks.</summary>
    public Task<bool> FlushAsync()
    {
        lock (gate) return writer?.FlushAsync() ?? Task.FromResult(true);
    }

    /// <summary>
    /// Closes history admission without waiting on the file writer. The returned task drains
    /// every immutable document accepted before the close and is intended to be awaited after
    /// Framework-owned native cleanup has returned.
    /// </summary>
    public Task<bool> BeginDispose()
    {
        PortraitHistoryWriter? historyWriter;
        lock (gate)
        {
            if (disposeTask is not null) return disposeTask;
            disposed = true;
            historyWriter = writer;
            disposeTask = historyWriter?.BeginDispose() ?? Task.FromResult(true);
            return disposeTask;
        }
    }

    public void Dispose()
    {
        BeginDispose().GetAwaiter().GetResult();
    }

    private sealed record Cursor(long ID, List<long> Redo);
    private sealed record SavedCursor(PortraitHistoryKey Key, long ID, List<long> Redo);
    private sealed record Document(int Version, long NextID, List<PortraitHistoryRecord> Records, List<SavedCursor> Cursors);
    private sealed record ValidationRecord<TKey>(long ID, TKey Key, long? ParentID, long? TargetID, PortraitHistoryRecordKind Kind, string SnapshotJSON);
    private sealed record ValidationCursor<TKey>(TKey Key, long ID, List<long> Redo);
    private readonly record struct LegacyKey(ulong AccountID, ulong ContentID, PortraitHistoryEntryPoint EntryPoint, int SourceID, string CompatToken)
    {
        public bool IsValid => AccountID != 0 && Canonical.IsValid;
        public PortraitHistoryKey Canonical => new(ContentID, EntryPoint, SourceID, CompatToken);
    }
    private sealed record LegacyRecord(long ID, LegacyKey Key, DateTimeOffset Timestamp, long? ParentID,
        PortraitHistoryRecordKind Kind, string SnapshotJSON, long? TargetID = null, string Label = "编辑肖像");
    private sealed record LegacyCursor(LegacyKey Key, long ID, List<long> Redo);
    private sealed record LegacyDocument(int Version, long NextID, List<LegacyRecord> Records, List<LegacyCursor> Cursors);
}

internal interface IPortraitHistoryMigrationFiles
{
    bool Exists(string path);
    byte[] ReadAllBytes(string path);
    void WriteNewAndFlush(string path, byte[] bytes);
    void MoveNew(string source, string destination);
    void Replace(string source, string destination);
    void Delete(string path);
}

internal sealed class PortraitHistoryMigrationFiles : IPortraitHistoryMigrationFiles
{
    public bool Exists(string path) => File.Exists(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public void WriteNewAndFlush(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    public void Replace(string source, string destination) => File.Replace(source, destination, null);
    public void MoveNew(string source, string destination) => File.Move(source, destination);
    public void Delete(string path) => File.Delete(path);
}
