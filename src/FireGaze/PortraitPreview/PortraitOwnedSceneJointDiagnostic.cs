namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedSceneJointDiagnosticState { Off, Observing, Retiring, Faulted }
internal sealed record PortraitOwnedSceneJointDiagnosticBindings(Func<IDisposable?> Counters, Func<IDisposable?> Resources,
    Func<bool> RequestCurrent, Action<PortraitOwnedSceneJointMutationNotice> MutationReceiver,
    Action<PortraitOwnedSceneHooks, PortraitOwnedSceneHooks.JointSourceInvocation> SourceReturn);
internal sealed record PortraitOwnedSceneJointDiagnosticSnapshot(PortraitOwnedSceneJointDiagnosticState State, long Attempt,
    string Reason, PortraitSourceCensusContext? Context, PortraitNativeThreadStamp ExpectedThread,
    PortraitOwnedSceneJointHostSnapshot? Host, PortraitOwnedSceneJointSlotObservation? Census)
{ public bool ProductionAdmission => false; }

// Explicit Framework command owner for one bounded same-installation read-only
// diagnostic. RequestCurrent is detached; native callbacks never resolve Feature
// source/player state or call SDK methods to refresh the request.
internal sealed class PortraitOwnedSceneJointDiagnostic
{
    private readonly Func<bool> framework;
    private readonly Func<PortraitSourceCensusContext?> context;
    private readonly Func<PortraitOwnedSceneJointDiagnosticBindings, PortraitOwnedSceneJointHost> createHost;
    private readonly Func<PortraitOwnedSceneJointPlan?> createPlan;
    private readonly IPortraitOwnedSceneSlotNativeApi? native;
    private readonly Func<PortraitNativeThreadStamp> thread;
    private Run? run;
    private PortraitConsumerObservationSnapshot? lastConsumerObservation;
    private long attempts;
    internal PortraitOwnedSceneJointDiagnostic(Func<bool> framework, Func<PortraitSourceCensusContext?> context,
        Func<PortraitOwnedSceneJointDiagnosticBindings, PortraitOwnedSceneJointHost> createHost,
        Func<PortraitOwnedSceneJointPlan?> createPlan, IPortraitOwnedSceneSlotNativeApi? native = null,
        Func<PortraitNativeThreadStamp>? thread = null)
    {
        this.framework = framework; this.context = context; this.createHost = createHost; this.createPlan = createPlan;
        this.native = native; this.thread = thread ?? PortraitSourceCensusDiagnostic.CurrentThread;
    }
    internal bool BlocksOperations => Volatile.Read(ref run) is { } value && !value.Retired.Task.IsCompletedSuccessfully;
    internal Task DrainPhysicalAsync() => Volatile.Read(ref run)?.Retired.Task ?? Task.CompletedTask;
    internal PortraitConsumerObservationSnapshot? ConsumerObservation => Volatile.Read(ref lastConsumerObservation);
    internal PortraitOwnedSceneJointDiagnosticSnapshot Snapshot
    {
        get
        {
            var value = Volatile.Read(ref run);
            if (value is null) return new(PortraitOwnedSceneJointDiagnosticState.Off, 0, "Disabled; no joint diagnostic started.", null, default, null, null);
            var state = value.Retired.Task.IsCompletedSuccessfully ? PortraitOwnedSceneJointDiagnosticState.Off
                : value.Faulted ? PortraitOwnedSceneJointDiagnosticState.Faulted
                : Volatile.Read(ref value.Stopping) != 0 ? PortraitOwnedSceneJointDiagnosticState.Retiring : PortraitOwnedSceneJointDiagnosticState.Observing;
            return new(state, value.Attempt, value.Reason, value.Context, value.Thread, value.Host?.Snapshot(), value.Census.Result);
        }
    }
    internal bool TryStart(out string reason)
    {
        reason = "A ready Framework source and fully drained diagnostic owners are required.";
        if (!framework() || BlocksOperations) return false;
        try
        {
            // A replacement request must never inherit a previous detached
            // observation, even when the previous run retired cleanly.
            Volatile.Write(ref lastConsumerObservation, null);
            var fresh = context(); var expected = thread(); var plan = createPlan();
            if (fresh is not { IsValid: true } || expected.NativeID == 0 || expected.ManagedID <= 0 || plan is null) return false;
            var manifest = plan.Targets.SelectMany(target => target.Roles
                .Where(role => role.Consumer.HasValue)
                .Select(role => new PortraitOwnedSceneNativeTarget(role.Consumer!.Value, target.Address)))
                .ToArray();
            var value = new Run(checked(++attempts), fresh, expected, manifest);
            value.Census = new(fresh.Source, fresh.SessionEpoch, value.Attempt, () => Current(value), native);
            Volatile.Write(ref run, value);
            value.Host = createHost(new(value.Census.BeginCounterMutation, value.Census.BeginResourceMutation,
                () => Current(value), notice => { if (notice.Kind == PortraitOwnedSceneJointMutationKind.Revoked) value.Census.Cancel(); },
                (owner, invocation) => { if (value.Host is { } host) value.Census.Receive(host, owner, invocation); }));
            if (!Current(value))
            { reason = "The detached request was revoked before native installation."; Stop(reason); return false; }
            if (!value.Host.TryStart(plan, fresh.Source, fresh.SessionEpoch, value.Attempt, expected, TimeSpan.FromSeconds(2), out reason))
            { value.Faulted = true; Stop(reason); return false; }
            if (context() != fresh || thread() != expected || !framework())
            { Stop("The Framework source changed during joint installation."); reason = value.Reason; return false; }
            value.Reason = "Observing one actual source Render return and eight slots in the same physical installation; production admission is disabled.";
            reason = value.Reason; return true;
        }
        catch (Exception error)
        { reason = "Joint diagnostic failed: " + error.GetType().Name; Stop(reason); return false; }
    }
    private bool Current(Run value) => ReferenceEquals(Volatile.Read(ref run), value) && Volatile.Read(ref value.Live) == 1;
    internal void Update()
    {
        var value = Volatile.Read(ref run);
        if (value is null || Volatile.Read(ref value.Stopping) != 0) return;
        try
        {
            if (!framework() || context() != value.Context || thread() != value.Thread)
            { Stop("The Framework source, owner, session or thread changed."); return; }
            var host = value.Host?.Snapshot();
            if (host is not { Recording: true })
            { Stop("The bounded joint observation expired or lost qualification."); return; }
            if (value.Census.Result is { } census)
            {
                if (ConsumerObservation is null && host is { } && host.Installation != Guid.Empty)
                {
                    var expected = new PortraitConsumerObservationContext(host.Installation, value.Context.SessionEpoch,
                        host.RequestEpoch, value.Context.Source);
                    var consumers = value.Host!.ObserveConsumers(expected, census, value.Manifest);
                    // The census is published inside the source Return callback;
                    // the enclosing passive Render event is appended only after it
                    // unwinds. Keep this bounded request alive for a later normal
                    // refresh instead of stopping the collector before its append.
                    // Snapshot enforces the existing host deadline on every retry.
                    if (consumers.Issues.Contains(PortraitConsumerObservationIssue.SourceRenderNotAppended))
                        return;
                    Volatile.Write(ref lastConsumerObservation, consumers);
                }
                Stop("One complete historical eight-slot observation was captured in the joint installation.",
                    preserveConsumerObservation: true);
                return;
            }
        }
        catch (Exception error) { Stop("Joint diagnostic update failed: " + error.GetType().Name); }
    }
    // Lifecycle revocation is managed-only; Framework Stop/Update owns Hook APIs.
    internal void Revoke(string reason)
    {
        var value = Volatile.Read(ref run);
        if (value is null) return;
        Volatile.Write(ref lastConsumerObservation, null);
        Volatile.Write(ref value.Live, 0); value.Reason = reason; value.Census.Cancel(); value.Host?.RevokeEvidence();
    }
    internal void Stop(string reason, bool preserveConsumerObservation = false)
    {
        var value = Volatile.Read(ref run);
        if (value is null) return;
        if (!preserveConsumerObservation) Volatile.Write(ref lastConsumerObservation, null);
        Volatile.Write(ref value.Live, 0); value.Reason = reason; value.Census.Cancel(); value.Host?.RevokeEvidence();
        if (Interlocked.Exchange(ref value.Stopping, 1) != 0) return;
        value.Host?.TryStop(); value.Retirement = RetireAsync(value);
    }
    private static async Task RetireAsync(Run value)
    {
        try { if (value.Host is { } host) await host.DrainPhysicalAsync().ConfigureAwait(false); value.Retired.TrySetResult(); }
        finally { GC.KeepAlive(value); }
    }
    private sealed class Run(long attempt, PortraitSourceCensusContext context, PortraitNativeThreadStamp thread,
        IReadOnlyList<PortraitOwnedSceneNativeTarget> manifest)
    {
        internal readonly long Attempt = attempt;
        internal readonly PortraitSourceCensusContext Context = context;
        internal readonly PortraitNativeThreadStamp Thread = thread;
        internal readonly IReadOnlyList<PortraitOwnedSceneNativeTarget> Manifest = manifest;
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PortraitOwnedSceneJointSourceCensus Census = null!;
        internal PortraitOwnedSceneJointHost? Host;
        internal int Live = 1, Stopping;
        internal bool Faulted;
        internal string Reason = "Installing a bounded joint diagnostic.";
        internal Task? Retirement;
    }
}
