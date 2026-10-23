using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitOwnedSceneJointHostSnapshot(Guid Installation, long SessionEpoch, long RequestEpoch,
    long MutationEpoch, int CounterDepth, int ResourceDepth, bool Recording, bool Failed, PortraitNativeResourceState Resource,
    IReadOnlyList<PortraitOwnedSceneJointSourceReturn> SourceReturns, PortraitNativePhaseObservationSnapshot Observation,
    int Hooks, int InFlight, bool ReservationHeld, bool PhysicalRetired)
{ public bool ProductionAdmission => false; }

/// <summary>
/// Explicit one-attempt <=2s joint diagnostic. Its only native work is the same
/// official 39 consumer forwarding plus seven audited phase gateways. No scene
/// request API is exposed; snapshot evidence is distinct from the old census.
/// </summary>
internal sealed class PortraitOwnedSceneJointHost : IDisposable
{
    private readonly PortraitOwnedSceneHooks hooks;
    private readonly IPortraitOwnedSceneJointHookFactory factory;
    private readonly Func<IDisposable?> counters, resources;
    private readonly Func<bool> requestCurrent;
    private readonly Func<long> timestamp;
    private readonly Func<PortraitNativeThreadStamp> nativeThread;
    private readonly long frequency;
    private readonly Action<PortraitOwnedSceneJointMutationNotice>? mutationReceiver;
    private readonly Action<PortraitOwnedSceneHooks, PortraitOwnedSceneHooks.JointSourceInvocation>? sourceReceiver;
    private PortraitOwnedSceneJointIngress? ingress;
    private int attempted;
    internal PortraitOwnedSceneJointHost(IPortraitOwnedSceneHookEnvironment environment, IPortraitOwnedSceneJointHookFactory factory,
        PortraitNativePhaseHostReservations reservations, Func<IDisposable?> counters, Func<IDisposable?> resources,
        Func<bool> requestCurrent, Func<long>? timestamp = null, long timestampFrequency = 0,
        Func<PortraitNativeThreadStamp>? nativeThread = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<PortraitOwnedSceneJointMutationNotice>? mutationReceiver = null,
        Action<PortraitOwnedSceneHooks, PortraitOwnedSceneHooks.JointSourceInvocation>? sourceReceiver = null,
        Action<string>? sourceRevoked = null)
    {
        this.factory = factory; this.counters = counters; this.resources = resources; this.requestCurrent = requestCurrent;
        this.mutationReceiver = mutationReceiver;
        this.sourceReceiver = sourceReceiver;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        frequency = timestampFrequency == 0 ? Stopwatch.Frequency : timestampFrequency;
        if (frequency <= 0 || frequency > long.MaxValue / 300) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        this.nativeThread = nativeThread ?? (() => new(GetCurrentThreadID(), Environment.CurrentManagedThreadId));
        hooks = new(environment, reservations, this.timestamp, frequency, this.nativeThread, delay, sourceRevoked);
    }
    internal bool TryStart(PortraitOwnedSceneJointPlan plan, PortraitIdentity source, long sessionEpoch, long requestEpoch,
        PortraitNativeThreadStamp expectedThread, TimeSpan duration, out string reason)
    {
        reason = "Joint diagnostic requires one current detached source/session/request and a <=2 second duration.";
        if (!source.IsValid || source.Generation == 0 || sessionEpoch <= 0 || requestEpoch <= 0
            || expectedThread.NativeID == 0 || expectedThread.ManagedID <= 0 || duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(2)
            || Interlocked.CompareExchange(ref attempted, 1, 0) != 0) return false;
        ingress = new(source, sessionEpoch, requestEpoch, expectedThread, requestCurrent, counters, resources,
            timestamp, frequency, nativeThread, mutationReceiver, sourceReceiver);
        var accepted = hooks.TryInstallJointPassive(factory, plan, ingress, duration);
        reason = hooks.LastError ?? "One physical joint diagnostic installed; no native scene admission.";
        return accepted;
    }
    internal bool TryStartActive(PortraitOwnedSceneJointPlan plan, PortraitIdentity source, long sessionEpoch, long requestEpoch,
        PortraitNativeThreadStamp expectedThread, TimeSpan duration, out string reason)
    {
        reason = "Active joint transport requires one bounded current native request.";
        if (!source.IsValid || source.Generation == 0 || sessionEpoch <= 0 || requestEpoch <= 0
            || expectedThread.NativeID == 0 || expectedThread.ManagedID <= 0 || duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(2)
            || Interlocked.CompareExchange(ref attempted, 1, 0) != 0) return false;
        ingress = new(source, sessionEpoch, requestEpoch, expectedThread, requestCurrent, counters, resources,
            timestamp, frequency, nativeThread, mutationReceiver, sourceReceiver);
        var accepted = hooks.TryInstallJointActive(factory, plan, ingress, duration);
        reason = hooks.LastError ?? reason;
        return accepted;
    }
    internal bool TryRequest(PortraitIdentity source, uint slot, Action<IPortraitOwnedSceneLease> advance,
        Func<IPortraitOwnedSceneLease, bool> retire) => hooks.TryRequest(source, slot, advance, retire);
    internal bool IsActiveDispatchCurrent => hooks.IsActiveJointDispatchCurrent;
    internal bool IsActiveAdvanceAdmitted => hooks.IsActiveAdvanceAdmitted;
    internal bool NativeReleaseConfirmed => hooks.NativeReleaseConfirmed;
    internal void RequestStop() => hooks.RequestStop();
    internal bool TryCancelUnstartedRequest() => hooks.TryCancelUnstartedRequest();
    internal PortraitOwnedSceneJointHostSnapshot Snapshot()
    {
        var value = ingress?.Snapshot() ?? new(Guid.Empty, 0, 0, 0, 0, 0, false, false, default, [],
            new(false, PortraitNativeObservationStop.Disabled, 0, 0, 0, []));
        return new(value.Installation, value.SessionEpoch, value.RequestEpoch, value.MutationEpoch, value.CounterDepth,
            value.ResourceDepth, value.Recording, value.Failed, value.Resource, value.SourceReturns, value.Observation,
            hooks.PhysicalHookCount, hooks.PhysicalInFlight, hooks.ReservationHeld, hooks.DrainPhysicalAsync().IsCompletedSuccessfully);
    }

    /// <summary>
    /// Reads the detached consumer evidence after the passive callback has unwound.
    /// This method performs no native work and never grants ownership, a lease, or
    /// resource readiness. The caller must provide the request-owned census and
    /// immutable target manifest; neither is reconstructed from the last event.
    /// </summary>
    internal PortraitConsumerObservationSnapshot ObserveConsumers(
        PortraitConsumerObservationContext expected,
        PortraitOwnedSceneJointSlotObservation? slots,
        IReadOnlyList<PortraitOwnedSceneNativeTarget> consumerManifest)
    {
        var before = Snapshot();
        var nativeEvents = hooks.PassiveNativeEvents;
        var after = Snapshot();
        return PortraitConsumerObservation.Collect(new(expected, before, after, slots, nativeEvents, consumerManifest));
    }
    internal bool TryStop() => hooks.TryDispose();
    internal void RevokeEvidence() => ingress?.Revoke();
    internal bool IsSourceReturnCurrent(PortraitOwnedSceneHooks owner, PortraitOwnedSceneHooks.JointSourceInvocation invocation)
        => ingress?.IsSourceReturnCurrent(owner, invocation) == true;
    internal bool TryPublishCensus(PortraitOwnedSceneHooks owner, PortraitOwnedSceneHooks.JointSourceInvocation invocation,
        Func<bool> publishDetached) => ingress?.TryPublishCensus(owner, invocation, publishDetached) == true;
    internal Task DrainPhysicalAsync() => hooks.DrainPhysicalAsync();
    public void Dispose() => TryStop();
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)] private static extern uint GetCurrentThreadID();
}
