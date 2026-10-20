using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FireGaze.PortraitPreview;

internal enum PortraitNativeObservedPhase { ManagerRender, ManagerUpdateCandidate, SceneCameraUpdate, SourceCharaViewRender, CounterAdd, CounterSub }
internal enum PortraitNativeObservedEdge { Enter, Return, Throw }
internal enum PortraitNativeFrameOrigin { None, SourceRenderArgument, EnclosingSourceRenderArgument }
internal enum PortraitNativeObservationStop { Disabled, Expired, Explicit, Disposed, ThreadLimit, DepthLimit, ObserverFailure, InvalidNesting }
internal readonly record struct PortraitNativeThreadStamp(uint NativeID, int ManagedID);
internal readonly record struct PortraitNativeFrameEvidence(PortraitNativeFrameOrigin Origin, uint Value, nint SourceView);
internal readonly record struct PortraitNativePhaseRecord(long Sequence, long Capture, long Call, long ParentCall,
    PortraitNativeObservedPhase Phase, PortraitNativeObservedEdge Edge, nint Receiver, PortraitNativeThreadStamp Thread,
    int Depth, long Timestamp, PortraitNativeFrameEvidence Frame, float? FloatArgument, uint? CounterSlot = null);
internal readonly record struct PortraitNativePhaseCall(long Capture, long Call, PortraitNativeThreadStamp Thread);
internal sealed record PortraitNativePhaseObservationSnapshot(bool Observing, PortraitNativeObservationStop Stop,
    long Capture, long OverwrittenRecords, int ObservedThreads, IReadOnlyList<PortraitNativePhaseRecord> Records)
{
    public bool ProductionAdmission => false;
}

internal sealed class PortraitNativePhaseObservation : IDisposable
{
    private readonly object gate = new();
    private readonly PortraitNativePhaseRecord[] ring;
    private readonly Dictionary<PortraitNativeThreadStamp, ThreadStack> threads = [];
    private readonly Func<long> timestamp;
    private readonly Func<PortraitNativeThreadStamp> threadStamp;
    private readonly long frequency;
    private readonly int maxThreads;
    private readonly int maxDepth;
    private bool observing;
    private bool disposed;
    private long capture;
    private long started;
    private long lastTimestamp;
    private long durationTicks;
    private long sequence;
    private long calls;
    private long overwritten;
    private int count;
    private int head;
    private PortraitNativeObservationStop stop = PortraitNativeObservationStop.Disabled;

    public PortraitNativePhaseObservation(int capacity = 1024, int maxThreads = 16, int maxDepth = 32,
        Func<long>? timestamp = null, long timestampFrequency = 0, Func<PortraitNativeThreadStamp>? threadStamp = null)
    {
        if (capacity is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxThreads is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxThreads));
        if (maxDepth is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        frequency = timestampFrequency == 0 ? Stopwatch.Frequency : timestampFrequency;
        if (frequency < 1 || frequency > long.MaxValue / 300) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        ring = new PortraitNativePhaseRecord[capacity];
        this.maxThreads = maxThreads;
        this.maxDepth = maxDepth;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        this.threadStamp = threadStamp ?? NativeThreadStamp;
    }

    // Construction never reads a clock, thread, process, SDK address, or native object.
    public bool TryStart(TimeSpan duration)
    {
        lock (gate)
        {
            if (disposed || observing || duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5)) return false;
            try
            {
                started = timestamp();
                lastTimestamp = started;
                durationTicks = checked((long)Math.Ceiling(duration.TotalSeconds * frequency));
                capture = checked(capture + 1);
                sequence = calls = overwritten = 0;
                count = head = 0;
                Array.Clear(ring);
                threads.Clear();
                observing = true;
                stop = PortraitNativeObservationStop.Disabled;
                return true;
            }
            catch (Exception) { StopCore(PortraitNativeObservationStop.ObserverFailure); return false; }
        }
    }

    public void Stop() { lock (gate) { if (!disposed) StopCore(PortraitNativeObservationStop.Explicit); } }
    public void Dispose() { lock (gate) { disposed = true; StopCore(PortraitNativeObservationStop.Disposed); } }

    public PortraitNativePhaseObservationSnapshot Snapshot()
    {
        lock (gate)
        {
            try { if (observing) _ = ReadTimestamp(out _); }
            catch (Exception) { StopCore(PortraitNativeObservationStop.ObserverFailure); }
            var rows = new PortraitNativePhaseRecord[count];
            for (var index = 0; index < count; index++) rows[index] = ring[(head + index) % ring.Length];
            return new(observing, stop, capture, overwritten, threads.Count, Array.AsReadOnly(rows));
        }
    }

    internal PortraitNativePhaseCall Begin(PortraitNativeObservedPhase phase, nint receiver, uint? frame = null, float? floatArgument = null, uint? counterSlot = null)
    {
        lock (gate)
        {
            if (!observing) return default;
            try
            {
                if (!ReadTimestamp(out var now)) return default;
                var thread = threadStamp();
                if (thread.NativeID == 0 || thread.ManagedID <= 0) { StopCore(PortraitNativeObservationStop.ObserverFailure); return default; }
                if (!threads.TryGetValue(thread, out var stack))
                {
                    if (threads.Count == maxThreads) { StopCore(PortraitNativeObservationStop.ThreadLimit); return default; }
                    stack = new(maxDepth); threads.Add(thread, stack);
                }
                if (stack.Count == maxDepth) { StopCore(PortraitNativeObservationStop.DepthLimit); return default; }
                var parent = stack.Count == 0 ? default : stack.Calls[stack.Count - 1];
                var evidence = phase == PortraitNativeObservedPhase.SourceCharaViewRender && frame.HasValue
                    ? new PortraitNativeFrameEvidence(PortraitNativeFrameOrigin.SourceRenderArgument, frame.Value, receiver)
                    : parent.Frame.Origin == PortraitNativeFrameOrigin.None ? default
                    : parent.Frame with { Origin = PortraitNativeFrameOrigin.EnclosingSourceRenderArgument };
                var call = new Invocation(checked(++calls), parent.Call, phase, receiver, evidence, floatArgument,
                    phase is PortraitNativeObservedPhase.CounterAdd or PortraitNativeObservedPhase.CounterSub ? counterSlot : null);
                Append(call, thread, stack.Count, now, PortraitNativeObservedEdge.Enter);
                stack.Calls[stack.Count++] = call;
                return new(capture, call.Call, thread);
            }
            catch (Exception) { StopCore(PortraitNativeObservationStop.ObserverFailure); return default; }
        }
    }

    internal void End(PortraitNativePhaseCall call, bool returned)
    {
        lock (gate)
        {
            // A stopped/expired/older capture must never append a fabricated matching edge.
            if (!observing || call.Call == 0 || call.Capture != capture) return;
            try
            {
                if (!ReadTimestamp(out var now)) return;
                if (threadStamp() != call.Thread || !threads.TryGetValue(call.Thread, out var stack)
                    || stack.Count == 0 || stack.Calls[stack.Count - 1].Call != call.Call)
                { StopCore(PortraitNativeObservationStop.InvalidNesting); return; }
                var invocation = stack.Calls[--stack.Count];
                stack.Calls[stack.Count] = default;
                Append(invocation, call.Thread, stack.Count, now, returned ? PortraitNativeObservedEdge.Return : PortraitNativeObservedEdge.Throw);
            }
            catch (Exception) { StopCore(PortraitNativeObservationStop.ObserverFailure); }
        }
    }

    private bool ReadTimestamp(out long now)
    {
        now = timestamp();
        var elapsed = checked(now - started);
        if (elapsed < 0 || now < lastTimestamp) { StopCore(PortraitNativeObservationStop.ObserverFailure); return false; }
        lastTimestamp = now;
        if (elapsed < durationTicks) return true;
        StopCore(PortraitNativeObservationStop.Expired); return false;
    }

    private void Append(Invocation call, PortraitNativeThreadStamp thread, int depth, long now, PortraitNativeObservedEdge edge)
    {
        var row = new PortraitNativePhaseRecord(checked(++sequence), capture, call.Call, call.Parent, call.Phase, edge,
            call.Receiver, thread, depth, now, call.Frame, call.FloatArgument, call.CounterSlot);
        if (count == ring.Length) { ring[head] = row; head = (head + 1) % ring.Length; overwritten++; }
        else { ring[(head + count) % ring.Length] = row; count++; }
    }

    private void StopCore(PortraitNativeObservationStop reason)
    {
        observing = false; stop = reason;
        foreach (var stack in threads.Values) { Array.Clear(stack.Calls); stack.Count = 0; }
    }

    private static PortraitNativeThreadStamp NativeThreadStamp() => new(GetCurrentThreadID(), Environment.CurrentManagedThreadId);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)]
    private static extern uint GetCurrentThreadID();

    private readonly record struct Invocation(long Call, long Parent, PortraitNativeObservedPhase Phase,
        nint Receiver, PortraitNativeFrameEvidence Frame, float? FloatArgument, uint? CounterSlot);
    private sealed class ThreadStack(int capacity)
    { public readonly Invocation[] Calls = new Invocation[capacity]; public int Count; }
}
