#if !HAS_HISTORY
Console.Error.WriteLine("FAIL portrait history has no persistent, isolated navigation or input batching implementation");
return 1;
#else
using FireGaze.PortraitPreview;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    passed++;
    Console.WriteLine("PASS " + description);
}
var directory = Path.Combine(Path.GetTempPath(), "portrait-history-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string FileFor(string name) => Path.Combine(directory, name + ".json");
var now = new DateTimeOffset(2026, 10, 17, 23, 59, 0, TimeSpan.FromHours(8));
var key = new PortraitHistoryKey(10, 20, PortraitHistoryEntryPoint.GearsetPortrait, 3, "race-1-job-19-v1");
try
{
    // Missing validation or shared cursors would accept bad identities or expose another portrait.
    using (var store = new PortraitHistoryStore(FileFor("isolation"), () => now))
    {
        Check(store.Record(key with { ContentID = 0 }, "{}").Status == PortraitHistoryWriteStatus.InvalidKey, "zero content identity is rejected");
        Check(store.Record(key with { AccountID = 0 }, "{}").Status == PortraitHistoryWriteStatus.InvalidKey, "zero account identity is rejected");
        Check(store.Record(key, "not-json").Status == PortraitHistoryWriteStatus.InvalidJson, "malformed snapshot is rejected before changing history");
        Check(store.Record(key, "{\"head\":1}").Status == PortraitHistoryWriteStatus.Added, "first compatible portrait snapshot is recorded");
        var exportedFirst = JsonSerializer.SerializeToElement(store.GetRecords(key)[0]);
        Check(exportedFirst.TryGetProperty("Label", out var defaultLabel) && defaultLabel.GetString() == "编辑肖像", "history snapshots persist a readable operation label for the list UI");
        foreach (var other in new[] { key with { AccountID = 11 }, key with { ContentID = 21 }, key with { SourceID = 4 }, key with { EntryPoint = PortraitHistoryEntryPoint.AdventurerPlate }, key with { CompatToken = "race-2-job-19-v1" } })
            Check(store.GetRecords(other).Count == 0 && !store.TryGetUndo(other, out _), "account character entry source and compatibility boundaries isolate history");
        Check(store.Record(key, "{ \"head\" : 1 }").Status == PortraitHistoryWriteStatus.Duplicate && store.GetRecords(key).Count == 1, "format-only JSON differences do not create repeated snapshots");
        Check(store.CurrentRecordID(key) == store.GetRecords(key)[0].ID && store.CurrentRecordID(key with { SourceID = 77 }) is null,
            "the UI can mark the actual selected navigation node without inventing a cursor for another portrait");
        now = now.AddMinutes(2);
        store.Record(key, "{\"head\":2}");
        var groups = store.GetDayGroups(key);
        Check(groups.Count == 2 && groups[0].Date == new DateOnly(2026, 10, 18) && groups[1].Date == new DateOnly(2026, 10, 17), "history groups snapshots by recorded local date with newest day first");
        Check(await store.FlushAsync(), "accepted history reaches the atomic persistence worker");
        Check(store.Count == 2 && store.Bytes == new FileInfo(FileFor("isolation")).Length && store.GetRecordsForOwner(10, 20).Count == 2
            && store.GetRecordsForOwner(11, 20).Count == 0, "owner-wide history and cached byte diagnostics remain available without a live editor");
    }
    using (var loaded = new PortraitHistoryStore(FileFor("isolation")))
    {
        Check(loaded.GetRecords(key).Count == 2 && loaded.TryGetUndo(key, out var undo) && undo.Record.SnapshotJSON == "{\"head\":1}", "reload preserves records and compatible portrait cursor");
        var text = File.ReadAllText(FileFor("isolation"));
        Check(!text.Contains("EditorAddress") && !text.Contains("CharacterPointer") && !text.Contains(directory), "serialized history metadata contains no process pointers or machine paths");
    }

    // Navigation must not change the cursor until the native caller has successfully applied it.
    using (var store = new PortraitHistoryStore(FileFor("navigation"), () => now))
    {
        var first = store.Record(key, "{\"pose\":1}").Record!;
        var second = store.Record(key, "{\"pose\":2}").Record!;
        var third = store.Record(key, "{\"pose\":3}").Record!;
        Check(store.TryGetUndo(key, out var undo) && undo.Record.ID == second.ID && store.GetRecords(key).Count == 3, "undo candidate is read-only until native apply succeeds");
        Check(store.PrepareCommit(undo).Status == PortraitHistoryWriteStatus.Added && store.Count == 3 && store.CurrentRecordID(key) == third.ID,
            "preparing navigation checks budget and validity without recording an unapplied state");
        Check(!store.TryGetRedo(key, out _), "requesting but not committing undo does not fabricate redo");
        Check(store.CommitNavigation(undo).Status == PortraitHistoryWriteStatus.Added && store.GetRecords(key).Count == 4, "successful undo appends an audit event without deleting any snapshot");
        Check(store.CurrentRecordID(key) == second.ID, "undo cursor selects the restored node rather than its newly appended audit event");
        Check(store.TryGetRedo(key, out var redo) && redo.Record.ID == third.ID, "undo keeps the former state available for redo");
        Check(store.CommitNavigation(undo).Status == PortraitHistoryWriteStatus.StaleSelection, "reusing an already committed navigation candidate is refused");
        Check(store.CommitNavigation(redo).Status == PortraitHistoryWriteStatus.Added && store.GetRecords(key).Count == 5, "successful redo also remains in the audit history");
        Check(store.TryGetRecord(key, first.ID, out var restore) && store.GetRecords(key).Count == 5, "selecting an old history entry does not persist an unapplied restoration");
        var restored = store.CommitRestore(restore);
        Check(restored.Status == PortraitHistoryWriteStatus.Added && restored.Record!.ID > third.ID && restored.Record.SnapshotJSON == first.SnapshotJSON && store.GetRecords(key).Count == 6, "restoring an old snapshot appends a new record and retains future branches");
        Check(store.TryGetUndo(key, out var afterRestore) && afterRestore.Record.ID == third.ID, "undo after explicit restore returns to the state before restoration");
        store.CommitNavigation(afterRestore);
        var branch = store.Record(key, "{\"pose\":4}");
        Check(branch.Status == PortraitHistoryWriteStatus.Added && !store.TryGetRedo(key, out _) && store.GetRecords(key).Any(record => record.ID == restored.Record!.ID), "editing after undo starts a branch while keeping earlier restore and redo records");
        Check(store.CommitRestore(restore).Status == PortraitHistoryWriteStatus.StaleSelection, "stale restore candidates cannot silently change a newer cursor");
        Check(store.TryGetRecord(key, branch.Record!.ID, out var same) && store.CommitRestore(same).Status == PortraitHistoryWriteStatus.Duplicate, "restoring the selected current record does not create a redundant record");
        Check(await store.FlushAsync(), "navigation audit is persisted in submission order");
    }
    using (var loaded = new PortraitHistoryStore(FileFor("navigation")))
        Check(loaded.GetRecords(key).Count == 8 && loaded.TryGetUndo(key, out var undo) && undo.Record.SnapshotJSON == "{\"pose\":3}", "branch ancestry survives process reload");

    // Explicitly choosing a different historical ID is an operation even if its appearance is unchanged.
    using (var store = new PortraitHistoryStore(FileFor("same-content-restore"), () => now))
    {
        var original = store.Record(key, "{\"pose\":1}").Record!;
        store.Record(key, "{\"pose\":2}");
        var current = store.Record(key, "{\"pose\":1}").Record!;
        Check(store.TryGetRecord(key, original.ID, out var restore) && store.PrepareCommit(restore).Status == PortraitHistoryWriteStatus.Added
            && store.Count == 3 && store.CurrentRecordID(key) == current.ID,
            "preparing an older identical snapshot reserves a real restore operation without moving the cursor");
        var restored = store.CommitRestore(restore);
        Check(restored.Status == PortraitHistoryWriteStatus.Added && store.Count == 4 && restored.Record!.ID > current.ID
            && restored.Record.Kind == PortraitHistoryRecordKind.Restore && restored.Record.ParentID == current.ID
            && restored.Record.TargetID == original.ID && restored.Record.SnapshotJSON == original.SnapshotJSON,
            "restoring an older ID with identical content appends its own restore audit and target provenance");
        Check(store.CurrentRecordID(key) == restored.Record!.ID && store.TryGetUndo(key, out var undo) && undo.Record.ID == current.ID,
            "an identical-content restore becomes current while keeping the previous user state as undo");
        Check(store.TryGetRecord(key, restored.Record.ID, out var same) && store.CommitRestore(same).Status == PortraitHistoryWriteStatus.Duplicate
            && store.Count == 4 && store.CurrentRecordID(key) == restored.Record.ID,
            "reclicking the current restore record does not append a second no-op audit");
        Check(await store.FlushAsync(), "identical-content restore is accepted by the persistent writer");
    }
    using (var loaded = new PortraitHistoryStore(FileFor("same-content-restore")))
    {
        var records = loaded.GetRecords(key);
        Check(records.Count == 4 && records[0].Kind == PortraitHistoryRecordKind.Restore && records[0].TargetID == records[3].ID
            && loaded.CurrentRecordID(key) == records[0].ID && loaded.TryGetUndo(key, out var undo) && undo.Record.ID == records[1].ID,
            "identical-content restore audit provenance and cursor survive process reload");
    }

    // A global UTF-8 byte budget must not erase valid records when one snapshot cannot fit.
    using (var store = new PortraitHistoryStore(FileFor("budget"), () => now, byteLimit: 1800))
    {
        var oldest = store.Record(key, "{\"text\":\"" + new string('a', 220) + "\"}").Record!;
        for (var i = 1; i < 12; i++)
            Check(store.Record(key, "{\"text\":\"" + new string((char)('a' + i), 220) + "\"}").Status == PortraitHistoryWriteStatus.Added, "new snapshots trim globally oldest unprotected records within the byte budget");
        Check(store.GetRecords(key).Count < 12 && !store.TryGetRecord(key, oldest.ID, out _), "budget pruning retires oldest history before the latest cursor");
        Check(await store.FlushAsync() && new FileInfo(FileFor("budget")).Length <= 1800, "actual serialized UTF-8 file obeys the configured global byte ceiling");
        var bytesBefore = File.ReadAllBytes(FileFor("budget"));
        var recordsBefore = store.GetRecords(key).Count;
        Check(store.Record(key, "{\"text\":\"" + new string('x', 3000) + "\"}").Status == PortraitHistoryWriteStatus.TooLarge, "one oversized snapshot is rejected");
        Check(await store.FlushAsync() && store.GetRecords(key).Count == recordsBefore && File.ReadAllBytes(FileFor("budget")).SequenceEqual(bytesBefore), "oversized rejection preserves existing in-memory records and file bytes");
        Check(!Directory.GetFiles(directory, "*.tmp").Any(), "atomic replacement leaves no incomplete temporary snapshot behind");
    }
    var corrupt = FileFor("corrupt");
    File.WriteAllText(corrupt, "{broken history", new UTF8Encoding(false));
    using (var store = new PortraitHistoryStore(corrupt))
    {
        Check(store.IsReadOnly && store.LoadError is not null, "a corrupted history file opens in explicit read-only mode");
        Check(store.Record(key, "{}").Status == PortraitHistoryWriteStatus.ReadOnlyCorrupt && await store.FlushAsync() && File.ReadAllText(corrupt) == "{broken history", "recording cannot silently overwrite corrupt evidence");
    }
    var tooBig = FileFor("over-limit-input");
    File.WriteAllText(tooBig, new string('x', 10 * 1024 * 1024 + 1));
    using (var store = new PortraitHistoryStore(tooBig))
        Check(store.IsReadOnly && store.Record(key, "{}").Status == PortraitHistoryWriteStatus.ReadOnlyCorrupt, "an already oversized file is preserved and not loaded into an active writer");
    var sequenceOverflow = FileFor("sequence-overflow");
    var overflowDocument = JsonNode.Parse(File.ReadAllText(FileFor("isolation")))!;
    overflowDocument["NextID"] = long.MaxValue;
    File.WriteAllText(sequenceOverflow, overflowDocument.ToJsonString());
    using (var store = new PortraitHistoryStore(sequenceOverflow))
        Check(store.IsReadOnly && store.Record(key, "{\"head\":3}").Status == PortraitHistoryWriteStatus.ReadOnlyCorrupt, "a corrupt exhausted sequence cannot wrap the next persistent history identity");

    // Replacing a pending write with a newer full document must never let an older save win.
    var orderedPath = FileFor("ordered");
    using (var store = new PortraitHistoryStore(orderedPath, () => now))
    {
        for (var i = 0; i < 150; i++) store.Record(key, "{\"pose\":" + i + "}");
        Check(await store.FlushAsync(), "burst of edits drains through one sequential writer");
    }
    using (var loaded = new PortraitHistoryStore(orderedPath))
        Check(loaded.GetRecords(key).Count == 150 && loaded.GetRecords(key)[0].SnapshotJSON == "{\"pose\":149}", "the final persisted document contains every accepted edit and the newest state");

    using (var store = new PortraitHistoryStore(FileFor("prepare-budget"), () => now, byteLimit: 2700))
    {
        store.Record(key, "{\"text\":\"" + new string('a', 700) + "\"}");
        var current = store.Record(key, "{\"text\":\"" + new string('b', 700) + "\"}").Record!;
        Check(store.TryGetUndo(key, out var undo) && store.PrepareCommit(undo).Status == PortraitHistoryWriteStatus.TooLarge
            && store.CurrentRecordID(key) == current.ID && store.Count == 2,
            "prepare refuses an audit that cannot retain cursor and redo within budget before native mutation");
    }
    using (var store = new PortraitHistoryStore(FileFor("global-budget"), () => now, byteLimit: 3500))
    {
        var another = key with { AccountID = 100, ContentID = 200 };
        var protectedCurrent = store.Record(another, "{\"text\":\"" + new string('z', 500) + "\"}").Record!;
        for (var i = 0; i < 8; i++) store.Record(key, "{\"text\":\"" + new string((char)('a' + i), 500) + "\"}");
        Check(store.CurrentRecordID(another) == protectedCurrent.ID && store.TryGetRecord(another, protectedCurrent.ID, out _) && store.Bytes <= 3500,
            "global pruning preserves another character's current snapshot while trimming older edits");
    }
    var blockedPath = FileFor("disk-failure");
    Directory.CreateDirectory(blockedPath);
    using (var store = new PortraitHistoryStore(blockedPath, () => now))
    {
        store.Record(key, "{\"pose\":1}");
        Check(!await store.FlushAsync() && store.PersistenceError is not null && Directory.Exists(blockedPath),
            "disk replacement failure is observable and does not delete an unrelated directory");
        Directory.Delete(blockedPath);
        store.Record(key, "{\"pose\":2}");
        Check(await store.FlushAsync() && store.PersistenceError is null && File.Exists(blockedPath),
            "a later valid write recovers the sequential worker after an earlier disk failure");
    }

    // Mouse holds cannot be committed by idle; keyboard activity is coalesced until release + idle.
    now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(8));
    using (var store = new PortraitHistoryStore(FileFor("batch"), () => now))
    {
        store.Record(key, "{\"camera\":0}");
        var batch = new PortraitHistoryBatch(store, () => now, TimeSpan.FromMilliseconds(500));
        batch.Observe(key, "{\"camera\":1}", pointerDown: true, keyDown: false);
        now = now.AddSeconds(3);
        Check(batch.FlushIdle() is null && store.GetRecords(key).Count == 1, "holding a mouse gesture never commits merely because idle time passed");
        batch.Observe(key, "{\"camera\":2}", pointerDown: true, keyDown: false);
        batch.Observe(key, "{\"camera\":3}", pointerDown: false, keyDown: false);
        Check(store.GetRecords(key).Count == 2 && store.GetRecords(key)[0].SnapshotJSON == "{\"camera\":3}", "mouse release commits only the final snapshot of a drag");
        batch.Observe(key, "{\"camera\":4}", pointerDown: false, keyDown: true);
        now = now.AddSeconds(2);
        Check(batch.FlushIdle() is null && store.GetRecords(key).Count == 2, "holding a relevant keyboard key defers idle commit");
        batch.Observe(key, "{\"camera\":5}", pointerDown: false, keyDown: false);
        now = now.AddMilliseconds(499);
        Check(batch.FlushIdle() is null, "keyboard release waits for the full quiet interval");
        now = now.AddMilliseconds(1);
        Check(batch.FlushIdle()?.Status == PortraitHistoryWriteStatus.Added && store.GetRecords(key).Count == 3, "keyboard burst commits a single final snapshot after 500 ms quiet");
        batch.Observe(key, "{\"camera\":5}", pointerDown: false, keyDown: false);
        now = now.AddSeconds(1);
        batch.FlushIdle();
        Check(store.GetRecords(key).Count == 3, "idle observations of the same snapshot cannot grow history");
        batch.Observe(key, "{\"camera\":6}", pointerDown: true, keyDown: false);
        batch.Cancel();
        now = now.AddSeconds(1);
        Check(batch.FlushIdle() is null && store.GetRecords(key).Count == 3, "cancelling an invalidated editor discards its unfinished batch");
        batch.Observe(key, "{\"camera\":7}", pointerDown: false, keyDown: true);
        Check(batch.Flush()?.Status == PortraitHistoryWriteStatus.Added && store.GetRecords(key).Count == 4, "explicit editor-close flush commits its final valid pending snapshot");
        batch.Observe(key, "{\"camera\":8}", pointerDown: false, keyDown: true);
        var different = key with { SourceID = 9 };
        batch.Observe(different, "{\"camera\":90}", pointerDown: false, keyDown: false);
        Check(store.GetRecords(key).Count == 5 && store.GetRecords(key)[0].SnapshotJSON == "{\"camera\":8}", "switching portrait keys flushes the old detached safe snapshot without a native apply");
        Check(batch.Flush()?.Status == PortraitHistoryWriteStatus.Added && store.GetRecords(different).Count == 1, "a switched editor starts its own independent input batch");
        var label = store.Record(different, "{\"camera\":91}", "镜头调整").Record!;
        Check(label.Label == "镜头调整", "explicit operation descriptions remain attached to the accepted snapshot");
    }
    // Observe can commit on a release or editor switch; callers must see those results immediately.
    using (var store = new PortraitHistoryStore(FileFor("batch-results"), () => now))
    {
        var batch = new PortraitHistoryBatch(store, () => now);
        PortraitHistoryWriteResult? Observe(PortraitHistoryKey observedKey, string snapshot, bool down) =>
            batch.Observe(observedKey, snapshot, down, false);
        Check(Observe(key, "{\"camera\":1}", true) is null, "an unfinished input observation has no commit result");
        Check(Observe(key, "{\"camera\":2}", false)?.Status == PortraitHistoryWriteStatus.Added,
            "mouse release exposes its committed result so history UI and errors can refresh immediately");
        Observe(key, "{\"camera\":3}", true);
        var other = key with { SourceID = 17 };
        Check(Observe(other, "{\"camera\":17}", true)?.Status == PortraitHistoryWriteStatus.Added,
            "switching editor keys exposes the detached old snapshot commit result");
        batch.Cancel();
        Observe(key, "invalid-json", true);
        var firstFailure = Observe(other, "{\"camera\":18}", false);
        Check(firstFailure?.Status == PortraitHistoryWriteStatus.InvalidJson
            && store.GetRecords(other)[0].SnapshotJSON == "{\"camera\":18}",
            "a switch plus mouse release retains an earlier failure while committing the new valid snapshot");
        Observe(key, "{\"camera\":4}", true);
        var lastFailure = Observe(other, "invalid-json", false);
        Check(lastFailure?.Status == PortraitHistoryWriteStatus.InvalidJson
            && store.GetRecords(key)[0].SnapshotJSON == "{\"camera\":4}",
            "a later release failure is exposed even when the editor switch successfully committed first");
        Observe(key, "{\"camera\":5}", true);
        Check(Observe(other, "{\"camera\":18}", false)?.Status == PortraitHistoryWriteStatus.Added,
            "an added switch snapshot is still reported when the following release snapshot is duplicate");
    }
    // A passive sample after preview rollback must preserve the user's history cursor and redo.
    using (var store = new PortraitHistoryStore(FileFor("passive-rollback"), () => now))
    {
        store.Record(key, "{\"camera\":0}");
        var latest = store.Record(key, "{\"camera\":1}").Record!;
        Check(store.TryGetUndo(key, out var undo) && store.CommitNavigation(undo).Status == PortraitHistoryWriteStatus.Added,
            "rollback regression starts with a real redo branch");
        var batch = new PortraitHistoryBatch(store, () => now);
        var advance = typeof(PortraitHistoryBatch).GetMethod("AdvanceInput");
        Check(advance is not null, "passive input edges have an API that cannot enqueue a rollback snapshot");
        PortraitHistoryWriteResult? Advance(bool pointer, bool keyboard) => (PortraitHistoryWriteResult?)advance!.Invoke(batch, [pointer, keyboard]);
        var count = store.Count;
        Check(Advance(true, false) is null && Advance(false, false) is null, "passive mouse edges cannot create a history transaction");
        now = now.AddSeconds(1);
        Check(batch.FlushIdle() is null && store.Count == count && store.TryGetRedo(key, out var redo) && redo.Record.ID == latest.ID,
            "automatic visual rollback followed by idle preserves redo and history count");
        batch.Observe(key, "{\"camera\":2}", true, false);
        Check(Advance(false, false)?.Status == PortraitHistoryWriteStatus.Added && store.GetRecords(key)[0].SnapshotJSON == "{\"camera\":2}",
            "a release with no changed payload commits the pending mouse gesture immediately");
        batch.Observe(key, "{\"camera\":3}", false, true);
        Advance(false, false);
        now = now.AddMilliseconds(500);
        Check(batch.FlushIdle()?.Status == PortraitHistoryWriteStatus.Added, "passive key release starts the final quiet interval for an existing keyboard edit");
        batch.Observe(key, "{\"camera\":4}", true, false);
        batch.Cancel();
        Advance(false, false);
        now = now.AddSeconds(1);
        Check(batch.FlushIdle() is null && store.GetRecords(key)[0].SnapshotJSON == "{\"camera\":3}", "cancelled preview transactions cannot be resurrected by input release");
    }
    Console.WriteLine($"{passed}/{passed} portrait history checks passed");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    Directory.Delete(directory, true);
}
#endif
