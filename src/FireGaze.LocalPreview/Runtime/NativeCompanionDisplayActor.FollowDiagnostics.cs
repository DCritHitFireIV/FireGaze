using System.Numerics;
using System.Runtime.InteropServices;
using FireGaze.LocalPreview.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using static FireGaze.LocalPreview.Runtime.NativeCompanionFollowReader;

namespace FireGaze.LocalPreview.Runtime;

internal readonly record struct CompanionFollowBinding(
    CompanionExecutionKey Key, long BindingRevision, long DiagnosticEpoch,
    nint Manager, ushort HostSlot, nint Host, ushort HostIndex, uint HostGeneration,
    nint Child, ushort ChildIndex, uint ChildGeneration, nint Source, uint SourceGeneration, uint Territory);

public sealed unsafe partial class NativeCompanionDisplayActor
{
    // Generated SDK layout is a FixedSizeArray249 at _battleCharas (offset 0x10).
    // Keep this bound detached from the manager pointer: diagnostics must never
    // dereference the manager merely to discover its array length.
    private const int BattleCharaCapacity = 249;
    private long bindingRevision;
    private static class FollowLayout
    {
        internal static readonly int ObjectIndexOffset = Offset<GameObject>(nameof(GameObject.ObjectIndex));
        internal static readonly int EntityIDOffset = Offset<GameObject>(nameof(GameObject.EntityId));
        internal static readonly int ObjectKindOffset = Offset<GameObject>(nameof(GameObject.ObjectKind));
        internal static readonly int BaseIDOffset = Offset<GameObject>(nameof(GameObject.BaseId));
        internal static readonly int PositionOffset = Offset<GameObject>(nameof(GameObject.Position));
        internal static readonly int DrawOffset = Offset<GameObject>(nameof(GameObject.DrawObject));
        internal static readonly int ContainerOffset = Offset<NativeCharacter>(nameof(NativeCharacter.CompanionData));
        internal static readonly int ContainerOwnerOffset = Offset<CompanionContainer>(nameof(CompanionContainer.OwnerObject));
        internal static readonly int ContainerChildOffset = Offset<CompanionContainer>(nameof(CompanionContainer.CompanionObject));
        internal static readonly int ManagerSlotsOffset = Offset<ClientObjectManager>("_battleCharas");
    }

    internal static bool FollowLayoutValid()
    {
        try
        {
            return FollowLayout.ObjectIndexOffset >= 0 && FollowLayout.EntityIDOffset >= 0
                && FollowLayout.ObjectKindOffset >= 0 && FollowLayout.BaseIDOffset >= 0
                && FollowLayout.PositionOffset >= 0 && FollowLayout.DrawOffset >= 0
                && FollowLayout.ContainerOffset >= 0 && FollowLayout.ContainerOwnerOffset >= 0
                && FollowLayout.ContainerChildOffset >= 0 && FollowLayout.ManagerSlotsOffset == 0x10
                && Marshal.SizeOf<ClientObjectManager.BattleCharaEntry>() > 0
                && Marshal.SizeOf<ClientObjectManager>() == 4000;
        }
        catch (Exception) { return false; }
    }

    internal bool TryReadFollowBinding(ICompanionFollowSnapshotReader reader, CompanionExecutionKey key,
        uint territory, long diagnosticEpoch, out CompanionFollowBinding binding, out CompanionNativeChildState state)
    {
        binding = default; state = default;
        if (!reader.IsFrameworkThread || !IsReady || disposed || retirementRequested || childDetached
            || key.SourceGeneration != sourceGeneration || key.SourceID == 0 || key.SceneEpoch <= 0
            || key.LeaseID <= 0 || key.LeaseGeneration <= 0 || diagnosticEpoch <= 0 || bindingRevision <= 0) return false;
        var candidate = new CompanionFollowBinding(key, bindingRevision, diagnosticEpoch, (nint)manager, slot,
            (nint)owner, ownerIndex, ownerGeneration, (nint)child, childIndex, childGeneration,
            (nint)source, sourceGeneration, territory);
        if (!TryReadFollowState(reader, candidate, out state)) return false;
        binding = candidate;
        return true;
    }

    internal bool TryReadFollowState(ICompanionFollowSnapshotReader reader, in CompanionFollowBinding binding,
        out CompanionNativeChildState state)
    {
        state = default;
        if (!reader.IsFrameworkThread || !IsReady || disposed || retirementRequested || childDetached
            || binding.BindingRevision != bindingRevision || binding.Child != (nint)child || binding.Host != (nint)owner
            || binding.Source != (nint)source || binding.Manager != (nint)manager || binding.HostSlot != slot
            || binding.SourceGeneration != sourceGeneration || binding.Key.SourceGeneration != sourceGeneration
            || binding.HostIndex != ownerIndex || binding.HostGeneration != ownerGeneration
            || binding.ChildIndex != childIndex || binding.ChildGeneration != childGeneration
            || !IdentityCurrent(reader, binding)) return false;
        if (!Read(reader, binding.Child + FollowLayout.PositionOffset, out Vector3 position)
            || !Read(reader, binding.Host + FollowLayout.PositionOffset, out Vector3 hostPosition)
            || !Read(reader, binding.Child + 0x2457, out byte spawn)
            || !Read(reader, binding.Child + 0x2458, out byte behaviorState)
            || !Read(reader, binding.Child + 0x245A, out byte behaviorSpecial)
            || !Finite(position) || !Finite(hostPosition) || !IdentityCurrent(reader, binding)
            || binding.BindingRevision != bindingRevision || !IsReady || disposed || retirementRequested) return false;
        state = new(binding.Key, binding.BindingRevision, slot, ownerIndex, ownerGeneration,
            childIndex, childGeneration, CompanionId, position, hostPosition, spawn, behaviorState, behaviorSpecial, true);
        return true;
    }

    private bool IdentityCurrent(ICompanionFollowSnapshotReader reader, in CompanionFollowBinding b)
        => b.Child != 0 && b.Host != 0 && b.Source != 0 && b.Manager > 0 && b.DiagnosticEpoch > 0
            && b.Manager == ClientObjectManager.Addresses.Instance.Value
            && Read(reader, Control.Addresses.GetLocalPlayer.Value, out nint local) && local == b.Source
            && Read(reader, b.Source + GenerationOffset, out uint sourceStamp) && sourceStamp == b.SourceGeneration
            // The qualified native GetGameObjectId fast path zero-extends a real
            // entity ID. Sentinel identities require native layout/helper state;
            // this read-only diagnostic does not invoke or guess that branch.
            && Read(reader, b.Source + FollowLayout.EntityIDOffset, out uint entityID)
            && entityID is not 0 and not 0xE0000000 && entityID == sourceEntityId && (ulong)entityID == b.Key.SourceID
            && b.HostSlot < BattleCharaCapacity
            && Read(reader, b.Manager + FollowLayout.ManagerSlotsOffset + b.HostSlot * sizeof(ClientObjectManager.BattleCharaEntry), out ClientObjectManager.BattleCharaEntry entry)
            && (nint)entry.BattleChara == b.Host
            && Read(reader, b.Host + FollowLayout.ObjectIndexOffset, out ushort hostObjectIndex) && hostObjectIndex == b.HostIndex
            && Read(reader, b.Host + GenerationOffset, out uint hostStamp) && hostStamp == b.HostGeneration
            && Read(reader, b.Host + FollowLayout.DrawOffset, out nint hostDraw) && hostDraw == 0
            && Read(reader, b.Host + FollowLayout.ContainerOffset + FollowLayout.ContainerOwnerOffset, out nint containerOwner) && containerOwner == b.Host
            && Read(reader, b.Host + FollowLayout.ContainerOffset + FollowLayout.ContainerChildOffset, out nint containerChild) && containerChild == b.Child
            && Read(reader, b.Child + FollowLayout.ObjectIndexOffset, out ushort index) && index == b.ChildIndex
            && Read(reader, b.Child + GenerationOffset, out uint stamp) && stamp == b.ChildGeneration
            && Read(reader, b.Child + 0x2400, out nint physicalOwner) && physicalOwner == b.Host
            && Read(reader, b.Child + FollowLayout.BaseIDOffset, out uint row) && row == CompanionId
            && Read(reader, b.Child + FollowLayout.ObjectKindOffset, out byte kind) && kind == (byte)ObjectKind.Companion
            && Read(reader, b.Child + FollowLayout.DrawOffset, out nint childDraw) && childDraw > 0
            // Reuse only this actor's current native-ready resource observation.
            // Render submission bit3 can clear between reads; draw-enable bit0
            // does not substitute for the exact draw/binding/load-stage proof.
            && childDraw == provenVisibleDraw && b.BindingRevision == provenVisibleBindingRevision
            && Read(reader, childDraw, out DrawObject draw) && (draw.Flags & 1) != 0
            && draw.LoadState == provenVisibleLoadState;

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
