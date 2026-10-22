namespace FireGaze.PortraitPreview;

internal sealed record PortraitOwnedSceneJointSlotObservation(Guid Installation,
    PortraitOwnedSceneJointSourceReturn SourceReturn, PortraitOwnedSceneSlotCensus Slots)
{ public bool ProductionAdmission => false; }

// The old three-Host census is deliberately not consulted. This reader is open
// only during the new owner's actual private post-Original callback marker.
internal sealed class PortraitOwnedSceneJointSourceCensus
{
    private readonly object gate = new();
    private readonly PortraitIdentity source;
    private readonly long session, request;
    private readonly Func<bool> requestCurrent;
    private readonly PortraitOwnedSceneSlotNativePhaseSource phases;
    private readonly PortraitOwnedSceneSlotReader reader;
    private ReturnRead? active;
    private PortraitOwnedSceneJointSlotObservation? result;
    private int cancelled;
    internal PortraitOwnedSceneJointSourceCensus(PortraitIdentity source, long session, long request,
        Func<bool> requestCurrent, IPortraitOwnedSceneSlotNativeApi? native = null)
    {
        this.source = source; this.session = session; this.request = request; this.requestCurrent = requestCurrent;
        var copies = native ?? new PortraitOwnedSceneSlotNativeApi(() => IsNativeReadAdmitted);
        phases = new(copies, IsCurrent, IsCurrent, () => session);
        reader = new(copies, phases);
    }
    internal PortraitOwnedSceneJointSlotObservation? Result => Volatile.Read(ref result);
    internal IDisposable BeginCounterMutation() => phases.BeginCounterMutation();
    internal IDisposable BeginResourceMutation() => phases.BeginResourceMutation();
    internal bool IsNativeReadAdmitted => IsCurrent() && phases.IsReadScopeActive;
    internal void Cancel()
    {
        lock (gate) Volatile.Write(ref cancelled, 1);
        phases.EndRequest(request);
    }
    internal void Receive(PortraitOwnedSceneJointHost host, PortraitOwnedSceneHooks owner,
        PortraitOwnedSceneHooks.JointSourceInvocation invocation)
    {
        if (Volatile.Read(ref cancelled) != 0 || Result is not null || !requestCurrent()
            || !host.IsSourceReturnCurrent(owner, invocation)) return;
        var current = new ReturnRead(host, owner, invocation);
        if (Interlocked.CompareExchange(ref active, current, null) is not null) return;
        try
        {
            PortraitOwnedSceneSlotCensus? slots = null;
            using (var scope = phases.EnterSource(source, invocation.Observation.Frame, request))
            {
                if (scope is null || !phases.TryCapture(out var phase) || phase.Source != source || phase.SessionEpoch != session
                    || phase.NativeFrame != invocation.Observation.Frame || !reader.TryReadCensus(phase, out var candidate)
                    || !candidate.Complete || candidate.Rows.Count != 8 || !IsCurrent()
                    || !phases.TryCapture(out var after) || after != phase) return;
                slots = candidate;
            }
            // No native scope survives publication. Health/time checks run before
            // the nonblocking gate; cancellation shares the same managed gate.
            if (!IsCurrent()) return;
            host.TryPublishCensus(owner, invocation, () =>
            {
                if (!Monitor.TryEnter(gate)) return false;
                try
                {
                    if (cancelled != 0 || result is not null || !ReferenceEquals(Volatile.Read(ref active), current)) return false;
                    var value = invocation.Observation;
                    Volatile.Write(ref result, new(invocation.Installation,
                        new(invocation.Installation, source, session, request, value.Epoch, value.Frame, value.Call.Thread, value.Call.Call), slots));
                    return true;
                }
                finally { Monitor.Exit(gate); }
            });
        }
        finally { Interlocked.CompareExchange(ref active, null, current); }
    }
    private bool IsCurrent()
        => Volatile.Read(ref cancelled) == 0 && requestCurrent() && Volatile.Read(ref active) is { } current
            && current.Host.IsSourceReturnCurrent(current.Owner, current.Invocation);
    private sealed record ReturnRead(PortraitOwnedSceneJointHost Host, PortraitOwnedSceneHooks Owner,
        PortraitOwnedSceneHooks.JointSourceInvocation Invocation);
}
