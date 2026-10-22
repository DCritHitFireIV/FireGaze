using System.Diagnostics;

namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedScenePassiveDiagnosticState { Off, WaitingForCensusDrain, Installing, WaitingForSourceReturn, Retiring, Faulted }
internal sealed record PortraitOwnedScenePassiveDiagnosticSnapshot(PortraitOwnedScenePassiveDiagnosticState State, long Attempt,
    long CensusAttempt, string Reason, PortraitSourceCensusContext? Context, PortraitNativeThreadStamp Thread,
    PortraitOwnedScenePassiveBaseline? SourceReturn)
{
    public bool ProductionAdmission => false;
}

/// <summary>Bounded forwarding-only handoff; old census results never authorize slots or native reads.</summary>
internal sealed class PortraitOwnedScenePassiveDiagnostic
{
    private readonly Func<bool> framework;
    private readonly Func<PortraitSourceCensusContext?> context;
    private readonly PortraitSourceCensusDiagnostic census;
    private readonly Func<PortraitOwnedSceneHooks> createHooks;
    private readonly IPortraitOwnedSceneHookFactory factory;
    private readonly Func<PortraitNativeThreadStamp> thread;
    private readonly Func<long> timestamp;
    private readonly long frequency;
    private Run? run;
    private long attempts;
    private PortraitOwnedScenePassiveDiagnosticSnapshot snapshot = new(PortraitOwnedScenePassiveDiagnosticState.Off, 0, 0,
        "Disabled; no owned native forwarding has started.", null, default, null);

    internal PortraitOwnedScenePassiveDiagnostic(Func<bool> framework, Func<PortraitSourceCensusContext?> context,
        PortraitSourceCensusDiagnostic census, Func<PortraitOwnedSceneHooks> createHooks, IPortraitOwnedSceneHookFactory factory,
        Func<PortraitNativeThreadStamp>? thread = null, Func<long>? timestamp = null, long timestampFrequency = 0)
    {
        this.framework = framework; this.context = context; this.census = census; this.createHooks = createHooks; this.factory = factory;
        this.thread = thread ?? PortraitSourceCensusDiagnostic.CurrentThread; this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        frequency = timestampFrequency == 0 ? Stopwatch.Frequency : timestampFrequency;
        if (frequency <= 0 || frequency > long.MaxValue / 300) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
    }
    internal PortraitOwnedScenePassiveDiagnosticSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal PortraitOwnedScenePassiveContextEvents? ContextEvents => Volatile.Read(ref run)?.Hooks?.PassiveContextEvents;
    internal PortraitOwnedScenePassiveNativeEvents? NativeEvents => Volatile.Read(ref run)?.Hooks?.PassiveNativeEvents;
    internal bool BlocksOperations => Volatile.Read(ref run) is { } current && !current.Retired.Task.IsCompletedSuccessfully;
    internal Task DrainPhysicalAsync() => Volatile.Read(ref run) is { } current ? RetainUntilRetiredAsync(current) : Task.CompletedTask;
    private static async Task RetainUntilRetiredAsync(Run current)
    {
        try { await current.Retired.Task.ConfigureAwait(false); }
        finally { GC.KeepAlive(current); }
    }
    internal PortraitOwnedScenePassiveSafetyState ReadSafetyState()
    {
        var current = Volatile.Read(ref run);
        if (current is null) return new(true, "Off", 0, 0, false, true, 0, 0, false, false);
        var state = Snapshot; var hooks = current.Hooks;
        return new(true, state.State.ToString(), current.Attempt, current.CensusAttempt, !current.Retired.Task.IsCompletedSuccessfully,
            current.Retired.Task.IsCompletedSuccessfully, hooks?.PhysicalHookCount ?? 0, hooks?.PhysicalInFlight ?? 0,
            hooks?.ReservationHeld == true, state.State == PortraitOwnedScenePassiveDiagnosticState.Faulted);
    }
    internal bool TryStart(out string reason)
    {
        reason = "A complete historical census and unchanged ready Framework source are required.";
        if (!framework() || BlocksOperations) return false;
        try
        {
            var previous = census.Snapshot; var fresh = context(); var expected = thread();
            if (fresh is not { IsValid: true } || previous.Attempt <= 0 || previous.Context != fresh
                || previous.Result is not { Complete: true, Rows.Count: 8 } || previous.ObservedThread != expected
                || previous.ExpectedThread != expected || expected.NativeID == 0 || expected.ManagedID <= 0
                || previous.State is not (PortraitSourceCensusDiagnosticState.Off or PortraitSourceCensusDiagnosticState.Retiring)) return false;
            var now = timestamp();
            var current = new Run(checked(++attempts), previous.Attempt, fresh, expected, census.DrainPhysicalAsync(), now, checked(now + 5 * frequency));
            Volatile.Write(ref run, current);
            Publish(current, PortraitOwnedScenePassiveDiagnosticState.WaitingForCensusDrain, "Waiting for this actual census attempt's three physical Hosts to retire.");
            census.Stop("Owned forwarding handoff revoked the old census authority.");
            current.Expiry = ExpireAsync(current);
            reason = Snapshot.Reason; return true;
        }
        catch (Exception error)
        {
            reason = "Owned forwarding request failed: " + error.GetType().Name;
            Stop(reason); return false;
        }
    }
    internal void Update()
    {
        var current = Volatile.Read(ref run);
        if (current is null || Volatile.Read(ref current.Retiring) != 0 || Interlocked.CompareExchange(ref current.Operation, 1, 0) != 0) return;
        try
        {
            if (Volatile.Read(ref current.Retiring) != 0) return;
            if (!ContextPreserved(current))
            { BeginRetirement(current, "The bounded ready source/session/owner/thread changed."); return; }
            if (Snapshot.State == PortraitOwnedScenePassiveDiagnosticState.WaitingForCensusDrain)
            {
                if (!current.CensusDrain.IsCompleted) return;
                if (!current.CensusDrain.IsCompletedSuccessfully || census.Snapshot.Attempt != current.CensusAttempt
                    || !CensusPhysicallyDrained(census.ReadSafetyState(), current.CensusAttempt))
                { BeginRetirement(current, "The exact old census physical drain could not be confirmed."); return; }
                if (Volatile.Read(ref current.Retiring) != 0) return;
                Publish(current, PortraitOwnedScenePassiveDiagnosticState.Installing, "Old three-Host drain confirmed; qualifying a new owned forwarding generation.");
                var hooks = createHooks(); Volatile.Write(ref current.Hooks, hooks);
                if (Volatile.Read(ref current.Retiring) != 0) { hooks.RequestStop(); return; }
                var duration = TimeSpan.FromSeconds(Math.Min(5, (double)(current.Deadline - timestamp()) / frequency));
                if (!hooks.TryInstallPassive(factory, duration) || !hooks.IsInstalled)
                { BeginRetirement(current, hooks.LastError ?? "Qualified owned forwarding did not install."); return; }
                if (!ContextPreserved(current)
                    || !hooks.TryArmPassiveSourceBaseline(current.Context.Source, current.Context.SessionEpoch, current.Thread, TimeSpan.FromSeconds(1))
                    || !ContextPreserved(current))
                { BeginRetirement(current, "A future source Return could not arm under the new completely installed owner."); return; }
                Publish(current, PortraitOwnedScenePassiveDiagnosticState.WaitingForSourceReturn, "Waiting for a future actual source Return of the new owner; production admission is disabled.");
            }
            if (current.Hooks?.PassiveSourceBaseline is { } baseline)
            {
                if (baseline.State == PortraitOwnedScenePassiveBaselineState.Revoked)
                { BeginRetirement(current, baseline.Reason); return; }
                if (baseline.State == PortraitOwnedScenePassiveBaselineState.Observed)
                {
                    current.SourceReturn = baseline;
                    BeginRetirement(current, "A new-owner future source Return was observed; retiring the bounded forwarding probe.");
                }
            }
        }
        catch (Exception error) { BeginRetirement(current, "Owned forwarding evidence failed: " + error.GetType().Name); }
        finally { Volatile.Write(ref current.Operation, 0); }
    }
    internal void Stop(string reason)
    {
        var current = Volatile.Read(ref run);
        if (current is not null) BeginRetirement(current, reason);
    }
    private bool ContextPreserved(Run current)
    {
        if (Volatile.Read(ref current.Retiring) != 0 || !framework()) return false;
        var before = timestamp();
        if (before < current.LastTimestamp || before >= current.Deadline || context() != current.Context || thread() != current.Thread) return false;
        var after = timestamp();
        if (after < before || after >= current.Deadline || Volatile.Read(ref current.Retiring) != 0) return false;
        current.LastTimestamp = after; return true;
    }
    private void BeginRetirement(Run current, string reason)
    {
        if (Interlocked.Exchange(ref current.Retiring, 1) != 0) return;
        current.CancelExpiry.Cancel(); current.Hooks?.RequestStop();
        Publish(current, PortraitOwnedScenePassiveDiagnosticState.Retiring, reason);
        current.Retirement = RetireAsync(current, reason);
    }
    private async Task RetireAsync(Run current, string reason)
    {
        // Timer cancellation may happen inside creation/enable. Hold the whole
        // request until that operation returns and reveals its actual hook owner.
        while (Volatile.Read(ref current.Operation) != 0) await Task.Delay(5).ConfigureAwait(false);
        try
        {
            current.Hooks?.TryDispose();
            await Task.WhenAll(current.CensusDrain, current.Hooks?.DrainPhysicalAsync() ?? Task.CompletedTask).ConfigureAwait(false);
            Publish(current, PortraitOwnedScenePassiveDiagnosticState.Off, reason);
            current.Retired.TrySetResult();
        }
        catch (Exception error)
        {
            // A failed physical drain is quarantined. The pending drain task
            // retains this exact run and its actual hook owner for inspection.
            Publish(current, PortraitOwnedScenePassiveDiagnosticState.Faulted,
                "Physical owned forwarding retirement failed: " + error.GetType().Name);
        }
    }
    private async Task ExpireAsync(Run current)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), current.CancelExpiry.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        BeginRetirement(current, "The total five-second forwarding handoff expired.");
    }
    private void Publish(Run current, PortraitOwnedScenePassiveDiagnosticState state, string reason)
    {
        if (ReferenceEquals(Volatile.Read(ref run), current)) Volatile.Write(ref snapshot,
            new(state, current.Attempt, current.CensusAttempt, reason, current.Context, current.Thread, current.SourceReturn));
    }
    private static bool CensusPhysicallyDrained(PortraitSourceCensusSafetyState state, long attempt)
    {
        static bool Retired(PortraitSourceCensusSafetyHost? host) => host is { State: "Retired", Hooks: 0, InFlight: 0, ReservationHeld: false, Failed: false };
        return state.Known && state.Attempt == attempt && state.State == "Off" && !state.BlocksOperations && state.PhysicalDrainCompleted
            && Retired(state.Source) && Retired(state.Counters) && Retired(state.Resources);
    }
    private sealed class Run(long attempt, long censusAttempt, PortraitSourceCensusContext context, PortraitNativeThreadStamp thread,
        Task censusDrain, long now, long deadline)
    {
        internal readonly long Attempt = attempt, CensusAttempt = censusAttempt, Deadline = deadline;
        internal readonly PortraitSourceCensusContext Context = context;
        internal readonly PortraitNativeThreadStamp Thread = thread;
        internal readonly Task CensusDrain = censusDrain;
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource CancelExpiry = new();
        internal PortraitOwnedSceneHooks? Hooks;
        internal PortraitOwnedScenePassiveBaseline? SourceReturn;
        internal Task? Expiry, Retirement;
        internal int Operation, Retiring;
        internal long LastTimestamp = now;
    }
}
