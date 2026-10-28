namespace FireGaze.LocalPreview.Runtime;

[Flags]
public enum GroundedMotionLane { None = 0, Rider = 1, Mount = 2, Both = Rider | Mount }

public enum GroundedMotionKind { Unknown, Idle, Walk, Run, Sprint, Turn, Jump, Landing }

/// <summary>A classified source reading. It contains no target animation or gameplay state.</summary>
public readonly record struct GroundedMotionSample(bool Known, ushort TimelineId, GroundedMotionKind Kind);

/// <summary>
/// Reads only known on-foot ground timeline IDs. Unknown, mounted, swimming, flying,
/// emote and accessory clips produce no inferred locomotion.
/// </summary>
public static class GroundedMotionClassifier
{
    public static GroundedMotionSample Classify(ushort timelineId, bool sampleValid = true, bool standingPose = false)
    {
        if (!sampleValid) return default;
        var kind = timelineId switch
        {
            3 or 34 or 12001 or 4040 => GroundedMotionKind.Idle,
            7 or 8 or 35 or 36 or 12006 or 12007 => GroundedMotionKind.Turn,
            13 or 14 or 15 or 16 or 41 or 42 or 43 or 44 or 12002 or 12003 or 12004 or 12005
                or 4053 or 4046 => GroundedMotionKind.Walk,
            22 or 23 or 24 or 25 or 26 or 27 or 50 or 51 or 52 or 12008 or 12009 or 12010 or 4043 => GroundedMotionKind.Run,
            30 or 58 or 12011 => GroundedMotionKind.Sprint,
            31 or 32 or 59 or 60 or 12012 or 12013 => GroundedMotionKind.Jump,
            33 or 61 or 12014 => GroundedMotionKind.Landing,
            1052 or 1053 or 1054 or 1055 or 3123 or 3124 or 3125 or 3126 or 3181 or 3182 or 3183 or 3184
                or 7404 or 7405 or 7406 or 7407
                or 13330 or 13331 or 13332 or 13333 when standingPose => GroundedMotionKind.Idle,
            _ => GroundedMotionKind.Unknown,
        };
        return kind == GroundedMotionKind.Unknown ? default : new(true, timelineId, kind);
    }
}

/// <summary>
/// Identity is an opaque, nonzero ownership generation, not an object-table index.
/// MountId describes the owned display actor, never the real player's mount.
/// </summary>
public readonly record struct GroundedMotionTarget(ulong Identity, ushort MountId,
    ulong MountIdentity = 0, uint MountGeneration = 0, bool FlightPose = false,
    GroundedFlightPoseClips FlightClips = default);

/// <summary>Resident loop IDs verified against one mount's own animation pack.</summary>
public readonly record struct GroundedFlightPoseClips(ushort Idle, ushort Forward, ushort Backward, ushort Run)
{
    public static GroundedFlightPoseClips Standard => new(4040, 4053, 4046, 4043);
    public bool IsValid => Idle != 0 && Forward != 0 && Backward != 0 && Run != 0;
}

/// <summary>
/// Independent plans for the owned rider/root and its owned mount child. Zero playback
/// means no native call; a written zero override releases only this planner's held clip.
/// The runtime must revalidate both allocations before applying each lane.
/// </summary>
public readonly record struct GroundedMotionPlan(bool WriteBaseOverride, ushort BaseOverride, ushort PlayTimeline,
    bool WriteMountBaseOverride = false, ushort MountBaseOverride = 0, ushort PlayMountTimeline = 0);

/// <summary>
/// Pure edge/sustain planner; never calls native code or modifies movement, speed,
/// mode or mount containers. Mounted rider poses and canonical normal model clips
/// are separate: source on-foot clips must never be played on the mounted rider.
/// Only ground visual poses are selected; no physics or movement flags are copied.
/// </summary>
public sealed class GroundedPreviewMotion
{
    private ulong ownerIdentity;
    private Lane root;
    private Lane mount;

    /// <param name="emoteActuallyActive">
    /// Actual native/emote lifecycle evidence supplied by the runtime. Preview slot
    /// ownership or EmoteController.EmoteId alone is insufficient. Active emotes own
    /// all animation writes; the next known sample resumes after they finish.
    /// </param>
    /// <param name="currentBaseOverride">
    /// Current owned-root reading, used to avoid clearing another writer's override
    /// when source sampling becomes invalid or returns an unknown clip.
    /// </param>
    /// <param name="ownedLanes">
    /// Only these lanes may observe this sample or consume a playback/cleanup edge.
    /// None leaves all state untouched. Owner loss or active emotes revoke both lanes.
    /// </param>
    public GroundedMotionPlan Plan(GroundedMotionSample sample, GroundedMotionTarget nextTarget,
        bool emoteActuallyActive, ushort currentBaseOverride, ushort currentMountBaseOverride = 0,
        ushort? currentTimeline = null, ushort? currentMountTimeline = null,
        GroundedMotionLane ownedLanes = GroundedMotionLane.Both)
    {
        ownedLanes &= GroundedMotionLane.Both;
        if (ownedLanes == GroundedMotionLane.None) return default;
        if (nextTarget.Identity == 0 || emoteActuallyActive)
        {
            // Emotes and lost owners revoke all bookkeeping without issuing cleanup
            // against an emote writer or an allocation we no longer own.
            Reset();
            return default;
        }
        if (ownerIdentity != nextTarget.Identity)
        {
            Reset();
            ownerIdentity = nextTarget.Identity;
        }

        // Compare the configuration the selector actually uses: inactive flight
        // resources are irrelevant, and every invalid flight pack uses Standard.
        nextTarget = nextTarget with
        {
            FlightClips = !nextTarget.FlightPose ? default : nextTarget.FlightClips.IsValid
                ? nextTarget.FlightClips : GroundedFlightPoseClips.Standard,
        };
        bool ownsRoot = (ownedLanes & GroundedMotionLane.Rider) != 0;
        bool ownsMount = (ownedLanes & GroundedMotionLane.Mount) != 0;
        if (ownsRoot) root.ObserveTarget(nextTarget, mountLane: false);
        if (ownsMount) mount.ObserveTarget(nextTarget, mountLane: true);
        if (!sample.Known)
        {
            return Combine(ownsRoot ? root.Release(currentBaseOverride) : default,
                ownsMount ? mount.Release(currentMountBaseOverride) : default);
        }
        if (nextTarget.MountId == 0)
        {
            if (ownsMount) mount = default;
            var foot = !ownsRoot ? default : sample.Kind == GroundedMotionKind.Idle ? root.Release(currentBaseOverride)
                : root.Play(sample.TimelineId, sample.Kind == GroundedMotionKind.Landing, currentTimeline,
                    currentBaseOverride);
            return Combine(foot, default);
        }

        if (nextTarget.FlightPose)
        {
            // Appearance only: the owned model uses its resident flying posture clips.
            // Real source locomotion remains the sole input. No takeoff/landing events,
            // movement flags, height, velocity or flight permission are requested.
            // Forward resources are a fallback for models such as Ark lacking lateral
            // flight clips. Ground jumps keep the hovering visual pose at real height.
            var clips = nextTarget.FlightClips;
            ushort visualChild = sample.Kind switch
            {
                GroundedMotionKind.Walk => sample.TimelineId is 16 or 44 or 12005 ? clips.Backward : clips.Forward,
                GroundedMotionKind.Run or GroundedMotionKind.Sprint => clips.Run,
                _ => clips.Idle,
            };
            ushort visualRider = sample.Kind is GroundedMotionKind.Walk or GroundedMotionKind.Run or GroundedMotionKind.Sprint
                ? (ushort)4048 : (ushort)4047;
            return Combine(ownsRoot ? root.Play(visualRider, false, currentTimeline, currentBaseOverride, visualChild) : default,
                ownsMount ? mount.Play(visualChild, false, currentMountTimeline, currentMountBaseOverride, visualChild) : default);
        }

        // Current CN ActionTimeline rows: mount_idle 166, mount_run 167,
        // mount_walk 6709, mount_jump 6710, mount_turn_loop_l/r 9306/9307.
        // Walk/run agree with the native ground movement selector. Turn/jump use
        // these ground-only visual poses from accepted source animation phases;
        // they do not reproduce the engine's physics-dependent rider selector.
        ushort rider = sample.Kind switch
        {
            GroundedMotionKind.Idle or GroundedMotionKind.Landing => 166,
            GroundedMotionKind.Walk => 6709,
            GroundedMotionKind.Jump => 6710,
            GroundedMotionKind.Turn => sample.TimelineId is 8 or 36 or 12007 ? (ushort)9307 : (ushort)9306,
            _ => 167,
        };
        ushort child = sample.TimelineId switch
        {
            // Reviewed mount PAPs do not consistently contain normal turn clips.
            // Keep the child's idle loop while the rider uses its ground turn pose.
            7 or 8 or 35 or 36 or 12006 or 12007 => 3,
            13 or 41 or 12002 or 4053 => 13, 14 or 42 or 12003 => 14,
            15 or 43 or 12004 => 15, 16 or 44 or 12005 or 4046 => 16,
            23 or 51 or 12009 => 23, 24 or 52 or 12010 => 24,
            31 or 59 or 12012 => 31, 32 or 60 or 12013 => 32,
            33 or 61 or 12014 => 33,
            _ => sample.Kind == GroundedMotionKind.Idle ? (ushort)3 : (ushort)22,
        };
        // Source run-start clips are one-shots; sustaining them freezes a mount.
        // Sprint also uses normal/run as a conservative model-resource fallback.
        // Mounted TimelineContainer.Update compares the root's BaseOverride with
        // the CHILD's base lane. Pinning the rider clip here (167 vs child 22)
        // makes native selection restart that posture on every update. Playback
        // still targets the rider; its native base request follows the child.
        ushort nativeBase = sample.Kind == GroundedMotionKind.Landing ? (ushort)0 : child;
        return Combine(ownsRoot ? root.Play(rider, false, currentTimeline, currentBaseOverride, nativeBase) : default,
            ownsMount ? mount.Play(child, sample.Kind == GroundedMotionKind.Landing, currentMountTimeline,
                currentMountBaseOverride, child) : default);
    }

    private static GroundedMotionPlan Combine(LanePlan root, LanePlan mount) =>
        new(root.Write, root.Override, root.Play, mount.Write, mount.Override, mount.Play);

    private readonly record struct LanePlan(bool Write, ushort Override, ushort Play);

    private struct Lane
    {
        private GroundedMotionTarget target;
        private bool hasTarget;
        private bool previous;
        private ushort timeline;
        private ushort ownedPin;

        public void ObserveTarget(GroundedMotionTarget nextTarget, bool mountLane)
        {
            if (hasTarget && target != nextTarget)
            {
                if (mountLane && (target.MountIdentity != nextTarget.MountIdentity
                    || target.MountGeneration != nextTarget.MountGeneration))
                {
                    // A replaced child must never inherit the old allocation's pin.
                    ForgetMotion();
                }
                else
                {
                    // Same allocation, new configuration: keep its pin until this
                    // lane can apply a replacement or release on its own boundary.
                    previous = false;
                }
            }
            target = nextTarget;
            hasTarget = true;
        }

        public LanePlan Play(ushort next, bool terminal, ushort? observedTimeline,
            ushort currentBaseOverride, ushort? nativeBase = null)
        {
            // Native Character.Update can overwrite a display rider after Framework.Update.
            // Repair a changed resident loop only when the native base selector no longer
            // carries the pin owned by this lane. A mounted update can publish a transient
            // timeline while preserving that pin; replaying on every such observation
            // restarts the rider and produces visible locomotion twitching. One-shot landing
            // remains edge-only even after the native clip ends.
            bool basePinLost = nativeBase is { } expected && expected != 0 && currentBaseOverride != expected;
            bool edge = !previous || timeline != next
                || (!terminal && basePinLost && observedTimeline is { } observed && observed != next);
            timeline = next;
            previous = true;
            ownedPin = terminal ? (ushort)0 : nativeBase ?? next;
            return terminal ? edge ? new(true, 0, next) : default : new(true, ownedPin, edge ? next : (ushort)0);
        }

        public LanePlan Release(ushort current)
        {
            bool release = ownedPin != 0 && current == ownedPin;
            ForgetMotion();
            return release ? new(true, 0, 0) : default;
        }

        private void ForgetMotion()
        {
            previous = false;
            timeline = 0;
            ownedPin = 0;
        }
    }

    /// <summary>Forgets bookkeeping only. Never applies cleanup to an obsolete actor.</summary>
    public void Reset()
    {
        ownerIdentity = 0;
        root = default;
        mount = default;
    }
}
