using System.Diagnostics;
using System.Reflection;
using System.Text;
using FireGaze.PortraitPreview;

internal static class HistoryBoundedCompletionTests
{
    private const int Burst = 100_000;

    internal static async Task<int> Run(string root)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        var path = Path.Combine(root, "history-bounded-pending.json");
        using (var writer = new PortraitHistoryWriter(path, 1024))
        {
            var gate = Gate(writer);
            var document = Document("{\"value\":0}");
            Task<bool> first, drain;
            var changedTasks = 0;
            long allocated;
            // The real file worker cannot take its pending document while this scheduling
            // gate is held. Repeated admissions must not retain one waiter per edit.
            lock (gate)
            {
                Check(writer.Queue(document), "blocked writer accepts its first pending immutable document");
                first = writer.FlushAsync();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < Burst; index++)
                {
                    if (!writer.Queue(document)) throw new InvalidOperationException("pending burst admission failed");
                    if (!ReferenceEquals(first, writer.FlushAsync())) changedTasks++;
                }
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(changedTasks == 0,
                    $"{Burst} blocked Queue/Flush calls share one pending completion task (different tasks: {changedTasks})");
                Check(allocated < 64 * 1024,
                    $"blocked Queue/Flush metadata allocation stays below 64 KiB for {Burst} calls (actual: {allocated})");
                Check(writer.Queue(Document("{\"value\":100001}")) && ReferenceEquals(first, writer.FlushAsync()),
                    "replacing the pending document preserves every earlier flush boundary");
                drain = writer.BeginDispose();
                Check(!first.IsCompleted && !drain.IsCompleted && ReferenceEquals(drain, writer.BeginDispose())
                    && !writer.Queue(document), "sealing the blocked writer waits only for accepted work and rejects later admission");
            }
            Check(await drain.WaitAsync(TimeSpan.FromSeconds(5)) && await first
                && File.ReadAllText(path) == "{\"value\":100001}",
                "shared pending completion succeeds only after the newest accepted document is durable");
        }

        var stagedPath = Path.Combine(root, "history-bounded-inflight.json");
        using (var writer = new PortraitHistoryWriter(stagedPath, 10 * 1024 * 1024,
            retryDelay: (_, _) => Task.CompletedTask))
        {
            // Many real stream writes keep the worker observable between taking its request
            // and completing it. No fake I/O or production test callback replaces the writer.
            var record = Encoding.UTF8.GetBytes("\"" + new string('a', 120) + "\"");
            var firstDocument = new PortraitHistoryWriteDocument("["u8.ToArray(),
                Enumerable.Repeat(record, 65_536).ToArray(), "]"u8.ToArray());
            var gate = Gate(writer);
            Task<bool> first;
            lock (gate)
            {
                Check(writer.Queue(firstDocument), "two-generation fixture accepts a real near-limit file document");
                first = writer.FlushAsync();
            }
            EnterWhileInFlight(writer, gate, first);
            Task<bool> second, drain;
            try
            {
                var changedInflightTasks = 0;
                var beforeInflight = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < Burst; index++)
                    if (!ReferenceEquals(first, writer.FlushAsync())) changedInflightTasks++;
                var inflightAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeInflight;
                Check(changedInflightTasks == 0 && inflightAllocated < 64 * 1024,
                    $"{Burst} inflight-only flushes share their physical write completion without growing metadata (different tasks: {changedInflightTasks}, bytes: {inflightAllocated})");
                Check(writer.Queue(Document("{\"value\":2}")), "inflight writer admits a separate pending generation");
                second = writer.FlushAsync();
                Check(!ReferenceEquals(first, second) && !first.IsCompleted && !second.IsCompleted,
                    "inflight and pending persistence boundaries remain separate while the real worker is blocked");
                var changedTasks = 0;
                var replacement = Document("{\"value\":3}");
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < Burst; index++)
                {
                    if (!writer.Queue(replacement)) throw new InvalidOperationException("inflight burst admission failed");
                    if (!ReferenceEquals(second, writer.FlushAsync())) changedTasks++;
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(changedTasks == 0 && allocated < 64 * 1024,
                    $"inflight plus {Burst} pending replacements retain two shared tasks and bounded metadata (different tasks: {changedTasks}, bytes: {allocated})");
                Check(SpinWait.SpinUntil(() => File.Exists(stagedPath), TimeSpan.FromSeconds(5)),
                    "the actual first atomic publication finishes before its completion gate is released");
                // The first publication has succeeded, but the pending generation must fail
                // when its rotating backup destination is an unrelated directory.
                Directory.CreateDirectory(stagedPath + ".bak");
                drain = writer.BeginDispose();
                Check(!drain.IsCompleted && !writer.Queue(replacement),
                    "two-generation disposal seals its latest accepted boundary without completing early");
            }
            finally { Monitor.Exit(gate); }
            Check(await first.WaitAsync(TimeSpan.FromSeconds(5)) && !await second.WaitAsync(TimeSpan.FromSeconds(5)),
                "a later real publication failure cannot rewrite the earlier inflight success result");
            Check(!await drain.WaitAsync(TimeSpan.FromSeconds(5)) && !string.IsNullOrWhiteSpace(writer.LastError)
                && new FileInfo(stagedPath).Length == firstDocument.Length && Directory.Exists(stagedPath + ".bak"),
                "dispose reports the final accepted failure and preserves the earlier file plus unrelated backup directory");
        }
        return checks;
    }

    private static PortraitHistoryWriteDocument Document(string json) => new(Encoding.UTF8.GetBytes(json), [], []);

    private static object Gate(PortraitHistoryWriter writer) => typeof(PortraitHistoryWriter)
        .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;

    // Returns with the gate held, fixing the worker's real completion boundary while
    // allowing its physical file I/O to finish. The fixture never awaits while holding it.
    private static void EnterWhileInFlight(PortraitHistoryWriter writer, object gate, Task<bool> boundary)
    {
        var pending = typeof(PortraitHistoryWriter).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Monitor.Enter(gate);
            if (pending.GetValue(writer) is null && !boundary.IsCompleted) return;
            Monitor.Exit(gate);
            if (boundary.IsCompleted) throw new InvalidOperationException("real worker finished before the inflight fixture acquired its completion gate");
            Thread.Yield();
        }
        throw new TimeoutException("real worker did not take its accepted document");
    }
}
