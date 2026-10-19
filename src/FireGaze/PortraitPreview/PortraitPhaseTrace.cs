namespace FireGaze.PortraitPreview;

internal sealed class PortraitPhaseTrace(Action<string> logger, Func<long>? milliseconds = null)
{
    private readonly object sync = new();
    private readonly Func<long> clock = milliseconds ?? (() => Environment.TickCount64);
    private readonly Dictionary<long, Scope> active = [];
    private readonly Dictionary<string, (int Count, long Last)> sampled = [];
    private long serial;
    private long detailedUntil;
    private long slowStages;
    public string[] ActiveStages { get { lock (sync) return active.Values.Select(value => value.Phase).ToArray(); } }
    public long SlowStages { get { lock (sync) return slowStages; } }

    public void Arm()
    {
        lock (sync) { sampled.Clear(); detailedUntil = clock() + 15_000; }
    }

    public IDisposable Measure(string phase)
    {
        Scope scope;
        lock (sync)
        {
            var now = clock();
            sampled.TryGetValue(phase, out var last);
            var log = now < detailedUntil && (last.Count < 2 || now - last.Last >= 1000);
            if (log) sampled[phase] = (last.Count + 1, now);
            scope = new(this, ++serial, phase, now, log);
            active.Add(scope.ID, scope);
        }
        // Never hold the trace lock while invoking a plugin logger.
        if (scope.Log) Log($"begin {scope.Phase} #{scope.ID}");
        return scope;
    }

    private void Complete(Scope scope)
    {
        long elapsed;
        lock (sync)
        {
            if (!active.Remove(scope.ID)) return;
            elapsed = Math.Max(0, clock() - scope.Start);
            if (elapsed >= 40) slowStages++;
        }
        if (scope.Log || elapsed >= 40) Log($"end {scope.Phase} #{scope.ID} {elapsed}ms");
    }

    private void Log(string message)
    {
        try { logger(message); }
        catch { /* Diagnostic storage cannot interrupt the game loop. */ }
    }

    private sealed class Scope(PortraitPhaseTrace owner, long id, string phase, long start, bool log) : IDisposable
    {
        private int completed;
        public long ID { get; } = id;
        public string Phase { get; } = phase;
        public long Start { get; } = start;
        public bool Log { get; } = log;
        public void Dispose() { if (Interlocked.Exchange(ref completed, 1) == 0) owner.Complete(this); }
    }
}
