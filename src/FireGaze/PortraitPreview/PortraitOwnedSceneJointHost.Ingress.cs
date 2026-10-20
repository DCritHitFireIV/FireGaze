namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedSceneJointMutationKind { Counter, Resource, Revoked }
// Detached receiver contract for the next owned-copy slice. A subscriber may
// revoke/cancel managed work only; this notice never permits SDK/native calls.
internal sealed record PortraitOwnedSceneJointMutationNotice(Guid Installation, long SessionEpoch, long RequestEpoch,
    long MutationEpoch, PortraitOwnedSceneJointMutationKind Kind);
internal sealed record PortraitOwnedSceneJointSourceReturn(Guid Installation, PortraitIdentity Source,
    long SessionEpoch, long RequestEpoch, long MutationEpoch, uint NativeFrame, PortraitNativeThreadStamp Thread, long Call);
internal sealed record PortraitOwnedSceneJointIngressSnapshot(Guid Installation, long SessionEpoch, long RequestEpoch,
    long MutationEpoch, int CounterDepth, int ResourceDepth, bool Recording, bool Failed, PortraitNativeResourceState Resource,
    IReadOnlyList<PortraitOwnedSceneJointSourceReturn> SourceReturns, PortraitNativePhaseObservationSnapshot Observation);

// Bounded detached recording only. This ingress grants no native read scope and
// cannot allocate a character. Source evidence uses the actual owned callback's
// private return authority, independently of the older three-Host census.
internal sealed class PortraitOwnedSceneJointIngress
{
    private readonly object gate = new();
    private readonly PortraitIdentity source;
    private readonly long session, request;
    private readonly PortraitNativeThreadStamp expectedThread;
    private readonly Func<bool> requestCurrent;
    private readonly Func<long> timestamp;
    private readonly long frequency;
    private readonly Func<PortraitNativeThreadStamp> nativeThread;
    private readonly Func<IDisposable?> counters, resources;
    private readonly Action<PortraitOwnedSceneJointMutationNotice>? mutationReceiver;
    private readonly Action<PortraitOwnedSceneHooks, PortraitOwnedSceneHooks.JointSourceInvocation>? sourceReceiver;
    private readonly PortraitNativePhaseObservation observation;
    private readonly Dictionary<PortraitNativeThreadStamp, SourceTree> trees = [];
    private readonly Queue<PortraitOwnedSceneJointSourceReturn> returns = new();
    private Guid installation;
    private long epoch, started, deadline, lastTimestamp;
    private int counterDepth, resourceDepth;
    private bool active, failed, revoked, activeExperiment;
    private long retirementCalls;
    private PortraitNativeResourceState resource;
    private ActiveDispatch? activeDispatch;
    private PortraitOwnedSceneHooks? activeOwner;
    internal void EnableActiveOwner(PortraitOwnedSceneHooks owner) => activeOwner = owner;
    internal PortraitOwnedSceneJointIngress(PortraitIdentity source, long session, long request,
        PortraitNativeThreadStamp expectedThread, Func<bool> requestCurrent,
        Func<IDisposable?> counters, Func<IDisposable?> resources,
        Func<long> timestamp, long frequency, Func<PortraitNativeThreadStamp> nativeThread,
        Action<PortraitOwnedSceneJointMutationNotice>? mutationReceiver,
        Action<PortraitOwnedSceneHooks, PortraitOwnedSceneHooks.JointSourceInvocation>? sourceReceiver)
    {
        this.source = source; this.session = session; this.request = request; this.expectedThread = expectedThread;
        this.requestCurrent = requestCurrent; this.counters = counters; this.resources = resources;
        this.timestamp = timestamp; this.frequency = frequency; this.nativeThread = nativeThread;
        this.mutationReceiver = mutationReceiver;
        this.sourceReceiver = sourceReceiver;
        observation = new(1024, timestamp: timestamp, timestampFrequency: frequency, threadStamp: nativeThread);
    }
    internal void Bind(Guid value)
    {
        lock (gate)
        {
            if (installation != Guid.Empty || value == Guid.Empty || revoked) throw new InvalidOperationException("Joint callback identity cannot be rebound.");
            installation = value;
        }
    }
    internal bool TryCommit(TimeSpan duration, bool activeExperiment = false)
    {
        var now = timestamp();
        if (!requestCurrent() || !observation.TryStart(duration)) return false;
        lock (gate)
        {
            if (installation == Guid.Empty || failed || revoked || counterDepth != 0 || resourceDepth != 0) return false;
            this.activeExperiment = activeExperiment;
            started = lastTimestamp = now; deadline = checked(now + (long)Math.Ceiling(duration.TotalSeconds * frequency)); active = true; return true;
        }
    }
    internal void Revoke()
    {
        PortraitOwnedSceneJointMutationNotice? notice;
        lock (gate) notice = RevokeLocked(false);
        FinishRevocation(notice);
    }
    internal PortraitOwnedSceneJointIngressSnapshot Snapshot()
    {
        bool refresh;
        lock (gate) refresh = active && !revoked;
        if (refresh) _ = Current();
        var captured = observation.Snapshot();
        ReconcileRecorder(captured);
        lock (gate) return new(installation, session, request, epoch, counterDepth, resourceDepth,
            active && captured.Observing && !failed && !revoked, failed, resource, Array.AsReadOnly(returns.ToArray()), captured);
    }
    internal Action<bool>? Observe(int phase, nint receiver, uint argument, float delta)
    {
        var isCounter = phase is (int)PortraitNativeObservedPhase.CounterAdd or (int)PortraitNativeObservedPhase.CounterSub;
        IDisposable? mutation = null;
        var guarded = isCounter && argument < 8;
        var own = false;
        if (isCounter && activeOwner is not null && activeDispatch is null)
            activeOwner.InvalidateActiveJoint("Counter outside the proved active native invocation revoked scene ownership.");
        if (isCounter && activeDispatch is { } dispatch)
        {
            own = dispatch.Owner.TryBeginOwnedCounter(receiver, argument, out mutation);
            if (!own) dispatch.Owner.InvalidateActiveJoint("Unknown or foreign counter invalidated active ownership.");
        }
        if (guarded) mutation = BeginMutation(false, existing: own ? mutation : null);
        var call = default(PortraitNativePhaseCall);
        try
        {
            if (Current()) call = observation.Begin((PortraitNativeObservedPhase)phase, receiver,
                floatArgument: phase == (int)PortraitNativeObservedPhase.ManagerUpdateCandidate ? delta : null,
                counterSlot: isCounter ? argument : null);
            ReconcileRecorder();
        }
        catch (Exception) { Fail(); }
        return returned => {
            try { mutation?.Dispose(); }
            catch (Exception) { Fail(); }
            finally
            {
                if (guarded) EndMutation(false, phase, returned, 0);
                if (own) activeDispatch?.Owner.CompleteOwnedCounter(returned);
                try { observation.End(call, returned); ReconcileRecorder(); } catch (Exception) { Fail(); }
            }
        };
    }
    internal Action<bool, byte>? ObserveResource(int phase, nint receiver)
    {
        activeOwner?.InvalidateActiveJoint("Global RT resource mutation revoked active scene ownership.");
        var mutation = BeginMutation(true, receiver);
        var call = default(PortraitNativePhaseCall);
        try { if (Current()) call = observation.Begin((PortraitNativeObservedPhase)phase, receiver); ReconcileRecorder(); }
        catch (Exception) { Fail(); }
        return (returned, result) => {
            try { mutation?.Dispose(); }
            catch (Exception) { Fail(); }
            finally
            {
                EndMutation(true, phase, returned, result);
                try { observation.End(call, returned, phase == (int)PortraitNativeObservedPhase.ResourceRecreate ? result : null); ReconcileRecorder(); }
                catch (Exception) { Fail(); }
            }
        };
    }
    private IDisposable? BeginMutation(bool isResource, nint receiver = 0, IDisposable? existing = null)
    {
        PortraitOwnedSceneJointMutationNotice notice;
        PortraitOwnedSceneJointMutationNotice? terminal;
        lock (gate)
        {
            terminal = BumpLocked() ? null : RevokeLocked(true);
            try
            {
                if (isResource)
                {
                    resourceDepth = checked(resourceDepth + 1);
                    resource = new(checked(resource.Generation + 1), resourceDepth, PortraitNativeResourceTransition.Transitioning, receiver, null);
                }
                else counterDepth = checked(counterDepth + 1);
            }
            catch (OverflowException) { terminal ??= RevokeLocked(true); }
            notice = Notice(isResource ? PortraitOwnedSceneJointMutationKind.Resource : PortraitOwnedSceneJointMutationKind.Counter);
        }
        if (terminal is not null) FinishRevocation(terminal);
        // Same-installation managed copy cancellation happens before Original,
        // including during partial Enable and after the recorder expires.
        Notify(notice);
        // No receiver is passed to the detached invalidation sink. Its scope
        // spans the real Original even when recording has already expired.
        try { return existing ?? (isResource ? resources : counters)() ?? throw new InvalidOperationException("Joint mutation guard was not issued."); }
        catch (Exception) { Fail(); return null; }
    }
    private void EndMutation(bool isResource, int phase, bool returned, byte result)
    {
        PortraitOwnedSceneJointMutationNotice? terminal;
        lock (gate)
        {
            var invalid = false;
            if (isResource)
            {
                if (resourceDepth <= 0) invalid = true; else resourceDepth--;
                resource = resource with { Depth = resourceDepth,
                    Transition = resourceDepth > 0 ? PortraitNativeResourceTransition.Transitioning : !returned ? PortraitNativeResourceTransition.OriginalThrew
                        : phase == (int)PortraitNativeObservedPhase.ResourceCleanup ? PortraitNativeResourceTransition.CleanupReturned : PortraitNativeResourceTransition.RecreateReturned,
                    NativeReturn = returned && resourceDepth == 0 && phase == (int)PortraitNativeObservedPhase.ResourceRecreate ? result : null };
            }
            else { if (counterDepth <= 0) invalid = true; else counterDepth--; }
            if (!BumpLocked()) invalid = true;
            terminal = invalid ? RevokeLocked(true) : null;
        }
        if (terminal is not null) FinishRevocation(terminal);
    }
    internal SourceObservation? BeginSource(nint receiver, uint frame)
    {
        var retirement = !Current();
        if (retirement && !CanRetire()) return null;
        var call = retirement ? new PortraitNativePhaseCall(0, -Interlocked.Increment(ref retirementCalls), nativeThread())
            : observation.Begin(PortraitNativeObservedPhase.SourceCharaViewRender, receiver, frame);
        if (!retirement && call.Call <= 0) { ReconcileRecorder(); return null; }
        PortraitOwnedSceneJointMutationNotice? terminal = null;
        SourceObservation value;
        lock (gate)
        {
            if (!trees.TryGetValue(call.Thread, out var tree))
            {
                if (trees.Count >= 16) terminal = RevokeLocked(true);
                else { tree = new(); trees.Add(call.Thread, tree); }
            }
            else tree.Poisoned = true;
            if (tree is not null) tree.Depth++;
            value = new(call, receiver, frame, epoch, observation.ActiveCapture, tree, retirement);
        }
        if (terminal is not null) FinishRevocation(terminal);
        return value;
    }
    internal bool EndSource(SourceObservation value, bool returned)
    {
        if (value.RetirementOnly) return returned && CanRetire() && value.Call.Thread == nativeThread();
        try { var confirmed = observation.TryEnd(value.Call, returned); ReconcileRecorder(); return confirmed; }
        catch (Exception) { Fail(); return false; }
    }
    internal void RecordSource(PortraitOwnedSceneHooks owner, PortraitOwnedSceneHooks.JointSourceInvocation invocation)
    {
        var value = invocation.Observation;
        if (!invocation.IsReturningFor(owner) || value.Capture is null) return;
        if (!observation.IsCaptureActive(value.Call)) { ReconcileRecorder(); return; }
        if (!requestCurrent()) { Revoke(); return; }
        if (nativeThread() != expectedThread) return;
        // Recorder/delegate checks precede this final time sample. Publication
        // never waits for gate after sampling; contention discards the row.
        var now = timestamp();
        if (!Monitor.TryEnter(gate)) return;
        PortraitOwnedSceneJointMutationNotice? terminal = null;
        try
        {
            if (!active || failed || revoked) return;
            if (now < started || now < lastTimestamp || now >= deadline)
            { terminal = RevokeLocked(now < started || now < lastTimestamp); return; }
            if (installation != invocation.Installation || value.Epoch != epoch
                || !ReferenceEquals(observation.ActiveCapture, value.Capture)
                || counterDepth != 0 || resourceDepth != 0 || value.Receiver != source.Model || value.Call.Thread != expectedThread
                || value.Tree is not { Depth: 1, Poisoned: false }) return;
            lastTimestamp = now;
            if (returns.Count == 32) returns.Dequeue();
            returns.Enqueue(new(installation, source, session, request, epoch, value.Frame, value.Call.Thread, value.Call.Call));
        }
        finally { Monitor.Exit(gate); if (terminal is not null) FinishRevocation(terminal); }
        sourceReceiver?.Invoke(owner, invocation);
    }
    // Only the actual still-open callback marker can authorize a synchronous
    // read-only census. Detached rows and manually constructed markers cannot.
    internal bool IsSourceReturnCurrent(PortraitOwnedSceneHooks owner, PortraitOwnedSceneHooks.JointSourceInvocation invocation)
    {
        var value = invocation.Observation;
        if (!invocation.IsReturningFor(owner) || value.Capture is null || !observation.IsCaptureActive(value.Call))
        { ReconcileRecorder(); return false; }
        if (!Current() || nativeThread() != expectedThread) return false;
        lock (gate) return active && !failed && !revoked && installation == invocation.Installation && value.Epoch == epoch
            && ReferenceEquals(observation.ActiveCapture, value.Capture) && counterDepth == 0 && resourceDepth == 0
            && value.Receiver == source.Model && value.Call.Thread == expectedThread && value.Tree is { Depth: 1, Poisoned: false };
    }
    internal ActiveDispatch? TryBeginActiveDispatch(PortraitOwnedSceneHooks owner, PortraitOwnedSceneHooks.JointSourceInvocation invocation, bool retirementOnly)
    {
        if (activeDispatch is not null) return null;
        if (retirementOnly)
        {
            if (!invocation.IsReturningFor(owner) || !CanRetire()) return null;
            var value = invocation.Observation;
            lock (gate) if (installation != invocation.Installation || value.Epoch != epoch || value.Receiver != source.Model
                || value.Call.Thread != expectedThread || value.Tree is not { Depth: 1, Poisoned: false }) return null;
        }
        else if (!IsSourceReturnCurrent(owner, invocation)) return null;
        return activeDispatch = new(this, owner, invocation, retirementOnly);
    }
    internal bool IsActiveDispatchCurrent(PortraitOwnedSceneHooks owner, ActiveDispatch dispatch)
        => IsActiveDispatchIdentityCurrent(owner, dispatch) && NoMutation();
    private bool NoMutation() { lock (gate) return counterDepth == 0 && resourceDepth == 0; }
    internal bool IsActiveDispatchIdentityCurrent(PortraitOwnedSceneHooks owner, ActiveDispatch dispatch)
    {
        if (!ReferenceEquals(dispatch.Ingress, this) || !ReferenceEquals(dispatch.Owner, owner) || dispatch.Closed) return false;
        if (!dispatch.RetirementOnly && !Current())
        {
            if (!CanRetire(true)) return false;
            dispatch.RetirementOnly = true; owner.MarkActiveRetirementOnly();
        }
        if (!dispatch.Invocation.IsReturningFor(owner) || !(dispatch.RetirementOnly ? CanRetire(true) : Current())
            || nativeThread() != expectedThread) return false;
        var value = dispatch.Invocation.Observation;
        lock (gate) return !dispatch.Invalid && (dispatch.RetirementOnly || active && !revoked) && !failed && installation == dispatch.Invocation.Installation
            && value.Receiver == source.Model && value.Call.Thread == expectedThread
            && value.Tree is { Depth: 1, Poisoned: false };
    }
    internal sealed class ActiveDispatch(PortraitOwnedSceneJointIngress ingress, PortraitOwnedSceneHooks owner,
        PortraitOwnedSceneHooks.JointSourceInvocation invocation, bool retirementOnly) : IDisposable
    {
        internal readonly PortraitOwnedSceneJointIngress Ingress = ingress;
        internal readonly PortraitOwnedSceneHooks Owner = owner;
        internal readonly PortraitOwnedSceneHooks.JointSourceInvocation Invocation = invocation;
        internal bool RetirementOnly = retirementOnly;
        internal bool Invalid, Closed;
        public void Dispose() { Closed = true; if (ReferenceEquals(Ingress.activeDispatch, this)) Ingress.activeDispatch = null; }
    }
    // Called only after the native read scope has closed. The publisher may
    // perform a nonblocking managed DTO commit only; no SDK/native/recorder work.
    internal bool TryPublishCensus(PortraitOwnedSceneHooks owner, PortraitOwnedSceneHooks.JointSourceInvocation invocation,
        Func<bool> publishDetached)
    {
        if (!IsSourceReturnCurrent(owner, invocation)) return false;
        var now = timestamp();
        if (!Monitor.TryEnter(gate)) return false;
        PortraitOwnedSceneJointMutationNotice? terminal = null;
        try
        {
            var value = invocation.Observation;
            if (!active || failed || revoked) return false;
            if (now < started || now < lastTimestamp || now >= deadline)
            { terminal = RevokeLocked(now < started || now < lastTimestamp); return false; }
            if (installation != invocation.Installation || value.Epoch != epoch || counterDepth != 0 || resourceDepth != 0
                || !ReferenceEquals(observation.ActiveCapture, value.Capture) || value.Tree is not { Depth: 1, Poisoned: false }) return false;
            lastTimestamp = now;
            return publishDetached();
        }
        finally { Monitor.Exit(gate); if (terminal is not null) FinishRevocation(terminal); }
    }
    internal void CloseSource(SourceObservation value)
    {
        lock (gate)
            if (value.Tree is { } tree && trees.TryGetValue(value.Call.Thread, out var current) && ReferenceEquals(current, tree)
                && --tree.Depth == 0) trees.Remove(value.Call.Thread);
    }
    private bool CanRetire(bool executionIdentity = false)
    {
        try
        {
            if (!requestCurrent() || nativeThread() != expectedThread) return false;
            lock (gate) return activeExperiment && !failed && installation != Guid.Empty && (executionIdentity || counterDepth == 0 && resourceDepth == 0);
        }
        catch (Exception) { Fail(); return false; }
    }
    private bool Current()
    {
        try
        {
            var now = timestamp(); var current = requestCurrent();
            lock (gate)
            {
                if (!active || failed || revoked) return false;
                if (current && now >= started && now >= lastTimestamp && now < deadline)
                { lastTimestamp = now; return true; }
                // Deny publication before leaving the gate, then use the common
                // revocation path so pending managed work is notified exactly once.
                active = false;
            }
            Revoke(); return false;
        }
        catch (Exception) { Fail(); return false; }
    }
    private bool BumpLocked() { if (epoch == long.MaxValue) return false; epoch++; return true; }
    private PortraitOwnedSceneJointMutationNotice? RevokeLocked(bool failure)
    {
        failed |= failure; active = false;
        if (revoked) return null;
        revoked = true;
        if (!BumpLocked()) failed = true;
        return Notice(PortraitOwnedSceneJointMutationKind.Revoked);
    }
    private void FinishRevocation(PortraitOwnedSceneJointMutationNotice? notice)
    {
        // Keep an already-stopped recorder's diagnostic reason where possible.
        if (observation.ActiveCapture is not null) observation.Stop();
        if (notice is not null) Notify(notice);
    }
    private void ReconcileRecorder(PortraitNativePhaseObservationSnapshot? captured = null)
    {
        lock (gate) if (!active || revoked) return;
        if (captured is null && observation.ActiveCapture is not null) return;
        captured ??= observation.Snapshot();
        if (captured.Observing) return;
        PortraitOwnedSceneJointMutationNotice? notice;
        lock (gate)
        {
            if (!active || revoked) return;
            notice = RevokeLocked(captured.Stop != PortraitNativeObservationStop.Expired);
        }
        FinishRevocation(notice);
    }
    private PortraitOwnedSceneJointMutationNotice Notice(PortraitOwnedSceneJointMutationKind kind)
        => new(installation, session, request, epoch, kind);
    private void Notify(PortraitOwnedSceneJointMutationNotice notice)
    {
        try { mutationReceiver?.Invoke(notice); }
        catch (Exception)
        {
            PortraitOwnedSceneJointMutationNotice? terminal;
            lock (gate) terminal = RevokeLocked(true);
            if (observation.ActiveCapture is not null) observation.Stop();
            // A throwing receiver gets at most one terminal invocation attempt.
            // Do not route its second failure back through Notify or Fail.
            if (terminal is not null) try { mutationReceiver?.Invoke(terminal); } catch (Exception) { }
        }
    }
    private void Fail()
    {
        PortraitOwnedSceneJointMutationNotice? notice;
        lock (gate) notice = RevokeLocked(true);
        FinishRevocation(notice);
    }
    internal sealed class SourceTree { internal int Depth; internal bool Poisoned; }
    internal sealed record SourceObservation(PortraitNativePhaseCall Call, nint Receiver, uint Frame, long Epoch,
        PortraitNativePhaseCapture? Capture, SourceTree? Tree, bool RetirementOnly = false);
}
