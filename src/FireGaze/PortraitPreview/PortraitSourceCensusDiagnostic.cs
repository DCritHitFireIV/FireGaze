using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FireGaze.PortraitPreview;

internal enum PortraitSourceCensusDiagnosticState { Off, ObservingThread, Armed, Retiring, Faulted }
internal sealed record PortraitSourceCensusContext(PortraitIdentity Source, ulong AccountID, ulong ContentID,
    long SessionEpoch, string? BlockedReason = null)
{
    internal bool IsValid => Source.IsValid && Source.Generation > 0 && AccountID > 0 && ContentID > 0
        && SessionEpoch > 0 && string.IsNullOrEmpty(BlockedReason);
}
internal sealed record PortraitSourceCensusDiagnosticSnapshot(PortraitSourceCensusDiagnosticState State, long Attempt,
    string Reason, PortraitNativeThreadStamp ExpectedThread, PortraitNativeThreadStamp? ObservedThread,
    PortraitSourceCensusContext? Context, PortraitOwnedSceneSlotCensus? Result,
    PortraitNativePhaseHostSnapshot? Source, PortraitNativePhaseHostSnapshot? Counters, PortraitNativePhaseHostSnapshot? Resources)
{
    public bool ProductionAdmission => false;
}

/// <summary>One explicit read-only diagnostic. Only a future real Host return can sample the native table.</summary>
internal sealed class PortraitSourceCensusDiagnostic
{
    private static readonly TimeSpan AttemptDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ArmedDuration = TimeSpan.FromSeconds(1);
    private readonly Func<bool> framework;
    private readonly Func<PortraitSourceCensusContext?> context;
    private readonly IPortraitNativePhaseHostFactory factory;
    private readonly IPortraitOwnedSceneSlotNativeApi? native;
    private readonly Func<long> timestamp;
    private readonly Func<PortraitNativeThreadStamp> thread;
    private readonly long frequency;
    private readonly PortraitNativePhaseHostResolve? sourceResolve, counterResolve, resourceResolve;
    private Run? run;
    private long attempts;
    private PortraitSourceCensusDiagnosticSnapshot snapshot = new(PortraitSourceCensusDiagnosticState.Off, 0,
        "Disabled; no native diagnostic has started.", default, null, null, null, null, null, null);

    internal PortraitSourceCensusDiagnostic(Func<bool> framework, Func<PortraitSourceCensusContext?> context,
        IPortraitNativePhaseHostFactory factory, IPortraitOwnedSceneSlotNativeApi? native = null,
        Func<long>? timestamp = null, long timestampFrequency = 0, Func<PortraitNativeThreadStamp>? thread = null,
        PortraitNativePhaseHostResolve? sourceResolve = null, PortraitNativePhaseHostResolve? counterResolve = null,
        PortraitNativePhaseHostResolve? resourceResolve = null)
    {
        this.framework = framework; this.context = context; this.factory = factory; this.native = native;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        frequency = timestampFrequency == 0 ? Stopwatch.Frequency : timestampFrequency;
        if (frequency <= 0 || frequency > long.MaxValue / 300) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        this.thread = thread ?? CurrentThread;
        this.sourceResolve = sourceResolve; this.counterResolve = counterResolve; this.resourceResolve = resourceResolve;
    }

    internal PortraitSourceCensusDiagnosticSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal bool IsActive => Snapshot.State is PortraitSourceCensusDiagnosticState.ObservingThread or PortraitSourceCensusDiagnosticState.Armed;
    internal bool BlocksOperations => Volatile.Read(ref run) is { } active && !active.Retired.Task.IsCompleted;

    internal PortraitSourceCensusSafetyState ReadSafetyState()
    {
        var current = Volatile.Read(ref run);
        if (current is null) return new(true, "Off", 0, false, true, null, null, null);
        var state = Snapshot;
        static PortraitSourceCensusSafetyHost ReadHost(PortraitNativePhaseHost host)
        {
            var value = host.ReadCensusSafetyState();
            return new(value.State, value.Hooks, value.InFlight, value.ReservationHeld, value.Failed);
        }
        var source = ReadHost(current.Source);
        var counters = ReadHost(current.Counters);
        var resources = ReadHost(current.Resources);
        var drained = current.Retired.Task.IsCompletedSuccessfully;
        return new(true, state.State.ToString(), current.Attempt, !drained, drained, source, counters, resources);
    }

    internal bool TryStart(out string reason)
    {
        reason = "Run on Framework after the previous diagnostic has retired.";
        if (!framework() || BlocksOperations) return false;
        try
        {
            // This independent stamp is taken before creating/starting any native observer.
            var expected = thread();
            var fresh = context();
            if (expected.NativeID == 0 || expected.ManagedID <= 0 || fresh is not { IsValid: true })
            { reason = fresh?.BlockedReason ?? "A ready source, active save-protected session and current owner are required."; return false; }
            var now = timestamp();
            var attempt = checked(++attempts);
            var current = new Run(this, attempt, fresh, expected, now, checked(now + 5 * frequency));
            Volatile.Write(ref run, current);
            Publish(current, PortraitSourceCensusDiagnosticState.ObservingThread, "Observing the known source thread without native table reads.");
            if (!current.Counters.TryStart(AttemptDuration, out reason) || !current.Resources.TryStart(AttemptDuration, out reason)
                || !current.Source.TryStart(AttemptDuration, out reason) || !PlansMatch(current))
            { BeginRetirement(current, "Native observation could not start: " + reason); return false; }
            Publish(current, PortraitSourceCensusDiagnosticState.ObservingThread, "Waiting for a successful actual source Render return.");
            reason = Snapshot.Reason; return true;
        }
        catch (Exception error)
        {
            reason = "Diagnostic start failed: " + error.GetType().Name;
            if (Volatile.Read(ref run) is { } current) BeginRetirement(current, reason);
            return false;
        }
    }

    internal void Update()
    {
        var current = Volatile.Read(ref run);
        if (current is null || Volatile.Read(ref current.Retiring) != 0) return;
        if (Volatile.Read(ref current.RevokedReason) is { } revoked) { BeginRetirement(current, revoked); return; }
        if (!framework()) { Revoke("Framework ownership unavailable."); return; }
        try
        {
            var now = timestamp();
            if (now < current.LastTimestamp || now >= current.Deadline
                || current.Armed && now >= current.ArmedDeadline)
            { BeginRetirement(current, "The bounded diagnostic expired."); return; }
            current.LastTimestamp = now;
            if (context() != current.Context || thread() != current.Thread)
            { BeginRetirement(current, "The source, owner, session or Framework thread changed."); return; }
            var source = current.Source.Snapshot(); var counters = current.Counters.Snapshot(); var resources = current.Resources.Snapshot();
            if (!source.Observation.Observing || !counters.Observation.Observing || !resources.Observation.Observing
                || source.Observation.OverwrittenRecords != 0 || !PlansMatch(source, counters, resources))
            { BeginRetirement(current, "Native recording, complete evidence or build qualification became unavailable."); return; }
            if (current.Armed)
            {
                if (!ReferenceEquals(current.Source.Health, current.SourceHealth)
                    || !ReferenceEquals(current.Counters.Health, current.CounterHealth)
                    || !ReferenceEquals(current.Resources.Health, current.ResourceHealth))
                { BeginRetirement(current, "Native host or mutation generation changed."); return; }
                if (current.Census.TryTake(out var result) && result is { Complete: true, Rows.Count: 8 }
                    && result.Phase.Source == current.Context.Source && result.Phase.SessionEpoch == current.Context.SessionEpoch)
                { current.Result = result; BeginRetirement(current, "One complete historical eight-slot observation was accepted."); }
                return;
            }
            var rows = source.Observation.Records.Where(row => row.Phase == PortraitNativeObservedPhase.SourceCharaViewRender
                && row.Receiver == current.Context.Source.Model).ToArray();
            if (rows.Any(row => row.Thread != current.Thread || row.Edge == PortraitNativeObservedEdge.Throw))
            { BeginRetirement(current, "The target source did not render exclusively on the independently captured Framework thread."); return; }
            var returned = rows.FirstOrDefault(row => row.Edge == PortraitNativeObservedEdge.Return
                && row.Frame.Origin == PortraitNativeFrameOrigin.SourceRenderArgument
                && row.Frame.SourceView == current.Context.Source.Model
                && rows.Any(enter => enter.Edge == PortraitNativeObservedEdge.Enter && enter.Capture == row.Capture
                    && enter.Call == row.Call && enter.Thread == row.Thread && enter.Frame == row.Frame
                    && enter.Depth == row.Depth && enter.ParentCall == row.ParentCall && enter.Sequence < row.Sequence));
            if (returned.Call == 0) return;
            current.Observed = returned.Thread;
            if (context() != current.Context || thread() != current.Thread)
            { BeginRetirement(current, "Fresh Framework source qualification changed before arming."); return; }
            current.Qualifications.Publish(new(current.Context.Source, current.Attempt, current.Context.SessionEpoch,
                current.Attempt, current.Thread));
            if (!current.Census.TryArm(current.Attempt, ArmedDuration))
            { BeginRetirement(current, "The independently qualified future source request could not arm."); return; }
            current.SourceHealth = current.Source.Health; current.CounterHealth = current.Counters.Health; current.ResourceHealth = current.Resources.Health;
            current.Armed = true; current.ArmedDeadline = Math.Min(current.Deadline, checked(now + frequency));
            Publish(current, PortraitSourceCensusDiagnosticState.Armed, "Qualified; waiting for one future actual source Render return.");
        }
        catch (Exception error) { BeginRetirement(current, "Diagnostic evidence failed: " + error.GetType().Name); }
    }

    // Safe from a lifecycle callback: only revoke detached authority here.
    internal void Revoke(string reason)
    {
        var current = Volatile.Read(ref run);
        if (current is null) return;
        current.Qualifications.Publish(null); current.Census.Cancel();
        Volatile.Write(ref current.RevokedReason, reason);
    }
    internal void Stop(string reason)
    {
        var current = Volatile.Read(ref run);
        if (current is not null) BeginRetirement(current, reason);
    }
    internal Task DrainPhysicalAsync() => Volatile.Read(ref run)?.Retired.Task ?? Task.CompletedTask;

    private void BeginRetirement(Run current, string reason)
    {
        current.Qualifications.Publish(null); current.Census.Cancel();
        if (Interlocked.Exchange(ref current.Retiring, 1) != 0) return;
        // Framework detaches and disables; it never waits for an Original/completion.
        foreach (var host in current.Hosts) host.BeginRetirement(out _);
        Publish(current, PortraitSourceCensusDiagnosticState.Retiring, reason);
        current.Retirement = RetireAsync(current, reason);
    }
    private async Task RetireAsync(Run current, string reason)
    {
        while (true)
        {
            var complete = true; var faulted = false;
            foreach (var host in current.Hosts)
            {
                if (host.TryCompleteRetirement(out _)) continue;
                complete = false;
                faulted |= host.Health.State == PortraitNativePhaseHostState.Faulted;
            }
            if (complete)
            {
                foreach (var recorder in current.Recorders) recorder.Dispose();
                Publish(current, PortraitSourceCensusDiagnosticState.Off, reason);
                current.Retired.TrySetResult(); return;
            }
            if (faulted)
            {
                Publish(current, PortraitSourceCensusDiagnosticState.Faulted, reason + " Native retirement is unconfirmed; provider ownership is retained.");
                // The retained task captures the whole attempt. Failure/false is not successful unload.
                await current.Retired.Task.ConfigureAwait(false); return;
            }
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
    private void Publish(Run current, PortraitSourceCensusDiagnosticState state, string reason)
    {
        if (!ReferenceEquals(Volatile.Read(ref run), current)) return;
        Volatile.Write(ref snapshot, new(state, current.Attempt, reason, current.Thread, current.Observed,
            current.Context, current.Result, current.Source.Snapshot(), current.Counters.Snapshot(), current.Resources.Snapshot()));
    }
    private static bool PlansMatch(Run current) => PlansMatch(current.Source.Snapshot(), current.Counters.Snapshot(), current.Resources.Snapshot());
    private static bool PlansMatch(PortraitNativePhaseHostSnapshot source, PortraitNativePhaseHostSnapshot counter, PortraitNativePhaseHostSnapshot resource)
    {
        if (source.Plan is not { } plan || counter.Plan is not { } c || resource.Plan is not { } r) return false;
        return plan.ModuleBase == c.ModuleBase && plan.ModuleBase == r.ModuleBase && plan.ModuleSize == c.ModuleSize && plan.ModuleSize == r.ModuleSize
            && plan.OnDiskEXESHA256 == PortraitNativePhaseObservationBinding.EXE && c.OnDiskEXESHA256 == plan.OnDiskEXESHA256 && r.OnDiskEXESHA256 == plan.OnDiskEXESHA256
            && plan.OnDiskSDKSHA256 == PortraitNativePhaseObservationBinding.SDK && c.OnDiskSDKSHA256 == plan.OnDiskSDKSHA256 && r.OnDiskSDKSHA256 == plan.OnDiskSDKSHA256
            && plan.SDKMVID != Guid.Empty && plan.SDKMVID == c.SDKMVID && plan.SDKMVID == r.SDKMVID
            && plan.Targets.Count == 4 && c.Targets.Count == 2 && r.Targets.Count == 2
            && source.BackendIdentity == counter.BackendIdentity && source.BackendIdentity == resource.BackendIdentity;
    }
    internal static PortraitNativeThreadStamp CurrentThread() => new(GetCurrentThreadID(), Environment.CurrentManagedThreadId);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)] private static extern uint GetCurrentThreadID();

    private sealed class Run
    {
        internal readonly long Attempt, Deadline;
        internal readonly PortraitSourceCensusContext Context;
        internal readonly PortraitNativeThreadStamp Thread;
        public readonly PortraitNativePhaseHost Source, Counters, Resources;
        internal readonly PortraitNativePhaseHost[] Hosts;
        internal readonly PortraitNativePhaseObservation[] Recorders;
        internal readonly PortraitSourceCensusQualifications Qualifications = new();
        internal readonly PortraitOwnedSceneSourceCensus Census;
        private readonly Func<long> timestamp;
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal PortraitNativePhaseHostHealth? SourceHealth, CounterHealth, ResourceHealth;
        internal PortraitNativeThreadStamp? Observed;
        internal PortraitOwnedSceneSlotCensus? Result;
        internal Task? Retirement;
        internal string? RevokedReason;
        internal long LastTimestamp, ArmedDeadline;
        internal bool Armed;
        internal int Retiring;
        private int deadlineExpired;
        internal Run(PortraitSourceCensusDiagnostic owner, long attempt, PortraitSourceCensusContext context,
            PortraitNativeThreadStamp thread, long now, long deadline)
        {
            Attempt = attempt; Context = context; Thread = thread; LastTimestamp = now; Deadline = deadline;
            timestamp = owner.timestamp;
            Recorders = Enumerable.Range(0, 3).Select(_ => new PortraitNativePhaseObservation(timestamp: owner.timestamp,
                timestampFrequency: owner.frequency)).ToArray();
            var reservations = new PortraitNativePhaseHostReservations();
            Source = new(owner.factory, Recorders[0], reservations, owner.sourceResolve);
            PortraitOwnedSceneSourceCensus? binding = null;
            Counters = PortraitNativePhaseHost.CreateCounterIngress(owner.factory, Recorders[1], reservations,
                () => binding?.BeginCounterMutation(), owner.counterResolve);
            Resources = PortraitNativePhaseHost.CreateResourceIngress(owner.factory, Recorders[2], reservations,
                () => binding?.BeginResourceMutation(), owner.resourceResolve);
            Census = binding = new(Source, Counters, Resources, Qualifications, owner.native, ReadRequestTimestamp, owner.frequency, capacity: 1);
            Hosts = [Source, Counters, Resources];
        }
        private long ReadRequestTimestamp()
        {
            var now = timestamp();
            // Recorder start follows installation. The request's own clock must
            // also enforce the earlier controller deadline without Update polling.
            if (now >= Deadline && Interlocked.Exchange(ref deadlineExpired, 1) == 0)
            {
                Volatile.Write(ref RevokedReason, "The total diagnostic deadline expired.");
                Qualifications.Publish(null);
            }
            return now;
        }
    }
}
