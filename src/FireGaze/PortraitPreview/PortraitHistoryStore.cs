using System.Text.Json;

namespace FireGaze.PortraitPreview;

internal enum PortraitHistoryEntryPoint { GearsetPortrait, AdventurerPlate }
internal readonly record struct PortraitHistoryKey(ulong AccountID, ulong ContentID, PortraitHistoryEntryPoint EntryPoint,
    int SourceID, string CompatToken)
{
    public bool IsValid => AccountID != 0 && ContentID != 0 && Enum.IsDefined(EntryPoint) && SourceID >= 0
        && !string.IsNullOrWhiteSpace(CompatToken) && CompatToken.Length <= 512;
}

internal enum PortraitHistoryRecordKind { Edit, Restore, Undo, Redo }
internal enum PortraitHistoryWriteStatus { Added, Duplicate, InvalidKey, InvalidJson, TooLarge, ReadOnlyCorrupt, StaleSelection, Disposed }
internal sealed record PortraitHistoryRecord(long ID, PortraitHistoryKey Key, DateTimeOffset Timestamp,
    long? ParentID, PortraitHistoryRecordKind Kind, string SnapshotJSON, long? TargetID = null, string Label = "编辑肖像");
internal sealed record PortraitHistoryWriteResult(PortraitHistoryWriteStatus Status, PortraitHistoryRecord? Record = null);
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
    private Dictionary<PortraitHistoryKey, Cursor> cursors = [];
    private long nextID = 1;
    private long revision;
    private int serializedBytes;
    private bool disposed;
    public bool IsReadOnly { get; private set; }
    public string? LoadError { get; private set; }
    public string? PersistenceError => writer?.LastError;
    public int Count { get { lock (gate) return records.Count; } }
    public int Bytes { get { lock (gate) return serializedBytes; } }

    public PortraitHistoryStore(string filePath, Func<DateTimeOffset>? clock = null, int byteLimit = MaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (byteLimit is <= 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.byteLimit = byteLimit;
        var path = Path.GetFullPath(filePath);
        Load(path);
        if (!IsReadOnly) writer = new(path, byteLimit);
    }

    public IReadOnlyList<PortraitHistoryRecord> GetRecords(PortraitHistoryKey key)
    {
        lock (gate) return records.Where(record => record.Key == key).Reverse().ToArray();
    }

    public IReadOnlyList<PortraitHistoryRecord> GetRecordsForOwner(ulong accountID, ulong contentID)
    {
        lock (gate) return records.Where(record => record.Key.AccountID == accountID && record.Key.ContentID == contentID).Reverse().ToArray();
    }

    public long? CurrentRecordID(PortraitHistoryKey key)
    {
        lock (gate) return cursors.GetValueOrDefault(key)?.ID;
    }

    public IReadOnlyList<PortraitHistoryDay> GetDayGroups(PortraitHistoryKey key) => GetRecords(key)
        .GroupBy(record => DateOnly.FromDateTime(record.Timestamp.Date))
        .OrderByDescending(group => group.Key).Select(group => new PortraitHistoryDay(group.Key, group.ToArray())).ToArray();

    public PortraitHistoryWriteResult Record(PortraitHistoryKey key, string snapshotJSON, string label = "编辑肖像")
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
            return Accept(added, nextCursors);
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
        byte[] bytes;
        while (true)
        {
            var ids = nextRecords.Select(record => record.ID).ToHashSet();
            for (var index = 0; index < nextRecords.Count; index++)
            {
                var record = nextRecords[index];
                nextRecords[index] = record with
                {
                    ParentID = record.ParentID is { } parent && ids.Contains(parent) ? parent : null,
                    TargetID = record.TargetID is { } target && ids.Contains(target) ? target : null,
                };
            }
            bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, nextID + 1, nextRecords,
                nextCursors.Select(pair => new SavedCursor(pair.Key, pair.Value.ID, pair.Value.Redo)).ToList()));
            if (bytes.Length <= byteLimit) break;
            var remove = nextRecords.FindIndex(record => !protectedIDs.Contains(record.ID));
            if (remove < 0) return new(PortraitHistoryWriteStatus.TooLarge);
            nextRecords.RemoveAt(remove);
        }
        if (!commit) return new(PortraitHistoryWriteStatus.Added, nextRecords.First(record => record.ID == added.ID));
        if (writer is null || !writer.Queue(bytes)) return new(PortraitHistoryWriteStatus.Disposed);
        records = nextRecords;
        cursors = nextCursors;
        nextID++;
        revision++;
        serializedBytes = bytes.Length;
        return new(PortraitHistoryWriteStatus.Added, records.First(record => record.ID == added.ID));
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

    private void Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > byteLimit) throw new InvalidDataException("肖像历史文件超过容量上限，已保留原文件。");
            var fileBytes = File.ReadAllBytes(path);
            var document = JsonSerializer.Deserialize<Document>(fileBytes);
            if (document is not { Version: 1, NextID: > 0 and < long.MaxValue, Records: not null, Cursors: not null }
                || document.Records.Any(record => record is null || !record.Key.IsValid || record.ID <= 0
                    || !Enum.IsDefined(record.Kind) || !TryNormalize(record.SnapshotJSON, out _))
                || document.Records.Select(record => record.ID).Distinct().Count() != document.Records.Count
                || document.Records.Any(record => record.ID >= document.NextID)
                || !document.Records.Select(record => record.ID).SequenceEqual(document.Records.Select(record => record.ID).Order()))
                throw new InvalidDataException("肖像历史文件无效，已保留原文件。");
            var byID = document.Records.ToDictionary(record => record.ID);
            if (document.Records.Any(record => record.ParentID is { } parent && (!byID.TryGetValue(parent, out var previous)
                    || previous.Key != record.Key || parent >= record.ID)
                || record.TargetID is { } target && (!byID.TryGetValue(target, out var previousTarget) || previousTarget.Key != record.Key || target >= record.ID))
                || document.Cursors.Any(cursor => cursor is null || !cursor.Key.IsValid || cursor.Redo is null
                    || !byID.TryGetValue(cursor.ID, out var current) || current.Key != cursor.Key
                    || cursor.Redo.Any(id => !byID.TryGetValue(id, out var redo) || redo.Key != cursor.Key))
                || document.Cursors.Select(cursor => cursor.Key).Distinct().Count() != document.Cursors.Count
                || document.Records.Any(record => !document.Cursors.Any(cursor => cursor.Key == record.Key)))
                throw new InvalidDataException("肖像历史记录关系无效，已保留原文件。");
            records = document.Records;
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

    /// <summary>Waits for the newest accepted immutable document, without native or Framework callbacks.</summary>
    public Task<bool> FlushAsync()
    {
        lock (gate) return writer?.FlushAsync() ?? Task.FromResult(true);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        writer?.Dispose();
    }

    private sealed record Cursor(long ID, List<long> Redo);
    private sealed record SavedCursor(PortraitHistoryKey Key, long ID, List<long> Redo);
    private sealed record Document(int Version, long NextID, List<PortraitHistoryRecord> Records, List<SavedCursor> Cursors);
}
