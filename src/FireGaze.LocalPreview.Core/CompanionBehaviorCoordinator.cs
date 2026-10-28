using System.Numerics;

namespace FireGaze.LocalPreview.Core;

/// <summary>
/// Owns only follow intent and attachment arbitration. No native object, animation,
/// floor collision, owner companion slot or global targeting state is mutated here.
/// </summary>
public sealed class CompanionBehaviorCoordinator
{
    private sealed class ActorState(long generation, int formationSlot)
    {
        public long Generation { get; } = generation;
        public int FormationSlot { get; } = formationSlot;
        public bool Observed { get; set; }
        public float IdleSeconds { get; set; }
        public bool AwaitingSummonPerch { get; set; } = true;
        public CompanionPerchSlot Perch { get; set; }
    }

    private readonly Dictionary<long, ActorState> states = [];
    private long? sceneEpoch;
    private Vector3 ownerPosition;
    private bool wasSuspended;

    /// <summary>Reserves the same stable slot later consumed by Update. A reservation
    /// contains no native actor and does not participate in crowd separation.</summary>
    public int ReserveFormationSlot(long epoch, long leaseId, long generation)
    {
        if (leaseId <= 0 || generation <= 0) throw new ArgumentOutOfRangeException(nameof(leaseId));
        if (sceneEpoch != epoch) { states.Clear(); sceneEpoch = epoch; }
        return GetOrReserveState(leaseId, generation).FormationSlot;
    }

    /// <summary>Releases an unsuccessful allocation without changing an observed actor.</summary>
    public void ReleaseUnobservedReservation(long epoch, long leaseId, long generation)
    {
        if (sceneEpoch == epoch && states.TryGetValue(leaseId, out var state)
            && state.Generation == generation && !state.Observed) states.Remove(leaseId);
    }

    private ActorState GetOrReserveState(long leaseId, long generation)
    {
        if (states.TryGetValue(leaseId, out var existing) && existing.Generation == generation) return existing;
        int slot = existing?.FormationSlot ?? FirstFreeFormationSlot();
        return states[leaseId] = new ActorState(generation, slot);
    }

    public IReadOnlyList<CompanionBehaviorIntent> Update(CompanionBehaviorFrame frame, IReadOnlyList<CompanionBehaviorActor> actors)
    {
        Validate(frame, actors);
        bool sceneChanged = sceneEpoch != frame.SceneEpoch;
        bool resetAll = sceneChanged || wasSuspended || Vector3.DistanceSquared(ownerPosition, frame.OwnerPosition) > 400;
        if (sceneChanged) states.Clear();
        sceneEpoch = frame.SceneEpoch;
        ownerPosition = frame.OwnerPosition;
        wasSuspended = frame.Suspended;

        var actorsByLease = actors.OrderBy(x => x.LeaseId).ToArray();
        var active = actorsByLease.Select(x => x.LeaseId).ToHashSet();
        foreach (long removed in states.Keys.Where(x => !active.Contains(x)).ToArray()) states.Remove(removed);

        var fresh = new HashSet<long>();
        foreach (var actor in actorsByLease)
        {
            var state = GetOrReserveState(actor.LeaseId, actor.Generation);
            if (!state.Observed) fresh.Add(actor.LeaseId);
            state.Observed = true;
        }

        var occupied = new HashSet<CompanionPerchSlot>();
        foreach (var actor in actorsByLease)
        {
            var state = states[actor.LeaseId];
            if (frame.Suspended || resetAll || !actor.Capabilities.NativePerchAvailable
                || !SlotAvailable(frame.AvailablePerchSlots, state.Perch)
                || (frame.OwnerMoving && actor.Capabilities.LeavePerchOnOwnerMovement))
                state.Perch = CompanionPerchSlot.None;
            if (state.Perch != CompanionPerchSlot.None) occupied.Add(state.Perch);
        }

        float spacing = CompanionFormationLayout.GetSpacing(actorsByLease.Select(x => x.Capabilities.BodyRadius).DefaultIfEmpty(0).Max());
        var result = new List<CompanionBehaviorIntent>(actors.Count);
        foreach (var actor in actorsByLease)
        {
            var state = states[actor.LeaseId];
            var capability = actor.Capabilities;
            bool reset = resetAll || fresh.Contains(actor.LeaseId);
            if (frame.Suspended)
            {
                state.IdleSeconds = 0;
                result.Add(Intent(actor, CompanionBehaviorMode.Suspended, actor.Position, actor.Position, true));
                continue;
            }
            state.IdleSeconds = frame.OwnerMoving ? 0 : state.IdleSeconds + frame.DeltaSeconds;
            bool requested = (state.AwaitingSummonPerch && capability.PerchTriggers.HasFlag(CompanionPerchTriggers.OnSummon))
                || (frame.BeckonTargetLeaseId == actor.LeaseId && capability.PerchTriggers.HasFlag(CompanionPerchTriggers.Beckon))
                || (state.IdleSeconds >= capability.IdleBeforePerchSeconds && capability.PerchTriggers.HasFlag(CompanionPerchTriggers.AutomaticIdle));
            if (state.Perch == CompanionPerchSlot.None && requested && capability.NativePerchAvailable
                && !(frame.OwnerMoving && capability.LeavePerchOnOwnerMovement))
            {
                state.Perch = FindFreePerch(frame.AvailablePerchSlots, occupied);
                if (state.Perch != CompanionPerchSlot.None)
                {
                    occupied.Add(state.Perch);
                    state.AwaitingSummonPerch = false;
                }
            }
            if (state.Perch != CompanionPerchSlot.None)
            {
                result.Add(new(actor.LeaseId, actor.Generation, CompanionBehaviorMode.Perched,
                    actor.Position, frame.OwnerPosition, reset, state.Perch));
                continue;
            }

            var destination = CompanionFormationLayout.GetDestination(state.FormationSlot, frame.OwnerPosition, frame.OwnerRotation, spacing);
            destination.Y = actor.Position.Y;
            var mode = CompanionBehaviorMode.Follow;
            if (frame.OwnerInWater)
            {
                if (capability.CanSwim && frame.WaterSurfaceHeight is { } water)
                {
                    destination.Y = water;
                    mode = CompanionBehaviorMode.Swim;
                }
                else if (capability.CanFly)
                {
                    destination.Y = (frame.WaterSurfaceHeight ?? frame.OwnerPosition.Y) + .8f;
                    mode = CompanionBehaviorMode.Hover;
                }
                else
                {
                    result.Add(Intent(actor, CompanionBehaviorMode.WaitingAtShore, actor.Position, actor.Position, reset));
                    continue;
                }
            }
            else if (reset) destination.Y = frame.OwnerPosition.Y;

            // The exact exponential avoids integration dependence on render frame rate.
            var position = reset ? destination : Vector3.Lerp(actor.Position, destination, 1 - MathF.Exp(-4 * frame.DeltaSeconds));
            if (Vector3.DistanceSquared(position, destination) < .000001f) position = destination;
            if (mode == CompanionBehaviorMode.Follow && Vector3.DistanceSquared(position, destination) < .000001f)
                mode = CompanionBehaviorMode.Idle;
            result.Add(Intent(actor, mode, position, destination, reset));
        }
        SeparateCrowd(result, actorsByLease);
        return result.AsReadOnly();
    }

    private int FirstFreeFormationSlot()
    {
        var occupied = states.Values.Select(x => x.FormationSlot).ToHashSet();
        int slot = 0;
        while (occupied.Contains(slot)) slot++;
        return slot;
    }

    private static CompanionBehaviorIntent Intent(CompanionBehaviorActor actor, CompanionBehaviorMode mode,
        Vector3 position, Vector3 destination, bool reset)
        => new(actor.LeaseId, actor.Generation, mode, position, destination, reset, CompanionPerchSlot.None);

    private static bool SlotAvailable(CompanionPerchSlots available, CompanionPerchSlot slot) => slot switch
    {
        CompanionPerchSlot.LeftShoulder => available.HasFlag(CompanionPerchSlots.LeftShoulder),
        CompanionPerchSlot.RightShoulder => available.HasFlag(CompanionPerchSlots.RightShoulder),
        CompanionPerchSlot.Head => available.HasFlag(CompanionPerchSlots.Head),
        _ => false,
    };

    private static CompanionPerchSlot FindFreePerch(CompanionPerchSlots available, HashSet<CompanionPerchSlot> occupied)
    {
        // A head socket describes the Lalafell layout; it is exclusive with shoulders.
        if (available.HasFlag(CompanionPerchSlots.Head))
            return occupied.Count == 0 ? CompanionPerchSlot.Head : CompanionPerchSlot.None;
        if (occupied.Contains(CompanionPerchSlot.Head)) return CompanionPerchSlot.None;
        foreach (var slot in new[] { CompanionPerchSlot.LeftShoulder, CompanionPerchSlot.RightShoulder })
            if (SlotAvailable(available, slot) && !occupied.Contains(slot)) return slot;
        return CompanionPerchSlot.None;
    }

    private static void SeparateCrowd(List<CompanionBehaviorIntent> result, CompanionBehaviorActor[] actors)
    {
        static bool CanMove(CompanionBehaviorIntent item)
            => item.Mode is CompanionBehaviorMode.Follow or CompanionBehaviorMode.Idle or CompanionBehaviorMode.Swim or CompanionBehaviorMode.Hover;
        var proposal = result.Select(x => x.TargetPosition).ToArray();
        var maximum = new float[result.Count];
        for (int i = 0; i < result.Count; i++)
        {
            if (!CanMove(result[i])) continue;
            for (int j = 0; j < result.Count; j++)
            {
                if (i == j || !CanMove(result[j])) continue;
                maximum[i] = Math.Max(maximum[i], actors[i].Capabilities.BodyRadius
                    + actors[j].Capabilities.BodyRadius + .2f);
            }
        }
        // Local separation only; the host still owns terrain projection and navigation.
        for (int pass = 0; pass < 64; pass++)
        {
            bool moved = false;
            for (int i = 0; i < result.Count; i++)
            for (int j = i + 1; j < result.Count; j++)
            {
                if (!CanMove(result[i]) || !CanMove(result[j])) continue;
                var delta = result[j].TargetPosition - result[i].TargetPosition;
                delta.Y = 0;
                float minimum = actors[i].Capabilities.BodyRadius + actors[j].Capabilities.BodyRadius + .2f;
                float distance = delta.Length();
                if (distance >= minimum - .0001f) continue;
                var direction = distance > .0001f ? delta / distance : Vector3.UnitX;
                var correction = direction * ((minimum - distance) * .5f);
                result[i] = result[i] with { TargetPosition = result[i].TargetPosition - correction };
                result[j] = result[j] with { TargetPosition = result[j].TargetPosition + correction };
                moved = true;
            }
            if (!moved) break;
        }

        // Preserve the separated short proposal while exposing its same-frame net
        // XZ change. MaximumSeparation is a plugin planning limit, not a qualified
        // native species envelope. FromIntent applies that bound exactly once.
        for (int i = 0; i < result.Count; i++)
        {
            var correction = result[i].TargetPosition - proposal[i];
            correction.Y = 0;
            if (correction.LengthSquared() <= .00000001f)
                correction = Vector3.Zero;
            result[i] = result[i] with
            {
                SeparationCorrection = correction,
                MaximumSeparation = maximum[i]
            };
        }
    }

    private static void Validate(CompanionBehaviorFrame frame, IReadOnlyList<CompanionBehaviorActor> actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        if (!float.IsFinite(frame.DeltaSeconds) || frame.DeltaSeconds < 0 || frame.DeltaSeconds > 5
            || !Finite(frame.OwnerPosition) || !float.IsFinite(frame.OwnerRotation)
            || (frame.WaterSurfaceHeight is { } height && !float.IsFinite(height)))
            throw new ArgumentOutOfRangeException(nameof(frame));
        var seen = new HashSet<long>();
        foreach (var actor in actors)
        {
            if (!seen.Add(actor.LeaseId)) throw new ArgumentException("Duplicate companion lease.", nameof(actors));
            var capability = actor.Capabilities;
            if (actor.LeaseId <= 0 || actor.Generation <= 0 || actor.CompanionId == 0 || !Finite(actor.Position)
                || capability is null || !float.IsFinite(capability.IdleBeforePerchSeconds) || capability.IdleBeforePerchSeconds < 0
                || !float.IsFinite(capability.BodyRadius) || capability.BodyRadius <= 0 || capability.BodyRadius > 10)
                throw new ArgumentException("Invalid companion behavior actor.", nameof(actors));
        }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
