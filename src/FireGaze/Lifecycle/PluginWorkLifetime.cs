namespace FireGaze.Lifecycle;

public enum PluginWorkLifetimeState
{
    Loading,
    Running,
    Closing,
    Closed,
}

public sealed record PluginWorkCloseResult(bool Completed, Exception? BeginCloseError, Exception? CleanupError)
{
    public Exception? Error => BeginCloseError ?? CleanupError;
}

/// <summary>
/// Pure managed admission and drain coordinator for plugin work. It does not know about
/// Dalamud, Framework, native pointers or UI. Construction is intentionally inert. A lease
/// admits work for exactly one generation; closing advances the generation before invoking
/// either callback, so work queued before close still checks its generation at execution time.
/// </summary>
public sealed class PluginWorkLifetime : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly TaskCompletionSource<bool> drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PluginWorkLifetimeState state = PluginWorkLifetimeState.Loading;
    private long generation = 1;
    private int activeOperations;
    private Task<PluginWorkCloseResult>? closeTask;

    public PluginWorkLifetimeState State { get { lock (sync) return state; } }
    public long Generation { get { lock (sync) return generation; } }
    public CancellationToken ShutdownToken => shutdown.Token;

    public bool TryStartRunning(out string reason)
    {
        lock (sync)
        {
            if (state != PluginWorkLifetimeState.Loading)
            { reason = "The plugin lifetime is no longer loading."; return false; }
            state = PluginWorkLifetimeState.Running;
            reason = "Plugin work admission is running."; return true;
        }
    }

    public bool TryAcquire(out PluginWorkLease? lease, out string reason)
        => TryAcquire(out lease, false, out reason);

    public bool TryAcquire(out PluginWorkLease? lease, bool allowLoading, out string reason)
    {
        lock (sync)
        {
            var accepted = state == PluginWorkLifetimeState.Running || allowLoading && state == PluginWorkLifetimeState.Loading;
            if (!accepted)
            { lease = null; reason = "The plugin lifetime is closing or has not entered the requested admission phase."; return false; }
            lease = new PluginWorkLease(this, generation);
            reason = "Work lease admitted."; return true;
        }
    }

    public bool TryAcquire(out PluginWorkLease? lease, out string reason, bool allowLoading)
        => TryAcquire(out lease, allowLoading, out reason);

    /// <summary>Queues a delegate without invoking it under the admission lock. The worker
    /// rechecks the lease generation when it actually starts.</summary>
    public bool Queue(Func<PluginWorkLease, ValueTask> work, out Task? task, bool allowLoading = false)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!TryAcquire(out var lease, allowLoading, out _)) { task = null; return false; }
        try
        {
            // Scheduling is deliberately outside the admission lock. The worker calls
            // TryExecuteAsync at its actual start, so a close racing this queue cannot
            // make stale work touch plugin services.
            task = Task.Run(() => RunQueuedAsync(lease!, work));
            return true;
        }
        catch
        {
            lease!.Dispose(); task = null; return false;
        }
    }

    private static async Task RunQueuedAsync(PluginWorkLease lease, Func<PluginWorkLease, ValueTask> work)
    {
        try { await lease.TryExecuteAsync(work).ConfigureAwait(false); }
        finally { lease.Dispose(); }
    }

    /// <summary>Begins close once and returns the same task for every subsequent call. The
    /// callbacks are cleanup-only lifecycle phases and are never admitted as ordinary work.</summary>
    public Task<PluginWorkCloseResult> CloseAsync(Func<ValueTask>? beginClose = null,
        Func<ValueTask>? cleanupAfterDrain = null)
    {
        TaskCompletionSource<PluginWorkCloseResult>? completion = null;
        lock (sync)
        {
            if (closeTask is not null) return closeTask;
            state = PluginWorkLifetimeState.Closing;
            generation++;
            if (activeOperations == 0) drained.TrySetResult(true);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            closeTask = completion.Task;
        }
        // Cancellation can invoke arbitrary registered callbacks; never hold sync while doing it.
        Exception? cancellationError = null;
        try { shutdown.Cancel(); }
        catch (Exception error) { cancellationError = error; }
        _ = FinishCloseAsync(completion!, beginClose, cleanupAfterDrain, cancellationError);
        return completion.Task;
    }

    private async Task FinishCloseAsync(TaskCompletionSource<PluginWorkCloseResult> completion,
        Func<ValueTask>? beginClose, Func<ValueTask>? cleanupAfterDrain, Exception? cancellationError)
    {
        Exception? beginError = cancellationError, cleanupError = null;
        try { if (beginClose is not null) await beginClose().ConfigureAwait(false); }
        catch (Exception error) { beginError = error; }
        try { await drained.Task.ConfigureAwait(false); }
        catch (Exception error) { cleanupError = error; }
        try { if (cleanupAfterDrain is not null) await cleanupAfterDrain().ConfigureAwait(false); }
        catch (Exception error) { cleanupError ??= error; }

        var complete = beginError is null && cleanupError is null;
        lock (sync) if (complete) state = PluginWorkLifetimeState.Closed;
        completion.TrySetResult(new(complete, beginError, cleanupError));
    }

    internal bool IsCurrent(long leaseGeneration, bool disposed)
    {
        lock (sync) return !disposed && state == PluginWorkLifetimeState.Running && generation == leaseGeneration;
    }

    internal bool TryBeginOperation(long leaseGeneration, bool disposed, out CancellationToken token)
    {
        lock (sync)
        {
            if (disposed || state != PluginWorkLifetimeState.Running || generation != leaseGeneration)
            { token = default; return false; }
            activeOperations++; token = shutdown.Token; return true;
        }
    }

    internal bool TryTrack(long leaseGeneration, bool disposed, Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!TryBeginOperation(leaseGeneration, disposed, out _)) return false;
        try
        {
            _ = task.ContinueWith(static (_, state) => ((PluginWorkLifetime)state!).FinishOperation(), this,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return true;
        }
        catch
        {
            FinishOperation(); throw;
        }
    }

    internal void FinishOperation()
    {
        lock (sync)
        {
            if (activeOperations > 0) activeOperations--;
            if (state == PluginWorkLifetimeState.Closing && activeOperations == 0) drained.TrySetResult(true);
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

public sealed class PluginWorkLease : IDisposable
{
    private readonly PluginWorkLifetime owner;
    private readonly long generation;
    private int disposed;

    internal PluginWorkLease(PluginWorkLifetime owner, long generation)
    { this.owner = owner; this.generation = generation; }

    public long Generation => generation;
    public CancellationToken ShutdownToken => owner.ShutdownToken;
    public bool IsCurrent => owner.IsCurrent(generation, Volatile.Read(ref disposed) != 0);

    public bool TryExecute(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!owner.TryBeginOperation(generation, Volatile.Read(ref disposed) != 0, out _)) return false;
        try { action(); return true; }
        finally { owner.FinishOperation(); }
    }

    public async ValueTask<bool> TryExecuteAsync(Func<PluginWorkLease, ValueTask> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!owner.TryBeginOperation(generation, Volatile.Read(ref disposed) != 0, out _)) return false;
        try { await action(this).ConfigureAwait(false); return true; }
        finally { owner.FinishOperation(); }
    }

    public bool TryTrack(Task task) => owner.TryTrack(generation, Volatile.Read(ref disposed) != 0, task);

    public bool TryRegister(Task task) => TryTrack(task);

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
}
