using FireGaze.PortraitPreview;
using System.Reflection;

internal static class HistoryDurableAcceptanceTests
{
    internal static async Task<int> Run(string root, PortraitHistoryKey key)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        Task<bool> Boundary(PortraitHistoryWriteResult result)
        {
            var task = typeof(PortraitHistoryWriteResult).GetProperty("Persistence")?.GetValue(result) as Task<bool>;
            Check(task is not null, "each admitted edit exposes its own awaitable persistence boundary");
            return task!;
        }

        // The caller can distinguish immediate admission from completed durable storage.
        // The real writer is held at its scheduling boundary, without replacing its I/O.
        var pendingPath = Path.Combine(root, "history-durable-pending.json");
        using (var pending = new PortraitHistoryStore(pendingPath))
        {
            var writer = Writer(pending);
            var writerGate = typeof(PortraitHistoryWriter).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;
            Task<bool> firstBoundary, secondBoundary, drain;
            lock (writerGate)
            {
                var pendingFirst = pending.Record(key, "{\"pose\":1}");
                var pendingSecond = pending.Record(key, "{\"pose\":2}");
                Check(pendingFirst.Status == PortraitHistoryWriteStatus.Added && pendingSecond.Status == PortraitHistoryWriteStatus.Added,
                    "admitting edits remains nonblocking while the real file worker cannot advance");
                firstBoundary = Boundary(pendingFirst);
                secondBoundary = Boundary(pendingSecond);
                Check(!firstBoundary.IsCompleted && !secondBoundary.IsCompleted && !File.Exists(pendingPath),
                    "queue admission is visibly pending rather than falsely reported durable");
                drain = pending.BeginDispose();
                Check(!drain.IsCompleted && !firstBoundary.IsCompleted && !secondBoundary.IsCompleted,
                    "closing admission returns a drain without blocking on pending per-edit boundaries");
            }
            Check(await drain.WaitAsync(TimeSpan.FromSeconds(5)) && await firstBoundary && await secondBoundary,
                "BeginDispose drains every admitted persistence boundary in submission order");
            using var loaded = new PortraitHistoryStore(pendingPath);
            Check(!loaded.IsReadOnly && loaded.Count == 2 && loaded.GetRecords(key)[0].SnapshotJSON == "{\"pose\":2}",
                "coalesced persistence retains both admitted records and the final cursor");
        }

        // A directory at the final file path is a real permanent publication failure.
        var blockedPath = Path.Combine(root, "history-unwritable-durable.json");
        Directory.CreateDirectory(blockedPath);
        using (var blocked = new PortraitHistoryStore(blockedPath, retryDelay: (_, _) => Task.CompletedTask))
        {
            var result = blocked.Record(key, "{\"pose\":1}");
            Check(result.Status == PortraitHistoryWriteStatus.Added && !await Boundary(result).WaitAsync(TimeSpan.FromSeconds(5)),
                "an admitted edit with a real publication failure never reports successful durability");
            Check(blocked.Count == 1 && blocked.CurrentRecordID(key) == result.Record?.ID,
                "a failed background write retains its admitted in-memory edit for a later full snapshot retry");
            Check(!string.IsNullOrWhiteSpace(blocked.PersistenceError) && Directory.Exists(blockedPath),
                "a failed durable edit exposes its persistence error and preserves the destination");
        }

        var path = Path.Combine(root, "history-durable-acceptance.json");
        using var store = new PortraitHistoryStore(path);
        var first = store.Record(key, "{\"pose\":1}");
        Check(first.Status == PortraitHistoryWriteStatus.Added && await Boundary(first).WaitAsync(TimeSpan.FromSeconds(5)) && File.Exists(path),
            "successful persistence completion means the atomic file exists before flush or dispose");
        using (var loaded = new PortraitHistoryStore(path))
            Check(!loaded.IsReadOnly && loaded.Count == 1 && loaded.CurrentRecordID(key) == first.Record?.ID,
                "a second store immediately reloads the accepted edit without a graceful logout");

        using (var rejected = new PortraitHistoryStore(Path.Combine(root, "history-durable-rejected.json")))
        {
            await Writer(rejected).BeginDispose();
            var result = rejected.Record(key, "{\"pose\":1}");
            Check(result.Status == PortraitHistoryWriteStatus.Disposed && result.Record is null
                && typeof(PortraitHistoryWriteResult).GetProperty("Persistence")?.GetValue(result) is null && rejected.Count == 0,
                "failed queue admission has no persistence success boundary or published record");
        }
        return checks;
    }

    private static PortraitHistoryWriter Writer(PortraitHistoryStore store) => (PortraitHistoryWriter)typeof(PortraitHistoryStore)
        .GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
}
