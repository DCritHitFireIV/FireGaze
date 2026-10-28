namespace FireGaze.LocalPreview.Core;

/// <summary>
/// Keeps one explicit UI request attached to one native lease. Companion IDs are
/// catalog identities and can repeat in the same-species mode, so a lookup must use
/// the request ID and the lease generation rather than the companion ID alone.
/// </summary>
public sealed class CompanionTrialLeaseMap
{
    private readonly Dictionary<long, CompanionDisplayLease> leasesByRequest = [];

    public IReadOnlyDictionary<long, CompanionDisplayLease> Bindings
        => leasesByRequest;

    public void Bind(CompanionTrialRequest request, CompanionDisplayLease lease)
    {
        if (request.RequestId <= 0) throw new ArgumentOutOfRangeException(nameof(request));
        if (lease.LeaseId <= 0 || lease.Generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(lease));
        if (request.CompanionId != lease.CompanionId)
            throw new ArgumentException("Request and lease companion IDs must match.", nameof(lease));
        leasesByRequest[request.RequestId] = lease;
    }

    public bool TryGetLease(long requestId, out CompanionDisplayLease lease)
        => leasesByRequest.TryGetValue(requestId, out lease);

    public bool TryGetRequest(CompanionDisplayLease lease, out long requestId)
    {
        foreach (var pair in leasesByRequest)
        {
            var bound = pair.Value;
            if (bound.LeaseId == lease.LeaseId && bound.Generation == lease.Generation
                && bound.CompanionId == lease.CompanionId)
            {
                requestId = pair.Key;
                return true;
            }
        }

        requestId = 0;
        return false;
    }

    public bool Remove(long requestId)
        => leasesByRequest.Remove(requestId);

    public void Clear()
        => leasesByRequest.Clear();

    /// <summary>
    /// Drops stale request/lease identities and associates newly-created leases with
    /// retained requests, including a failed instance awaiting an exact retry.
    /// When a map change replaces every lease, request order is
    /// the only stable identity available for repeated same-species instances.
    /// </summary>
    public void Reconcile(
        IReadOnlyList<CompanionTrialRequest> requests,
        IReadOnlyList<CompanionDisplayLease> leases)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(leases);

        var requestById = requests.ToDictionary(x => x.RequestId);
        var liveLeases = leases
            .Where(IsBindable)
            .OrderBy(x => x.LeaseId)
            .ToArray();
        var liveLeaseKeys = liveLeases
            .Select(x => (x.LeaseId, x.Generation))
            .ToHashSet();
        var claimedLeaseKeys = new HashSet<(long LeaseId, long Generation)>();

        foreach (var requestId in leasesByRequest.Keys.ToArray())
        {
            if (!requestById.TryGetValue(requestId, out var request)
                || !leasesByRequest.TryGetValue(requestId, out var lease)
                || !liveLeaseKeys.Contains((lease.LeaseId, lease.Generation))
                || lease.CompanionId != request.CompanionId)
            {
                leasesByRequest.Remove(requestId);
                continue;
            }

            claimedLeaseKeys.Add((lease.LeaseId, lease.Generation));
        }

        foreach (var request in requests.OrderBy(x => x.RequestId))
        {
            if (leasesByRequest.ContainsKey(request.RequestId)) continue;
            var replacement = liveLeases.FirstOrDefault(lease =>
                lease.CompanionId == request.CompanionId
                && !claimedLeaseKeys.Contains((lease.LeaseId, lease.Generation)));
            if (replacement.LeaseId == 0) continue;
            leasesByRequest[request.RequestId] = replacement;
            claimedLeaseKeys.Add((replacement.LeaseId, replacement.Generation));
        }
    }

    private static bool IsBindable(CompanionDisplayLease lease)
        => lease.State is CompanionDisplayState.Pending
            or CompanionDisplayState.Active
            or CompanionDisplayState.Failed
            or CompanionDisplayState.NativeHidden;
}
