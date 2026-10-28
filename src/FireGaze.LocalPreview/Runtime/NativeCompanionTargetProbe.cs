using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace FireGaze.LocalPreview.Runtime;

public sealed record CompanionTargetProbeReferences(ulong Hard, ulong Soft, ulong Focus, ulong MouseOver,
    ulong MouseOverNameplate, ulong Previous, ulong GPose, ulong IdleCam, ulong OutlineMouseOver, ulong OutlineSoft, ulong TargetId);
public sealed record CompanionTargetProbeMembership(bool IndexSorted, bool IdSorted, bool TargetableOnScreen,
    bool Filter1, bool Filter2, bool Filter3, int IdCount, int OnScreenCount, int Filter1Count, int Filter2Count, int Filter3Count);
public sealed record CompanionTargetProbeProjection(float ClientX, float ClientY, uint Width, uint Height, bool InFront, bool InViewport);
public sealed record CompanionTargetProbeObservation(ulong OwnerAddress, ushort OwnerIndex, ulong ChildAddress,
    ushort ChildIndex, ulong ChildObjectId, int ObjectKind, int TargetType, bool Targetable,
    CompanionTargetProbeMembership Membership, CompanionTargetProbeProjection Projection,
    bool HitTestPerformed, ulong HitAddress, bool HitIsChild,
    CompanionTargetProbeReferences BeforeTargets, CompanionTargetProbeReferences AfterTargets);
public sealed record NativeCompanionTargetProbeResult(long LeaseId, long Generation, bool Completed,
    string FailureStage, CompanionTargetProbeObservation? Observation);
public sealed record CompanionTargetProbeBatch(long Sequence, long SceneEpoch, DateTimeOffset CapturedAtUtc,
    string State, IReadOnlyList<NativeCompanionTargetProbeResult> Results);

/// <summary>Explicit, bounded development probe. Must run on Framework with an exact, non-native lease guard.</summary>
public static unsafe class NativeCompanionTargetProbe
{
    private const int GenerationOffset = 0x74;

    private sealed record Context(nint Manager, nint Target, nint Control, nint DeviceSlot, nint Device,
        nint PlayerSlot, nint Player, nint Camera, nint HitFunction, nint VirtualTable,
        nint IdentityFunction, nint TargetableFunction, nint TargetTypeFunction, nint CenterFunction,
        uint ChildGeneration, uint OwnerGeneration, ushort ChildIndex, ushort OwnerIndex, uint BaseId,
        Matrix4x4 Matrix, uint Width, uint Height, CompanionTargetProbeReferences Targets,
        CompanionTargetProbeMembership Membership, ObjectTables Tables);

    private sealed record ObjectTables(nint[] IndexSorted, nint[] IdSorted, nint[] OnScreen,
        nint[] Filter1, nint[] Filter2, nint[] Filter3);

    public static NativeCompanionTargetProbeResult Capture(Companion* child, Character* owner,
        long leaseId, long generation, Func<bool> isCurrent)
    {
        NativeCompanionTargetProbeResult Refused(string stage) => new(leaseId, generation, false, stage, null);
        if (child == null || owner == null || leaseId <= 0 || generation <= 0 || isCurrent is null)
            return Refused("lease-unavailable");
        try
        {
            if (!isCurrent()) return Refused("lease-unavailable");
            if (!TryContext(child, owner, out var context) || !Current(child, owner, isCurrent, context!))
                return Refused("context-unavailable");
            var c = context!;
            var obj = (GameObject*)child;
            var objectId = obj->GetGameObjectId().Id;
            if (!Current(child, owner, isCurrent, c)) return Refused("after-identity");
            var targetable = obj->GetIsTargetable();
            if (!Current(child, owner, isCurrent, c)) return Refused("after-targetable");
            var targetType = obj->GetTargetType();
            if (!Current(child, owner, isCurrent, c)) return Refused("after-target-type");
            FFXIVClientStructs.FFXIV.Common.Math.Vector3 center;
            obj->GetCenterPosition(&center);
            if (!Current(child, owner, isCurrent, c)) return Refused("after-center");
            if (!TryProjectClient((Vector3)center, c.Matrix, c.Width, c.Height, out var projection))
                return Refused("projection-unavailable");

            // Client pixels intentionally have no ImGui viewport origin or UI/DPI scale.
            // This casts one diagnostic ray; it does not move a cursor or dispatch input.
            GameObject* hit = null;
            if (projection!.InViewport)
            {
                var x = Math.Clamp((int)MathF.Round(projection.ClientX), 0, (int)c.Width - 1);
                var y = Math.Clamp((int)MathF.Round(projection.ClientY), 0, (int)c.Height - 1);
                var target = (TargetSystem*)c.Target;
                hit = target->GetMouseOverObject(x, y, &target->ObjectFilterArray1, (GameCamera*)c.Camera);
                if (!Current(child, owner, isCurrent, c)) return Refused("after-hit-test");
            }
            if (!Current(child, owner, isCurrent, c)) return Refused("before-publication");
            var observation = new CompanionTargetProbeObservation((ulong)owner, c.OwnerIndex, (ulong)child,
                c.ChildIndex, objectId, (int)obj->ObjectKind, (int)targetType, targetable, c.Membership,
                projection, projection.InViewport, (ulong)hit, hit == obj,
                c.Targets, ReadReferences((TargetSystem*)c.Target));
            return new(leaseId, generation, true, "", observation);
        }
        catch (Exception)
        {
            // Detached failure only. Never repair/restore tables or targets from this probe.
            return Refused("managed-exception");
        }
    }

    private static bool TryContext(Companion* child, Character* owner, out Context? result)
    {
        result = null;
        if (GameObjectManager.Addresses.Instance.Value == 0 || TargetSystem.Addresses.Instance.Value == 0
            || Control.Addresses.Instance.Value == 0 || Device.Addresses.Instance.Value == 0
            || Control.Addresses.GetLocalPlayer.Value == 0 || TargetSystem.Addresses.GetMouseOverObject.Value == 0)
            return false;
        var manager = GameObjectManager.Instance(); var target = TargetSystem.Instance();
        var control = Control.Instance(); var device = Device.Instance();
        var obj = (GameObject*)child;
        var table = obj->VirtualTable;
        if (device == null || control->CameraManager.Camera == null || Control.GetLocalPlayer() == null
            || table == null || table->GetGameObjectId == null || table->GetIsTargetable == null
            || table->GetTargetType == null || table->GetCenterPosition == null
            || !ValidLengths(manager, target) || device->Width == 0 || device->Height == 0
            || device->Width > int.MaxValue || device->Height > int.MaxValue) return false;
        result = new((nint)manager, (nint)target, (nint)control, Device.Addresses.Instance.Value, (nint)device,
            Control.Addresses.GetLocalPlayer.Value, (nint)Control.GetLocalPlayer(), (nint)control->CameraManager.Camera,
            TargetSystem.Addresses.GetMouseOverObject.Value, (nint)table, (nint)table->GetGameObjectId,
            (nint)table->GetIsTargetable, (nint)table->GetTargetType, (nint)table->GetCenterPosition,
            Generation(child), Generation(owner), child->ObjectIndex, owner->ObjectIndex, child->BaseId,
            *(Matrix4x4*)&control->ViewProjectionMatrix, device->Width, device->Height,
            ReadReferences(target), ReadMembership(manager, target, obj), ReadTables(manager, target));
        return true;
    }

    private static bool Current(Companion* child, Character* owner, Func<bool> isCurrent, Context c)
    {
        // The caller guard is raw lease identity only: no native work or ownership mutation.
        if (!isCurrent() || GameObjectManager.Addresses.Instance.Value != c.Manager
            || TargetSystem.Addresses.Instance.Value != c.Target || Control.Addresses.Instance.Value != c.Control
            || Device.Addresses.Instance.Value != c.DeviceSlot || Control.Addresses.GetLocalPlayer.Value != c.PlayerSlot
            || TargetSystem.Addresses.GetMouseOverObject.Value != c.HitFunction
            || (nint)Device.Instance() != c.Device || (nint)Control.GetLocalPlayer() != c.Player) return false;
        var obj = (GameObject*)child; var table = obj->VirtualTable;
        var control = (Control*)c.Control; var device = (Device*)c.Device;
        var target = (TargetSystem*)c.Target; var manager = (GameObjectManager*)c.Manager;
        return obj->ObjectKind == ObjectKind.Companion && child->Owner == (BattleChara*)owner
            && child->ObjectIndex == c.ChildIndex && owner->ObjectIndex == c.OwnerIndex
            && Generation(child) == c.ChildGeneration && Generation(owner) == c.OwnerGeneration && child->BaseId == c.BaseId
            && (nint)table == c.VirtualTable && (nint)table->GetGameObjectId == c.IdentityFunction
            && (nint)table->GetIsTargetable == c.TargetableFunction && (nint)table->GetTargetType == c.TargetTypeFunction
            && (nint)table->GetCenterPosition == c.CenterFunction && (nint)control->CameraManager.Camera == c.Camera
            && device->Width == c.Width && device->Height == c.Height && *(Matrix4x4*)&control->ViewProjectionMatrix == c.Matrix
            && ValidLengths(manager, target) && ReadReferences(target) == c.Targets
            && TablesCurrent(manager, target, c.Tables);
    }

    private static uint Generation(void* value) => *(uint*)((byte*)value + GenerationOffset);

    private static bool ValidLengths(GameObjectManager* manager, TargetSystem* target)
        => manager->Objects.GameObjectIdSortedCount >= 0
            && manager->Objects.GameObjectIdSortedCount <= manager->Objects.GameObjectIdSorted.Length
            && ValidLength(in target->TargetableObjectsOnScreen) && ValidLength(in target->ObjectFilterArray1)
            && ValidLength(in target->ObjectFilterArray2) && ValidLength(in target->ObjectFilterArray3);

    private static bool ValidLength(in GameObjectArray array) => array.Length >= 0 && array.Length <= array.Objects.Length;

    private static bool Contains(in GameObjectArray array, GameObject* obj)
    {
        for (var i = 0; i < array.Length; i++) if ((GameObject*)array.Objects[i] == obj) return true;
        return false;
    }

    private static ObjectTables ReadTables(GameObjectManager* manager, TargetSystem* target)
    {
        var index = new nint[manager->Objects.IndexSorted.Length];
        var ids = new nint[manager->Objects.GameObjectIdSortedCount];
        for (var i = 0; i < index.Length; i++) index[i] = (nint)(GameObject*)manager->Objects.IndexSorted[i];
        for (var i = 0; i < ids.Length; i++) ids[i] = (nint)(GameObject*)manager->Objects.GameObjectIdSorted[i];
        return new(index, ids, Copy(in target->TargetableObjectsOnScreen), Copy(in target->ObjectFilterArray1),
            Copy(in target->ObjectFilterArray2), Copy(in target->ObjectFilterArray3));
    }

    private static nint[] Copy(in GameObjectArray array)
    {
        var result = new nint[array.Length];
        for (var i = 0; i < result.Length; i++) result[i] = (nint)(GameObject*)array.Objects[i];
        return result;
    }

    private static bool TablesCurrent(GameObjectManager* manager, TargetSystem* target, ObjectTables tables)
    {
        if (manager->Objects.IndexSorted.Length != tables.IndexSorted.Length
            || manager->Objects.GameObjectIdSortedCount != tables.IdSorted.Length) return false;
        for (var i = 0; i < tables.IndexSorted.Length; i++)
            if ((nint)(GameObject*)manager->Objects.IndexSorted[i] != tables.IndexSorted[i]) return false;
        for (var i = 0; i < tables.IdSorted.Length; i++)
            if ((nint)(GameObject*)manager->Objects.GameObjectIdSorted[i] != tables.IdSorted[i]) return false;
        return Matches(in target->TargetableObjectsOnScreen, tables.OnScreen)
            && Matches(in target->ObjectFilterArray1, tables.Filter1)
            && Matches(in target->ObjectFilterArray2, tables.Filter2)
            && Matches(in target->ObjectFilterArray3, tables.Filter3);
    }

    private static bool Matches(in GameObjectArray array, nint[] expected)
    {
        if (array.Length != expected.Length) return false;
        for (var i = 0; i < expected.Length; i++) if ((nint)(GameObject*)array.Objects[i] != expected[i]) return false;
        return true;
    }

    private static CompanionTargetProbeMembership ReadMembership(GameObjectManager* manager, TargetSystem* target, GameObject* obj)
    {
        var idSorted = false;
        for (var i = 0; i < manager->Objects.GameObjectIdSortedCount; i++)
            if ((GameObject*)manager->Objects.GameObjectIdSorted[i] == obj) { idSorted = true; break; }
        return new(obj->ObjectIndex < manager->Objects.IndexSorted.Length && (GameObject*)manager->Objects.IndexSorted[obj->ObjectIndex] == obj,
            idSorted, Contains(in target->TargetableObjectsOnScreen, obj), Contains(in target->ObjectFilterArray1, obj),
            Contains(in target->ObjectFilterArray2, obj), Contains(in target->ObjectFilterArray3, obj),
            manager->Objects.GameObjectIdSortedCount, target->TargetableObjectsOnScreen.Length,
            target->ObjectFilterArray1.Length, target->ObjectFilterArray2.Length, target->ObjectFilterArray3.Length);
    }

    private static CompanionTargetProbeReferences ReadReferences(TargetSystem* target)
        => new((ulong)target->Target, (ulong)target->SoftTarget, (ulong)target->FocusTarget, (ulong)target->MouseOverTarget,
            (ulong)target->MouseOverNameplateTarget, (ulong)target->PreviousTarget, (ulong)target->GPoseTarget,
            (ulong)target->IdleCamTarget, (ulong)target->TargetOutlineInfo.MouseOverTarget,
            (ulong)target->TargetOutlineInfo.SoftTarget, target->TargetObjectId.Id);

    private static bool TryProjectClient(Vector3 center, Matrix4x4 matrix, uint width, uint height,
        out CompanionTargetProbeProjection? result)
    {
        result = null;
        var clip = Vector4.Transform(new Vector4(center, 1), matrix);
        if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) || !float.IsFinite(clip.Z) || !float.IsFinite(clip.W)
            || MathF.Abs(clip.W) < .0001f) return false;
        var x = (clip.X / clip.W + 1) * .5f * width;
        var y = (1 - clip.Y / clip.W) * .5f * height;
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
        var inFront = clip.W > 0;
        result = new(x, y, width, height, inFront, inFront && x >= 0 && x < width && y >= 0 && y < height);
        return true;
    }
}
