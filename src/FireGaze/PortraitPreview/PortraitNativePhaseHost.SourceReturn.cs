namespace FireGaze.PortraitPreview;

// Detached immutable health only. Reading it takes no peer Host/recorder lock
// and performs no native work. It grants neither a read scope nor ownership.
internal sealed record PortraitNativePhaseHostHealth(PortraitNativePhaseHostState State, PortraitNativePhaseTargetSet TargetSet,
    long Sequence, long HostGeneration, long ObservationGeneration, long MutationEpoch, int MutationDepth, bool Failed)
{
    public bool IsMutationLive => TargetSet is PortraitNativePhaseTargetSet.CounterIngress or PortraitNativePhaseTargetSet.ResourceIngress
        && State is PortraitNativePhaseHostState.Installed or PortraitNativePhaseHostState.Observing
        && HostGeneration > 0 && Sequence > 0 && MutationDepth == 0 && !Failed;
}

// The returned closure belongs exclusively to the actual Host invocation. Begin
// freezes detached state only; Cancel/publish must not enter native or Host locks.
internal interface IPortraitNativeSourceReturnSink
{
    Action<bool> Begin(PortraitNativePhaseHost.SourceReturnInvocation invocation);
    void Cancel();
}

internal sealed partial class PortraitNativePhaseHost
{
    // Only this Host can construct or open the marker. A recorder row/call or a
    // replayed sink closure cannot assert that native Original just returned.
    internal sealed class SourceReturnInvocation
    {
        private readonly PortraitNativePhaseHost owner;
        private readonly object? authority;
        private int returning;
        internal SourceReturnInvocation(PortraitNativePhaseHost owner, object authority, PortraitNativePhaseCall call, nint receiver,
            uint frame, PortraitNativePhaseHostHealth health)
        { this.owner = owner; this.authority = ReferenceEquals(authority, owner.sourceReturnAuthority) ? authority : null;
            Call = call; Receiver = receiver; Frame = frame; Health = health; }
        internal PortraitNativePhaseCall Call { get; }
        internal nint Receiver { get; }
        internal uint Frame { get; }
        internal PortraitNativePhaseHostHealth Health { get; }
        internal bool IsReturningFor(PortraitNativePhaseHost host)
            => authority is not null && ReferenceEquals(owner, host) && Volatile.Read(ref returning) == 1;
        internal void Open(object key, bool confirmed)
        { if (authority is not null && ReferenceEquals(key, authority)) Volatile.Write(ref returning, confirmed ? 1 : 2); }
        internal void Close(object key)
        { if (authority is not null && ReferenceEquals(key, authority)) Volatile.Write(ref returning, 2); }
    }

    private readonly object sourceReturnAuthority = new();
    private PortraitNativePhaseHostHealth health = new(PortraitNativePhaseHostState.Off, PortraitNativePhaseTargetSet.PhaseObservation, 0, 0, 0, 0, 0, false);
    private long healthSequence, hostGeneration, observationGeneration, mutationEpoch;
    private int healthMutationDepth;
    private bool healthFailed;
    private IPortraitNativeSourceReturnSink? sourceReturnSink;
    internal PortraitNativePhaseHostHealth Health => Volatile.Read(ref health);
    internal PortraitNativePhaseCapture? SourceCapture => observation.ActiveCapture;

    internal bool TryAttachSourceReturn(IPortraitNativeSourceReturnSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (gate)
        {
            if (targetSet != PortraitNativePhaseTargetSet.PhaseObservation || sourceReturnSink is not null
                || state is PortraitNativePhaseHostState.Retiring or PortraitNativePhaseHostState.Retired or PortraitNativePhaseHostState.Faulted) return false;
            sourceReturnSink = sink;
            PublishHealth(); return true;
        }
    }

    // Called from the private synchronous return marker, including before/after
    // every fallible copy. No Host gate or peer lock is acquired here.
    internal bool IsSourceCallCurrent(PortraitNativePhaseCall call, PortraitNativePhaseHostHealth expected)
        => ReferenceEquals(Health, expected) && expected.State == PortraitNativePhaseHostState.Observing
            && !expected.Failed && expected.HostGeneration > 0 && expected.ObservationGeneration > 0
            && observation.IsCaptureActive(call);

    private void PublishHealth()
    {
        try { healthSequence = checked(healthSequence + 1); }
        catch (OverflowException) { healthFailed = true; }
        Volatile.Write(ref health, new(state, targetSet, healthSequence, hostGeneration, observationGeneration,
            mutationEpoch, healthMutationDepth, healthFailed || counterInvalidationFailed || resourceInvalidationFailed));
    }

    private bool BeginMutationHealth()
    {
        lock (gate)
        {
            try { healthMutationDepth = checked(healthMutationDepth + 1); mutationEpoch = checked(mutationEpoch + 1); }
            catch (OverflowException) { healthFailed = true; PublishHealth(); return false; }
            PublishHealth(); return true;
        }
    }

    private void EndMutationHealth(bool began)
    {
        lock (gate)
        {
            if (began && healthMutationDepth > 0) healthMutationDepth--;
            else healthFailed = true;
            try { mutationEpoch = checked(mutationEpoch + 1); }
            catch (OverflowException) { healthFailed = true; }
            PublishHealth();
        }
    }

    private void MarkInvalidationFailure(bool resourceFailure)
    {
        lock (gate)
        {
            if (resourceFailure) resourceInvalidationFailed = true; else counterInvalidationFailed = true;
            PublishHealth();
        }
        observation.Stop();
    }
}
