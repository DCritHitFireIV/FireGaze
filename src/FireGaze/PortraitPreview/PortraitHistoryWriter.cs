namespace FireGaze.PortraitPreview;

/// <summary>One worker atomically writes the newest full document; superseded pending documents coalesce.</summary>
internal sealed class PortraitHistoryWriter : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly List<(long Sequence, TaskCompletionSource<bool> Completion)> waiters = [];
    private readonly string path;
    private readonly int byteLimit;
    private readonly Task worker;
    private (long Sequence, byte[] Bytes)? pending;
    private long submitted;
    private long completed;
    private bool stopping;
    private bool lastSuccess = true;
    private string? lastError;
    public string? LastError { get { lock (gate) return lastError; } }

    public PortraitHistoryWriter(string path, int byteLimit)
    {
        this.path = path;
        this.byteLimit = byteLimit;
        worker = Task.Run(Work);
    }

    public bool Queue(byte[] bytes)
    {
        lock (gate)
        {
            if (stopping || bytes.Length > byteLimit) return false;
            var needsSignal = pending is null;
            pending = (++submitted, bytes);
            if (needsSignal) signal.Release();
            return true;
        }
    }

    public Task<bool> FlushAsync()
    {
        lock (gate)
        {
            if (completed >= submitted) return Task.FromResult(lastSuccess);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((submitted, completion));
            return completion.Task;
        }
    }

    private async Task Work()
    {
        while (true)
        {
            await signal.WaitAsync().ConfigureAwait(false);
            (long Sequence, byte[] Bytes) request;
            lock (gate)
            {
                if (pending is not { } value)
                {
                    if (stopping) return;
                    continue;
                }
                request = value;
                pending = null;
            }
            string? failure = null;
            try { Write(request.Bytes); }
            catch (Exception exception) { failure = exception.Message; }
            lock (gate)
            {
                completed = request.Sequence;
                lastSuccess = failure is null;
                lastError = failure;
                for (var index = waiters.Count - 1; index >= 0; index--)
                {
                    if (waiters[index].Sequence > completed) continue;
                    waiters[index].Completion.TrySetResult(lastSuccess);
                    waiters.RemoveAt(index);
                }
                if (stopping && pending is null) return;
            }
        }
    }

    private void Write(byte[] bytes)
    {
        if (bytes.Length > byteLimit) throw new InvalidDataException("肖像历史快照超过容量上限。");
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!stopping) { stopping = true; signal.Release(); }
        }
        worker.GetAwaiter().GetResult();
    }
}
