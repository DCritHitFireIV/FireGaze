using FireGaze.PortraitPreview;

internal static class ManagedDrainOutcomeCases
{
    internal static async Task<int> RunAsync(string root, PortraitHistoryKey key, PortraitEditorSnapshot snapshot)
    {
        var checks = 0;
        var failures = new List<string>();
        void Check(bool value, string name)
        {
            checks++;
            Console.WriteLine((value ? "PASS " : "FAIL ") + name);
            if (!value) failures.Add(name);
        }

        // Actual writers complete normally on I/O failure; their bool/status outcome must reach
        // the host even though no Framework poll will run after it has disposed the feature.
        // Keep the directory name outside every registered portrait file family so
        // this fixture exercises the writer's path failure without poisoning the
        // shared storage census for later cases in the same root.
        var invalidPath = Path.Combine(root, "writer-fixture-directory");
        Directory.CreateDirectory(invalidPath);
        using var history = new PortraitHistoryStore(invalidPath);
        Check(history.Record(key, snapshot.ToJSON(), "history final write").Record is not null,
            "history failure fixture accepts an actual immutable document");
        using var owned = new OwnedCacheWriter(_ => false);
        Check(owned.Queue(1, "owned final snapshot"), "owned failure fixture accepts an actual immutable snapshot");
        var historyClose = history.BeginDispose();
        var ownedClose = owned.BeginDispose();
        await historyClose;
        await ownedClose;
        Check(!historyClose.Result && owned.LastWriteSucceeded == false,
            "real history and owned failures are non-fault task outcomes");

        foreach (var (name, failedWork) in new (string, Task)[] { ("history", historyClose), ("owned", ownedClose) })
        {
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reported = new List<Exception>();
            var combined = PortraitManagedDrain.Combine(reported.Add, failedWork, accepted.Task);
            Check(!combined.IsCompleted, name + " failed outcome cannot skip another accepted managed task");
            Check(reported.Count == 0, name + " failure is reported only after every accepted task settles");
            accepted.SetResult();
            Exception? failure = null;
            try { await combined; }
            catch (Exception error) { failure = error; }
            Check(failure is not null && combined.IsFaulted,
                name + " unsuccessful final persistence remains observable by the host drain");
            Check(reported.Count == 1 && ReferenceEquals(reported[0], failure),
                name + " final persistence failure is logged once and preserved for host cleanup");
        }

        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mixed = PortraitManagedDrain.Combine(Task.FromException(new IOException("disk unavailable")), pending.Task);
        Check(!mixed.IsCompleted, "faulted managed work still waits for every accepted task");
        pending.SetResult();
        try { await mixed; }
        catch (IOException) { }
        Check(mixed.IsFaulted, "exceptional managed failure stays faulted after the other task completes");
        await PortraitManagedDrain.Combine(Task.FromResult(true), Task.CompletedTask, null);
        Check(true, "successful or absent managed work completes normally");

        if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
        return checks;
    }
}
