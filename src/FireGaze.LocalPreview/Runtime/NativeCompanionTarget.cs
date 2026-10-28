using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace FireGaze.LocalPreview.Runtime;

public static unsafe class NativeCompanionTarget
{
    private const ulong InvalidObjectId = 0xE0000000;

    /// <summary>Call on the framework thread. The guard must validate the exact child allocation lease.</summary>
    public static bool TrySelect(Companion* companion, Func<bool> isCurrent)
    {
        if (TargetSystem.Addresses.SetHardTarget.Value == 0
            || !TryResolve(companion, isCurrent, out var manager, out var target, out _)) return false;
        var obj = (GameObject*)companion;
        if (target->Target == obj) return true;
        // This is an explicit player selection, so targeting-cycle filters do not apply.
        var accepted = target->SetHardTarget(obj, true);
        return accepted && ContextCurrent(companion, isCurrent, manager, target) && target->Target == obj;
    }

    /// <summary>
    /// Remove only this exact owned child's references before native deletion. False means the caller
    /// must retain the actor and retry; it must never free a still-referenced object.
    /// </summary>
    public static bool ClearReferencesBeforeRetire(Companion* companion, Func<bool> isCurrent)
    {
        if (companion == null || !isCurrent() || TargetSystem.Addresses.Instance.Value == 0) return false;
        var target = TargetSystem.Instance();
        var objectId = companion->GetGameObjectId().Id;
        if (!TargetContextCurrent(isCurrent, target)
            || !ValidLength(in target->TargetableObjectsOnScreen) || !ValidLength(in target->ObjectFilterArray1)
            || !ValidLength(in target->ObjectFilterArray2) || !ValidLength(in target->ObjectFilterArray3)) return false;
        var obj = (GameObject*)companion;
        if (target->Target == obj)
        {
            if (TargetSystem.Addresses.SetHardTarget.Value == 0 || !target->SetHardTarget(null)
                || !TargetContextCurrent(isCurrent, target) || target->Target == obj) return false;
        }
        if (target->SoftTarget == obj)
        {
            if (TargetSystem.Addresses.SetSoftTarget.Value == 0 || !target->SetSoftTarget(null)
                || !TargetContextCurrent(isCurrent, target) || target->SoftTarget == obj) return false;
        }
        if (target->FocusTarget == obj)
        {
            if (TargetSystem.Addresses.SetFocusTargetByObjectId.Value == 0) return false;
            target->SetFocusTargetByObjectId(InvalidObjectId);
            if (!TargetContextCurrent(isCurrent, target) || target->FocusTarget == obj) return false;
        }
        // No native call below this point. Compare raw pointer identity only, and retain unrelated targets.
        if (target->PreviousTarget == obj) target->PreviousTarget = null;
        if (target->MouseOverTarget == obj) target->MouseOverTarget = null;
        if (target->MouseOverNameplateTarget == obj) target->MouseOverNameplateTarget = null;
        if (target->GPoseTarget == obj) target->GPoseTarget = null;
        if (target->IdleCamTarget == obj) target->IdleCamTarget = null;
        if (target->TargetOutlineInfo.MouseOverTarget == obj) target->TargetOutlineInfo.MouseOverTarget = null;
        if (target->TargetOutlineInfo.SoftTarget == obj) target->TargetOutlineInfo.SoftTarget = null;
        if (target->TargetObjectId.Id == objectId) target->TargetObjectId = InvalidObjectId;
        Remove(ref target->TargetableObjectsOnScreen, obj);
        Remove(ref target->ObjectFilterArray1, obj);
        Remove(ref target->ObjectFilterArray2, obj);
        Remove(ref target->ObjectFilterArray3, obj);
        return TargetContextCurrent(isCurrent, target);
    }

    private static bool TargetContextCurrent(Func<bool> isCurrent, TargetSystem* target)
        => isCurrent() && (nint)target == TargetSystem.Addresses.Instance.Value;

    private static bool TryResolve(Companion* companion, Func<bool> isCurrent,
        out GameObjectManager* manager, out TargetSystem* target, out ulong objectId)
    {
        manager = null; target = null; objectId = 0;
        if (companion == null || !isCurrent() || GameObjectManager.Addresses.Instance.Value == 0
            || TargetSystem.Addresses.Instance.Value == 0) return false;
        manager = GameObjectManager.Instance(); target = TargetSystem.Instance();
        if (!ContextCurrent(companion, isCurrent, manager, target)) return false;
        objectId = companion->GetGameObjectId().Id;
        if (!ContextCurrent(companion, isCurrent, manager, target)
            || objectId is 0 or InvalidObjectId) return false;
        // GetIsTargetable is virtual and may invoke hooks; never trust the lease after that call.
        var targetable = companion->GetIsTargetable();
        return targetable && ContextCurrent(companion, isCurrent, manager, target);
    }

    private static bool ContextCurrent(Companion* companion, Func<bool> isCurrent,
        GameObjectManager* manager, TargetSystem* target)
    {
        if (!isCurrent() || (nint)manager != GameObjectManager.Addresses.Instance.Value
            || (nint)target != TargetSystem.Addresses.Instance.Value
            || companion->ObjectKind != ObjectKind.Companion
            || companion->ObjectIndex >= manager->Objects.IndexSorted.Length
            || (GameObject*)manager->Objects.IndexSorted[companion->ObjectIndex] != (GameObject*)companion) return false;
        var count = manager->Objects.GameObjectIdSortedCount;
        if (count < 0 || count > manager->Objects.GameObjectIdSorted.Length) return false;
        for (var i = 0; i < count; i++)
            if ((GameObject*)manager->Objects.GameObjectIdSorted[i] == (GameObject*)companion) return true;
        return false;
    }

    private static bool ValidLength(in GameObjectArray array) => array.Length >= 0 && array.Length <= 819;

    private static void Remove(ref GameObjectArray array, GameObject* companion)
    {
        var oldLength = array.Length;
        var count = 0;
        for (var i = 0; i < oldLength; i++)
            if ((GameObject*)array.Objects[i] != companion) array.Objects[count++] = array.Objects[i];
        for (var i = count; i < oldLength; i++) array.Objects[i] = null;
        for (var i = oldLength; i < array.Objects.Length; i++)
            if ((GameObject*)array.Objects[i] == companion) array.Objects[i] = null;
        array.Length = count;
    }
}
