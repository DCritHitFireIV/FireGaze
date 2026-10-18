using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FireGaze.PortraitPreview;

// An offline, reproducible probe. No plugin, native API, game process, or real user history is opened.
var output = args.Length > 0 ? Path.GetFullPath(args[0]) : null;
var directory = Path.Combine(Path.GetTempPath(), "firegaze-history-performance-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "synthetic-history.json");
var key = new PortraitHistoryKey(101, 202, PortraitHistoryEntryPoint.GearsetPortrait, 1, "qa-synthetic-v1");
var zone = TimeZoneInfo.CreateCustomTimeZone("QA-plus-eight", TimeSpan.FromHours(8), "QA", "QA");
var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.FromHours(8));
var checks = new List<string>();
var measurements = new List<Measurement>();
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks.Add(label);
    Console.WriteLine("PASS " + label);
}
PortraitEditorSnapshot Snapshot(int index)
{
    var customize = new byte[26];
    customize[0] = 1; customize[1] = 0; customize[4] = 1; customize[5] = 1; customize[6] = 1;
    return new()
    {
        GameBuild = "qa-synthetic-build",
        Look = new(new PortraitPiece[13], true, false, false),
        Customize = customize,
        Composition = new()
        {
            CameraPosition = new(0, .15f, 1.25f, 0), CameraTarget = new(0, .1f, 0, 0),
            CameraYaw = (index % 360) * .01f, CameraPitch = (index % 100) * .001f,
            CameraDistance = 1.25f, CameraZoomNormalized = .5f, CameraZoom = 160,
            BannerTimeline = 1, Paused = true, AnimationProgress = .001f,
            DirectionalRed = 255, DirectionalGreen = 255, DirectionalBlue = 255,
            DirectionalBrightness = 127, AmbientRed = 51, AmbientGreen = 51, AmbientBlue = 51,
            AmbientBrightness = 127, Background = 1, Frame = 1, Decoration = 1,
        },
    };
}
byte[] Fixture(int count)
{
    var records = Enumerable.Range(1, count).Select(index => new PortraitHistoryRecord(index, key,
        now.AddMinutes((index - count) * 45), index == 1 ? null : index - 1,
        PortraitHistoryRecordKind.Edit, Snapshot(index).ToJSON(), Label: "调整构图" )).ToArray();
    return JsonSerializer.SerializeToUtf8Bytes(new
    {
        Version = 1, NextID = count + 1L, Records = records,
        Cursors = new[] { new { Key = key, ID = (long)count, Redo = Array.Empty<long>() } },
    });
}
Measurement Measure(string name, int iterations, Action action)
{
    var elapsed = new double[iterations];
    var allocated = new long[iterations];
    var totalAllocated = new long[iterations];
    var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
    for (var index = 0; index < iterations; index++)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var totalBefore = GC.GetTotalAllocatedBytes(false);
        var started = Stopwatch.GetTimestamp();
        action();
        elapsed[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        allocated[index] = GC.GetAllocatedBytesForCurrentThread() - before;
        totalAllocated[index] = GC.GetTotalAllocatedBytes(false) - totalBefore;
    }
    var sorted = elapsed.Order().ToArray();
    var result = new Measurement(name, iterations, sorted[0], sorted[iterations / 2], sorted[^1],
        allocated.Average(), totalAllocated.Average(), GC.CollectionCount(0) - gen0,
        GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
    measurements.Add(result);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return result;
}
void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

try
{
    // Match the current production serializer's actual escaped UTF-8 size, not an invented bytes/row estimate.
    var low = 1; var high = 10000;
    var target = (int)(PortraitHistoryStore.MaximumBytes * .9995);
    while (low < high)
    {
        var middle = (low + high + 1) / 2;
        if (Fixture(middle).Length <= target) low = middle; else high = middle - 1;
    }
    var count = low;
    var bytes = Fixture(count);
    var originalBytes = bytes.Length;
    File.WriteAllBytes(path, bytes);
    bytes = [];
    Check(originalBytes <= PortraitHistoryStore.MaximumBytes && originalBytes >= PortraitHistoryStore.MaximumBytes * .99,
        "fixture occupies at least 99 percent of the real 10 MiB UTF-8 budget");
    Check(Snapshot(1).IsValid && PortraitEditorSnapshot.FromJSON(Snapshot(1).ToJSON()) is not null,
        "fixture uses structurally valid full production snapshot DTOs, no padding or process addresses");

    Collect();
    Measure("load-first-uncached-process", 1, () =>
    {
        using var loaded = new PortraitHistoryStore(path);
        Check(!loaded.IsReadOnly && loaded.Count == count && loaded.Bytes == originalBytes,
            "near-limit file loads through the production relationship and JSON validation");
    });
    Measure("load-repeated-warm-file", 5, () => { using var loaded = new PortraitHistoryStore(path); });
    Collect();
    var retainedBefore = GC.GetTotalMemory(true);
    using var store = new PortraitHistoryStore(path, () => now);
    var retainedStoreBytes = GC.GetTotalMemory(true) - retainedBefore;
    Check(store.GetRecords(key).All(record => PortraitEditorSnapshot.FromJSON(record.SnapshotJSON) is not null),
        "every loaded synthetic payload round trips the production snapshot structural validator");

    PortraitHistoryListItem[] rows = [];
    void RefreshRows()
    {
        var cursor = store.CurrentRecordID(key);
        rows = store.GetRecords(key).Select(record => new PortraitHistoryListItem(record.ID, record.Timestamp,
            record.Label, record.ID == cursor)).ToArray();
    }
    Measure("feature-equivalent-row-snapshot", 10, RefreshRows);
    var presentation = new PortraitHistoryPresentation();
    IReadOnlyList<PortraitHistoryViewDay> days = [];
    Measure("first-local-date-grouping", 1, () => days = presentation.Get(rows, zone));
    Check(days.Sum(day => day.Records.Count) == count && days.Count > 30,
        "local-date grouping preserves all near-limit records across more than thirty calendar days");
    var countingRows = new CountingRows(rows);
    var unchanged = presentation.Get(countingRows, zone);
    var reads = countingRows.Reads;
    var cached = Measure("one-thousand-unchanged-frames", 1000, () =>
    {
        if (!ReferenceEquals(unchanged, presentation.Get(countingRows, zone))) throw new InvalidOperationException("cache rebuilt");
    });
    Check(countingRows.Reads == reads, "one thousand stable frames enumerate or index the full source zero times");
    Check(cached.MeanCurrentThreadAllocatedBytes < 256,
        "stable cached frames allocate no record-proportional objects");
    var changedRows = rows.ToArray();
    changedRows[0] = changedRows[0] with { IsCurrent = !changedRows[0].IsCurrent };
    Measure("single-replaced-snapshot-rebuild", 1, () => days = presentation.Get(changedRows, zone));
    Check(!ReferenceEquals(unchanged, days) && days.SelectMany(day => day.Records).Single(row => row.ID == changedRows[0].ID).IsCurrent == changedRows[0].IsCurrent,
        "replaced snapshots rebuild and expose the new current marker");

    // Extreme header distribution: same legal rows spread across one day per record.
    var sparseRows = rows.Select((row, index) => row with { At = now.AddDays(-index) }).ToArray();
    Measure("one-calendar-day-per-record-grouping", 1, () => days = presentation.Get(sparseRows, zone));
    Check(days.Count == count, "sparse date grouping does not combine distinct local days");

    var nextSnapshot = count + 100;
    var edits = new List<PortraitHistoryWriteStatus>();
    var commit = Measure("near-cap-record-and-oldest-trim", 8, () =>
    {
        now = now.AddSeconds(1);
        edits.Add(store.Record(key, Snapshot(nextSnapshot++).ToJSON(), "近容量新编辑").Status);
    });
    Check(commit.MeanCurrentThreadAllocatedBytes < 2 * 1024 * 1024,
        "near-cap commit allocates under 2 MiB on the caller thread instead of serializing a full 10 MiB document");
    Check(edits.All(status => status == PortraitHistoryWriteStatus.Added), "all eight near-limit new edits are accepted");
    Check(store.Bytes <= PortraitHistoryStore.MaximumBytes && store.Count < count + 8,
        "global pruning gradually removes old full records and stays within the UTF-8 ceiling");
    Check(!store.TryGetRecord(key, 1, out _), "global pruning removes the oldest unprotected history first");
    var previousTop = store.CurrentRecordID(key)!.Value;
    Check(store.TryGetUndo(key, out var undo), "near-limit current record retains an undo predecessor");
    var countBeforePrepare = store.Count; var bytesBeforePrepare = store.Bytes;
    var prepared = new List<PortraitHistoryWriteStatus>();
    var prepare = Measure("near-cap-prepare-without-publishing", 8,
        () => prepared.Add(store.PrepareCommit(undo).Status));
    Check(prepared.All(status => status == PortraitHistoryWriteStatus.Added)
        && store.Count == countBeforePrepare && store.Bytes == bytesBeforePrepare && store.CurrentRecordID(key) == previousTop,
        "near-limit preparation preserves published records, bytes and navigation cursor");
    Check(prepare.MeanCurrentThreadAllocatedBytes < 2 * 1024 * 1024,
        "near-limit preparation also avoids a full document allocation on the caller thread");
    var undoStatus = store.CommitNavigation(undo).Status;
    Check(undoStatus == PortraitHistoryWriteStatus.Added && store.TryGetRedo(key, out var redo) && redo.Record.ID == previousTop,
        "trimming an undo audit preserves the protected redo target");
    var branchStatus = store.Record(key, Snapshot(nextSnapshot++).ToJSON(), "撤回后新分支").Status;
    Check(branchStatus == PortraitHistoryWriteStatus.Added && !store.TryGetRedo(key, out _)
        && store.TryGetRecord(key, previousTop, out _),
        "a new branch clears redo navigation while retaining the prior branch for explicit history restore");
    var flushWatch = Stopwatch.StartNew();
    Check(await store.FlushAsync(), "newest near-limit document is atomically persisted by the production writer");
    flushWatch.Stop();
    Check(new FileInfo(path).Length == store.Bytes && store.Bytes <= PortraitHistoryStore.MaximumBytes,
        "persisted file length equals the accepted in-memory UTF-8 accounting");
    using (var reopened = new PortraitHistoryStore(path))
        Check(!reopened.IsReadOnly && reopened.Count == store.Count && reopened.CurrentRecordID(key) == store.CurrentRecordID(key),
            "trimmed parent relationships and branch cursor reload without dangling references");
    Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "atomic writes leave no incomplete temporary history files");
    GC.KeepAlive(store);
    var report = new
    {
        CapturedAtUTC = DateTimeOffset.UtcNow, OfflineOnly = true,
        Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
        SnapshotCount = count, OriginalFileBytes = originalBytes, LimitBytes = PortraitHistoryStore.MaximumBytes,
        RetainedManagedStoreBytesAfterFullCollection = retainedStoreBytes, FinalFileBytes = store.Bytes,
        FlushTailMilliseconds = flushWatch.Elapsed.TotalMilliseconds, ChecksPassed = checks.Count,
        Checks = checks, Measurements = measurements,
        PerformanceObservation = commit.MedianMilliseconds > 16.67
            ? "Record/trim synchronous work exceeds a 60 Hz frame on this machine; record is called on Framework thread."
            : "No median over one 60 Hz frame observed for synchronous record/trim in this offline probe.",
        Limits = new[] { "Structurally valid synthetic snapshots; no native/game-sheet acceptance claimed.",
            "First load is process/JIT cold but OS filesystem cache is warm after fixture write.",
            "Current-thread allocation excludes writer-worker allocation; approximate total allocation includes other .NET threads.",
            "Managed retained-memory delta is a GC estimate, not process-private bytes or GPU memory.",
            "No ImGui render, real input, plugin-host frame time, device variation, or long-session stability measured." },
    };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, json); }
    Console.WriteLine(json);
}
finally
{
    // The unique generated temp directory is the only deletion target; never a user configuration path.
    if (Path.GetDirectoryName(directory) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
        && Path.GetFileName(directory).StartsWith("firegaze-history-performance-", StringComparison.Ordinal))
        Directory.Delete(directory, recursive: true);
}

sealed record Measurement(string Name, int Iterations, double MinimumMilliseconds, double MedianMilliseconds,
    double MaximumMilliseconds, double MeanCurrentThreadAllocatedBytes, double MeanApproximateAllThreadAllocatedBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections);
sealed class CountingRows(IReadOnlyList<PortraitHistoryListItem> rows) : IReadOnlyList<PortraitHistoryListItem>
{
    public int Reads { get; private set; }
    public int Count => rows.Count;
    public PortraitHistoryListItem this[int index] { get { Reads++; return rows[index]; } }
    public IEnumerator<PortraitHistoryListItem> GetEnumerator()
    { foreach (var row in rows) { Reads++; yield return row; } }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
