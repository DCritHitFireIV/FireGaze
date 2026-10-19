namespace FireGaze.PortraitPreview;

internal enum PortraitNativePhaseHostState { Off, Observing, Installed, Retiring, Retired, Faulted }
internal sealed record PortraitNativePhaseHostSnapshot(PortraitNativePhaseHostState State, int Hooks, int InFlight,
    string BackendIdentity, string Detail, PortraitNativePhaseResolvedPlan? Plan, PortraitNativePhaseObservationSnapshot Observation)
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
/// This component has no production feature caller. Overlapping hook owners (including
/// owned-scene Render) must join the same registry before that wiring is allowed. A Create
/// failure with no returned handle currently has no attested production compensation and
/// deliberately cannot complete retirement or authorize provider-scope disposal.
/// </summary>
internal sealed class PortraitNativePhaseHost
{
    private readonly object gate = new();
    private readonly IPortraitNativePhaseHostFactory factory;
    private readonly PortraitNativePhaseObservation observation;
    private readonly PortraitNativePhaseHostReservations reservations;
    private readonly PortraitNativePhaseHostResolve resolve;
    private readonly List<Entry> entries = [];
    private PortraitNativePhaseHostState state;
    private PortraitNativePhaseResolvedPlan? plan;
    private PortraitNativePhaseHostReservation? reservation;
    private string detail = "Disabled; no native hooks installed.";

    public PortraitNativePhaseHost(IPortraitNativePhaseHostFactory factory, PortraitNativePhaseObservation observation,
        PortraitNativePhaseHostReservations reservations, PortraitNativePhaseHostResolve? resolve = null)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.observation = observation ?? throw new ArgumentNullException(nameof(observation));
        this.reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        this.resolve = resolve ?? PortraitNativePhaseObservationResolver.TryResolveCurrentProcess;
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
                if (entries.Count != 4 || entries.Any(entry => entry.Hook is null || entry.Hook.IsDisposed || !entry.Hook.IsEnabled))
                    throw new InvalidOperationException("An installed observation hook changed state.");
                // Install all four observers before starting; while stopped their
                // Begin returns no completion token and cannot fabricate records.
                foreach (var entry in entries) entry.Gateway.SetObserver(Observe);
                if (!observation.TryStart(duration))
                { foreach (var entry in entries) entry.Gateway.DetachObserver(); reason = detail = "Bounded recorder did not start."; return false; }
                state = PortraitNativePhaseHostState.Observing;
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
            observation.Stop(); foreach (var entry in entries) entry.Gateway.DetachObserver();
            if (state == PortraitNativePhaseHostState.Observing) state = PortraitNativePhaseHostState.Installed;
        }
    }

    public PortraitNativePhaseHostSnapshot Snapshot()
    {
        lock (gate)
        {
            var captured = observation.Snapshot();
            if (state == PortraitNativePhaseHostState.Observing && !captured.Observing)
            { foreach (var entry in entries) entry.Gateway.DetachObserver(); state = PortraitNativePhaseHostState.Installed; }
            return new(state, entries.Count(entry => entry.Hook is { IsDisposed: false }), entries.Sum(entry => entry.Gateway.ActiveCount),
                factory.Identity, detail, plan, captured);
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
                    reason = detail = "Native Create did not return a handle; compensation is unproven and the complete address reservation must remain held.";
                    return false;
                }
                if (!ReleaseAddresses(out reason)) throw new InvalidOperationException(reason);
                state = PortraitNativePhaseHostState.Retired;
                reason = detail = "Native hooks retired; stable gateways contain no plugin observers. Provider scope may dispose."; return true;
            }
            catch (Exception error)
            {
                reason = detail = "Native retirement failed: " + error.GetType().Name + "; provider scope must remain alive.";
                state = PortraitNativePhaseHostState.Faulted; return false;
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
                if (!PortraitNativePhaseHostGateway.TryCreate((int)target.Phase, signature, target.Address, out var gateway, out reason))
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
            foreach (var entry in entries) { entry.Hook!.Enable(); if (!entry.Hook.IsEnabled) throw new InvalidOperationException("Official hook did not enable."); }
            state = PortraitNativePhaseHostState.Installed; reason = "Four official typed observation hooks installed."; return true;
        }
        catch
        {
            BeginRetirementCore(); _ = TryCompleteRetirement(out _); throw;
        }
    }

    private bool BeginRetirementCore()
    {
        observation.Stop(); foreach (var entry in entries) entry.Gateway.DetachObserver();
        if (state == PortraitNativePhaseHostState.Retired) return true;
        state = PortraitNativePhaseHostState.Retiring;
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
        return allDisabled;
    }

    private Action<bool>? Observe(int phase, nint receiver, uint frame, float delta)
    {
        try
        {
            var call = observation.Begin((PortraitNativeObservedPhase)phase, receiver,
                phase == (int)PortraitNativeObservedPhase.SourceCharaViewRender ? frame : null,
                phase == (int)PortraitNativeObservedPhase.ManagerUpdateCandidate ? delta : null);
            return call.Call == 0 ? null : returned => observation.End(call, returned);
        }
        catch (Exception) { observation.Stop(); return null; }
    }

    private static bool ValidPlan(PortraitNativePhaseResolvedPlan? value)
    {
        if (value is null || value.ModuleBase <= 0 || value.ModuleSize <= 0 || value.SDKMVID == Guid.Empty
            || value.OnDiskEXESHA256 != PortraitNativePhaseObservationBinding.EXE || value.OnDiskSDKSHA256 != PortraitNativePhaseObservationBinding.SDK
            || value.Targets.Count != PortraitNativePhaseObservationBinding.Inventory.Count) return false;
        return value.Targets.SequenceEqual(PortraitNativePhaseObservationBinding.Inventory.Select(contract =>
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
