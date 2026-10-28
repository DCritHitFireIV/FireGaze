namespace FireGaze.LocalPreview.Core;

/// <summary>Lifecycle of one independent companion display lease.</summary>
public enum CompanionDisplayState
{
    Pending,
    Active,
    Failed,
    Retiring,
    /// <summary>Previously shown native actor retained while its current draw is not visible.</summary>
    NativeHidden,
}

/// <summary>Reasons that an actor lease must leave the display bridge.</summary>
public enum CompanionDisplayRetireReason
{
    TerritoryChange,
    Logout,
    PluginReload,
    DeviceReset,
    UserCancelled,
    Replaced,
    Combat,
}

/// <summary>A native-free request result. Lease and generation are opaque to the host.</summary>
public readonly record struct CompanionDisplayAdmission(
    bool Accepted,
    long LeaseId,
    long Generation,
    long? ExistingLeaseId,
    string Reason)
{
    public static CompanionDisplayAdmission Rejected(string reason, long? existingLeaseId = null)
        => new(false, 0, 0, existingLeaseId, reason);
}

/// <summary>
/// A detached view of one companion lease. It contains no game pointers and is safe to
/// hand to UI, persistence or a future native adapter. A generation changes on retry so
/// late callbacks from a retired actor cannot mutate a replacement actor.
/// </summary>
public readonly record struct CompanionDisplayLease(
    long LeaseId,
    uint CompanionId,
    long Generation,
    CompanionDisplayState State,
    string? FailureReason,
    CompanionDisplayRetireReason? RetireReason);

/// <summary>Detached bridge state used by diagnostics and UI.</summary>
public sealed class CompanionDisplaySnapshot
{
    internal CompanionDisplaySnapshot(long epoch, int maximumActors, IEnumerable<CompanionDisplayLease> leases)
    {
        Epoch = epoch;
        MaximumActors = maximumActors;
        Leases = Array.AsReadOnly(leases.ToArray());
    }

    public long Epoch { get; }
    public int MaximumActors { get; }
    public IReadOnlyList<CompanionDisplayLease> Leases { get; }
}

/// <summary>
/// Detached queue counters for the player-facing companion workbench and diagnostics.
/// The counts are derived from the same lease snapshot consumed by the native session;
/// they never imply that a Pending lease has a drawable or targetable actor.
/// </summary>
public readonly record struct CompanionDisplayQueueSnapshot(
    int RequestedCount,
    int ActiveCount,
    int PendingCount,
    int CapacityPendingCount,
    int DeferredCreationCount,
    int NativeHiddenCount,
    int RetiringCount,
    int FailedCount)
{
    public bool HasUnresolvedRequests
        => PendingCount != 0 || NativeHiddenCount != 0 || RetiringCount != 0;
}

/// <summary>
/// Contract for a future multi-actor companion renderer. Implementations must keep actor
/// ownership outside this contract: no native pointers, object lookups or game calls appear
/// here. The current FireGaze host does not attach this bridge to ProductionAdmission.
/// </summary>
public interface ICompanionDisplayBridge
{
    int MaximumActors { get; }
    void SetMaximumActors(int maximumActors);
    CompanionDisplaySnapshot Snapshot { get; }

    CompanionDisplayAdmission Request(uint companionId, bool allowDuplicate = false);
    bool MarkActive(long leaseId, long generation);
    bool MarkFailed(long leaseId, string failureReason);
    bool Retry(long leaseId);
    bool BeginTeardown(long leaseId, CompanionDisplayRetireReason reason);
    bool CompleteTeardown(long leaseId);
    int RetireAll(CompanionDisplayRetireReason reason);
}

/// <summary>
/// Deterministic, native-free state machine for independent companion actor leases.
/// A future adapter can consume each Pending lease and report Active/Failed, then tear
/// down and recreate each lease independently. Until such an adapter exists this class
/// is only a contract fixture and is not used by the live preview runtime.
/// </summary>
public sealed class CompanionDisplayBridge : ICompanionDisplayBridge
{
    private sealed class Lease
    {
        public required long LeaseId { get; init; }
        public required uint CompanionId { get; init; }
        public required long Generation { get; set; }
        public CompanionDisplayState State { get; set; }
        public string? FailureReason { get; set; }
        public CompanionDisplayRetireReason? RetireReason { get; set; }

        public CompanionDisplayLease View()
            => new(LeaseId, CompanionId, Generation, State, FailureReason, RetireReason);
    }

    private readonly Dictionary<long, Lease> leases = [];
    private long nextLeaseId;
    private long nextGeneration;
    private long epoch;

    public CompanionDisplayBridge(int maximumActors)
    {
        if (maximumActors < 0) throw new ArgumentOutOfRangeException(nameof(maximumActors));
        MaximumActors = maximumActors;
    }

    /// <summary>Zero removes the application limit; native allocation may still fail.</summary>
    public int MaximumActors { get; private set; }

    /// <summary>Only future requests use the new limit. Existing leases retain ownership.</summary>
    public void SetMaximumActors(int maximumActors)
    {
        if (maximumActors < 0) throw new ArgumentOutOfRangeException(nameof(maximumActors));
        if (MaximumActors == maximumActors) return;
        MaximumActors = maximumActors;
        Touch();
    }

    public CompanionDisplaySnapshot Snapshot
        => new(epoch, MaximumActors, leases.Values.OrderBy(x => x.LeaseId).Select(x => x.View()));

    internal bool TryGetLease(long leaseId, out CompanionDisplayLease lease)
    {
        if (leases.TryGetValue(leaseId, out var current))
        {
            lease = current.View();
            return true;
        }
        lease = default;
        return false;
    }

    public CompanionDisplayAdmission Request(uint companionId, bool allowDuplicate = false)
    {
        if (companionId == 0) return CompanionDisplayAdmission.Rejected("companion id must be positive");

        // Keep failed leases visible so their evidence is diagnosable and callers use Retry
        // explicitly. Duplicate admission is available only through the explicit caller
        // opt-in; each accepted duplicate receives its own lease and generation.
        var duplicate = leases.Values.FirstOrDefault(x => x.CompanionId == companionId);
        if (!allowDuplicate && duplicate is not null)
            return CompanionDisplayAdmission.Rejected("companion already has a lease", duplicate.LeaseId);

        // Retiring and failed leases retain their allocation until the host acknowledges
        // teardown; this prevents a late callback from consuming a newly created slot.
        if (MaximumActors > 0 && leases.Count >= MaximumActors)
            return CompanionDisplayAdmission.Rejected("maximum companion display actors reached");

        var lease = new Lease
        {
            LeaseId = checked(++nextLeaseId),
            CompanionId = companionId,
            Generation = checked(++nextGeneration),
            State = CompanionDisplayState.Pending,
        };
        leases.Add(lease.LeaseId, lease);
        Touch();
        return new(true, lease.LeaseId, lease.Generation, null, string.Empty);
    }

    public bool MarkActive(long leaseId, long generation)
    {
        if (!leases.TryGetValue(leaseId, out var lease)
            || lease.State is not (CompanionDisplayState.Pending or CompanionDisplayState.NativeHidden)
            || lease.Generation != generation) return false;
        lease.State = CompanionDisplayState.Active;
        lease.FailureReason = null;
        lease.RetireReason = null;
        Touch();
        return true;
    }

    /// <summary>Readiness can temporarily disappear while the same native actor reloads its
    /// draw resources. Keep the lease and generation, but remove it from the visible Active
    /// set until the backend observes it again.</summary>
    public bool MarkPending(long leaseId, long generation)
    {
        if (!leases.TryGetValue(leaseId, out var lease)
            || lease.State is not (CompanionDisplayState.Active or CompanionDisplayState.NativeHidden)
            || lease.Generation != generation) return false;
        lease.State = CompanionDisplayState.Pending;
        Touch();
        return true;
    }

    /// <summary>Retain ownership without claiming visibility or permitting interaction.</summary>
    public bool MarkNativeHidden(long leaseId, long generation)
    {
        if (!leases.TryGetValue(leaseId, out var lease)
            || lease.State is not (CompanionDisplayState.Pending or CompanionDisplayState.Active)
            || lease.Generation != generation) return false;
        lease.State = CompanionDisplayState.NativeHidden;
        Touch();
        return true;
    }

    public bool MarkFailed(long leaseId, string failureReason)
    {
        if (!leases.TryGetValue(leaseId, out var lease)
            || lease.State is not (CompanionDisplayState.Pending or CompanionDisplayState.Active or CompanionDisplayState.NativeHidden)
            || string.IsNullOrWhiteSpace(failureReason)) return false;
        lease.State = CompanionDisplayState.Failed;
        lease.FailureReason = failureReason.Trim();
        lease.RetireReason = null;
        Touch();
        return true;
    }

    public bool Retry(long leaseId)
    {
        if (!leases.TryGetValue(leaseId, out var lease) || lease.State != CompanionDisplayState.Failed)
            return false;
        lease.Generation = checked(++nextGeneration);
        lease.State = CompanionDisplayState.Pending;
        lease.FailureReason = null;
        lease.RetireReason = null;
        Touch();
        return true;
    }

    public bool BeginTeardown(long leaseId, CompanionDisplayRetireReason reason)
    {
        if (!leases.TryGetValue(leaseId, out var lease)
            || lease.State is CompanionDisplayState.Retiring
            || !Enum.IsDefined(reason)) return false;
        lease.State = CompanionDisplayState.Retiring;
        lease.RetireReason = reason;
        Touch();
        return true;
    }

    public bool CompleteTeardown(long leaseId)
    {
        if (!leases.TryGetValue(leaseId, out var lease) || lease.State != CompanionDisplayState.Retiring)
            return false;
        leases.Remove(leaseId);
        Touch();
        return true;
    }

    public int RetireAll(CompanionDisplayRetireReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        var count = 0;
        foreach (var lease in leases.Values)
        {
            if (lease.State == CompanionDisplayState.Retiring) continue;
            lease.State = CompanionDisplayState.Retiring;
            lease.RetireReason = reason;
            count++;
        }
        if (count != 0) Touch();
        return count;
    }

    private void Touch() => epoch = checked(epoch + 1);
}
