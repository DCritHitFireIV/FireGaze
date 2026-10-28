using System.Numerics;

namespace FireGaze.LocalPreview.Core;

/// <summary>Exact detached source/scene identity. No address or game object escapes the Framework adapter.</summary>
public readonly record struct CompanionExecutionContext(long SceneEpoch, ulong SourceID, uint SourceGeneration);
public readonly record struct CompanionExecutionKey(
    long SceneEpoch, ulong SourceID, uint SourceGeneration, long LeaseID, long LeaseGeneration)
{
    public CompanionExecutionContext Context => new(SceneEpoch, SourceID, SourceGeneration);
}

public enum CompanionExecutionOrigin { PlayerInteraction, NativeAutomatic }
public enum CompanionExecutionState { Reserved, Requested, Attached, Detaching, Unconfirmed, Following, Refused, Retiring, Retired }
public enum CompanionExecutionSpawnState { Unknown, Pending, Ready, Lost, PhysicallyRetired }

/// <summary>
/// Desired behavior only. ProposedPosition preserves the planner's interpolated output;
/// FormationGoal and the bounded horizontal correction are the actual goal-space request.
/// A native adapter must still qualify movement, terrain, species and attachment behavior.
/// </summary>
public sealed record CompanionExecutionPlan(
    CompanionExecutionKey Key, CompanionBehaviorMode Mode, Vector3 ProposedPosition,
    Vector3 FormationGoal, Vector3 SeparationCorrection, float MaximumSeparation,
    bool ResetMotion, CompanionPerchSlot DesiredAttachment,
    long RequestSequence, TimeSpan Deadline, CompanionExecutionOrigin Origin)
{
    public Vector3 CorrectedGoal => FormationGoal + SeparationCorrection;

    public static CompanionExecutionPlan FromIntent(CompanionExecutionKey key, CompanionBehaviorIntent intent,
        Vector3 separationCorrection, float maximumSeparation, long requestSequence, TimeSpan deadline,
        CompanionExecutionOrigin origin = CompanionExecutionOrigin.PlayerInteraction)
    {
        if (intent.LeaseId != key.LeaseID || intent.Generation != key.LeaseGeneration
            || !Finite(separationCorrection) || separationCorrection.Y != 0
            || !float.IsFinite(maximumSeparation) || maximumSeparation < 0)
            throw new ArgumentException("Intent identity or separation envelope is invalid.");
        // The executor receives a correction in goal space, not TargetPosition - Position.
        // Terrain projection and final native species offsets remain the native adapter's job.
        var length = separationCorrection.Length();
        if (!float.IsFinite(length)) throw new ArgumentException("Separation magnitude is invalid.");
        if (length > maximumSeparation) separationCorrection *= maximumSeparation / length;
        var plan = new CompanionExecutionPlan(key, intent.Mode, intent.TargetPosition, intent.Destination,
            separationCorrection, maximumSeparation, intent.ResetMotion, intent.AttachmentSlot,
            requestSequence, deadline, origin);
        if (!plan.IsValid()) throw new ArgumentException("Execution plan is invalid.");
        return plan;
    }

    internal bool IsValid()
        => ValidKey(Key) && Enum.IsDefined(Mode) && Enum.IsDefined(Origin) && Enum.IsDefined(DesiredAttachment)
            && Finite(ProposedPosition) && Finite(FormationGoal) && Finite(SeparationCorrection) && Finite(CorrectedGoal)
            && SeparationCorrection.Y == 0 && float.IsFinite(MaximumSeparation) && MaximumSeparation >= 0
            && SeparationCorrection.Length() <= MaximumSeparation + .00001f && RequestSequence > 0 && Deadline > TimeSpan.Zero
            && (Mode == CompanionBehaviorMode.Perched) == (DesiredAttachment != CompanionPerchSlot.None);

    internal static bool ValidKey(CompanionExecutionKey key)
        => ValidContext(key.Context) && key.LeaseID > 0 && key.LeaseGeneration > 0;
    internal static bool ValidContext(CompanionExecutionContext context)
        => context.SceneEpoch > 0 && context.SourceID != 0 && context.SourceGeneration > 0;
    internal static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// Current admission evidence supplied by the Framework adapter after verifying the exact
/// physical owner-child pair. Unknown species/model sockets must remain None.
/// </summary>
public readonly record struct CompanionExecutionAdmission(
    CompanionExecutionKey Key, bool Active, bool ModelReady, bool OwnerChildCurrent, bool SourceCurrent,
    CompanionPerchSlots SpeciesSockets, CompanionPerchSlots ModelSockets);

/// <summary>
/// Native observation is authoritative. AttachmentConfirmed must already include the
/// admitted native state/socket and actual placement verification, never a managed intent.
/// DetachConfirmed includes cancellation/drain of the matching request, not merely one empty frame.
/// </summary>
public sealed record CompanionExecutionObservation(
    CompanionExecutionKey Key, long RequestSequence, long ObservationSequence, TimeSpan ObservedAt,
    Vector3 ActualChildPosition, CompanionExecutionSpawnState SpawnState,
    int BehaviorState, int BehaviorSpecial, int NativeSlot, bool? RequestAccepted,
    bool AttachmentConfirmed, CompanionPerchSlot ActualAttachment, bool DetachConfirmed,
    float Progress, string? FailureReason, bool BindingCurrent);

public sealed record CompanionExecutionSnapshot(
    CompanionExecutionPlan Plan, CompanionExecutionState State, CompanionPerchSlot HeldSocket,
    bool RequestDispatched, bool NativeOccupancyPossible, bool DeadlineExpired,
    CompanionExecutionObservation? Observation, string? FailureReason);
