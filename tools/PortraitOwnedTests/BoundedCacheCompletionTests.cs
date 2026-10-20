using System.Reflection;
using FireGaze.PortraitPreview;

internal static class BoundedCacheCompletionTests
{
    internal static (int Passed, int Failed) Run()
    {
        var passed = 0; var failed = 0;
        void Check(bool value, string name)
        {
            if (value) passed++;
            else { failed++; Console.WriteLine("FAIL bounded-cache " + name); }
        }
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var writer = new OwnedCacheWriter(snapshot =>
        {
            if (snapshot == "first") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            return snapshot != "last";
        });
        try
        {
            var gate = typeof(OwnedCacheWriter).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;
            Task<bool> first;
            lock (gate)
            {
                writer.Queue(1, "first");
                first = writer.FlushAsync();
                var changed = 0;
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 100_000; index++)
                    if (!ReferenceEquals(first, writer.FlushAsync())) changed++;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(changed == 0 && allocated == 0,
                    $"100000 pending Flush calls share one completion with zero caller metadata (new tasks {changed}, bytes {allocated})");
            }
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "first physical write reaches its real delegate barrier");
            var inflightChanges = 0;
            var beforeInflight = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 100_000; index++)
                if (!ReferenceEquals(first, writer.FlushAsync())) inflightChanges++;
            Check(inflightChanges == 0 && GC.GetAllocatedBytesForCurrentThread() - beforeInflight == 0,
                "100000 inflight Flush calls share the actual running publication without waiter growth");
            writer.Queue(2, "last");
            var second = writer.FlushAsync();
            var pendingChanges = 0;
            var beforePending = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 100_000; index++)
            {
                if (!writer.Queue(index + 3L, "last")) throw new InvalidOperationException("newer revision was rejected");
                if (!ReferenceEquals(second, writer.FlushAsync())) pendingChanges++;
            }
            Check(pendingChanges == 0 && GC.GetAllocatedBytesForCurrentThread() - beforePending == 0 && !ReferenceEquals(first, second),
                "100000 pending replacements keep exactly one separate completion and zero queue metadata");
            var drain = writer.BeginDispose();
            Check(ReferenceEquals(second, writer.RetryAsync()) && !writer.RetryAsync().IsCompleted,
                "Retry during close joins the latest outstanding completion without reporting its earlier success");
            release.Set();
            Check(first.Wait(TimeSpan.FromSeconds(5)) && first.Result && second.Wait(TimeSpan.FromSeconds(5)) && !second.Result,
                "a later failed generation cannot rewrite its earlier running success");
            Check(drain.Wait(TimeSpan.FromSeconds(5)) && !drain.Result && writer.CompletedWrites == 2,
                "close returns the latest accepted failure after both shared completions settle");
        }
        finally { release.Set(); }
        return (passed, failed);
    }
}
