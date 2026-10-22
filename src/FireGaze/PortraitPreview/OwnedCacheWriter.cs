namespace FireGaze.PortraitPreview;

/// <summary>
/// One worker writes detached snapshots. Running and newest pending generations each own one
/// shared completion. The write delegate must not call Dispose; disposal drains accepted work.
/// </summary>
internal sealed class OwnedCacheWriter : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly Func<string, PortraitStorageResult> write;
    private readonly PortraitStorageBudget? storage;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
    private readonly CancellationTokenSource capacityClose = new();
    private readonly Task worker;
    private Request? pending;
    private Request? running;
    private (long? Revision, string Snapshot)? failedSnapshot;
    private long submitted;
    private long completed;
    private long savedRevision;
    private long acceptedRevision;
    private bool stopping;
    private int completedWrites;
    private int failedWrites;
    private bool? lastWriteSucceeded;
    private string? lastError;
    private Task<bool>? drainTask;

    /// <summary>Compatibility delegate: each accepted submission receives one physical attempt.</summary>
    public OwnedCacheWriter(Func<string, bool> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        this.write = snapshot => write(snapshot) ? new(PortraitStorageCode.Success, "")
            : new(PortraitStorageCode.StorageError, "拥有装备缓存暂时无法保存，已保留最后快照。");
        retryDelay = Task.Delay;
        worker = Task.Run(Work);
    }

    /// <summary>Production delegate: typed storage failures use bounded retries in the shared authority.</summary>
    public OwnedCacheWriter(Func<string, PortraitStorageResult> write, PortraitStorageBudget storage,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        this.write = write ?? throw new ArgumentNullException(nameof(write));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.retryDelay = retryDelay ?? Task.Delay;
        worker = Task.Run(Work);
    }

    public int CompletedWrites { get { lock (gate) return completedWrites; } }
    public int FailedWrites { get { lock (gate) return failedWrites; } }
    public bool? LastWriteSucceeded { get { lock (gate) return lastWriteSucceeded; } }
    public string? LastError { get { lock (gate) return lastError; } }
    public long SavedRevision { get { lock (gate) return savedRevision; } }
    public long OutstandingRevision { get { lock (gate) return OutstandingRevisionCore(); } }

    /// <summary>Whether an explicit submission of this revision is still needed for durability.</summary>
    public bool NeedsRevision(long revision)
    {
        lock (gate) return NeedsRevisionCore(revision);
    }

    /// <summary>Background observations admit each content revision once; failures require an explicit retry.</summary>
    public bool NeedsAutomaticRevision(long revision)
    {
        lock (gate) return revision > acceptedRevision && NeedsRevisionCore(revision);
    }

    private long OutstandingRevisionCore() => Math.Max(pending?.Revision ?? 0, running?.Revision ?? 0);
    private bool NeedsRevisionCore(long revision) => !stopping && revision >= acceptedRevision
        && revision > savedRevision && revision > OutstandingRevisionCore();

    public bool Queue(string snapshot) => QueueCore(null, snapshot);
    public bool Queue(long revision, string snapshot) => revision > 0 && QueueCore(revision, snapshot);

    private bool QueueCore(long? revision, string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return false;
        lock (gate)
        {
            if (stopping || revision is { } value && !NeedsRevisionCore(value)) return false;
            var needsSignal = pending is null;
            var completion = pending?.Completion ?? NewCompletion();
            pending = new(++submitted, revision, snapshot, completion);
            if (revision is { } accepted) acceptedRevision = Math.Max(acceptedRevision, accepted);
            failedSnapshot = null;
            if (needsSignal) signal.Release();
            return true;
        }
    }

    /// <summary>Returns the shared task for the newest accepted generation, without a per-call waiter.</summary>
    public Task<bool> FlushAsync()
    {
        lock (gate)
        {
            if (completed >= submitted) return Task.FromResult(lastWriteSucceeded ?? true);
            return pending is { } queued ? queued.Completion.Task : running!.Value.Completion.Task;
        }
    }

    /// <summary>Retries the exact final failed snapshot without serializing or admitting another observation.</summary>
    public Task<bool> RetryAsync()
    {
        lock (gate)
        {
            if (pending is { } queued) return queued.Completion.Task;
            if (running is { } active) return active.Completion.Task;
            if (stopping) return Task.FromResult(lastWriteSucceeded ?? true);
            if (failedSnapshot is not { } failed) return Task.FromResult(lastWriteSucceeded ?? true);
            var completion = NewCompletion();
            pending = new(++submitted, failed.Revision, failed.Snapshot, completion);
            failedSnapshot = null;
            signal.Release();
            return completion.Task;
        }
    }

    public Task<bool> BeginDispose()
    {
        lock (gate)
        {
            if (drainTask is not null) return drainTask;
            stopping = true;
            capacityClose.Cancel();
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

    private async Task Work()
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
            PortraitStorageResult result;
            try { result = await WriteWithRetryAsync(request.Snapshot).ConfigureAwait(false); }
            catch (Exception error) { result = new(PortraitStorageCode.StorageError, error.Message); }
            bool finished;
            lock (gate)
            {
                running = null;
                completed = request.Sequence;
                completedWrites++;
                var success = result.Code == PortraitStorageCode.Success;
                if (!success) failedWrites++;
                else if (request.Revision is { } revision) savedRevision = Math.Max(savedRevision, revision);
                lastWriteSucceeded = success;
                lastError = success ? null : result.Reason;
                failedSnapshot = !success && pending is null ? (request.Revision, request.Snapshot) : null;
                request.Completion.TrySetResult(success);
                finished = stopping && pending is null;
            }
            request = default;
            if (finished) return;
        }
    }

    private async Task<PortraitStorageResult> WriteWithRetryAsync(string snapshot)
    {
        var transientRetries = 0;
        var capacityRetried = false;
        while (true)
        {
            var observedStorage = storage?.ChangeVersion ?? 0;
            PortraitStorageResult result;
            try { result = write(snapshot); }
            catch (Exception error) { result = new(PortraitStorageCode.StorageError, error.Message); }
            if (result.Code == PortraitStorageCode.Success) return result;
            lock (gate) lastError = result.Reason;
            if (storage is null) return result;
            if (result.Code is PortraitStorageCode.Busy or PortraitStorageCode.StorageError)
            {
                if (transientRetries == 3) return result;
                await retryDelay(TimeSpan.FromSeconds(1 << transientRetries++), CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            if (result.Code != PortraitStorageCode.Capacity || capacityRetried) return result;
            capacityRetried = true;
            using var reclaimWait = CancellationTokenSource.CreateLinkedTokenSource(capacityClose.Token);
            reclaimWait.CancelAfter(TimeSpan.FromSeconds(5));
            try { await storage.WaitForChangeAsync(observedStorage, reclaimWait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return result; }
        }
    }

    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose() => BeginDispose().GetAwaiter().GetResult();
    private readonly record struct Request(long Sequence, long? Revision, string Snapshot, TaskCompletionSource<bool> Completion);
}
