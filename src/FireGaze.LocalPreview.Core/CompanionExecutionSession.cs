namespace FireGaze.LocalPreview.Core;

/// <summary>
/// Small execution/acknowledgment adapter for the existing behavior coordinator.
/// Framework-owned; reserves sockets and deduplicates requests without invoking native code.
/// Consuming TryTakeRequest is the submission boundary: uncertainty after that call retains occupancy.
/// </summary>
public sealed class CompanionExecutionSession
{
    private sealed class Entry(CompanionExecutionPlan plan, TimeSpan createdAt)
    {
        public CompanionExecutionPlan Plan = plan;
        public CompanionExecutionState State = CompanionExecutionState.Reserved;
        public CompanionPerchSlot HeldSocket = plan.DesiredAttachment;
        public bool RequestDispatched;
        public bool NativeOccupancyPossible;
        public bool DeadlineExpired;
        public TimeSpan CreatedAt = createdAt;
        public TimeSpan SubmittedAt;
        public CompanionExecutionObservation? Observation;
        public string? FailureReason;
        public CompanionExecutionSnapshot View()
            => new(Plan, State, HeldSocket, RequestDispatched, NativeOccupancyPossible,
                DeadlineExpired, Observation, FailureReason);
    }

    private readonly Dictionary<CompanionExecutionKey, Entry> entries = [];
    private readonly HashSet<CompanionExecutionContext> invalidatedContexts = [];
    private CompanionExecutionContext context;
    private TimeSpan lastTime;

    public CompanionExecutionSession(CompanionExecutionContext context)
    {
        if (!CompanionExecutionPlan.ValidContext(context)) throw new ArgumentException("Invalid execution context.", nameof(context));
        this.context = context;
    }

    public IReadOnlyList<CompanionExecutionSnapshot> Snapshot
        => Array.AsReadOnly(entries.Values.OrderBy(x => x.Plan.Key.LeaseID)
            .ThenBy(x => x.Plan.Key.LeaseGeneration).Select(x => x.View()).ToArray());

    /// <summary>Player-triggered and automatic native placement both use this exact reservation path.</summary>
    public bool TryReserve(CompanionExecutionPlan plan, CompanionExecutionAdmission admission, TimeSpan now, out string reason)
    {
        reason = "Current active lease, source, model and physical owner-child evidence are required.";
        if (!Admitted(plan, admission, now) || plan.DesiredAttachment == CompanionPerchSlot.None) return false;
        Advance(now);
        if (entries.TryGetValue(plan.Key, out var prior))
        {
            if (prior.Plan == plan && prior.State is CompanionExecutionState.Reserved
                or CompanionExecutionState.Requested or CompanionExecutionState.Attached)
            { reason = ""; return true; }
            if (plan.RequestSequence <= prior.Plan.RequestSequence
                || prior.State is not (CompanionExecutionState.Following or CompanionExecutionState.Refused))
            { reason = "A matching request is already outstanding, or its sequence is stale."; return false; }
        }
        if (entries.Any(pair => pair.Key != plan.Key && pair.Key.LeaseID == plan.Key.LeaseID
            && pair.Value.State is not (CompanionExecutionState.Retired or CompanionExecutionState.Following or CompanionExecutionState.Refused)))
        { reason = "The previous actor generation has not finished its native operation."; return false; }
        if (entries.Values.Any(entry => Conflicts(entry.HeldSocket, plan.DesiredAttachment)))
        { reason = "The requested attachment socket is reserved or physically occupied."; return false; }
        entries[plan.Key] = new(plan, now);
        reason = "";
        return true;
    }

    /// <summary>
    /// Returns a request once, before any native call. A missing response or exception after
    /// this point cannot prove non-submission and must never release the socket.
    /// </summary>
    public bool TryTakeRequest(CompanionExecutionKey key, long sequence, CompanionExecutionAdmission admission, TimeSpan now, out CompanionExecutionPlan? plan)
    {
        plan = null;
        if (!TimeCurrent(now)) return false;
        Advance(now);
        if (!Current(key, sequence, out var entry) || entry.RequestDispatched || entry.DeadlineExpired
            || entry.State is not (CompanionExecutionState.Reserved or CompanionExecutionState.Detaching)
            || !Admitted(entry.Plan, admission, now)) return false;
        entry.RequestDispatched = true;
        entry.NativeOccupancyPossible = true;
        entry.SubmittedAt = now;
        if (entry.State == CompanionExecutionState.Reserved) entry.State = CompanionExecutionState.Requested;
        plan = entry.Plan;
        return true;
    }

    /// <summary>Preserves the old socket while a distinct, monotonically numbered detach request is pending.</summary>
    public bool BeginDetach(CompanionExecutionPlan plan, CompanionExecutionAdmission admission, TimeSpan now, out string reason)
    {
        reason = "A current attached actor and a new detach request sequence are required.";
        if (!Admitted(plan, admission, now) || plan.DesiredAttachment != CompanionPerchSlot.None
            || !entries.TryGetValue(plan.Key, out var entry) || entry.State != CompanionExecutionState.Attached
            || plan.RequestSequence <= entry.Plan.RequestSequence) return false;
        Advance(now);
        entry.Plan = plan;
        entry.State = CompanionExecutionState.Detaching;
        entry.RequestDispatched = false;
        entry.DeadlineExpired = false;
        entry.CreatedAt = now;
        entry.Observation = null;
        entry.FailureReason = null;
        reason = "";
        return true;
    }

    public bool CancelReserved(CompanionExecutionKey key, long sequence, string reason)
    {
        if (!Current(key, sequence, out var entry) || entry.State != CompanionExecutionState.Reserved
            || entry.RequestDispatched) return false;
        entry.State = CompanionExecutionState.Refused;
        entry.HeldSocket = CompanionPerchSlot.None;
        entry.FailureReason = string.IsNullOrWhiteSpace(reason) ? "Reservation cancelled before submission." : reason;
        return true;
    }

    /// <summary>Physical cleanup belongs to the actor backend; this never declares retirement complete.</summary>
    public bool BeginRetire(CompanionExecutionKey key)
    {
        if (key.Context != context || !entries.TryGetValue(key, out var entry)
            || entry.State is CompanionExecutionState.Retired or CompanionExecutionState.Retiring) return false;
        entry.State = CompanionExecutionState.Retiring;
        if (!entry.NativeOccupancyPossible) entry.HeldSocket = CompanionPerchSlot.None;
        return true;
    }

    public bool Observe(CompanionExecutionObservation observation, TimeSpan now)
    {
        if (!TimeCurrent(now)) return false;
        Advance(now);
        if (!Current(observation.Key, observation.RequestSequence, out var entry)
            || entry.State == CompanionExecutionState.Retired || !ValidObservation(observation, entry, now)) return false;
        if (observation.SpawnState == CompanionExecutionSpawnState.PhysicallyRetired)
        {
            entry.Observation = observation;
            entry.State = CompanionExecutionState.Retired;
            entry.HeldSocket = CompanionPerchSlot.None;
            entry.NativeOccupancyPossible = false;
            entry.FailureReason = observation.FailureReason;
            return true;
        }
        if (observation.AttachmentConfirmed)
        {
            if (!entry.RequestDispatched || entry.DeadlineExpired || !observation.BindingCurrent
                || observation.SpawnState != CompanionExecutionSpawnState.Ready || observation.RequestAccepted is not true
                || observation.BehaviorState < 0 || observation.BehaviorSpecial < 0 || observation.NativeSlot < 0
                || observation.ActualAttachment != entry.HeldSocket || entry.HeldSocket == CompanionPerchSlot.None
                || entry.State is not (CompanionExecutionState.Requested or CompanionExecutionState.Attached))
            {
                // Fresh current evidence can invalidate the prior success even though it
                // cannot acknowledge a new attachment. Keep the exact occupied socket.
                if (entry.State == CompanionExecutionState.Attached)
                {
                    entry.State = CompanionExecutionState.Unconfirmed;
                    entry.Observation = observation;
                    entry.FailureReason = observation.FailureReason ?? "Current native attachment evidence is inconsistent.";
                }
                return false;
            }
            entry.State = CompanionExecutionState.Attached;
        }
        else if (observation.DetachConfirmed)
        {
            if (!entry.RequestDispatched || !entry.NativeOccupancyPossible || !observation.BindingCurrent) return false;
            entry.HeldSocket = CompanionPerchSlot.None;
            entry.NativeOccupancyPossible = false;
            if (entry.State != CompanionExecutionState.Retiring) entry.State = CompanionExecutionState.Following;
        }
        else if (entry.State == CompanionExecutionState.Attached)
            entry.State = CompanionExecutionState.Unconfirmed;
        entry.Observation = observation;
        entry.FailureReason = observation.FailureReason;
        return true;
    }

    /// <summary>Timeout is a refusal only before submission; native uncertainty retains its exact socket.</summary>
    public void Advance(TimeSpan now)
    {
        if (!TimeCurrent(now)) throw new ArgumentOutOfRangeException(nameof(now), "Execution clock must be monotonic.");
        lastTime = now;
        foreach (var entry in entries.Values)
        {
            if (now < entry.Plan.Deadline || entry.State is not (CompanionExecutionState.Reserved
                or CompanionExecutionState.Requested or CompanionExecutionState.Detaching or CompanionExecutionState.Unconfirmed)) continue;
            entry.DeadlineExpired = true;
            entry.FailureReason ??= "Native request deadline elapsed; verified cleanup is still required if submitted.";
            if (entry.State == CompanionExecutionState.Reserved && !entry.RequestDispatched)
            {
                entry.State = CompanionExecutionState.Refused;
                entry.HeldSocket = CompanionPerchSlot.None;
            }
        }
    }

    /// <summary>
    /// Invalid source/scene identities may release their arbitration scope, without claiming
    /// their actors physically retired. Invalidated contexts can never be reused for late results.
    /// </summary>
    public bool ResetContext(CompanionExecutionContext next)
    {
        if (!CompanionExecutionPlan.ValidContext(next) || next == context || next.SceneEpoch < context.SceneEpoch
            || invalidatedContexts.Contains(next)
            || next.SceneEpoch == context.SceneEpoch && next.SourceID == context.SourceID
                && next.SourceGeneration <= context.SourceGeneration) return false;
        invalidatedContexts.Add(context);
        context = next;
        entries.Clear();
        return true;
    }

    private bool Admitted(CompanionExecutionPlan plan, CompanionExecutionAdmission admission, TimeSpan now)
    {
        if (!TimeCurrent(now) || plan is null || !plan.IsValid() || plan.Key.Context != context
            || plan.Deadline <= now || admission.Key != plan.Key || !admission.Active || !admission.ModelReady
            || !admission.OwnerChildCurrent || !admission.SourceCurrent) return false;
        const CompanionPerchSlots known = CompanionPerchSlots.Shoulders | CompanionPerchSlots.Head;
        if ((admission.SpeciesSockets & ~known) != 0 || (admission.ModelSockets & ~known) != 0) return false;
        var requested = Mask(plan.DesiredAttachment);
        return requested == CompanionPerchSlots.None
            || ((admission.SpeciesSockets & admission.ModelSockets) & requested) == requested;
    }

    private bool Current(CompanionExecutionKey key, long sequence, out Entry entry)
    {
        entry = null!;
        return key.Context == context && entries.TryGetValue(key, out entry!) && entry.Plan.RequestSequence == sequence;
    }

    private bool TimeCurrent(TimeSpan now) => now >= TimeSpan.Zero && now >= lastTime;

    private static bool ValidObservation(CompanionExecutionObservation value, Entry entry, TimeSpan now)
        => value.ObservationSequence > (entry.Observation?.ObservationSequence ?? 0)
            && value.ObservedAt >= entry.CreatedAt && value.ObservedAt <= now
            && value.ObservedAt >= (entry.Observation?.ObservedAt ?? TimeSpan.Zero)
            && (!entry.RequestDispatched || value.ObservedAt >= entry.SubmittedAt)
            && CompanionExecutionPlan.Finite(value.ActualChildPosition)
            && float.IsFinite(value.Progress) && value.Progress is >= 0 and <= 1
            && Enum.IsDefined(value.SpawnState) && Enum.IsDefined(value.ActualAttachment)
            && (!value.AttachmentConfirmed || value.ActualAttachment != CompanionPerchSlot.None)
            && (!value.DetachConfirmed || !value.AttachmentConfirmed && value.ActualAttachment == CompanionPerchSlot.None)
            && (value.SpawnState != CompanionExecutionSpawnState.PhysicallyRetired
                || !value.AttachmentConfirmed && value.ActualAttachment == CompanionPerchSlot.None);

    private static bool Conflicts(CompanionPerchSlot existing, CompanionPerchSlot desired)
        => existing != CompanionPerchSlot.None && (existing == desired
            || existing == CompanionPerchSlot.Head || desired == CompanionPerchSlot.Head);

    private static CompanionPerchSlots Mask(CompanionPerchSlot slot) => slot switch
    {
        CompanionPerchSlot.LeftShoulder => CompanionPerchSlots.LeftShoulder,
        CompanionPerchSlot.RightShoulder => CompanionPerchSlots.RightShoulder,
        CompanionPerchSlot.Head => CompanionPerchSlots.Head,
        _ => CompanionPerchSlots.None,
    };
}
