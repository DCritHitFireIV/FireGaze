using System.Reflection;
using System.Text;
using FireGaze.PortraitPreview;

internal static class HistoryManagedDrainTests
{
    internal static async Task<int> Run(string root, PortraitHistoryKey key)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++; Console.WriteLine("PASS " + name);
        }

        // Hold only the existing writer admission gate, rather than replacing its worker or I/O.
        // This fixes the scheduling boundary while the actual atomic file writer remains unchanged.
        var path = Path.Combine(root, "history-async-close.json");
        using var store = new PortraitHistoryStore(path);
        var writer = (PortraitHistoryWriter)typeof(PortraitHistoryStore)
            .GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var gate = typeof(PortraitHistoryWriter).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;
        using var release = new ManualResetEventSlim();
        var sealedDrain = new TaskCompletionSource<Task<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Task.Run(() =>
        {
            lock (gate)
            {
                if (store.Record(key, "{\"pose\":1}").Status != PortraitHistoryWriteStatus.Added
                    || store.Record(key, "{\"pose\":2}").Status != PortraitHistoryWriteStatus.Added)
                    throw new InvalidOperationException("history close fixture failed to admit real snapshots");
                sealedDrain.SetResult(store.BeginDispose());
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
        });
        try
        {
            var drain = await sealedDrain.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!drain.IsCompleted, "history Framework seal returns before accepted writer work can complete");
            Check(ReferenceEquals(drain, store.BeginDispose()), "history store seals once and returns one drain task");
            Check(store.Record(key, "{\"pose\":3}").Status == PortraitHistoryWriteStatus.Disposed,
                "history close immediately rejects a late edit without awaiting disk");
            release.Set();
            await fixture.WaitAsync(TimeSpan.FromSeconds(5));
            Check(await drain.WaitAsync(TimeSpan.FromSeconds(5)), "history close finishes its real atomic writer after the worker is released");
            Check(ReferenceEquals(drain, store.BeginDispose()) && ReferenceEquals(writer.BeginDispose(), writer.BeginDispose()),
                "history writer and store keep completed close task identities");
            using var loaded = new PortraitHistoryStore(path);
            Check(loaded.GetRecords(key).Count == 2 && loaded.GetRecords(key)[0].SnapshotJSON == "{\"pose\":2}",
                "history drain makes all accepted records and the final cursor durable");
        }
        finally { release.Set(); await fixture; }

        var blockedPath = Path.Combine(root, "history-close-disk-failure.json");
        Directory.CreateDirectory(blockedPath);
        using var failed = new PortraitHistoryWriter(blockedPath, 1024, retryDelay: (_, _) => Task.CompletedTask);
        Check(failed.Queue(new(Encoding.UTF8.GetBytes("{\"records\":["), [Encoding.UTF8.GetBytes("{}")], Encoding.UTF8.GetBytes("]}"))),
            "failed history close admits an immutable document before the disk boundary");
        var failureDrain = failed.BeginDispose();
        Check(!await failureDrain.WaitAsync(TimeSpan.FromSeconds(5)) && !string.IsNullOrWhiteSpace(failed.LastError),
            "history close reports real disk failure after its worker settles");
        Check(ReferenceEquals(failureDrain, failed.BeginDispose()) && !failed.Queue(new([], [], [])) && Directory.Exists(blockedPath),
            "failed history close is idempotent and preserves the unrelated destination directory");
        return checks;
    }
}
