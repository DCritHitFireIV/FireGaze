using System.Numerics;

namespace FireGaze.LocalPreview.Core;

[Flags]
public enum CompanionPerchTriggers { None = 0, Beckon = 1, AutomaticIdle = 2, OnSummon = 4 }
[Flags]
public enum CompanionPerchSlots { None = 0, LeftShoulder = 1, RightShoulder = 2, Shoulders = 3, Head = 4 }
public enum CompanionPerchSlot { None, LeftShoulder, RightShoulder, Head }
public enum CompanionBehaviorMode { Idle, Follow, Perched, Swim, Hover, WaitingAtShore, Suspended }

/// <summary>
/// Values must come from reviewed game data or observed native behavior. In particular,
/// Companion.Special is not decoded here; unknown abilities remain disabled. The host
/// must separately qualify the native attachment operation before enabling it.
/// </summary>
public sealed record CompanionBehaviorCapabilities(
    CompanionPerchTriggers PerchTriggers = CompanionPerchTriggers.None,
    bool LeavePerchOnOwnerMovement = false,
    bool CanSwim = false,
    bool CanFly = false,
    bool NativePerchAvailable = false,
    float IdleBeforePerchSeconds = 20,
    float BodyRadius = .35f);

public readonly record struct CompanionBehaviorFrame(
    long SceneEpoch, float DeltaSeconds, Vector3 OwnerPosition, float OwnerRotation,
    bool OwnerMoving, bool OwnerInWater, float? WaterSurfaceHeight,
    long? BeckonTargetLeaseId = null,
    CompanionPerchSlots AvailablePerchSlots = CompanionPerchSlots.None,
    bool Suspended = false);

public readonly record struct CompanionBehaviorActor(
    long LeaseId, long Generation, uint CompanionId, Vector3 Position,
    CompanionBehaviorCapabilities Capabilities);

/// <summary>
/// Detached intent only. TargetPosition is this frame's smoothed proposed position;
/// Destination is the formation goal. The native adapter must project onto valid
/// terrain and select real movement/attachment animations; this is not a navmesh.
/// SeparationCorrection is the net XZ crowd change from this frame's proposal;
/// MaximumSeparation bounds plugin goal perturbation, never native physical clearance.
/// </summary>
public readonly record struct CompanionBehaviorIntent(
    long LeaseId, long Generation, CompanionBehaviorMode Mode,
    Vector3 TargetPosition, Vector3 Destination, bool ResetMotion,
    CompanionPerchSlot AttachmentSlot,
    Vector3 SeparationCorrection = default, float MaximumSeparation = 0);
