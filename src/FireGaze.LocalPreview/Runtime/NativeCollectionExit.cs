using System;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>A short-lived real-state observation used only by one Framework exit request.</summary>
public readonly record struct NativeCollectionExitSnapshot(nint Source, uint Generation, uint ActorId,
    uint Territory, ushort Mount, ushort Accessory, nint Manager)
{
    public bool HasIdentity => Source != 0 && Manager != 0 && ActorId is not (0 or 0xE0000000);

    public bool SameIdentity(NativeCollectionExitSnapshot other)
        => HasIdentity && other.HasIdentity && Source == other.Source && Generation == other.Generation
            && ActorId == other.ActorId && Territory == other.Territory && Manager == other.Manager;
}

/// <summary>
/// Native removal owns its state change. Re-observe after display cleanup and after
/// each native call, because either boundary may replace the source or its collections.
/// </summary>
public static class NativeCollectionExit
{
    /// <summary>Framework only; do not authorize removal from a stale managed object-table cache.</summary>
    public static unsafe NativeCollectionExitSnapshot? Capture(Character* source, uint territory, nint manager)
    {
        if (source == null || manager == 0 || Control.Addresses.Instance.Value == 0
            || Control.Addresses.GetLocalPlayer.Value == 0) return null;
        var control = (Control*)Control.Addresses.Instance.Value;
        if (control->LocalPlayer != (BattleChara*)source
            || *(BattleChara**)Control.Addresses.GetLocalPlayer.Value != (BattleChara*)source) return null;
        if (source->ObjectIndex >= 200 || source->ObjectKind != ObjectKind.Pc
            || source->EntityId is 0 or 0xE0000000 || control->LocalPlayerEntityId != source->EntityId) return null;
        return new((nint)source, *(uint*)((byte*)source + 0x74), source->EntityId, territory,
            source->Mount.MountId, source->OrnamentData.OrnamentId, manager);
    }

    public static void Execute(NativeCollectionExitSnapshot entry,
        Func<NativeCollectionExitSnapshot?> observe, Func<bool> removeMount, Func<ushort, bool> removeAccessory)
    {
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentNullException.ThrowIfNull(removeMount);
        ArgumentNullException.ThrowIfNull(removeAccessory);
        if (!entry.HasIdentity || observe() is not { } current || !entry.SameIdentity(current)
            || current.Mount != entry.Mount || current.Accessory != entry.Accessory) return;

        bool dismountAccepted = entry.Mount != 0 && removeMount();
        if (entry.Accessory == 0 || observe() is not { } after || !entry.SameIdentity(after)
            || after.Accessory != entry.Accessory
            || after.Mount != entry.Mount && !(dismountAccepted && after.Mount == 0)) return;
        removeAccessory(entry.Accessory);
    }
}
