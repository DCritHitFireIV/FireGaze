namespace FireGaze.LocalPreview.Runtime;

public enum GroundedMotionKind { Unknown, Idle, Walk, Run, Sprint, Turn, Jump, Landing }

/// <summary>A classified source reading. It contains no target animation or gameplay state.</summary>
public readonly record struct GroundedMotionSample(bool Known, ushort TimelineId, GroundedMotionKind Kind);

/// <summary>
/// Reads only known on-foot ground timeline IDs. Unknown, mounted, swimming, flying,
/// emote and accessory clips produce no inferred locomotion.
/// </summary>
public static class GroundedMotionClassifier
{
    public static GroundedMotionSample Classify(ushort timelineId, bool sampleValid = true)
    {
        if (!sampleValid) return default;
        var kind = timelineId switch
        {
            3 or 34 => GroundedMotionKind.Idle,
            7 or 8 or 35 or 36 => GroundedMotionKind.Turn,
            13 or 14 or 15 or 16 or 41 or 42 or 43 or 44 => GroundedMotionKind.Walk,
            22 or 23 or 24 or 25 or 26 or 27 or 50 or 51 or 52 => GroundedMotionKind.Run,
            30 or 58 => GroundedMotionKind.Sprint,
            31 or 32 or 59 or 60 => GroundedMotionKind.Jump,
            33 or 61 => GroundedMotionKind.Landing,
            _ => GroundedMotionKind.Unknown,
        };
        return kind == GroundedMotionKind.Unknown ? default : new(true, timelineId, kind);
    }
}

/// <summary>
/// Identity is an opaque, nonzero ownership generation, not an object-table index.
/// MountId describes the owned display actor, never the real player's mount.
/// </summary>
public readonly record struct GroundedMotionTarget(ulong Identity, ushort MountId);

/// <summary>
/// A plan for the owned RIDER/ROOT only. PlayTimeline == 0 means no native playback.
/// Writing BaseOverride = 0 releases the planner's held clip. Native application,
/// pointer revalidation and ownership checks belong to the runtime integration.
/// </summary>
public readonly record struct GroundedMotionPlan(bool WriteBaseOverride, ushort BaseOverride, ushort PlayTimeline);

/// <summary>
/// Pure edge/sustain planner; never calls native code or modifies movement, speed,
/// mode, mount containers, flight or swimming state. Ground clips on a mounted root
/// are an HMS-derived candidate: the current client's mounted rendering still needs
/// visual acceptance. These IDs must not be sent directly to MountObject.
/// </summary>
public sealed class GroundedPreviewMotion
{
    private GroundedMotionTarget target;
    private bool hasTarget;
    private bool hasPrevious;
    private ushort previousTimeline;
    private ushort ownedPin;

    /// <param name="emoteActuallyActive">
    /// Actual native/emote lifecycle evidence supplied by the runtime. Preview slot
    /// ownership or EmoteController.EmoteId alone is insufficient. Active emotes own
    /// all animation writes; the next known sample resumes after they finish.
    /// </param>
    /// <param name="currentBaseOverride">
    /// Current owned-root reading, used to avoid clearing another writer's override
    /// when source sampling becomes invalid or returns an unknown clip.
    /// </param>
    public GroundedMotionPlan Plan(GroundedMotionSample sample, GroundedMotionTarget nextTarget,
        bool emoteActuallyActive, ushort currentBaseOverride)
    {
        if (nextTarget.Identity == 0) { Reset(); return default; }
        if (!hasTarget || target.Identity != nextTarget.Identity)
        {
            Reset();
            hasTarget = true;
        }
        else if (target.MountId != nextTarget.MountId)
        {
            // A new mount on the same actor needs a fresh playback edge. Its previous
            // pin can still be released when switching back to on-foot natural idle.
            hasPrevious = false;
        }
        target = nextTarget;

        if (emoteActuallyActive)
        {
            // No cleanup write may interrupt the emote. Relinquish the pin claim and
            // force fresh resolution from the current sample once the emote is over.
            hasPrevious = false;
            ownedPin = 0;
            return default;
        }
        if (!sample.Known)
        {
            hasPrevious = false;
            return ReleaseOwnedPin(currentBaseOverride);
        }
        if (sample.Kind == GroundedMotionKind.Idle && nextTarget.MountId == 0)
        {
            hasPrevious = false;
            return ReleaseOwnedPin(currentBaseOverride);
        }

        ushort timeline = sample.Kind == GroundedMotionKind.Idle ? (ushort)166 : sample.TimelineId;
        bool edge = !hasPrevious || previousTimeline != timeline;
        previousTimeline = timeline;
        hasPrevious = true;
        if (sample.Kind == GroundedMotionKind.Landing)
        {
            ownedPin = 0;
            // Fire the terminal landing once and let native animation finish. Pinning
            // it or replaying repeated source frames would repeat/freeze the squat.
            return edge ? new(true, 0, timeline) : default;
        }

        ownedPin = timeline;
        return new(true, timeline, edge ? timeline : (ushort)0);
    }

    private GroundedMotionPlan ReleaseOwnedPin(ushort currentBaseOverride)
    {
        bool release = ownedPin != 0 && currentBaseOverride == ownedPin;
        ownedPin = 0;
        return release ? new(true, 0, 0) : default;
    }

    /// <summary>Forgets bookkeeping only. Never applies cleanup to an obsolete actor.</summary>
    public void Reset()
    {
        target = default;
        hasTarget = false;
        hasPrevious = false;
        previousTimeline = 0;
        ownedPin = 0;
    }
}
