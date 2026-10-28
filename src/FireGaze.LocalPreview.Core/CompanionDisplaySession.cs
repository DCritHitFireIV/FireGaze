namespace FireGaze.LocalPreview.Core;

public enum CompanionActorReadiness { Pending, Ready, Lost, NativeHidden }

/// <summary>Result of crossing the native actor allocation boundary.</summary>
public enum CompanionActorCreateKind
{
    /// <summary>Zero is deliberately not a success value for forward compatible adapters.</summary>
    Failed = 0,
    Created,
    /// <summary>The native pool is known to be full. Retry on a later Framework frame.</summary>
    CapacityPending,
    /// <summary>The census or allocation evidence is not safe to use this frame.</summary>
    Deferred,
}

public readonly record struct CompanionActorCreateResult(CompanionActorCreateKind Kind, string Failure)
{
    public static CompanionActorCreateResult Created { get; } = new(CompanionActorCreateKind.Created, string.Empty);
    public static CompanionActorCreateResult CapacityPending(string reason)
        => new(CompanionActorCreateKind.CapacityPending, reason);
    public static CompanionActorCreateResult Deferred(string reason)
        => new(CompanionActorCreateKind.Deferred, reason);
    public static CompanionActorCreateResult Failed(string reason)
        => new(CompanionActorCreateKind.Failed, reason);
}

/// <summary>All operations are called by the owning Framework thread.</summary>
public interface ICompanionActorBackend
{
    bool TryCreate(CompanionDisplayLease lease, out string failure);
    /// <summary>
    /// Typed allocation boundary. The default keeps existing adapters source-compatible;
    /// adapters that can read native capacity should override it.
    /// </summary>
    CompanionActorCreateResult Create(CompanionDisplayLease lease)
    {
        return TryCreate(lease, out var failure)
            ? CompanionActorCreateResult.Created
            : CompanionActorCreateResult.Failed(failure);
    }
    CompanionActorReadiness Observe(CompanionDisplayLease lease, out string failure);
    bool TryRetire(CompanionDisplayLease lease, out string failure);
    bool TrySelect(CompanionDisplayLease lease);
    /// <summary>Read the owned native hard target without changing it. The session revalidates its identity and readiness.</summary>
    CompanionDisplayLease? ObserveSelected();
}

/// <summary>Tracks requests separately from actual native readiness and physical retirement.</summary>
public sealed class CompanionDisplaySession
{
    private readonly CompanionDisplayBridge bridge;
    private readonly ICompanionActorBackend backend;
    private readonly HashSet<(long Lease, long Generation)> created = [];
    private readonly HashSet<(long Lease, long Generation)> capacityPending = [];
    private readonly HashSet<(long Lease, long Generation)> deferredCreation = [];
    private readonly Dictionary<long, TimeSpan> waiting = [];
    private CompanionDisplayLease? selectedLease;
    private static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(12);
    private readonly int creationBudget;
    private long? creationFrame;
    private long? capacityBlockedFrame;
    private int creationsThisFrame;
    private bool usesExplicitFrames;
    private bool updating;
    public CompanionDisplaySession(int maximumActors, ICompanionActorBackend backend, int creationBudget = int.MaxValue)
    {
        bridge = new(maximumActors);
        this.backend = backend;
        if (creationBudget < 1) throw new ArgumentOutOfRangeException(nameof(creationBudget));
        this.creationBudget = creationBudget;
    }
    public CompanionDisplaySnapshot Snapshot => bridge.Snapshot;
    /// <summary>Allocation-free exact lease check for a native operation's reentry guard.</summary>
    public bool IsCurrentLiveLease(CompanionDisplayLease lease)
        => bridge.TryGetLease(lease.LeaseId, out var current) && current.Generation == lease.Generation
            && current.CompanionId == lease.CompanionId
            && current.State is CompanionDisplayState.Pending or CompanionDisplayState.Active or CompanionDisplayState.NativeHidden;
    /// <summary>
    /// Returns queue counters from one detached lease snapshot. Capacity/deferred sets
    /// are intersected with current Pending generations so a cancelled request can
    /// never leave stale waiting numbers in the UI.
    /// </summary>
    public CompanionDisplayQueueSnapshot QueueSnapshot
    {
        get
        {
            var snapshot = bridge.Snapshot;
            var current = snapshot.Leases.ToDictionary(x => x.LeaseId,
                x => (x.Generation, x.State));
            var capacity = capacityPending.Count(identity => current.TryGetValue(identity.Lease, out var state)
                && state.Generation == identity.Generation && state.State == CompanionDisplayState.Pending);
            var deferred = deferredCreation.Count(identity => current.TryGetValue(identity.Lease, out var state)
                && state.Generation == identity.Generation && state.State == CompanionDisplayState.Pending);
            return new(
                snapshot.Leases.Count,
                snapshot.Leases.Count(x => x.State == CompanionDisplayState.Active),
                snapshot.Leases.Count(x => x.State == CompanionDisplayState.Pending),
                capacity,
                deferred,
                snapshot.Leases.Count(x => x.State == CompanionDisplayState.NativeHidden),
                snapshot.Leases.Count(x => x.State == CompanionDisplayState.Retiring),
                snapshot.Leases.Count(x => x.State == CompanionDisplayState.Failed));
        }
    }
    /// <summary>Maximum new native actors admitted during one Framework update.</summary>
    public int CreationBudget => creationBudget;
    /// <summary>Current active companion selected as the native action target, if any.</summary>
    public uint? SelectedCompanionID => selectedLease?.CompanionId;
    /// <summary>The exact active native target, including duplicate-instance identity.</summary>
    public CompanionDisplayLease? SelectedLease => selectedLease;
    /// <summary>Requests waiting for a confirmed native slot pair. This is not failure.</summary>
    public int CapacityPendingCount => capacityPending.Count;
    /// <summary>Requests deferred because the native census was not safe to use this frame.</summary>
    public int DeferredCreationCount => deferredCreation.Count;
    /// <summary>Requests one instance. Duplicate instances require an explicit caller opt-in.</summary>
    public CompanionDisplayAdmission Request(uint id, bool allowDuplicate = false)
        => bridge.Request(id, allowDuplicate);
    public void SetMaximumActors(int maximumActors) => bridge.SetMaximumActors(maximumActors);

    /// <summary>
    /// Compatibility path for hosts whose call is already exactly one Framework frame.
    /// Once the explicit-frame overload is used, this path may drain/observe but can no
    /// longer reset its creation budget. Mutation callbacks should use DrainRetired.
    /// </summary>
    public void Update(TimeSpan elapsed)
    {
        if (!usesExplicitFrames)
        {
            creationFrame = checked((creationFrame ?? 0) + 1);
            creationsThisFrame = 0;
        }
        UpdateCore(elapsed, allowCreation: !usesExplicitFrames);
    }

    /// <summary>
    /// The owning host supplies one monotonic sequence for each real Framework update.
    /// Multiple calls and lifecycle callbacks in that frame share one allocation budget.
    /// </summary>
    public void Update(TimeSpan elapsed, long frameworkFrame)
    {
        if (frameworkFrame < 0) throw new ArgumentOutOfRangeException(nameof(frameworkFrame));
        usesExplicitFrames = true;
        if (creationFrame is { } previous && frameworkFrame < previous) return;
        if (creationFrame != frameworkFrame)
        {
            creationFrame = frameworkFrame;
            creationsThisFrame = 0;
        }
        UpdateCore(elapsed, allowCreation: true);
    }

    /// <summary>Reclaims cancelled/failed leases without creating or ticking any actor.</summary>
    public void DrainRetired()
    {
        if (updating) return;
        updating = true;
        try
        {
            foreach (var snapshot in bridge.Snapshot.Leases)
            {
                if (!bridge.TryGetLease(snapshot.LeaseId, out var lease)) continue;
                if (lease.State == CompanionDisplayState.Retiring)
                {
                    if (Retire(lease)) bridge.CompleteTeardown(lease.LeaseId);
                }
                else if (lease.State == CompanionDisplayState.Failed) Retire(lease);
            }
            ReconcileSelection();
        }
        finally { updating = false; }
    }

    private void UpdateCore(TimeSpan elapsed, bool allowCreation)
    {
        if (updating) return;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        updating = true;
        try
        {
            foreach (var snapshot in bridge.Snapshot.Leases)
            {
                // A preceding native call may synchronously cancel a later queued lease.
                // Re-read it immediately before crossing its native allocation boundary.
                if (!bridge.TryGetLease(snapshot.LeaseId, out var lease)) continue;
                try
                {
                    if (lease.State == CompanionDisplayState.Retiring)
                    {
                        if (Retire(lease)) bridge.CompleteTeardown(lease.LeaseId);
                        continue;
                    }
                    if (lease.State == CompanionDisplayState.Failed)
                    {
                        Retire(lease);
                        continue;
                    }
                    var identity = (lease.LeaseId, lease.Generation);
                    if (!created.Contains(identity))
                    {
                        // Requests remain Pending in the bridge until a later frame. This
                        // bounded admission is the native safety boundary for a "summon all"
                        // action: a large catalog never turns into one unbounded allocation burst.
                        if (!allowCreation || creationsThisFrame >= creationBudget
                            || capacityBlockedFrame == creationFrame) continue;
                        creationsThisFrame++;
                        var allocation = backend.Create(lease);
                        if (allocation.Kind == CompanionActorCreateKind.CapacityPending)
                        {
                            MarkCapacityPendingFrom(lease);
                            // A full or unknown native census pauses only later creates in
                            // this frame. Existing actors below/above this lease continue
                            // through Observe and retirement in the same pass.
                            capacityBlockedFrame = creationFrame;
                            continue;
                        }
                        if (allocation.Kind == CompanionActorCreateKind.Deferred)
                        {
                            ClearCapacityPendingFrom(lease);
                            deferredCreation.Add(identity);
                            capacityPending.Remove(identity);
                            capacityBlockedFrame = creationFrame;
                            continue;
                        }
                        capacityPending.Remove(identity);
                        deferredCreation.Remove(identity);
                        if (allocation.Kind != CompanionActorCreateKind.Created)
                        {
                            Fail(lease, allocation.Failure);
                            continue;
                        }
                        created.Add(identity);
                    }
                    // Do not observe a request cancelled by its native creation callback.
                    if (!bridge.TryGetLease(lease.LeaseId, out var afterCreate)
                        || afterCreate.Generation != lease.Generation
                        || afterCreate.State is CompanionDisplayState.Retiring or CompanionDisplayState.Failed)
                        continue;
                    var readiness = backend.Observe(lease, out var reason);
                    if (readiness == CompanionActorReadiness.Ready)
                    {
                        bridge.MarkActive(lease.LeaseId, lease.Generation);
                        waiting.Remove(lease.LeaseId);
                    }
                    else if (readiness == CompanionActorReadiness.Lost)
                        Fail(lease, reason);
                    else if (readiness == CompanionActorReadiness.NativeHidden)
                    {
                        bridge.MarkNativeHidden(lease.LeaseId, lease.Generation);
                        waiting.Remove(lease.LeaseId);
                    }
                    else
                    {
                        if (lease.State is CompanionDisplayState.Active or CompanionDisplayState.NativeHidden)
                            bridge.MarkPending(lease.LeaseId, lease.Generation);
                        var duration = waiting.GetValueOrDefault(lease.LeaseId) + elapsed;
                        waiting[lease.LeaseId] = duration;
                        if (duration >= ModelTimeout) Fail(lease, "宠物模型加载超时，请稍后重试。");
                    }
                }
                catch (Exception failure)
                {
                    Fail(lease, failure.Message);
                }
            }
            ReconcileSelection();
        }
        finally { updating = false; }
    }

    public bool Recall(uint id)
    {
        var lease = Find(id);
        if (lease is { } currentLease) ClearSelection(currentLease);
        return lease is { } current && bridge.BeginTeardown(current.LeaseId, CompanionDisplayRetireReason.UserCancelled);
    }

    /// <summary>
    /// Begins teardown for one exact lease. Companion IDs are directory identities and
    /// may intentionally repeat in the same-kind interaction mode, so callers that own
    /// a card or request must use this method instead of <see cref="Recall(uint)"/>.
    /// An optional generation rejects a stale card after a retry replaced the native
    /// actor; omitting it is only appropriate for a current lease ID from Snapshot.
    /// </summary>
    public bool RecallLease(long leaseId, long? generation = null)
    {
        if (leaseId <= 0) return false;
        var lease = bridge.Snapshot.Leases.FirstOrDefault(x => x.LeaseId == leaseId
            && (!generation.HasValue || x.Generation == generation.Value));
        if (lease.LeaseId == 0) return false;
        ClearSelection(lease);
        return bridge.BeginTeardown(lease.LeaseId, CompanionDisplayRetireReason.UserCancelled);
    }

    public void RecallAll(CompanionDisplayRetireReason reason)
    {
        selectedLease = null;
        bridge.RetireAll(reason);
    }

    public bool Select(uint id)
    {
        var lease = bridge.Snapshot.Leases.FirstOrDefault(candidate => candidate.CompanionId == id
            && candidate.State == CompanionDisplayState.Active);
        return lease.LeaseId > 0 && SelectLease(lease.LeaseId, lease.Generation);
    }

    /// <summary>Selects exactly one current active instance, without aliasing its siblings.</summary>
    public bool SelectLease(long leaseId, long generation)
    {
        if (leaseId <= 0 || generation <= 0 || !bridge.TryGetLease(leaseId, out var lease)
            || lease.Generation != generation || lease.State != CompanionDisplayState.Active) return false;
        try
        {
            if (!backend.TrySelect(lease)) return false;
            if (!bridge.TryGetLease(leaseId, out var current) || current.Generation != generation
                || current.CompanionId != lease.CompanionId || current.State != CompanionDisplayState.Active)
                return false;
            selectedLease = current;
            return true;
        }
        catch { return false; }
    }

    public bool Retry(uint id)
    {
        var lease = bridge.Snapshot.Leases.FirstOrDefault(candidate => candidate.CompanionId == id
            && candidate.State == CompanionDisplayState.Failed);
        return lease.LeaseId > 0 && RetryLease(lease.LeaseId, lease.Generation);
    }

    /// <summary>Retries one failed instance only after its exact native actor has physically retired.</summary>
    public bool RetryLease(long leaseId, long generation)
    {
        if (updating || leaseId <= 0 || generation <= 0 || !bridge.TryGetLease(leaseId, out var lease)
            || lease.Generation != generation || lease.State != CompanionDisplayState.Failed) return false;
        updating = true;
        try
        {
            if (!Retire(lease)) return false;
            // Retirement can synchronously cancel the request. Only the same failed
            // generation may become Pending; a native callback always keeps its change.
            if (!bridge.TryGetLease(leaseId, out var current) || current.Generation != generation
                || current.State != CompanionDisplayState.Failed) return false;
            return bridge.Retry(leaseId);
        }
        finally { updating = false; }
    }

    private CompanionDisplayLease? Find(uint id)
        => bridge.Snapshot.Leases.FirstOrDefault(x => x.CompanionId == id) is { LeaseId: > 0 } lease ? lease : null;

    private void MarkCapacityPendingFrom(CompanionDisplayLease firstWaiting)
    {
        // The queue is FIFO. Once the oldest unallocated request observes a confirmed
        // full native pool, every later unallocated request is waiting for the same
        // resource and should be counted in the player-facing status. Existing actors,
        // retiring leases, and failed evidence are excluded.
        foreach (var candidate in bridge.Snapshot.Leases)
        {
            if (candidate.LeaseId < firstWaiting.LeaseId) continue;
            var identity = (candidate.LeaseId, candidate.Generation);
            if (candidate.State == CompanionDisplayState.Pending && !created.Contains(identity))
            {
                capacityPending.Add(identity);
                deferredCreation.Remove(identity);
            }
        }
    }

    private void ClearCapacityPendingFrom(CompanionDisplayLease firstUnknown)
    {
        foreach (var identity in capacityPending.Where(x => x.Lease >= firstUnknown.LeaseId).ToArray())
            capacityPending.Remove(identity);
    }

    private void ReconcileSelection()
    {
        selectedLease = null;
        try
        {
            if (backend.ObserveSelected() is not { } observed) return;
            if (bridge.TryGetLease(observed.LeaseId, out var current)
                && current.State == CompanionDisplayState.Active
                && current.CompanionId == observed.CompanionId && current.Generation == observed.Generation)
                selectedLease = current;
        }
        catch { /* Missing target evidence only clears the UI selection; it never retires or retargets an actor. */ }
    }

    private void Fail(CompanionDisplayLease lease, string failure)
    {
        ClearSelection(lease);
        bridge.MarkFailed(lease.LeaseId, string.IsNullOrWhiteSpace(failure) ? "宠物暂时无法显示，请稍后重试。" : failure);
        // Failure evidence remains even when native cleanup succeeds.
        Retire(lease);
    }

    private void ClearSelection(CompanionDisplayLease lease)
    {
        if (selectedLease is { } selected && selected.LeaseId == lease.LeaseId
            && selected.Generation == lease.Generation) selectedLease = null;
    }

    private bool Retire(CompanionDisplayLease lease)
    {
        try
        {
            if (!backend.TryRetire(lease, out _)) return false;
            created.Remove((lease.LeaseId, lease.Generation));
            capacityPending.Remove((lease.LeaseId, lease.Generation));
            deferredCreation.Remove((lease.LeaseId, lease.Generation));
            waiting.Remove(lease.LeaseId);
            return true;
        }
        catch { return false; }
    }
}
