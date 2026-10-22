using FireGaze.PortraitPreview;

internal static class PortraitStorageObservationCases
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "slow-storage-observation"); Directory.CreateDirectory(directory);
        var files = new SlowStatFiles();
        var storage = new PortraitStorageBudget(directory, 100_000, files);
        using var feature = new PortraitPreviewFeature(Path.Combine(directory, "history.json"), storage: storage);
        foreach (var path in new[] { "editor", "inventory" })
        {
            feature.MakeStorageObservationDue();
            files.Arm();
            // The actual Framework owns a dedicated game thread. Do not put the simulated
            // callback in the same worker pool as the deliberately blocked disk operation.
            var frame = Task.Factory.StartNew(() =>
            {
                if (path == "editor") feature.PollThrottled();
                else feature.ObserveOwned(new(OwnedSourceKind.Inventory), [new(100, 1, 0)]);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!files.Entered.Task.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("The real directory stat was not reached.");
            // The callback must not wait on the deliberately blocked stat. Allow a full
            // second for a cold test process/JIT while still catching an accidental sync I/O.
            var returned = frame.Wait(TimeSpan.FromSeconds(1));
            files.Release();
            frame.GetAwaiter().GetResult();
            feature.WaitStorageObservation();
            check(returned, $"{path} Framework callback returns while a real storage stat is held");
        }
    }

    private sealed class SlowStatFiles : PortraitStorageFiles
    {
        private int armed;
        private ManualResetEventSlim release = new(false);
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm()
        {
            release = new(false);
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref armed, 1);
        }
        public void Release() => release.Set();
        public override long? GetLength(string path)
        {
            var length = base.GetLength(path);
            if (Interlocked.Exchange(ref armed, 0) != 0)
            {
                Entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Slow stat fixture was not released.");
            }
            return length;
        }
    }
}
