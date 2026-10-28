namespace FireGaze.LocalPreview.Core;

/// <summary>Actions that can be requested for several locally displayed companions.</summary>
public enum CompanionBatchInteractionKind
{
    Beckon,
}

/// <summary>
/// Detached actor identity supplied by the Framework host when a batch action is consumed.
/// The state and generation are checked before an action is released; no native object is
/// retained by this queue.
/// </summary>
public readonly record struct CompanionBatchInteractionActor(
    long LeaseId,
    long Generation,
    CompanionDisplayState State);

/// <summary>One immutable batch interaction request.</summary>
public readonly record struct CompanionBatchInteractionRequest(
    long RequestId,
    long SceneEpoch,
    long LeaseId,
    long Generation,
    CompanionBatchInteractionKind Kind);

/// <summary>
/// Framework-neutral FIFO for multi-companion actions. The host calls TryTakeForFrame once
/// per Framework update, so a large selection never turns into same-frame native work. A
/// request is released only when its lease is still active at the same generation and scene.
/// </summary>
public sealed class CompanionBatchInteractionQueue
{
    private readonly Queue<CompanionBatchInteractionRequest> pending = new();
    private readonly HashSet<(long LeaseId, long Generation, CompanionBatchInteractionKind Kind)> keys = [];
    private long nextRequestId;

    public int PendingCount => pending.Count;

    /// <summary>Adds one action unless the exact lease generation is already queued.</summary>
    public bool EnqueueBeckon(long sceneEpoch, long leaseId, long generation)
        => Enqueue(sceneEpoch, leaseId, generation, CompanionBatchInteractionKind.Beckon);

    public bool Enqueue(long sceneEpoch, long leaseId, long generation,
        CompanionBatchInteractionKind kind)
    {
        if (sceneEpoch <= 0 || leaseId <= 0 || generation <= 0 || !Enum.IsDefined(kind)) return false;
        var key = (leaseId, generation, kind);
        if (!keys.Add(key)) return false;
        pending.Enqueue(new(checked(++nextRequestId), sceneEpoch, leaseId, generation, kind));
        return true;
    }

    /// <summary>
    /// Removes stale requests until one valid action is found, then returns exactly one.
    /// Requests in another scene or for a non-active/replaced lease are discarded.
    /// </summary>
    public bool TryTakeForFrame(long sceneEpoch, IReadOnlyList<CompanionBatchInteractionActor> actors,
        out CompanionBatchInteractionRequest request)
    {
        ArgumentNullException.ThrowIfNull(actors);
        request = default;
        if (sceneEpoch <= 0) return false;

        var active = new Dictionary<long, CompanionBatchInteractionActor>();
        var ambiguous = new HashSet<long>();
        foreach (var actor in actors)
        {
            if (actor.LeaseId <= 0 || actor.Generation <= 0 || actor.State != CompanionDisplayState.Active)
                continue;
            if (!active.TryAdd(actor.LeaseId, actor)) ambiguous.Add(actor.LeaseId);
        }
        foreach (var leaseId in ambiguous) active.Remove(leaseId);

        while (pending.Count != 0)
        {
            var candidate = pending.Dequeue();
            keys.Remove((candidate.LeaseId, candidate.Generation, candidate.Kind));
            if (candidate.SceneEpoch != sceneEpoch
                || !active.TryGetValue(candidate.LeaseId, out var actor)
                || actor.Generation != candidate.Generation) continue;
            request = candidate;
            return true;
        }
        return false;
    }

    /// <summary>Cancels all pending actions at a lifecycle boundary.</summary>
    public int CancelAll()
    {
        var count = pending.Count;
        pending.Clear();
        keys.Clear();
        return count;
    }

    /// <summary>Cancels only actions tied to one lease generation.</summary>
    public int CancelLease(long leaseId, long? generation = null)
    {
        if (leaseId <= 0) return 0;
        var kept = new Queue<CompanionBatchInteractionRequest>(pending.Count);
        var removed = 0;
        while (pending.Count != 0)
        {
            var request = pending.Dequeue();
            if (request.LeaseId == leaseId && (!generation.HasValue || request.Generation == generation.Value))
            {
                keys.Remove((request.LeaseId, request.Generation, request.Kind));
                removed++;
            }
            else kept.Enqueue(request);
        }
        while (kept.Count != 0) pending.Enqueue(kept.Dequeue());
        return removed;
    }
}
