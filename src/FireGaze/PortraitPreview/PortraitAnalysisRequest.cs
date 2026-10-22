namespace FireGaze.PortraitPreview;

/// <summary>
/// Owns one managed analysis request, never native or texture resources. Factories run on the thread pool.
/// An accepted factory is always invoked, even when invalidated before it starts: its own using/finally must
/// release any resource lease it captured. A rejected factory is not invoked; the caller retains ownership.
/// The caller invalidates on view close, character/scene change or any input-state change.
/// </summary>
internal sealed class PortraitAnalysisRequest : IDisposable
{
    private readonly object gate = new();
    private Work? active;
    private long generation;
    private bool disposed;
    private string? currentKey;
    private PortraitImageAnalysisResult? currentResult;
    private string? error;

    public string? CurrentKey { get { lock (gate) return currentKey; } }
    public PortraitImageAnalysisResult? CurrentResult { get { lock (gate) return currentResult; } }
    public string? Error { get { lock (gate) return error; } }
    public bool IsBusy { get { lock (gate) return active is not null; } }

    /// <summary>Returns immediately. Success cached for the same key; a failed key can be retried.</summary>
    public bool TryStart(string stateKey, Func<CancellationToken, Task<PortraitImageAnalysisResult>> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateKey);
        ArgumentNullException.ThrowIfNull(factory);
        lock (gate)
        {
            if (disposed || active is not null || currentKey == stateKey && currentResult is not null) return false;
            var work = new Work(stateKey, generation);
            active = work;
            currentKey = stateKey; currentResult = null; error = null;
            // Do not pass a cancellation token to Task.Run or skip factory entry when canceled.
            // Accepted callers may have transferred a disposable texture lease to factory's using scope.
            _ = Task.Run(() => RunAsync(work, factory));
            return true;
        }
    }

    public void Invalidate() => Stop(false);
    public void Dispose() => Stop(true);

    private void Stop(bool disposing)
    {
        lock (gate)
        {
            if (disposed) return;
            if (disposing) disposed = true;
            generation++;
            currentKey = null; currentResult = null; error = null;
            // CancelAsync schedules arbitrary callbacks asynchronously; UI/Dispose never waits for them,
            // the task, Present, a decode or resource disposal owned by the factory.
            if (active is { Cancellation: null } work) work.Cancellation = work.Source.CancelAsync();
        }
    }

    private async Task RunAsync(Work work, Func<CancellationToken, Task<PortraitImageAnalysisResult>> factory)
    {
        PortraitImageAnalysisResult? result = null;
        string? failure = null;
        try
        {
            result = await factory(work.Source.Token).ConfigureAwait(false);
            if (result is null) failure = "暂时无法分析图像，请重试。";
        }
        catch (OperationCanceledException) when (work.Source.IsCancellationRequested) { }
        catch (Exception) { failure = "暂时无法分析图像，请重试。"; }
        Task? cancellation;
        lock (gate)
        {
            if (ReferenceEquals(active, work))
            {
                active = null;
                if (!disposed && generation == work.Generation && currentKey == work.Key)
                {
                    currentResult = result;
                    error = failure;
                }
            }
            cancellation = work.Cancellation;
        }
        // CTS Dispose is not raced against its asynchronous callbacks. This cleanup never blocks the UI
        // and holds no game resource. Callback failures are observed without replacing the analysis result.
        try { if (cancellation is not null) await cancellation.ConfigureAwait(false); }
        catch (Exception) { }
        finally { work.Source.Dispose(); }
    }

    private sealed class Work(string key, long generation)
    {
        public string Key { get; } = key;
        public long Generation { get; } = generation;
        public CancellationTokenSource Source { get; } = new();
        public Task? Cancellation { get; set; }
    }
}
