using System.Reflection;
using FireGaze.PortraitPreview;

internal static class PortraitHistoryPersistenceCases
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var favoriteDirectory = Path.Combine(root, "favorite-busy");
        Directory.CreateDirectory(favoriteDirectory);
        using (var saving = new PortraitPreviewFeature(Path.Combine(favoriteDirectory, "history.json")))
        {
            saving.Sample(); saving.OpenPreview();
            saving.Store.FlushAsync().GetAwaiter().GetResult();
            using (var held = new FileStream(Path.Combine(favoriteDirectory, ".portrait-storage.lock"),
                       FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                saving.SaveFavorite("最后一次收藏", []);
                check(saving.FavoritesStatus.Contains("正在保存") && saving.Favorites.Count == 0,
                    "favorite accepted during real storage Busy remains visibly pending rather than falsely saved");
            }
            Thread.Sleep(1200);
            saving.SampleLibrary();
            check(saving.Favorites.Count == 1 && saving.FavoritesStatus.Contains("已保存"),
                "favorite retries after the OS lease returns without requiring a second save action");
        }
        var recoveryDirectory = Path.Combine(root, "storage-recovery");
        Directory.CreateDirectory(recoveryDirectory);
        using (var recovery = new PortraitPreviewFeature(Path.Combine(recoveryDirectory, "history.json")))
        {
            var pressure = Path.Combine(recoveryDirectory, "portrait-favorites-v1.json.bak");
            using (var fill = File.Create(pressure)) fill.SetLength(PortraitStorageBudget.DefaultLimitBytes);
            recovery.Sample();
            check(!recovery.Store.FlushAsync().GetAwaiter().GetResult() && recovery.Store.PersistenceError is not null,
                "shared capacity exhaustion leaves the last accepted edit visibly undurable");
            var count = recovery.Store.Count;
            File.Delete(pressure);
            // Recovery runs before native snapshot throttling. No new edit is supplied.
            Thread.Sleep(2100);
            recovery.PollThrottled();
            recovery.WaitStorageObservation();
            check(recovery.Store.FlushAsync().GetAwaiter().GetResult()
                && recovery.Store.Count == count && recovery.Store.PersistenceError is null,
                "real external space recovery retries the retained last edit without a new editor operation");
        }
        using var feature = new PortraitPreviewFeature(Path.Combine(root, "feature-history-pending.json"));
        var writer = (PortraitHistoryWriter)typeof(PortraitHistoryStore)
            .GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(feature.Store)!;
        var writerGate = typeof(PortraitHistoryWriter).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;
        lock (writerGate)
        {
            feature.Sample();
            check(feature.Store.Count == 1 && feature.History.Count == 1 && feature.HistoryStatus.Contains("正在保存", StringComparison.Ordinal),
                "feature publishes admitted history immediately while identifying its incomplete persistence");
        }
        check(feature.Store.FlushAsync().GetAwaiter().GetResult(), "feature pending fixture reaches the real atomic file writer");
        feature.SetFitSuppression(true);
        var captures = feature.Native.CaptureCalls;
        feature.PollThrottled();
        check(!feature.HistoryStatus.Contains("正在保存", StringComparison.Ordinal) && feature.Native.CaptureCalls == captures,
            "completed persistence is observed before fit suppression without recapturing native state");

        feature.SetFitSuppression(false);
        feature.EditComposition(c => c with { CameraYaw = .25f });
        feature.RecordDirect();
        check(feature.Store.FlushAsync().GetAwaiter().GetResult(), "history navigation fixture has two durable snapshots");
        foreach (var operation in new[] { "undo", "redo", "restore" })
        {
            lock (writerGate)
            {
                if (operation == "undo") feature.Undo();
                else if (operation == "redo") feature.Redo();
                else feature.RestoreHistory(feature.Store.GetRecords(feature.Key!.Value)[^1].ID);
                check(feature.HistoryStatus.Contains("正在保存", StringComparison.Ordinal),
                    $"{operation} exposes pending persistence for its newly admitted audit record");
            }
            check(feature.Store.FlushAsync().GetAwaiter().GetResult(), $"{operation} persistence finishes through the real writer");
        }
    }
}
