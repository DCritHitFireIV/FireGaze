using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FireGaze.PortraitPreview;

// Issued on Framework after independently frozen owner/thread evidence matches
// passive source records. These fields never qualify native scene creation.
internal sealed record PortraitSourceCensusQualification(PortraitIdentity Source, long OwnerEpoch, long SessionEpoch,
    long BuildEpoch, PortraitNativeThreadStamp Thread)
{
    internal bool IsValid => Source.IsValid && Source.Generation > 0 && OwnerEpoch > 0 && SessionEpoch > 0
        && BuildEpoch > 0 && Thread.NativeID > 0 && Thread.ManagedID > 0;
}
internal sealed record PortraitSourceCensusQualificationStamp(long Epoch, PortraitSourceCensusQualification? Value);

internal sealed class PortraitSourceCensusQualifications
{
    private readonly object gate = new();
    private PortraitSourceCensusQualificationStamp current = new(0, null);
    internal PortraitSourceCensusQualificationStamp Current => Volatile.Read(ref current);
    internal void Publish(PortraitSourceCensusQualification? value)
    {
        lock (gate)
        {
            var previous = current;
            // A reissued identical value still gets a different instance/epoch.
            var next = previous.Epoch == long.MaxValue ? new PortraitSourceCensusQualificationStamp(long.MaxValue, null)
                : new(previous.Epoch + 1, value);
            Volatile.Write(ref current, next);
        }
    }
}

/// <summary>
/// Optional bounded observation consumer of the existing SourceRender Host.
/// Construction reads no clocks/threads/native state, installs no hooks, and
/// grants nothing. The explicit one-attempt diagnostic is its only Feature caller.
/// A complete DTO is historical data, never an idle reservation or scene lease.
/// </summary>
internal sealed class PortraitOwnedSceneSourceCensus : IPortraitNativeSourceReturnSink
{
    private readonly object gate = new();
    private readonly PortraitNativePhaseHost sourceHost, counters, resources;
    private readonly PortraitSourceCensusQualifications? qualifications;
    private readonly PortraitOwnedSceneSlotNativePhaseSource phases;
    private readonly PortraitOwnedSceneSlotReader reader;
    private readonly Func<long> timestamp;
    private readonly long frequency;
    private readonly int capacity;
    private readonly bool attached;
    private readonly Queue<PortraitOwnedSceneSlotCensus> results = new();
    private readonly Dictionary<PortraitNativeThreadStamp, InvocationTree> trees = [];
    private Request? request;
    private ReturnRead? activeReturn;
    private long lastRequest, cancellation, lastTimestamp = long.MinValue;
    private int failed;

    internal PortraitOwnedSceneSourceCensus(PortraitNativePhaseHost sourceHost, PortraitNativePhaseHost counters,
        PortraitNativePhaseHost resources, PortraitSourceCensusQualifications? qualifications = null,
        IPortraitOwnedSceneSlotNativeApi? native = null, Func<long>? timestamp = null, long timestampFrequency = 0, int capacity = 16)
    {
        this.sourceHost = sourceHost ?? throw new ArgumentNullException(nameof(sourceHost));
        this.counters = counters ?? throw new ArgumentNullException(nameof(counters));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.qualifications = qualifications;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        frequency = timestampFrequency == 0 ? Stopwatch.Frequency : timestampFrequency;
        if (frequency <= 0 || frequency > long.MaxValue / 300) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        if (capacity is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        var copies = native ?? new PortraitOwnedSceneSlotNativeApi(() => IsNativeReadAdmitted);
        phases = new(copies, IsSynchronousReturnCurrent, IsSynchronousReturnCurrent,
            () => Volatile.Read(ref activeReturn)?.Request.Qualification.Value?.SessionEpoch ?? 0);
        reader = new(copies, phases);
        attached = sourceHost.TryAttachSourceReturn(this);
    }

    internal IDisposable BeginCounterMutation() => phases.BeginCounterMutation();
    internal IDisposable BeginResourceMutation() => phases.BeginResourceMutation();
    internal bool IsNativeReadAdmitted => IsSynchronousReturnCurrent() && phases.IsReadScopeActive;

    internal bool TryArm(long requestEpoch, TimeSpan duration)
    {
        if (!attached || Volatile.Read(ref failed) != 0 || requestEpoch <= 0 || duration <= TimeSpan.Zero
            || duration > TimeSpan.FromMinutes(5) || qualifications is null || !TryReadTimestamp(out var now)) return false;
        var qualification = qualifications.Current;
        var capture = sourceHost.SourceCapture;
        if (qualification.Epoch <= 0 || qualification.Value is not { IsValid: true } value || !OnThread(value.Thread)
            || capture is not { Capture: > 0 }
            || !TryReadHealth(out var source, out var counter, out var resource)) return false;
        try
        {
            var deadline = checked(now + (long)Math.Ceiling(duration.TotalSeconds * frequency));
            lock (gate)
            {
                if (requestEpoch <= lastRequest || Volatile.Read(ref failed) != 0
                    || !ReferenceEquals(qualifications.Current, qualification)
                    || !ReferenceEquals(sourceHost.SourceCapture, capture)
                    || !HealthCurrent(source, counter, resource)) return false;
                var next = new Request(requestEpoch, cancellation, now, deadline, qualification, capture, source, counter, resource);
                lastRequest = requestEpoch; Volatile.Write(ref request, next); return true;
            }
        }
        catch (Exception) { Fail(); return false; }
    }

    public void Cancel()
    {
        // Completion publishes under this same short managed gate, after its
        // native scope is gone. No native call/delegate/Host lock occurs here.
        lock (gate)
        {
            Volatile.Write(ref request, null);
            if (cancellation == long.MaxValue) Volatile.Write(ref failed, 1); else cancellation++;
        }
    }

    internal bool TryTake(out PortraitOwnedSceneSlotCensus? census)
    {
        lock (gate)
        {
            if (results.Count == 0) { census = null; return false; }
            census = results.Dequeue(); return true;
        }
    }

    Action<bool> IPortraitNativeSourceReturnSink.Begin(PortraitNativePhaseHost.SourceReturnInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var call = invocation.Call;
        InvocationTree? tree = null;
        Request? frozen = null;
        Request? candidate = null;
        lock (gate)
        {
            if (trees.TryGetValue(call.Thread, out tree))
            { tree.Depth++; Volatile.Write(ref tree.Poisoned, 1); }
            else if (trees.Count < 16)
            { tree = new(); trees.Add(call.Thread, tree); }
            else { Fail(); }
            candidate = Volatile.Read(ref request);
            if (!(tree is { Depth: 1, Poisoned: 0 } && candidate is not null && candidate.Qualification.Value is { } value
                && invocation.Receiver == value.Source.Model && call.Thread == value.Thread && ReferenceEquals(invocation.Health, candidate.Source)
                && RequestStructurallyCurrent(candidate, call))) candidate = null;
        }
        // Clock/thread/recorder checks may call external delegates or take the
        // observation lock. They must never run while the short cancellation
        // gate is held. Reconfirm detached identity under the gate below.
        if (tree is { Depth: 1, Poisoned: 0 } && candidate is not null && RequestCurrent(candidate, call))
        {
            lock (gate)
            {
                if (trees.TryGetValue(call.Thread, out var currentTree) && ReferenceEquals(currentTree, tree)
                    && tree.Depth == 1 && Volatile.Read(ref tree.Poisoned) == 0
                    && ReferenceEquals(Volatile.Read(ref request), candidate)
                    && RequestStructurallyCurrent(candidate, call)) frozen = candidate;
            }
        }
        var completed = 0;
        return confirmed => {
            if (Interlocked.Exchange(ref completed, 1) != 0) return;
            try
            {
                if (confirmed && tree is not null && frozen is not null)
                    Complete(new(frozen, invocation, tree));
            }
            catch (Exception) { Fail(); }
            finally
            {
                lock (gate)
                {
                    if (tree is not null && trees.TryGetValue(call.Thread, out var currentTree) && ReferenceEquals(currentTree, tree))
                    { if (--tree.Depth == 0) trees.Remove(call.Thread); }
                }
            }
        };
    }

    private void Complete(ReturnRead active)
    {
        PortraitOwnedSceneSlotCensus? result = null;
        if (!Current(active) || Interlocked.CompareExchange(ref activeReturn, active, null) is not null) return;
        try
        {
            var value = active.Request.Qualification.Value!;
            using var scope = phases.EnterSource(value.Source, active.Invocation.Frame, active.Request.Epoch);
            if (scope is null || !phases.TryCapture(out var phase) || phase.Source != value.Source
                || phase.NativeFrame != active.Invocation.Frame || phase.SessionEpoch != value.SessionEpoch
                || !reader.TryReadCensus(phase, out var candidate) || !candidate.Complete
                || !Current(active) || !phases.TryCapture(out var after) || after != phase) return;
            result = candidate;
        }
        finally { Interlocked.CompareExchange(ref activeReturn, null, active); }
        // Scope and marker are gone before publication. Stop/Cancel shares this
        // gate, so no old completion can publish after cancellation returns.
        if (result is null) return;
        // Recorder checks precede the final request-clock sample. This is the
        // time qualification point; no blocking lock/delegate follows it on
        // the publish path. Contention discards this historical observation.
        if (!Current(active) || !Monitor.TryEnter(gate)) return;
        try
        {
            if (!CurrentStructurally(active)) return;
            if (results.Count == capacity) results.Dequeue();
            results.Enqueue(result);
        }
        finally { Monitor.Exit(gate); }
    }

    private bool IsSynchronousReturnCurrent()
        => Volatile.Read(ref activeReturn) is { } active && Current(active);

    private bool Current(ReturnRead active)
        => CurrentStructurally(active) && RequestCurrent(active.Request, active.Invocation.Call);

    private bool CurrentStructurally(ReturnRead active)
        => active.Invocation.IsReturningFor(sourceHost) && Volatile.Read(ref active.Tree.Poisoned) == 0
            && Volatile.Read(ref active.Tree.Depth) == 1 && RequestStructurallyCurrent(active.Request, active.Invocation.Call);

    private bool RequestStructurallyCurrent(Request expected, PortraitNativePhaseCall call)
        => Volatile.Read(ref failed) == 0 && ReferenceEquals(Volatile.Read(ref request), expected)
            && expected.Cancellation == Interlocked.Read(ref cancellation)
            && expected.Capture.Capture == call.Capture && ReferenceEquals(sourceHost.SourceCapture, expected.Capture)
            && qualifications is not null && ReferenceEquals(qualifications.Current, expected.Qualification)
            && expected.Qualification.Value is { IsValid: true } value && call.Thread == value.Thread
            && HealthCurrent(expected.Source, expected.Counter, expected.Resource);

    private bool RequestCurrent(Request expected, PortraitNativePhaseCall call)
    {
        if (!RequestStructurallyCurrent(expected, call) || expected.Qualification.Value is not { IsValid: true } value
            || !OnThread(value.Thread) || !sourceHost.IsSourceCallCurrent(call, expected.Source)) return false;
        return TryReadTimestamp(out var now) && now >= expected.Start && now < expected.Deadline
            && RequestStructurallyCurrent(expected, call);
    }

    private bool TryReadHealth(out PortraitNativePhaseHostHealth source, out PortraitNativePhaseHostHealth counter,
        out PortraitNativePhaseHostHealth resource)
    {
        source = sourceHost.Health; counter = counters.Health; resource = resources.Health;
        return HealthCurrent(source, counter, resource);
    }
    private bool HealthCurrent(PortraitNativePhaseHostHealth source, PortraitNativePhaseHostHealth counter, PortraitNativePhaseHostHealth resource)
        => ReferenceEquals(sourceHost.Health, source) && ReferenceEquals(counters.Health, counter) && ReferenceEquals(resources.Health, resource)
            && source.TargetSet == PortraitNativePhaseTargetSet.PhaseObservation && source.State == PortraitNativePhaseHostState.Observing
            && source.HostGeneration > 0 && source.ObservationGeneration > 0 && !source.Failed
            && counter.TargetSet == PortraitNativePhaseTargetSet.CounterIngress && counter.IsMutationLive
            && resource.TargetSet == PortraitNativePhaseTargetSet.ResourceIngress && resource.IsMutationLive
            && ReferenceEquals(counters.Health, counter) && ReferenceEquals(resources.Health, resource) && ReferenceEquals(sourceHost.Health, source);

    private bool TryReadTimestamp(out long now)
    {
        now = 0;
        try
        {
            now = timestamp();
            while (true)
            {
                var previous = Interlocked.Read(ref lastTimestamp);
                if (now < previous) { Fail(); return false; }
                if (Interlocked.CompareExchange(ref lastTimestamp, now, previous) != previous) continue;
                return true;
            }
        }
        catch (Exception) { Fail(); return false; }
    }
    private void Fail() { Volatile.Write(ref failed, 1); Interlocked.Exchange(ref request, null); }
    private static bool OnThread(PortraitNativeThreadStamp expected)
        => expected.ManagedID == Environment.CurrentManagedThreadId && expected.NativeID == GetCurrentThreadID();
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)] private static extern uint GetCurrentThreadID();

    private sealed record Request(long Epoch, long Cancellation, long Start, long Deadline,
        PortraitSourceCensusQualificationStamp Qualification, PortraitNativePhaseCapture Capture, PortraitNativePhaseHostHealth Source,
        PortraitNativePhaseHostHealth Counter, PortraitNativePhaseHostHealth Resource);
    private sealed class InvocationTree { internal int Depth = 1; internal int Poisoned; }
    private sealed record ReturnRead(Request Request, PortraitNativePhaseHost.SourceReturnInvocation Invocation, InvocationTree Tree);
}
