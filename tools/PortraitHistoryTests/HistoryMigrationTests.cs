using FireGaze.PortraitPreview;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

internal static class HistoryMigrationTests
{
    internal static async Task<int> Run(string directory)
    {
        var checks = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException(label);
            checks++;
            Console.WriteLine("PASS " + label);
        }
        var path = Path.Combine(directory, "legacy-migration.json");
        object Key(ulong account) => new { AccountID = account, ContentID = 20UL,
            EntryPoint = PortraitHistoryEntryPoint.GearsetPortrait, SourceID = 3, CompatToken = "race-1-job-19-v1" };
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(8));
        var rows = Enumerable.Range(1, 4).Select(id => new
        {
            ID = (long)id, Key = Key((ulong)id * 10), Timestamp = now.AddMinutes(id), ParentID = (long?)null,
            Kind = PortraitHistoryRecordKind.Edit, SnapshotJSON = "{\"pose\":" + id + "}", TargetID = (long?)null, Label = "姿势" + id,
        }).ToArray();
        var original = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, NextID = 5L, Records = rows,
            Cursors = rows.Select(row => new { row.Key, row.ID, Redo = Array.Empty<long>() }).ToArray() });
        File.WriteAllBytes(path, original);
        var stable = new PortraitHistoryKey(20, PortraitHistoryEntryPoint.GearsetPortrait, 3, "race-1-job-19-v1");
        using (var store = new PortraitHistoryStore(path))
        {
            Check(!store.IsReadOnly && store.GetRecords(stable).Count == 4,
                "v1 account aliases converge into one stable CID history without losing records");
            Check(store.CurrentRecordID(stable) == 4, "latest legacy operation branch supplies the canonical cursor");
            Check(await store.FlushAsync(), "migrated store remains writable");
        }
        var migrated = JsonNode.Parse(File.ReadAllBytes(path))!;
        Check(migrated["Version"]!.GetValue<int>() == 2 && !File.ReadAllText(path).Contains("AccountID"),
            "migrated v2 document drops unstable account identity from its serialized keys");
        var backups = Directory.GetFiles(directory, "legacy-migration.json.v1-*.bak");
        Check(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(original),
            "migration preserves an exact durable v1 byte backup before replacing the source");

        // A newer undo event can select an older cursor. Choose by branch activity, not cursor ID.
        LegacyKey K(ulong account = 10, ulong cid = 20, PortraitHistoryEntryPoint entry = PortraitHistoryEntryPoint.GearsetPortrait,
            int source = 3, string compat = "race-1-job-19-v1") => new(account, cid, entry, source, compat);
        LegacyRecord R(long id, LegacyKey key, long? parent = null, long? target = null,
            PortraitHistoryRecordKind kind = PortraitHistoryRecordKind.Edit, string? json = null) =>
            new(id, key, now.AddMinutes(-id), parent, kind, json ?? "{ \"pose\" : " + id + " }", target, "旧操作" + id);
        byte[] Bytes(LegacyRecord[] records, LegacyCursor[] cursors, long? next = null) => JsonSerializer.SerializeToUtf8Bytes(
            new { Version = 1, NextID = next ?? records.Max(row => row.ID) + 1, Records = records, Cursors = cursors });
        var undoPath = Path.Combine(directory, "legacy-undo-winner.json");
        var a = K(); var b = K(20);
        LegacyRecord[] branchRows = [R(1, a), R(2, a, 1), R(3, b), R(4, a, 2, 1, PortraitHistoryRecordKind.Undo, "{ \"pose\" : 1 }")];
        var branchBytes = Bytes(branchRows, [new(a, 1, [2]), new(b, 3, [])]);
        File.WriteAllBytes(undoPath, branchBytes);
        using (var store = new PortraitHistoryStore(undoPath))
        {
            Check(!store.IsReadOnly && store.CurrentRecordID(stable) == 1 && store.TryGetRedo(stable, out var redo) && redo.Record.ID == 2,
                "migration selects the latest operation branch even when its undo cursor has a lower ID and older timestamp");
            var all = store.GetRecords(stable).OrderBy(row => row.ID).ToArray();
            Check(all.Length == branchRows.Length && all.Zip(branchRows).All(pair => pair.First.ID == pair.Second.ID
                && pair.First.ParentID == pair.Second.ParentID && pair.First.TargetID == pair.Second.TargetID
                && pair.First.Timestamp == pair.Second.Timestamp && pair.First.SnapshotJSON == pair.Second.SnapshotJSON
                && pair.First.Kind == pair.Second.Kind && pair.First.Label == pair.Second.Label),
                "migration retains every record ID time parent target kind label and exact snapshot string");
            Check(store.TryGetRecord(stable, 3, out var losingBranch) && store.CommitRestore(losingBranch).Record is { ID: 5, ParentID: 1, TargetID: 3 },
                "a losing cursor branch remains restorable and the first new audit keeps the original NextID");
            Check(store.TryGetUndo(stable, out var undo) && undo.Record.ID == 1 && store.CommitNavigation(undo).Status == PortraitHistoryWriteStatus.Added
                && store.TryGetRedo(stable, out var redoAfter) && redoAfter.Record.ID == 5 && store.CommitNavigation(redoAfter).Status == PortraitHistoryWriteStatus.Added,
                "post-migration restore undo and redo preserve the selected branch ancestry");
            Check(await store.FlushAsync(), "navigation after migration persists through the ordinary writer");
        }
        var afterNavigation = File.ReadAllBytes(undoPath);
        using (var store = new PortraitHistoryStore(undoPath))
            Check(!store.IsReadOnly && store.Count == 7 && store.CurrentRecordID(stable) == 5 && File.ReadAllBytes(undoPath).SequenceEqual(afterNavigation),
                "v2 reload is idempotent and retains the migrated navigation state without rewriting");
        Check(Directory.GetFiles(directory, "legacy-undo-winner.json.v1-*.bak").Length == 1,
            "repeated v2 loading creates no duplicate migration backup");

        var isolationPath = Path.Combine(directory, "legacy-isolation.json");
        var keys = new[] { K(), K(20), K(30, 21), K(40, entry: PortraitHistoryEntryPoint.AdventurerPlate),
            K(50, source: 9), K(60, compat: "other-compat") };
        var isolationRows = keys.Select((key, index) => R(index + 1, key)).ToArray();
        File.WriteAllBytes(isolationPath, Bytes(isolationRows, isolationRows.Select(row => new LegacyCursor(row.Key, row.ID, [])).ToArray()));
        using (var store = new PortraitHistoryStore(isolationPath))
        {
            Check(store.GetRecords(stable).Count == 2 && store.GetRecordsForOwner(20).Count == 5 && store.GetRecordsForOwner(21).Count == 1,
                "only account aliases merge while different CIDs remain isolated");
            foreach (var other in new[] { stable with { ContentID = 21 }, stable with { EntryPoint = PortraitHistoryEntryPoint.AdventurerPlate },
                stable with { SourceID = 9 }, stable with { CompatToken = "other-compat" } })
                Check(store.GetRecords(other).Count == 1 && !store.TryGetRecord(other, 1, out _),
                    "canonical CID entry source and compatibility scopes keep separate records and selections");
        }

        // Legacy validation must run before aliases are collapsed, never laundering bad links.
        var corruptions = new Dictionary<string, Action<JsonNode>>
        {
            ["cross-account-parent"] = node => node["Records"]![2]!["ParentID"] = 1,
            ["cross-account-target"] = node => node["Records"]![2]!["TargetID"] = 1,
            ["zero-account"] = node => node["Records"]![0]!["Key"]!["AccountID"] = 0,
            ["zero-cid"] = node => node["Records"]![0]!["Key"]!["ContentID"] = 0,
            ["duplicate-id"] = node => node["Records"]![1]!["ID"] = 1,
            ["forward-parent"] = node => node["Records"]![0]!["ParentID"] = 2,
            ["missing-cursor"] = node => node["Cursors"]!.AsArray().RemoveAt(1),
            ["duplicate-cursor"] = node => node["Cursors"]!.AsArray().Add(node["Cursors"]![0]!.DeepClone()),
            ["invalid-redo"] = node => node["Cursors"]![0]!["Redo"]!.AsArray().Add(3),
            ["invalid-cursor"] = node => node["Cursors"]![0]!["ID"] = 3,
            ["null-record"] = node => node["Records"]![0] = null,
            ["bad-json"] = node => node["Records"]![0]!["SnapshotJSON"] = "bad-json",
            ["unknown-version"] = node => node["Version"] = 99,
        };
        foreach (var (name, corrupt) in corruptions)
        {
            var node = JsonNode.Parse(branchBytes)!;
            corrupt(node);
            var source = JsonSerializer.SerializeToUtf8Bytes(node);
            var corruptPath = Path.Combine(directory, "legacy-corrupt-" + name + ".json");
            File.WriteAllBytes(corruptPath, source);
            using var store = new PortraitHistoryStore(corruptPath);
            Check(store.IsReadOnly && store.Count == 0 && store.Record(stable, "{}").Status == PortraitHistoryWriteStatus.ReadOnlyCorrupt
                && File.ReadAllBytes(corruptPath).SequenceEqual(source) && Directory.GetFiles(directory, Path.GetFileName(corruptPath) + ".v1-*.bak").Length == 0,
                "invalid legacy " + name + " stays read-only and unchanged before any backup or migration");
        }

        foreach (var (name, input) in new[] { ("array-root", "[]"), ("null-root", "null"),
            ("string-version", "{\"Version\":\"1\"}"), ("bool-version", "{\"Version\":true}") })
        {
            var malformedPath = Path.Combine(directory, "legacy-shape-" + name + ".json");
            var source = System.Text.Encoding.UTF8.GetBytes(input);
            File.WriteAllBytes(malformedPath, source);
            using var store = new PortraitHistoryStore(malformedPath);
            Check(store.IsReadOnly && store.Record(stable, "{}").Status == PortraitHistoryWriteStatus.ReadOnlyCorrupt
                && File.ReadAllBytes(malformedPath).SequenceEqual(source) && Directory.GetFiles(directory, Path.GetFileName(malformedPath) + ".v1-*.bak").Length == 0,
                "non-object or non-numeric " + name + " history is safely read-only without starting a worker");
        }

        foreach (var stage in new[] { "backup-write", "backup-partial-write", "backup-read", "backup-mismatch", "backup-publish",
            "candidate-write", "candidate-read", "candidate-mismatch", "replace" })
        {
            var faultPath = Path.Combine(directory, "legacy-fault-" + stage + ".json");
            File.WriteAllBytes(faultPath, branchBytes);
            using (var store = new PortraitHistoryStore(faultPath, migrationFiles: new FaultFiles(stage)))
                Check(store.IsReadOnly && store.Record(stable, "{}").Status == PortraitHistoryWriteStatus.ReadOnlyCorrupt
                    && File.ReadAllBytes(faultPath).SequenceEqual(branchBytes),
                    "migration " + stage + " failure keeps original bytes and does not start a writable store");
            Check(Directory.GetFiles(directory, "." + Path.GetFileName(faultPath) + "*.tmp").Length == 0,
                "migration " + stage + " failure removes only its temporary files");
            if (stage is "backup-partial-write" or "replace")
            {
                using var recovered = new PortraitHistoryStore(faultPath);
                Check(!recovered.IsReadOnly && recovered.Count == 4,
                    "a failed migration can retry using only complete backups without poisoning the durable backup name: " + stage);
            }
        }
        var changedPath = Path.Combine(directory, "legacy-concurrent-source.json");
        File.WriteAllBytes(changedPath, branchBytes);
        var concurrent = Bytes(branchRows, [new(a, 1, [2]), new(b, 3, [])], next: 50);
        using (var store = new PortraitHistoryStore(changedPath, migrationFiles: new FaultFiles("source-change", concurrent)))
            Check(store.IsReadOnly && File.ReadAllBytes(changedPath).SequenceEqual(concurrent),
                "source changes detected before replacement are retained rather than overwritten by stale migration data");

        var preexistingPath = Path.Combine(directory, "legacy-backup-conflict.json");
        File.WriteAllBytes(preexistingPath, branchBytes);
        var expectedBackup = preexistingPath + ".v1-" + Convert.ToHexString(SHA256.HashData(branchBytes)) + ".bak";
        File.WriteAllText(expectedBackup, "do-not-overwrite");
        using (var store = new PortraitHistoryStore(preexistingPath))
            Check(store.IsReadOnly && File.ReadAllText(expectedBackup) == "do-not-overwrite" && File.ReadAllBytes(preexistingPath).SequenceEqual(branchBytes),
                "a conflicting preexisting backup is never overwritten and prevents migration");

        // Missing optional legacy fields can make the canonical document larger: do not prune to migrate.
        var budgetPath = Path.Combine(directory, "legacy-expansion-budget.json");
        var small = JsonNode.Parse(Bytes([R(1, K(1))], [new(K(1), 1, [])]))!;
        small["Records"]![0]!.AsObject().Remove("Label");
        var smallBytes = JsonSerializer.SerializeToUtf8Bytes(small);
        File.WriteAllBytes(budgetPath, smallBytes);
        using (var store = new PortraitHistoryStore(budgetPath, byteLimit: smallBytes.Length))
            Check(store.IsReadOnly && File.ReadAllBytes(budgetPath).SequenceEqual(smallBytes)
                && Directory.GetFiles(directory, Path.GetFileName(budgetPath) + ".v1-*.bak").Length == 0,
                "an expanded migration candidate exceeding quota fails before backup without trimming records");

        var largePath = Path.Combine(directory, "legacy-near-quota.json");
        var largeRows = Enumerable.Range(1, 20).Select(id => R(id, K((ulong)(id % 4 + 1) * 10),
            id <= 4 ? null : id - 4, json: "{\"payload\":\"" + new string('a', 400000) + "\"}")).ToArray();
        var largeCursors = largeRows.GroupBy(row => row.Key).Select(group => new LegacyCursor(group.Key, group.Max(row => row.ID), [])).ToArray();
        var near = Bytes(largeRows, largeCursors);
        largeRows[^1] = largeRows[^1] with { SnapshotJSON = "{\"payload\":\"" + new string('z', 400000 + PortraitHistoryStore.MaximumBytes - near.Length - 128) + "\"}" };
        near = Bytes(largeRows, largeCursors);
        Check(near.Length == PortraitHistoryStore.MaximumBytes - 128, "synthetic legacy fixture is genuinely within 128 bytes of the global quota");
        File.WriteAllBytes(largePath, near);
        using (var store = new PortraitHistoryStore(largePath))
        {
            Check(!store.IsReadOnly && store.Count == largeRows.Length && store.CurrentRecordID(stable) == 20
                && store.Bytes <= PortraitHistoryStore.MaximumBytes && store.GetRecords(stable).All(row => largeRows.Any(old => old.ID == row.ID && old.ParentID == row.ParentID)),
                "near-quota CID migration retains every ID and parent without triggering quota eviction");
            var added = store.Record(stable, "{\"payload\":\"" + new string('n', 1000000) + "\"}");
            Check(added.Status == PortraitHistoryWriteStatus.Added && store.Count < 21 && store.CurrentRecordID(stable) == 21
                && store.Bytes <= PortraitHistoryStore.MaximumBytes && await store.FlushAsync(),
                "ordinary post-migration edits still prune oldest unprotected records within the exact byte ceiling");
        }
        using (var store = new PortraitHistoryStore(largePath))
            Check(!store.IsReadOnly && store.CurrentRecordID(stable) == 21,
                "post-migration quota repairs persist a valid cursor and parent graph across reload");
        return checks;
    }

    private sealed record LegacyKey(ulong AccountID, ulong ContentID, PortraitHistoryEntryPoint EntryPoint, int SourceID, string CompatToken);
    private sealed record LegacyRecord(long ID, LegacyKey Key, DateTimeOffset Timestamp, long? ParentID,
        PortraitHistoryRecordKind Kind, string SnapshotJSON, long? TargetID, string Label);
    private sealed record LegacyCursor(LegacyKey Key, long ID, List<long> Redo);

    private sealed class FaultFiles(string fault, byte[]? changedSource = null) : IPortraitHistoryMigrationFiles
    {
        private readonly PortraitHistoryMigrationFiles inner = new();
        private bool IsBackup(string path) => path.EndsWith(".bak", StringComparison.Ordinal)
            || path.Contains(".backup-", StringComparison.Ordinal);
        public bool Exists(string path) => inner.Exists(path);
        public byte[] ReadAllBytes(string path)
        {
            var backup = IsBackup(path);
            if (fault == "backup-read" && backup || fault == "candidate-read" && path.Contains(".migration-", StringComparison.Ordinal))
                throw new IOException("injected migration verification read failure");
            if (fault == "backup-mismatch" && backup || fault == "candidate-mismatch" && path.Contains(".migration-", StringComparison.Ordinal))
                return [0];
            if (fault == "source-change" && !backup && !path.EndsWith(".tmp", StringComparison.Ordinal))
                File.WriteAllBytes(path, changedSource!);
            return inner.ReadAllBytes(path);
        }
        public void WriteNewAndFlush(string path, byte[] bytes)
        {
            var backup = IsBackup(path);
            if (fault == "backup-write" && backup || fault == "candidate-write" && !backup)
                throw new IOException("injected migration write failure");
            if (fault == "backup-partial-write" && backup)
            {
                File.WriteAllBytes(path, [1, 2, 3]);
                throw new IOException("injected partial backup failure");
            }
            inner.WriteNewAndFlush(path, bytes);
        }
        public void Replace(string source, string destination)
        {
            if (fault == "replace") throw new IOException("injected atomic replace failure");
            inner.Replace(source, destination);
        }
        public void MoveNew(string source, string destination)
        {
            if (fault == "backup-publish") throw new IOException("injected atomic backup publication failure");
            inner.MoveNew(source, destination);
        }
        public void Delete(string path) => inner.Delete(path);
    }
}
