namespace FireGaze.PortraitPreview;

/// <summary>Immutable UTF-8 fragments retain the compact history format without a full caller-thread buffer.</summary>
internal sealed class PortraitHistoryWriteDocument
{
    private readonly byte[] prefix;
    private readonly byte[][] records;
    private readonly byte[] suffix;
    public int Length { get; }

    public PortraitHistoryWriteDocument(byte[] prefix, byte[][] records, byte[] suffix)
    {
        this.prefix = prefix;
        this.records = records;
        this.suffix = suffix;
        Length = checked(prefix.Length + suffix.Length + records.Sum(record => record.Length) + Math.Max(0, records.Length - 1));
    }

    public void WriteTo(Stream stream)
    {
        stream.Write(prefix);
        for (var index = 0; index < records.Length; index++)
        {
            if (index != 0) stream.Write(","u8);
            stream.Write(records[index]);
        }
        stream.Write(suffix);
    }
}

/// <summary>One worker atomically writes the newest fragment snapshot; superseded pending documents coalesce.</summary>
internal sealed class PortraitHistoryWriter : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly string path;
    private readonly int byteLimit;
    private readonly PortraitStorageBudget storage;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
    private readonly CancellationTokenSource capacityClose = new();
    private readonly Task worker;
    private (long Sequence, PortraitHistoryWriteDocument Document, TaskCompletionSource<bool> Completion)? pending;
    private TaskCompletionSource<bool>? inFlight;
    private PortraitHistoryWriteDocument? failedDocument;
    private long submitted;
    private long completed;
    private bool stopping;
    private bool lastSuccess = true;
    private string? lastError;
    private Task<bool>? drainTask;
    public string? LastError { get { lock (gate) return lastError; } }

    public PortraitHistoryWriter(string path, int byteLimit, PortraitStorageBudget? storage = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        this.path = Path.GetFullPath(path);
        this.byteLimit = byteLimit;
        this.storage = storage ?? PortraitStorageBudget.ForDirectory(Path.GetDirectoryName(this.path)!);
        this.retryDelay = retryDelay ?? Task.Delay;
        this.storage.RegisterPath(this.path);
        worker = Task.Run(Work);
    }

    public bool Queue(PortraitHistoryWriteDocument document)
    {
        lock (gate)
        {
            if (stopping || document.Length > byteLimit) return false;
            var needsSignal = pending is null;
            // Every superseded pending document shares its next physical publication.
            // Keep that boundary rather than accumulating a waiter for every FlushAsync.
            var completion = pending?.Completion
                ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = (++submitted, document, completion);
            failedDocument = null;
            if (needsSignal) signal.Release();
            return true;
        }
    }

    public Task<bool> FlushAsync()
    {
        lock (gate)
        {
            if (completed >= submitted) return Task.FromResult(lastSuccess);
            // At most one physical write and one coalesced pending generation exist.
            // Returning their shared tasks creates no per-call waiter or async state.
            return pending is { } request ? request.Completion.Task : inFlight!.Task;
        }
    }

    /// <summary>Retries the last failed immutable state without creating another history record.</summary>
    public Task<bool> RetryAsync()
    {
        lock (gate)
        {
            if (pending is { } queued) return queued.Completion.Task;
            if (inFlight is not null) return inFlight.Task;
            if (stopping) return Task.FromResult(lastSuccess);
            if (failedDocument is not { } document) return Task.FromResult(lastSuccess);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = (++submitted, document, completion);
            failedDocument = null;
            signal.Release();
            return completion.Task;
        }
    }

    /// <summary>
    /// Seals writer admission and returns a task for the accepted immutable document.
    /// Callers that own a Framework-bound feature must await this task outside Framework.
    /// </summary>
    public Task<bool> BeginDispose()
    {
        lock (gate)
        {
            if (drainTask is not null) return drainTask;
            stopping = true;
            // Reclamation needs a live photo owner. Closing cannot wait indefinitely for it.
            capacityClose.Cancel();
            signal.Release();
            drainTask = DrainWorkerAsync();
            return drainTask;
        }
    }

    private async Task<bool> DrainWorkerAsync()
    {
        try
        {
            await worker.ConfigureAwait(false);
            lock (gate) return lastSuccess;
        }
        catch (Exception error)
        {
            lock (gate) { lastError ??= error.Message; }
            return false;
        }
    }

    private async Task Work()
    {
        while (true)
        {
            await signal.WaitAsync().ConfigureAwait(false);
            (long Sequence, PortraitHistoryWriteDocument Document, TaskCompletionSource<bool> Completion) request;
            lock (gate)
            {
                if (pending is not { } value)
                {
                    if (stopping) return;
                    continue;
                }
                request = value;
                pending = null;
                inFlight = request.Completion;
            }
            PortraitStorageResult result;
            try { result = await WriteWithRetryAsync(request.Document).ConfigureAwait(false); }
            catch (Exception exception) { result = new(PortraitStorageCode.StorageError, exception.Message); }
            bool finished;
            lock (gate)
            {
                completed = request.Sequence;
                lastSuccess = result.Code == PortraitStorageCode.Success;
                lastError = lastSuccess ? null : result.Reason;
                failedDocument = !lastSuccess && pending is null ? request.Document : null;
                request.Completion.TrySetResult(lastSuccess);
                inFlight = null;
                finished = stopping && pending is null;
            }
            // A completed drain task may remain rooted by its caller. Keep only the
            // deliberately retained failed document, not its successful request payload.
            request = default;
            if (finished) return;
        }
    }

    private async Task<PortraitStorageResult> WriteWithRetryAsync(PortraitHistoryWriteDocument document)
    {
        if (document.Length > byteLimit) return new(PortraitStorageCode.InvalidPath, "肖像历史快照超过容量上限。");
        var transientRetries = 0;
        var capacityRetried = false;
        while (true)
        {
            var observedStorage = storage.ChangeVersion;
            if (storage.TryPublish(path, document.Length, document.WriteTo, keepBackup: true, out var result)) return result;
            lock (gate) lastError = result.Reason;
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

    public void Dispose()
    {
        BeginDispose().GetAwaiter().GetResult();
    }
}
