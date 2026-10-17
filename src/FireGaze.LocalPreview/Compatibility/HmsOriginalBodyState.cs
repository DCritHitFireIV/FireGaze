using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Compatibility;

/// <summary>
/// Stable presentation fields of the genuine local actor. No draw flags, transform,
/// locomotion clips or animation clocks are compared, so walking does not end a trial.
/// This fallback observes changes; it cannot prove which input caused them.
/// </summary>
public readonly record struct HmsOriginalBodyState(nint Address, uint Generation, byte Mode, byte ModeParam,
    ushort EmoteID, byte PoseType, byte Pose, ushort BaseOverride, ushort MountID, nint Mount,
    ushort MinionID, nint Minion, ushort OrnamentID, nint Ornament)
{
    public static unsafe HmsOriginalBodyState? Read()
    {
        var actor = (NativeCharacter*)Control.GetLocalPlayer();
        return actor == null ? null : ReadFrom(actor);
    }

    internal static unsafe HmsOriginalBodyState ReadFrom(NativeCharacter* actor)
    {
        var minion = actor->CompanionData.CompanionObject;
        return new((nint)actor, *(uint*)((byte*)actor + 0x74), (byte)actor->Mode, actor->ModeParam,
            actor->EmoteController.EmoteId, (byte)actor->EmoteController.CurrentPoseType,
            actor->EmoteController.CPoseState, actor->Timeline.BaseOverride,
            actor->Mount.MountId, (nint)actor->Mount.MountObject,
            minion == null ? (ushort)0 : (ushort)minion->BaseId, (nint)minion,
            actor->OrnamentData.OrnamentId, (nint)actor->OrnamentData.OrnamentObject);
    }
}
