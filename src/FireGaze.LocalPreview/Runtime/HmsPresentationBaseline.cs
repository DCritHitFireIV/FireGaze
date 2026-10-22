using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>Value-only presentation: no pointer survives the initial synchronous read.</summary>
public readonly record struct HmsTimelineState(ushort T0, ushort T1, ushort T2, ushort T3,
    ushort BaseOverride, bool WeaponDrawn, byte ModelState)
{
    public static unsafe HmsTimelineState Read(NativeCharacter* actor)
        => new(actor->Timeline.TimelineSequencer.TimelineIds[0], actor->Timeline.TimelineSequencer.TimelineIds[1],
            actor->Timeline.TimelineSequencer.TimelineIds[2], actor->Timeline.TimelineSequencer.TimelineIds[3],
            actor->Timeline.BaseOverride, actor->Timeline.IsWeaponDrawn, actor->Timeline.ModelState);
}

public readonly record struct HmsActorPresentation(CharacterModes Mode, byte ModeParam, ushort EmoteId,
    byte Stance, byte PoseType, byte CPoseState, HmsTimelineState Timeline, Vector3 DrawOffset,
    bool HasDraw, Vector3 BodyOffset, Quaternion DrawRotation)
{
    public static unsafe HmsActorPresentation Read(NativeCharacter* actor)
    {
        var draw = actor->DrawObject;
        return new(actor->Mode, actor->ModeParam, actor->EmoteController.EmoteId, actor->EmoteController.Stance,
            (byte)actor->EmoteController.CurrentPoseType, actor->EmoteController.CPoseState,
            HmsTimelineState.Read(actor), actor->DrawOffset, draw != null,
            draw != null ? draw->Position - actor->Position : default,
            draw != null ? draw->Rotation : Quaternion.Identity);
    }
}

public readonly record struct HmsChildPresentation(bool Present, uint BaseId, HmsActorPresentation Actor,
    Vector3 RelativePosition, float Rotation, byte Behaviour = 0, uint OrnamentId = 0, uint CustomizeGroupId = 0)
{
    public static unsafe HmsChildPresentation Read(NativeCharacter* child, NativeCharacter* owner)
        => child == null ? default : new(true, child->BaseId, HmsActorPresentation.Read(child),
            child->Position - owner->Position, child->Rotation);
}

public readonly record struct HmsPresentationBaseline(HmsActorPresentation Root, ushort MountId,
    ushort MinionId, ushort OrnamentId, HmsChildPresentation Mount, HmsChildPresentation Minion,
    HmsChildPresentation Ornament)
{
    public static unsafe HmsPresentationBaseline Read(NativeCharacter* actor)
    {
        var minion = actor->CompanionData.CompanionObject;
        var ornament = actor->OrnamentData.OrnamentObject;
        var minionState = HmsChildPresentation.Read((NativeCharacter*)minion, actor);
        if (minion != null) minionState = minionState with { Behaviour = (byte)minion->Behavior };
        var ornamentState = HmsChildPresentation.Read((NativeCharacter*)ornament, actor);
        if (ornament != null) ornamentState = ornamentState with { OrnamentId = ornament->OrnamentId, CustomizeGroupId = ornament->CustomizeGroupId };
        return new(HmsActorPresentation.Read(actor), actor->Mount.MountId,
            minion != null ? (ushort)minion->BaseId : (ushort)0, actor->OrnamentData.OrnamentId,
            HmsChildPresentation.Read(actor->Mount.MountObject, actor), minionState, ornamentState);
    }
}

public readonly record struct HmsCaptureFrame(nint Address, ulong ActorId, uint Territory, long Revision,
    bool Project, HmsPresentationBaseline Baseline, bool OwnMount, bool OwnEmoteTimeline);

public interface IHmsShadowSource
{
    bool TryGetHmsCaptureFrame(out HmsCaptureFrame frame);
    bool IsHmsCaptureFrameCurrent(in HmsCaptureFrame frame);
}
