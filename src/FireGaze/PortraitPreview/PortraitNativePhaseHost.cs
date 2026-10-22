namespace FireGaze.PortraitPreview;

internal enum PortraitNativePhaseHostState { Off, Observing, Installed, Retiring, Retired, Faulted }
internal sealed record PortraitNativePhaseHostSnapshot(PortraitNativePhaseHostState State, int Hooks, int InFlight,
    string BackendIdentity, string Detail, PortraitNativePhaseResolvedPlan? Plan, PortraitNativePhaseObservationSnapshot Observation,
    long CounterInvalidationEpoch = 0, bool CounterInvalidationFailed = false,
    PortraitNativeResourceState Resource = default, bool ResourceInvalidationFailed = false)
{ public bool ProductionAdmission => false; }

internal delegate bool PortraitNativePhaseHostResolve(Func<nint, bool> isReserved,
    out PortraitNativePhaseResolvedPlan? plan, out string reason);

internal interface IPortraitNativePhaseHostHook : IDisposable
{
    nint Address { get; }
    string BackendName { get; }
    bool IsEnabled { get; }
    bool IsDisposed { get; }
    Delegate Original { get; }
    void Enable();
    void Disable();
}

internal interface IPortraitNativePhaseHostFactory
{
    string Identity { get; }
    bool TryQualifyStartDisabled(out string reason);
    bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason);
    Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type delegateType);
    IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type delegateType, Delegate detour);
}

/// <summary>
/// Explicit, passive, bounded phase observation only. Construction does not read
/// native memory, install hooks, start a recorder or grant any scene admission.
/// BeginRetirement must run before its plugin's asynchronous DisposeAsync waits;
/// provider scope disposal is allowed only after WaitForRetirementAsync succeeds.
/// Only the explicit one-attempt diagnostic wires this component into the Feature.
/// Overlapping hook owners (including owned-scene Render) join the same registry. A Create
/// failure with no returned handle currently has no attested production compensation and
/// deliberately cannot complete retirement or authorize provider-scope disposal.
/// </summary>
internal sealed partial class PortraitNativePhaseHost
{
    private readonly object gate = new();
    private readonly IPortraitNativePhaseHostFactory factory;
    private readonly PortraitNativePhaseObservation observation;
    private readonly PortraitNativePhaseHostReservations reservations;
    private readonly PortraitNativePhaseHostResolve resolve;
    private readonly PortraitNativePhaseTargetSet targetSet;
    private readonly Func<IDisposable?>? invalidateCounters;
    private readonly List<Entry> entries = [];
    private PortraitNativePhaseHostState state;
    private PortraitNativePhaseResolvedPlan? plan;
    private PortraitNativePhaseHostReservation? reservation;
    private string detail = "Disabled; no native hooks installed.";
    private long counterInvalidationEpoch;
    private volatile bool counterInvalidationFailed;
    private volatile bool resourceInvalidationFailed;
    private PortraitNativeResourceState resource;

    public PortraitNativePhaseHost(IPortraitNativePhaseHostFactory factory, PortraitNativePhaseObservation observation,
        PortraitNativePhaseHostReservations reservations, PortraitNativePhaseHostResolve? resolve = null)
        : this(factory, observation, reservations, PortraitNativePhaseTargetSet.PhaseObservation, null,
            resolve ?? PortraitNativePhaseObservationResolver.TryResolveCurrentProcess) { }

    private PortraitNativePhaseHost(IPortraitNativePhaseHostFactory factory, PortraitNativePhaseObservation observation,
        PortraitNativePhaseHostReservations reservations, PortraitNativePhaseTargetSet targetSet, Func<IDisposable?>? invalidateCounters,
        PortraitNativePhaseHostResolve resolve)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.observation = observation ?? throw new ArgumentNullException(nameof(observation));
        this.reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        this.resolve = resolve;
        this.targetSet = targetSet;
        this.invalidateCounters = invalidateCounters;
        PublishHealth();
    }

    // Explicit bounded diagnostic host only; no feature creates one or grants an
    // owned scene. The sink accepts no native receiver and may only revoke/block
    // detached phases (phaseSource.BeginCounterMutation), never release a scene.
    public static PortraitNativePhaseHost CreateCounterIngress(IPortraitNativePhaseHostFactory factory,
        PortraitNativePhaseObservation observation, PortraitNativePhaseHostReservations reservations, Func<IDisposable?> invalidate,
        PortraitNativePhaseHostResolve? resolve = null)
    {
        ArgumentNullException.ThrowIfNull(invalidate);
        return new(factory, observation, reservations, PortraitNativePhaseTargetSet.CounterIngress, invalidate,
            resolve ?? PortraitNativePhaseObservationResolver.TryResolveCounterIngressCurrentProcess);
    }

    // Resolution-change ingress only. The detached sink cannot read its receiver
    // or free/restore native objects. A returned byte never qualifies resources.
    public static PortraitNativePhaseHost CreateResourceIngress(IPortraitNativePhaseHostFactory factory,
        PortraitNativePhaseObservation observation, PortraitNativePhaseHostReservations reservations, Func<IDisposable?> invalidate,
        PortraitNativePhaseHostResolve? resolve = null)
    {
        ArgumentNullException.ThrowIfNull(invalidate);
        return new(factory, observation, reservations, PortraitNativePhaseTargetSet.ResourceIngress, invalidate,
            resolve ?? PortraitNativePhaseObservationResolver.TryResolveResourceIngressCurrentProcess);
    }

    public static bool IsReserved(nint address) => new PortraitNativePhaseHostReservations().IsReserved(address);

    public bool TryStart(TimeSpan duration, out string reason)
    {
        lock (gate)
        {
            reason = "Observation is already active, retired, or unavailable.";
            if (state is not (PortraitNativePhaseHostState.Off or PortraitNativePhaseHostState.Installed)
                || duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5)) return false;
            try
            {
                if (!factory.TryQualifyStartDisabled(out reason)) { detail = reason; return false; }
                if (state == PortraitNativePhaseHostState.Off && !TryInstall(out reason)) { detail = reason; return false; }
                if (entries.Count != PortraitNativeCounterIngressContract.For(targetSet).Count
                    || entries.Any(entry => entry.Hook is null || entry.Hook.IsDisposed || !entry.Hook.IsEnabled))
                    throw new InvalidOperationException("An installed observation hook changed state.");
                // Install all four observers before starting; while stopped their
                // Begin returns no completion token and cannot fabricate records.
                AttachObservers();
                if (!observation.TryStart(duration))
                { DetachRecordingObservers(); reason = detail = "Bounded recorder did not start."; return false; }
                state = PortraitNativePhaseHostState.Observing;
                observationGeneration = checked(observationGeneration + 1); PublishHealth();
                reason = detail = "Passive phase observation started; native scene admission remains disabled."; return true;
            }
            catch (Exception error)
            {
                reason = detail = "Observation installation failed: " + error.GetType().Name;
                BeginRetirementCore(); _ = TryCompleteRetirement(out _); return false;
            }
        }
    }

    // Stop recording but retain this generation's installed pass-through hooks.
    public void Stop()
    {
        lock (gate)
        {
            sourceReturnSink?.Cancel();
            observation.Stop(); DetachRecordingObservers();
            if (state == PortraitNativePhaseHostState.Observing) state = PortraitNativePhaseHostState.Installed;
            PublishHealth();
        }
    }

    public PortraitNativePhaseHostSnapshot Snapshot()
    {
        lock (gate)
        {
            var captured = observation.Snapshot();
            if (state == PortraitNativePhaseHostState.Observing && !captured.Observing)
            { sourceReturnSink?.Cancel(); DetachRecordingObservers(); state = PortraitNativePhaseHostState.Installed; PublishHealth(); }
            return new(state, entries.Count(entry => entry.Hook is { IsDisposed: false }), entries.Sum(entry => entry.Gateway.ActiveCount),
                factory.Identity, detail, plan, captured, Interlocked.Read(ref counterInvalidationEpoch), counterInvalidationFailed,
                resource, resourceInvalidationFailed);
        }
    }

    // Framework caller first detaches input/observer, then disables. This method
    // never waits for an Original or blocks Framework on a native worker thread.
    public bool BeginRetirement(out string reason)
    {
        lock (gate)
        {
            if (state == PortraitNativePhaseHostState.Retired) { reason = detail; return true; }
            var result = BeginRetirementCore(); reason = detail; return result;
        }
    }

    public bool TryCompleteRetirement(out string reason)
    {
        lock (gate)
        {
            reason = detail;
            if (state == PortraitNativePhaseHostState.Retired) return true;
            if (state != PortraitNativePhaseHostState.Retiring) return false;
            try
            {
                var unknownCreation = false;
                foreach (var entry in entries.AsEnumerable().Reverse())
                {
                    if (entry.Released) continue;
                    if (entry.Hook is null && entry.CreateAttempted)
                    {
                        // A provider call that throws without returning a handle has no
                        // attested compensation result. Clean up other returned resources,
                        // but neither release the batch nor permit provider-scope disposal.
                        unknownCreation = true; continue;
                    }
                    if (entry.Hook is null)
                    {
                        if (!entry.Gateway.TryAbandonUnpublishedOwnership()) return false;
                        entry.Released = true; continue;
                    }
                    if (entry.Hook is { IsEnabled: true }) { reason = detail = "A native hook has not disabled; provider scope must remain alive."; return false; }
                    if (!entry.Gateway.TryRetireTo(entry.Direct!))
                    { reason = detail = "Waiting asynchronously for native originals; provider scope must remain alive."; return false; }
                    // New/late native entries already read direct target. There
                    // are no copied trampoline delegates left when this frees it.
                    entry.Hook?.Dispose();
                    if (entry.Hook is { IsDisposed: false }) throw new InvalidOperationException("Official hook did not dispose.");
                    entry.Gateway.ReleaseOwnership(); entry.Released = true;
                }
                if (unknownCreation)
                {
                    state = PortraitNativePhaseHostState.Faulted;
                    PublishHealth();
                    reason = detail = "Native Create did not return a handle; compensation is unproven and the complete address reservation must remain held.";
                    return false;
                }
                if (!ReleaseAddresses(out reason)) throw new InvalidOperationException(reason);
                state = PortraitNativePhaseHostState.Retired;
                PublishHealth();
                reason = detail = "Native hooks retired; stable gateways contain no plugin observers. Provider scope may dispose."; return true;
            }
            catch (Exception error)
            {
                reason = detail = "Native retirement failed: " + error.GetType().Name + "; provider scope must remain alive.";
                state = PortraitNativePhaseHostState.Faulted; PublishHealth(); return false;
            }
        }
    }

    public async Task<bool> WaitForRetirementAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) return false;
        var until = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryCompleteRetirement(out _)) return true;
            lock (gate) { if (state != PortraitNativePhaseHostState.Retiring) return false; }
            if (System.Diagnostics.Stopwatch.GetTimestamp() >= until) return false;
            try { await Task.Delay(5, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private bool TryInstall(out string reason)
    {
        if (!resolve(reservations.IsReserved, out var resolved, out reason) || !ValidPlan(resolved)) return false;
        // Resolver queries are early rejection only. One shared atomic batch acquisition
        // closes resolution/Create races across participating users and collectible ALCs.
        if (!reservations.TryAcquireBatch(resolved!.Targets.Select(target => target.Address).ToArray(), out reservation, out reason)) return false;
        plan = resolved;
        // Recheck actual entry bytes under the same reservation transaction,
        // before any gateway/native Create; recheck again before any Enable.
        if (!factory.TryVerifyUnmodifiedTargets(plan, out reason))
        {
            if (!ReleaseAddresses(out var releaseReason)) throw new InvalidOperationException(releaseReason);
            plan = null; return false;
        }
        try
        {
            foreach (var target in plan.Targets)
            {
                var signature = target.DelegateType.GetMethod("Invoke")!.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
                PortraitNativePhaseHostGateway? gateway;
                var created = targetSet == PortraitNativePhaseTargetSet.ResourceIngress
                    ? PortraitNativePhaseHostGateway.TryCreateResource((int)target.Phase, signature, target.DelegateType.GetMethod("Invoke")!.ReturnType, target.Address, out gateway, out reason)
                    : PortraitNativePhaseHostGateway.TryCreate((int)target.Phase, signature, target.Address, out gateway, out reason);
                if (!created)
                    throw new InvalidOperationException(reason);
                var entry = new Entry(target, gateway!); entries.Add(entry);
                entry.Direct = factory.DirectTarget(target, gateway!.DelegateType);
            }
            foreach (var entry in entries)
            {
                entry.CreateAttempted = true;
                entry.Hook = factory.Create(entry.Target, entry.Gateway.DelegateType, entry.Gateway.Detour);
                if (entry.Hook.BackendName != "SafetyHook" || entry.Hook.IsEnabled || entry.Hook.IsDisposed || entry.Hook.Address != entry.Target.Address)
                    throw new InvalidOperationException("Official hook backend/address/start-disabled state differs from qualification.");
                entry.Gateway.PublishOriginal(entry.Hook.Original);
            }
            if (!factory.TryVerifyUnmodifiedTargets(plan, out reason)) throw new InvalidOperationException(reason);
            // Revocation must already be attached when the first counter hook
            // enables. Recording starts separately; no gap grants a valid lease.
            if (targetSet != PortraitNativePhaseTargetSet.PhaseObservation) AttachObservers();
            foreach (var entry in entries) { entry.Hook!.Enable(); if (!entry.Hook.IsEnabled) throw new InvalidOperationException("Official hook did not enable."); }
            state = PortraitNativePhaseHostState.Installed; hostGeneration = checked(hostGeneration + 1); PublishHealth();
            reason = "Official typed observation batch installed."; return true;
        }
        catch
        {
            BeginRetirementCore(); _ = TryCompleteRetirement(out _); throw;
        }
    }

    private bool BeginRetirementCore()
    {
        if (state == PortraitNativePhaseHostState.Retired) return true;
        state = PortraitNativePhaseHostState.Retiring;
        sourceReturnSink?.Cancel(); PublishHealth();
        observation.Stop(); foreach (var entry in entries) entry.Gateway.DetachObserver();
        var allDisabled = true;
        foreach (var entry in entries.AsEnumerable().Reverse())
        {
            if (entry.Hook is null || entry.Hook.IsDisposed) continue;
            try { entry.Hook.Disable(); if (entry.Hook.IsEnabled) allDisabled = false; }
            catch (Exception) { allDisabled = false; }
        }
        detail = allDisabled ? "Observation detached; waiting for asynchronous native retirement."
            : "A native hook could not disable; provider scope must remain alive.";
        if (!allDisabled) state = PortraitNativePhaseHostState.Faulted;
        PublishHealth();
        return allDisabled;
    }

    private void DetachRecordingObservers()
    {
        // Counter invalidation remains active while the installed generation is
        // alive, independently of the bounded recorder. Retirement always detaches.
        if (targetSet == PortraitNativePhaseTargetSet.PhaseObservation)
            foreach (var entry in entries) entry.Gateway.DetachObserver();
    }

    private void AttachObservers()
    {
        foreach (var entry in entries)
            if (targetSet == PortraitNativePhaseTargetSet.ResourceIngress) entry.Gateway.SetResourceObserver(ObserveResource);
            else entry.Gateway.SetObserver(Observe);
    }

    private Action<bool, byte> ObserveResource(int phase, nint receiver)
    {
        IDisposable? mutation = null;
        var healthBegan = BeginMutationHealth();
        var call = default(PortraitNativePhaseCall);
        lock (gate)
        {
            try { resource = new(checked(resource.Generation + 1), checked(resource.Depth + 1), PortraitNativeResourceTransition.Transitioning, receiver, null); }
            catch (Exception) { resourceInvalidationFailed = true; PublishHealth(); }
        }
        try
        {
            // Scope is obtained independently of the bounded recorder, and its
            // completion spans Original including worker/reentrant native calls.
            try { mutation = invalidateCounters!() ?? throw new InvalidOperationException("Resource mutation guard was not issued."); }
            catch (Exception) { MarkInvalidationFailure(true); }
            call = observation.Begin((PortraitNativeObservedPhase)phase, receiver);
        }
        catch (Exception) { observation.Stop(); }
        return (returned, value) => {
            try { mutation?.Dispose(); }
            catch (Exception) { MarkInvalidationFailure(true); }
            finally
            {
                lock (gate)
                {
                    var depth = Math.Max(0, resource.Depth - 1);
                    resource = resource with { Depth = depth, Receiver = receiver,
                        Transition = depth > 0 ? PortraitNativeResourceTransition.Transitioning : !returned ? PortraitNativeResourceTransition.OriginalThrew
                            : phase == (int)PortraitNativeObservedPhase.ResourceCleanup ? PortraitNativeResourceTransition.CleanupReturned : PortraitNativeResourceTransition.RecreateReturned,
                        NativeReturn = returned && depth == 0 && phase == (int)PortraitNativeObservedPhase.ResourceRecreate ? value : null };
                }
                EndMutationHealth(healthBegan);
                observation.End(call, returned, phase == (int)PortraitNativeObservedPhase.ResourceRecreate ? value : null);
            }
        };
    }

    private Action<bool>? Observe(int phase, nint receiver, uint argument, float delta)
    {
        IDisposable? mutation = null;
        var call = default(PortraitNativePhaseCall);
        var counter = phase is (int)PortraitNativeObservedPhase.CounterAdd or (int)PortraitNativeObservedPhase.CounterSub;
        var guardedCounter = counter && argument < 8;
        var healthBegan = false;
        Action<bool>? sourceCompletion = null;
        SourceReturnInvocation? sourceInvocation = null;
        try
        {
            if (guardedCounter)
            {
                healthBegan = BeginMutationHealth();
                // Unknown manager/off-thread calls are equally conservative: this
                // path never reads the receiver. An epoch change is detached data,
                // not permission to read a slot or run native cleanup here.
                if (Interlocked.Increment(ref counterInvalidationEpoch) <= 0) MarkInvalidationFailure(false);
                try { mutation = invalidateCounters!() ?? throw new InvalidOperationException("Counter mutation guard was not issued."); }
                catch (Exception) { MarkInvalidationFailure(false); }
            }
            call = observation.Begin((PortraitNativeObservedPhase)phase, receiver,
                phase == (int)PortraitNativeObservedPhase.SourceCharaViewRender ? argument : null,
                phase == (int)PortraitNativeObservedPhase.ManagerUpdateCandidate ? delta : null,
                counter ? argument : null);
            if (phase == (int)PortraitNativeObservedPhase.SourceCharaViewRender && call.Call > 0)
            {
                var sink = Volatile.Read(ref sourceReturnSink);
                if (sink is not null)
                {
                    sourceInvocation = new(this, sourceReturnAuthority, call, receiver, argument, Health);
                    sourceCompletion = sink.Begin(sourceInvocation);
                }
            }
        }
        catch (Exception) { observation.Stop(); }
        // The stable gateway invokes completion from finally even when Original
        // throws. Logical recording expiry cannot shorten this mutation scope.
        return call.Call == 0 && mutation is null && !guardedCounter && sourceCompletion is null ? null : returned => {
            try { mutation?.Dispose(); }
            catch (Exception) { MarkInvalidationFailure(false); }
            finally
            {
                if (guardedCounter) EndMutationHealth(healthBegan);
                var confirmed = observation.TryEnd(call, returned);
                sourceInvocation?.Open(sourceReturnAuthority, confirmed);
                try { sourceCompletion?.Invoke(confirmed); }
                finally { sourceInvocation?.Close(sourceReturnAuthority); }
            }
        };
    }

    private bool ValidPlan(PortraitNativePhaseResolvedPlan? value)
    {
        var inventory = PortraitNativeCounterIngressContract.For(targetSet);
        if (value is null || value.ModuleBase <= 0 || value.ModuleSize <= 0 || value.SDKMVID == Guid.Empty
            || value.OnDiskEXESHA256 != PortraitNativePhaseObservationBinding.EXE || value.OnDiskSDKSHA256 != PortraitNativePhaseObservationBinding.SDK
            || inventory.Count == 0 || value.Targets.Count != inventory.Count) return false;
        return value.Targets.SequenceEqual(inventory.Select(contract =>
            new PortraitNativePhaseBoundTarget(contract.Phase, value.ModuleBase + (nint)contract.RVA, contract.Delegate)))
            && value.Targets.Select(target => target.Address).Distinct().Count() == value.Targets.Count;
    }

    private bool ReleaseAddresses(out string reason)
    {
        reason = "No address reservation was acquired.";
        if (reservation is null) return true;
        if (!reservation.TryRelease(out reason)) return false;
        reservation = null; return true;
    }

    private sealed class Entry(PortraitNativePhaseBoundTarget target, PortraitNativePhaseHostGateway gateway)
    {
        public PortraitNativePhaseBoundTarget Target { get; } = target;
        public PortraitNativePhaseHostGateway Gateway { get; } = gateway;
        public Delegate? Direct { get; set; }
        public IPortraitNativePhaseHostHook? Hook { get; set; }
        public bool CreateAttempted { get; set; }
        public bool Released { get; set; }
    }
}
