using System.Collections.Concurrent;
using FireGaze.PortraitPreview;

internal static class AsyncCacheWriterTests
{
    internal static (int Passed, int Failed) Run()
    {
        var passed = 0;
        var failed = 0;
        void Check(bool value, string name)
        {
            if (value) passed++;
            else { failed++; Console.WriteLine("FAIL async-cache-writer " + name); }
        }
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var output = new ConcurrentQueue<string>();
        var writer = new OwnedCacheWriter(snapshot =>
        {
            if (snapshot == "running")
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            output.Enqueue(snapshot);
            return true;
        });
        try
        {
            Check(writer.Queue(1, "running") && entered.Wait(TimeSpan.FromSeconds(5)), "real managed write is blocked");
            Check(writer.Queue(2, "latest"), "newest accepted snapshot remains pending");
            var close = Task.Factory.StartNew(writer.BeginDispose, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            Check(close.Wait(TimeSpan.FromSeconds(5)), "Framework seal returns before blocked managed write is released");
            if (!close.IsCompletedSuccessfully) return (passed, failed + 1);
            var drain = close.Result;
            Check(!drain.IsCompleted && ReferenceEquals(drain, writer.BeginDispose()), "one idempotent drain covers running and pending writes");
            Check(!writer.Queue(3, "late") && !writer.NeedsRevision(3), "sealed writer refuses late observations");
            var flush = writer.FlushAsync();
            Check(!flush.IsCompleted, "closing does not acknowledge unfinished durable bytes");
            release.Set();
            Check(drain.Wait(TimeSpan.FromSeconds(5)) && flush.GetAwaiter().GetResult(), "drain and pre-close flush finish after actual managed writes");
            Check(output.SequenceEqual(["running", "latest"]) && writer.SavedRevision == 2 && writer.CompletedWrites == 2,
                "close persists accepted newest snapshot without allowing a later overwrite");
            Check(ReferenceEquals(drain, writer.BeginDispose()), "completed close preserves its task identity");
        }
        finally { release.Set(); writer.Dispose(); }

        foreach (var throws in new[] { false, true })
        {
            using var failedWriter = new OwnedCacheWriter(_ => throws ? throw new IOException("managed disk failure") : false);
            Check(failedWriter.Queue(1, "rejected"), "failure fixture accepts one immutable snapshot");
            var drain = failedWriter.BeginDispose();
            Check(drain.Wait(TimeSpan.FromSeconds(5)) && !failedWriter.FlushAsync().GetAwaiter().GetResult(),
                "failed managed drain completes while flush preserves the failed result");
            Check(failedWriter.LastWriteSucceeded == false && failedWriter.FailedWrites == 1 && failedWriter.SavedRevision == 0,
                "failure remains observable after seal without another observation");
            Check(ReferenceEquals(drain, failedWriter.BeginDispose()) && !failedWriter.Queue(2, "late"),
                "failed close is idempotent and cannot reopen admission");
        }
        return (passed, failed);
    }
}
