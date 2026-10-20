using System.Text;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FireGaze.PortraitPreview;

internal static class HistoryPersistenceRetryTests
{
    internal static async Task<int> Run(string root)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++; Console.WriteLine("PASS " + name);
        }

        var directory = Path.Combine(root, "history-real-busy-retry");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "portrait-edit-history-v1.json");
        using var writer = new PortraitHistoryWriter(path, 1024);
        Task<bool> boundary;
        using (var peerLease = new FileStream(Path.Combine(directory, ".portrait-storage.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Check(writer.Queue(Document("{\"value\":1}")), "busy retry fixture admits the last immutable edit");
            boundary = writer.FlushAsync();
            Check(SpinWait.SpinUntil(() => writer.LastError is not null, TimeSpan.FromSeconds(5)),
                "real peer OS lease produces a visible persistence error");
            Check(!boundary.IsCompleted && !File.Exists(path),
                "a real Busy result remains pending for a bounded retry instead of discarding the last edit");
        }
        Check(await boundary.WaitAsync(TimeSpan.FromSeconds(5)) && File.ReadAllText(path) == "{\"value\":1}",
            "releasing the peer lease persists the same last edit without another Queue or Record");
        Check(await writer.BeginDispose() && writer.LastError is null,
            "successful retry clears its error and disposal truthfully reports durability");

        var faultDirectory = Path.Combine(root, "history-storage-error-retry");
        var faults = new FaultFiles { FailuresRemaining = 3 };
        var faultBudget = new PortraitStorageBudget(faultDirectory, 1024, faults);
        var waits = new List<double>();
        using (var retried = new PortraitHistoryWriter(Path.Combine(faultDirectory, "portrait-edit-history-v1.json"), 1024,
            faultBudget, (duration, _) => { waits.Add(duration.TotalSeconds); return Task.CompletedTask; }))
        {
            retried.Queue(Document("{\"value\":2}"));
            Check(await retried.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)) && faults.Attempts == 4
                && waits.SequenceEqual([1d, 2d, 4d]),
                "StorageError uses exactly the initial attempt plus three 1/2/4-second backoffs and then persists");
        }

        var retainedDirectory = Path.Combine(root, "history-retained-last-failure");
        var permanent = new FaultFiles { FailuresRemaining = int.MaxValue };
        var retainedBudget = new PortraitStorageBudget(retainedDirectory, 1024, permanent);
        using (var retained = new PortraitHistoryWriter(Path.Combine(retainedDirectory, "portrait-edit-history-v1.json"), 1024,
            retainedBudget, (_, _) => Task.CompletedTask))
        {
            retained.Queue(Document("{\"value\":3}"));
            var originalFailure = retained.FlushAsync();
            Check(!await originalFailure.WaitAsync(TimeSpan.FromSeconds(5)) && permanent.Attempts == 4
                && retained.LastError is not null,
                "retry exhaustion settles false after four attempts and retains an honest failure");
            permanent.FailuresRemaining = 0;
            var gate = typeof(PortraitHistoryWriter).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(retained)!;
            Task<bool> recovery;
            var distinct = 0;
            long allocated;
            lock (gate)
            {
                recovery = retained.RetryAsync();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 100_000; index++)
                    if (!ReferenceEquals(recovery, retained.RetryAsync()) || !ReferenceEquals(recovery, retained.FlushAsync())) distinct++;
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Check(distinct == 0 && allocated == 0,
                "one hundred thousand explicit Retry/Flush calls share the same retained-document ticket with zero metadata allocation");
            Check(await recovery.WaitAsync(TimeSpan.FromSeconds(5)) && !await originalFailure
                && File.ReadAllText(Path.Combine(retainedDirectory, "portrait-edit-history-v1.json")) == "{\"value\":3}",
                "explicit retry persists the retained final edit without changing the previously failed result or adding a record");
            Check(await retained.BeginDispose() && await retained.RetryAsync(),
                "completed close retains its durable result while refusing new retry admissions");
        }

        var capacityDirectory = Path.Combine(root, "history-capacity-reclaim-retry");
        Directory.CreateDirectory(capacityDirectory);
        var capacityFill = Path.Combine(capacityDirectory, "portrait-favorites-v1.json.bak");
        File.WriteAllBytes(capacityFill, new byte[95]);
        var capacityBudget = new PortraitStorageBudget(capacityDirectory, 100);
        using (var capacity = new PortraitHistoryWriter(Path.Combine(capacityDirectory, "portrait-edit-history-v1.json"), 1024,
            capacityBudget, (_, _) => throw new InvalidOperationException("capacity must never use timed backoff")))
        {
            capacity.Queue(Document("{\"value\":4}"));
            var savedAfterReclaim = capacity.FlushAsync();
            Check(SpinWait.SpinUntil(() => capacity.LastError is not null, TimeSpan.FromSeconds(5))
                && !savedAfterReclaim.IsCompleted && capacityBudget.NeedsReclaim,
                "capacity holds one pending durability boundary and requests reclamation without polling or timed backoff");
            Check(capacityBudget.TryDeleteOwned(capacityFill, out _), "the independent quota owner completes real reclamation");
            Check(await savedAfterReclaim.WaitAsync(TimeSpan.FromSeconds(5)),
                "one reclamation change retries and persists the same last edit");
        }

        var repeatedDirectory = Path.Combine(root, "history-capacity-one-retry");
        Directory.CreateDirectory(repeatedDirectory);
        var repeatedFill = Path.Combine(repeatedDirectory, "portrait-favorites-v1.json.bak");
        File.WriteAllBytes(repeatedFill, new byte[95]);
        var repeatedBudget = new PortraitStorageBudget(repeatedDirectory, 100);
        using (var capacity = new PortraitHistoryWriter(Path.Combine(repeatedDirectory, "portrait-edit-history-v1.json"), 1024,
            repeatedBudget, (_, _) => throw new InvalidOperationException("capacity must never sleep")))
        {
            capacity.Queue(Document("{\"value\":5}"));
            var failedCapacity = capacity.FlushAsync();
            Check(SpinWait.SpinUntil(() => repeatedBudget.NeedsReclaim, TimeSpan.FromSeconds(5)),
                "capacity one-retry fixture reaches its reclaim boundary");
            repeatedBudget.CompleteReclaim(repeatedBudget.ReclaimRequestVersion);
            Check(!await failedCapacity.WaitAsync(TimeSpan.FromSeconds(5)) && capacity.LastError is not null,
                "a completed reclamation that frees nothing gets only one capacity retry before truthful failure");
            Check(repeatedBudget.TryDeleteOwned(repeatedFill, out _) && await capacity.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5)),
                "a later real storage change plus explicit retry recovers the retained document after capacity exhaustion");
        }

        var closeDirectory = Path.Combine(root, "history-capacity-close-wake");
        Directory.CreateDirectory(closeDirectory);
        File.WriteAllBytes(Path.Combine(closeDirectory, "portrait-favorites-v1.json.bak"), new byte[95]);
        using (var closing = new PortraitHistoryWriter(Path.Combine(closeDirectory, "portrait-edit-history-v1.json"), 1024,
            new PortraitStorageBudget(closeDirectory, 100)))
        {
            closing.Queue(Document("{\"value\":6}"));
            var unfinished = closing.FlushAsync();
            Check(SpinWait.SpinUntil(() => closing.LastError is not null, TimeSpan.FromSeconds(5)),
                "capacity close fixture reaches its bounded signal wait");
            var close = closing.BeginDispose();
            Check(!await close.WaitAsync(TimeSpan.FromSeconds(1)) && !await unfinished && !await closing.RetryAsync(),
                "closing wakes an uncompleted reclaim wait promptly and keeps both durability and later retry false");
        }

        var delayDirectory = Path.Combine(root, "history-close-transient-retry");
        var delayedFiles = new FaultFiles { FailuresRemaining = 1 };
        var delayed = new ControlledDelay();
        using (var closing = new PortraitHistoryWriter(Path.Combine(delayDirectory, "portrait-edit-history-v1.json"), 1024,
            new PortraitStorageBudget(delayDirectory, 1024, delayedFiles), delayed.Wait))
        {
            closing.Queue(Document("{\"value\":7}"));
            var boundaryDuringClose = closing.FlushAsync();
            var step = await delayed.Steps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var close = closing.BeginDispose();
            Check(step.Duration == TimeSpan.FromSeconds(1) && !close.IsCompleted && !boundaryDuringClose.IsCompleted,
                "closing keeps the already accepted transient retry pending instead of falsely reporting success");
            step.Completion.SetResult();
            Check(await close.WaitAsync(TimeSpan.FromSeconds(5)) && await boundaryDuringClose && delayedFiles.Attempts == 2,
                "a transient retry accepted before closing can complete its exact immutable document and drain");
        }

        var timeoutDirectory = Path.Combine(root, "history-capacity-timeout");
        Directory.CreateDirectory(timeoutDirectory);
        var timeoutFill = Path.Combine(timeoutDirectory, "portrait-favorites-v1.json.bak");
        File.WriteAllBytes(timeoutFill, new byte[95]);
        var timeoutBudget = new PortraitStorageBudget(timeoutDirectory, 100);
        using (var timeout = new PortraitHistoryWriter(Path.Combine(timeoutDirectory, "portrait-edit-history-v1.json"), 1024,
            timeoutBudget, (_, _) => throw new InvalidOperationException("capacity timeout must not poll")))
        {
            var watch = Stopwatch.StartNew();
            timeout.Queue(Document("{\"value\":8}"));
            var timedOut = timeout.FlushAsync();
            Check(!await timedOut.WaitAsync(TimeSpan.FromSeconds(8)) && watch.Elapsed >= TimeSpan.FromSeconds(4.5)
                && timeout.LastError is not null && !File.Exists(Path.Combine(timeoutDirectory, "portrait-edit-history-v1.json")),
                "an unchanged capacity wait expires after its real five-second bound with the last edit visibly unpersisted");
            Check(timeoutBudget.TryDeleteOwned(timeoutFill, out _) && await timeout.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5)),
                "capacity timeout retains the final document for an explicit retry after genuine space becomes available");
        }

        var supersededDirectory = Path.Combine(root, "history-superseded-failure");
        var supersededFiles = new FaultFiles { FailuresRemaining = int.MaxValue };
        using (var superseded = new PortraitHistoryWriter(Path.Combine(supersededDirectory, "portrait-edit-history-v1.json"), 1024,
            new PortraitStorageBudget(supersededDirectory, 1024, supersededFiles), (_, _) => Task.CompletedTask))
        {
            var tracked = QueueTracked(superseded, "{\"value\":9}");
            Check(!await tracked.Boundary.WaitAsync(TimeSpan.FromSeconds(5)), "superseded final-edit fixture first exhausts real write failures");
            Collect();
            Check(tracked.Document.IsAlive, "the latest failed immutable document stays strongly retained for a later retry");
            supersededFiles.FailuresRemaining = 0;
            superseded.Queue(Document("{\"value\":10}"));
            Check(await superseded.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)) && await superseded.RetryAsync()
                && File.ReadAllText(Path.Combine(supersededDirectory, "portrait-edit-history-v1.json")) == "{\"value\":10}",
                "a newly accepted document replaces the failed one and later Retry cannot resurrect an older edit");
            Collect();
            Check(!tracked.Document.IsAlive, "replacing a failed document releases its obsolete snapshot instead of retaining a second history payload");
            var successful = QueueTracked(superseded, "{\"value\":11}");
            Check(await successful.Boundary.WaitAsync(TimeSpan.FromSeconds(5)), "successful document lifetime fixture reaches the actual file boundary");
            Collect();
            Check(!successful.Document.IsAlive, "an idle writer releases the successful immutable document after its durability boundary settles");
            GC.KeepAlive(superseded);
        }

        var failedCloseDirectory = Path.Combine(root, "history-final-failure-close");
        var failedCloseFiles = new FaultFiles { FailuresRemaining = int.MaxValue };
        using (var failedClose = new PortraitHistoryWriter(Path.Combine(failedCloseDirectory, "portrait-edit-history-v1.json"), 1024,
            new PortraitStorageBudget(failedCloseDirectory, 1024, failedCloseFiles), (_, _) => Task.CompletedTask))
        {
            failedClose.Queue(Document("{\"value\":12}"));
            Check(!await failedClose.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)), "final failure close fixture exhausts its bounded retry budget");
            failedCloseFiles.FailuresRemaining = 0;
            var drain = failedClose.BeginDispose();
            Check(!await drain.WaitAsync(TimeSpan.FromSeconds(1)) && !await failedClose.RetryAsync()
                && failedCloseFiles.Attempts == 4 && failedClose.LastError is not null,
                "closing an already failed writer remains false and cannot silently start a new publication");
        }

        var retainedDrain = BeginCollectibleDrain(Path.Combine(root, "history-exported-retry-drain"));
        Collect();
        Check(!retainedDrain.Drain.IsCompleted && retainedDrain.Owner.IsAlive && retainedDrain.Document.IsAlive,
            "the exported close task roots the writer and accepted immutable document while its transient retry is blocked");
        retainedDrain.Release.SetResult();
        Check(await retainedDrain.Drain.WaitAsync(TimeSpan.FromSeconds(5))
            && File.ReadAllText(retainedDrain.Path) == "{\"value\":13}",
            "an exported drain survives forced collection and completes the final real publication");
        // The consumer may resume before the producer's final MoveNext stack unwinds.
        // Verify eventual collection without holding the weak target in this frame.
        for (var attempt = 0; attempt < 20 && retainedDrain.Document.IsAlive; attempt++)
        { Collect(); await Task.Delay(10); }
        Check(!retainedDrain.Document.IsAlive, "a completed exported drain releases its successful document even while the task remains rooted");
        GC.KeepAlive(retainedDrain.Drain);
        return checks;
    }

    private static PortraitHistoryWriteDocument Document(string json) => new(Encoding.UTF8.GetBytes(json), [], []);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Document, Task<bool> Boundary) QueueTracked(PortraitHistoryWriter writer, string json)
    {
        var document = Document(json);
        if (!writer.Queue(document)) throw new InvalidOperationException("tracked document was not accepted");
        return (new(document), writer.FlushAsync());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<bool> Drain, WeakReference Owner, WeakReference Document, TaskCompletionSource Release, string Path)
        BeginCollectibleDrain(string directory)
    {
        var files = new FaultFiles { FailuresRemaining = 1 };
        var delayed = new ControlledDelay();
        var path = Path.Combine(directory, "portrait-edit-history-v1.json");
        var writer = new PortraitHistoryWriter(path, 1024, new PortraitStorageBudget(directory, 1024, files), delayed.Wait);
        var tracked = QueueTracked(writer, "{\"value\":13}");
        var step = delayed.Steps.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return (writer.BeginDispose(), new(writer), tracked.Document, step.Completion, path);
    }

    private static void Collect()
    {
        for (var attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    }


    private sealed class FaultFiles : PortraitStorageFiles
    {
        public int FailuresRemaining;
        public int Attempts;
        public override FileStream CreateNew(string path)
        {
            Attempts++;
            if (FailuresRemaining-- > 0) throw new IOException("test storage is temporarily unavailable");
            return base.CreateNew(path);
        }
    }

    private sealed class ControlledDelay
    {
        public readonly Channel<(TimeSpan Duration, TaskCompletionSource Completion)> Steps =
            Channel.CreateBounded<(TimeSpan, TaskCompletionSource)>(3);
        public Task Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Steps.Writer.TryWrite((duration, completion))) throw new InvalidOperationException("unexpected retry backlog");
            return completion.Task;
        }
    }
}
