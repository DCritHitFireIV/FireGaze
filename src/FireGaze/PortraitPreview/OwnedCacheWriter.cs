namespace FireGaze.PortraitPreview;

/// <summary>
/// One worker writes detached snapshots. Only the running and newest pending snapshots are retained.
/// The write delegate must not call Dispose. Disposal drains accepted work before returning.
/// </summary>
internal sealed class OwnedCacheWriter : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly List<(long Sequence, TaskCompletionSource<bool> Completion)> waiters = [];
    private readonly Task worker;
    private Request? pending;
    private Request? running;
    private long submitted;
    private long completed;
    private long savedRevision;
    private bool stopping;
    private int completedWrites;
    private int failedWrites;
    private bool? lastWriteSucceeded;
    private Task<bool>? drainTask;

    public OwnedCacheWriter(Func<string, bool> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        worker = Task.Run(async () =>
        {
            while (true)
            {
                await signal.WaitAsync().ConfigureAwait(false);
                Request request;
                lock (gate)
                {
                    if (pending is not { } next)
                    {
                        if (stopping) return;
                        continue;
                    }
                    request = next;
                    pending = null;
                    running = request;
                }
                var success = false;
                try { success = write(request.Snapshot); }
                catch (Exception) { /* A failed attempt leaves this revision eligible for retry. */ }
                lock (gate)
                {
                    running = null;
                    completed = request.Sequence;
                    completedWrites++;
                    if (!success) failedWrites++;
                    else if (request.Revision is { } revision) savedRevision = Math.Max(savedRevision, revision);
                    lastWriteSucceeded = success;
                    for (var index = waiters.Count - 1; index >= 0; index--)
                    {
                        if (waiters[index].Sequence > completed) continue;
                        waiters[index].Completion.TrySetResult(success);
                        waiters.RemoveAt(index);
                    }
                    if (stopping && pending is null) return;
                }
            }
        });
    }

    public int CompletedWrites { get { lock (gate) return completedWrites; } }
    public int FailedWrites { get { lock (gate) return failedWrites; } }
    public bool? LastWriteSucceeded { get { lock (gate) return lastWriteSucceeded; } }
    public long SavedRevision { get { lock (gate) return savedRevision; } }
    public long OutstandingRevision { get { lock (gate) return OutstandingRevisionCore(); } }

    /// <summary>False while the same/newer content is durable or already queued; failure permits retry.</summary>
    public bool NeedsRevision(long revision)
    {
        lock (gate) return NeedsRevisionCore(revision);
    }

    private long OutstandingRevisionCore() => Math.Max(pending?.Revision ?? 0, running?.Revision ?? 0);
    private bool NeedsRevisionCore(long revision) => !stopping && revision > savedRevision && revision > OutstandingRevisionCore();

    /// <summary>Compatibility submission for callers without a content revision; newer pending snapshots coalesce.</summary>
    public bool Queue(string snapshot) => QueueCore(null, snapshot);

    /// <summary>Queue a new content revision, or retry it after failure. Stale/duplicate work is rejected.</summary>
    public bool Queue(long revision, string snapshot) => revision > 0 && QueueCore(revision, snapshot);

    private bool QueueCore(long? revision, string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return false;
        lock (gate)
        {
            if (stopping || revision is { } value && !NeedsRevisionCore(value)) return false;
            var needsSignal = pending is null;
            pending = new(++submitted, revision, snapshot);
            if (needsSignal) signal.Release();
            return true;
        }
    }

    /// <summary>Wait for the newest submission accepted before this call, including a superseding snapshot.</summary>
    public Task<bool> FlushAsync()
    {
        lock (gate)
        {
            if (completed >= submitted) return Task.FromResult(lastWriteSucceeded ?? true);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((submitted, completion));
            return completion.Task;
        }
    }

    /// <summary>Seal new cache writes and return the worker drain without blocking the caller.</summary>
    public Task<bool> BeginDispose()
    {
        lock (gate)
        {
            if (drainTask is not null) return drainTask;
            stopping = true;
            signal.Release();
            drainTask = DrainWorkerAsync();
            return drainTask;
        }
    }

    private async Task<bool> DrainWorkerAsync()
    {
        await worker.ConfigureAwait(false);
        lock (gate) return lastWriteSucceeded ?? true;
    }

    public void Dispose()
    {
        BeginDispose().GetAwaiter().GetResult();
    }

    private sealed record Request(long Sequence, long? Revision, string Snapshot);
}
