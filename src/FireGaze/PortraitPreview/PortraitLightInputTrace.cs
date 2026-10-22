namespace FireGaze.PortraitPreview;

// Only fixed stage names and numeric sequence IDs may enter this diagnostic stream.
internal enum PortraitLightTraceStage
{
    GestureBegin, GestureMove, GestureEnd, GestureCancel, CaptureState, HistoryPoll, HistoryFlush,
    NativeExportBefore, NativeSetAngle, NativeExportAfter, BannerEntryWrite, NativeSyncControls,
    VerticalSliderSetValue, HorizontalSliderSetValue, SetHasChanged
}

/// <summary>
/// Bounded tracing of explicit light input. Ambient ownership exists only while one input
/// callback executes, so normal history sampling and unrelated restores remain untraced.
/// Each admitted stage reserves both its entry and terminal event before native work begins.
/// </summary>
internal sealed class PortraitLightInputTrace(Action<string> logger, int maximumStages = 128)
{
    [ThreadStatic] private static PortraitLightInputTrace? current;
    private static long serial;
    private readonly long gesture = Interlocked.Increment(ref serial);
    private readonly int limit = Math.Clamp(maximumStages, 1, 256);
    private int admitted;
    private bool exhausted;

    public void Enter(PortraitLightTraceStage phase, Action operation)
    {
        var previous = current;
        current = this;
        try { Run(phase, operation); }
        finally { current = previous; }
    }

    public static void Run(PortraitLightTraceStage phase, Action operation)
    {
        using var scope = Measure(phase);
        operation();
        scope.Complete();
    }

    public static Scope Measure(PortraitLightTraceStage phase)
    {
        var owner = current;
        if (owner == null) return Scope.Untraced;
        if (owner.admitted >= owner.limit)
        {
            if (!owner.exhausted)
            {
                owner.exhausted = true;
                owner.Log(0, phase, "budget-exhausted");
            }
            return Scope.Untraced;
        }
        var step = ++owner.admitted;
        var scope = new Scope(owner, step, phase);
        owner.Log(step, phase, "begin");
        return scope;
    }

    private void Log(int step, PortraitLightTraceStage phase, string state)
    {
        // Neither logger failures nor exception formatting can alter native execution.
        try { logger($"[PortraitLightInput] gesture={gesture} step={step} phase={phase} {state}"); }
        catch { }
    }

    internal sealed class Scope(PortraitLightInputTrace? owner, int step, PortraitLightTraceStage phase) : IDisposable
    {
        internal static readonly Scope Untraced = new(null, 0, default);
        private bool successful;
        private int disposed;
        public void Complete() { if (owner != null) successful = true; }
        public void Dispose()
        {
            if (owner != null && Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Log(step, phase, successful ? "end" : "failed");
        }
    }
}
