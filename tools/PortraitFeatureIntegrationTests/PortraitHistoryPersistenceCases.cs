using System.Reflection;
using FireGaze.PortraitPreview;

internal static class PortraitHistoryPersistenceCases
{
    internal static void Run(string root, Action<bool, string> check)
    {
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
