using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;
using OffscreenManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.OffscreenRenderingManager;
using RenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitOwnedSceneSlotRoots(nint Renderer, nint SceneCameras, nint Offscreen,
    nint RenderTargets, nint ClientObjects)
{
    public bool IsValid => Renderer != 0 && SceneCameras != 0 && Offscreen != 0 && RenderTargets != 0 && ClientObjects != 0;
}

/// <summary>Issued by a native-phase owner; equality includes manager generation even when addresses are reused.</summary>
internal readonly record struct PortraitOwnedSceneSlotPhase(PortraitIdentity Source, PortraitOwnedSceneSlotRoots Roots,
    long SessionEpoch, long ManagerGeneration, uint NativeFrame, long PhaseToken, int ThreadID,
    bool InNativePhase, bool BuildValidated);

internal interface IPortraitOwnedSceneSlotPhaseSource
{
    bool TryCapture(out PortraitOwnedSceneSlotPhase phase);
}

internal interface IPortraitOwnedSceneSlotNativeApi
{
    bool TryReadRoots(out PortraitOwnedSceneSlotRoots roots);
    bool TryRead<T>(nint address, out T value) where T : unmanaged;
}

internal enum PortraitOwnedSceneSlotStage { Initialized, Prepared }
internal readonly record struct PortraitOwnedSceneSlotOwner(nint View, nint Character, uint CharacterGeneration,
    nint Context, nint Camera, PortraitOwnedSceneSlotStage Stage = PortraitOwnedSceneSlotStage.Prepared);
internal enum PortraitOwnedSceneSlotOwnership { None, Idle, Owned }

/// <summary>Detached values, qualified only by the phase in which they were read; never a retained native lease.</summary>
internal readonly record struct PortraitOwnedSceneSlotSnapshot(uint Slot, PortraitOwnedSceneSlotOwnership Ownership,
    PortraitOwnedSceneSlotPhase Phase, nint Character, uint CharacterGeneration, nint Context, nint RegisteredCamera,
    int ActiveCount, bool RenderActive, nint RetainedOffscreenCamera, nint RetainedViewCamera,
    PortraitOwnedSceneSlotStage? OwnerStage);

/// <summary>
/// Reads the audited eight-slot layout without native game calls or writes. It does not install hooks,
/// reserve slots, construct views or manufacture a native phase. Construction performs no reads.
/// </summary>
internal sealed unsafe class PortraitOwnedSceneSlotReader(IPortraitOwnedSceneSlotNativeApi native,
    IPortraitOwnedSceneSlotPhaseSource phases)
{
    [StructLayout(LayoutKind.Explicit, Size = 0x38)]
    private struct ViewHeader
    {
        [FieldOffset(8)] public uint State;
        [FieldOffset(0xC)] public uint ID;
        [FieldOffset(0x10)] public uint Slot;
        [FieldOffset(0x14)] public uint CameraType;
        [FieldOffset(0x18)] public nint Context;
        [FieldOffset(0x20)] public nint Camera;
        [FieldOffset(0x30)] public nint Agent;
    }
    [StructLayout(LayoutKind.Explicit, Size = 0x10)]
    private struct ObjectEntry
    {
        [FieldOffset(0)] public nint Character;
        [FieldOffset(8)] public ObjectKind Kind;
        [FieldOffset(0xC)] public byte Index;
        [FieldOffset(0xE)] public ushort MemoryIndex;
    }
    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    private struct ContextHeader
    {
        [FieldOffset(8)] public int References;
        [FieldOffset(0x10)] public uint Slot;
        [FieldOffset(0x14)] public uint Type;
        [FieldOffset(0x18)] public byte Active;
    }
    private readonly record struct SourceBinding(uint Slot, nint Context, nint Camera, uint Generation, nint DrawOwner,
        uint ContextType, int ContextReferences, int ActiveCount);
    private readonly record struct Sample(nint Character, ObjectKind Kind, byte Index, ushort MemoryIndex,
        nint RegisteredCamera, int Count, byte Active, nint OffscreenCamera, nint ViewCamera,
        uint ViewState = 0, uint Generation = 0, nint DrawOwner = 0, nint Context = 0,
        nint Camera = 0, uint ContextSlot = 0, uint ContextType = 0, int ContextReferences = 0, byte ContextActive = 0);

    public bool TryReadIdle(uint slot, PortraitOwnedSceneSlotPhase phase, out PortraitOwnedSceneSlotSnapshot snapshot)
        => TryRead(slot, default, phase, false, out snapshot);

    public bool TryReadOwned(uint slot, PortraitOwnedSceneSlotOwner owner, PortraitOwnedSceneSlotPhase phase,
        out PortraitOwnedSceneSlotSnapshot snapshot)
        => TryRead(slot, owner, phase, true, out snapshot);

    private bool TryRead(uint slot, PortraitOwnedSceneSlotOwner owner, PortraitOwnedSceneSlotPhase phase, bool owned,
        out PortraitOwnedSceneSlotSnapshot snapshot)
    {
        snapshot = default;
        if (slot >= 8 || !Qualified(phase) || !SDKLayoutMatches()) return false;
        if (owned && (owner.View == 0 || owner.View == phase.Source.Model || owner.Character == 0
            || owner.Character == (nint)phase.Source.Character || owner.CharacterGeneration == 0
            || owner.Stage == PortraitOwnedSceneSlotStage.Prepared && (owner.Context == 0 || owner.Camera == 0)
            || owner.Stage == PortraitOwnedSceneSlotStage.Initialized && (owner.Context != 0 || owner.Camera != 0)
            || owner.Stage is not (PortraitOwnedSceneSlotStage.Prepared or PortraitOwnedSceneSlotStage.Initialized))) return false;
        try
        {
            if (!Current(phase) || !ReadSource(phase, out var source) || source.Slot == slot
                || owned && (owner.Context != 0 && owner.Context == source.Context || owner.Camera != 0 && owner.Camera == source.Camera)
                || !ReadSample(slot, owner, phase, owned, out var first)
                || !ReadSource(phase, out var middle) || source != middle
                || !ReadSample(slot, owner, phase, owned, out var second) || first != second
                || !ReadSource(phase, out var after) || source != after || !Current(phase)) return false;
            if (owned)
            {
                if (first.Character != owner.Character || first.Generation != owner.CharacterGeneration
                    || first.DrawOwner != owner.Character || first.Kind == 0) return false;
                if (owner.Stage == PortraitOwnedSceneSlotStage.Initialized)
                {
                    if (first.ViewState != 1 || first.Count != 0 || first.RegisteredCamera != 0
                        || first.Context != 0 || first.Camera != 0) return false;
                }
                else if (first.Count != 1 || first.RegisteredCamera != owner.Camera || first.Camera != owner.Camera
                    || first.Context != owner.Context || first.ContextSlot != slot || first.ContextActive == 0
                    || first.ContextReferences <= 0 || first.ViewState is not (2 or 3 or 6)) return false;
            }
            else if (first.Character != 0 || first.Kind != 0 || first.RegisteredCamera != 0 || first.Count != 0)
                return false;
            snapshot = new(slot, owned ? PortraitOwnedSceneSlotOwnership.Owned : PortraitOwnedSceneSlotOwnership.Idle,
                phase, first.Character, first.Generation, first.Context, first.RegisteredCamera, first.Count,
                first.Active != 0, first.OffscreenCamera, first.ViewCamera, owned ? owner.Stage : null);
            return true;
        }
        catch (Exception) { snapshot = default; return false; }
    }

    private static bool Qualified(PortraitOwnedSceneSlotPhase phase) => phase.Source.IsValid
        && phase.Source.Generation != 0 && phase.Roots.IsValid && phase.SessionEpoch > 0 && phase.ManagerGeneration > 0
        && phase.PhaseToken > 0 && phase.InNativePhase && phase.BuildValidated
        && phase.ThreadID == Environment.CurrentManagedThreadId;

    private bool Current(PortraitOwnedSceneSlotPhase expected)
        => Qualified(expected) && phases.TryCapture(out var before) && before == expected
            && native.TryReadRoots(out var roots) && roots == expected.Roots
            && phases.TryCapture(out var after) && after == expected;

    private bool Read<T>(nint address, PortraitOwnedSceneSlotPhase phase, out T value) where T : unmanaged
    {
        value = default;
        if (address == 0 || !Current(phase) || !native.TryRead(address, out value) || !Current(phase))
        { value = default; return false; }
        return true;
    }

    private bool ReadSource(PortraitOwnedSceneSlotPhase phase, out SourceBinding binding)
    {
        binding = default;
        var source = phase.Source;
        if (!Read((nint)(&((AgentBannerEditor*)source.Editor)->EditorState), phase, out nint state) || state != source.State
            || !Read((nint)(&((AgentBannerEditorState*)state)->AgentBannerEditor), phase, out nint editor) || editor != source.Editor
            || !Read((nint)(&((AgentBannerEditorState*)state)->CharaView), phase, out nint view) || view != source.Model
            || !Read(view, phase, out ViewHeader header) || header.State != 6 || header.Slot >= 8
            || header.ID != 0xF0 + header.Slot || header.Context == 0 || header.Camera == 0
            || header.Agent != 0 && header.Agent != source.Editor
            || !Read(EntryAddress(phase.Roots, header.Slot), phase, out ObjectEntry entry)
            || entry.Character != (nint)source.Character || entry.Kind == 0
            || !Read(entry.Character + 0x74, phase, out uint generation) || generation != source.Generation
            || !Read((nint)(&((Character*)entry.Character)->DrawData.OwnerObject), phase, out nint drawOwner)
            || drawOwner != entry.Character
            || !Read(header.Context, phase, out ContextHeader context) || context.Slot != header.Slot
            || context.Type != header.CameraType || context.Active == 0 || context.References <= 0
            || !Read(phase.Roots.SceneCameras + 0xA0 + (nint)(header.Slot * 8), phase, out nint registered)
            || registered != header.Camera
            || !Read(phase.Roots.Renderer + 0x3835C + (nint)(header.Slot * 4), phase, out int count) || count <= 0) return false;
        binding = new(header.Slot, header.Context, header.Camera, generation, drawOwner, context.Type, context.References, count);
        return Current(phase);
    }

    private bool ReadSample(uint slot, PortraitOwnedSceneSlotOwner owner, PortraitOwnedSceneSlotPhase phase, bool owned,
        out Sample sample)
    {
        sample = default;
        var roots = phase.Roots;
        if (!Read(EntryAddress(roots, slot), phase, out ObjectEntry entry)
            || !Read(roots.SceneCameras + 0xA0 + (nint)(slot * 8), phase, out nint registered)
            || !Read(roots.Renderer + 0x3835C + (nint)(slot * 4), phase, out int count)
            || !Read(roots.Renderer + 0x3837C + (nint)slot, phase, out byte active)
            || !Read(roots.Offscreen + 0xC8 + (nint)(slot * 8), phase, out nint offscreen)
            || !Read(roots.Renderer + 0x2738 + (nint)(slot * 24), phase, out nint viewCamera)) return false;
        sample = new(entry.Character, entry.Kind, entry.Index, entry.MemoryIndex, registered, count, active, offscreen, viewCamera);
        if (!owned) return true;
        // A foreign actor/view is never dereferenced merely to explain why ownership failed.
        if (entry.Character != owner.Character || !Read(owner.View, phase, out ViewHeader header)
            || header.ID != 0xF0 + slot || header.Slot != slot || header.Context != owner.Context || header.Camera != owner.Camera
            || !Read(owner.Character + 0x74, phase, out uint generation)
            || !Read((nint)(&((Character*)owner.Character)->DrawData.OwnerObject), phase, out nint drawOwner)) return false;
        ContextHeader context = default;
        if (owner.Stage == PortraitOwnedSceneSlotStage.Prepared
            && (!Read(owner.Context, phase, out context) || context.Type != header.CameraType)) return false;
        sample = sample with { ViewState = header.State, Generation = generation, DrawOwner = drawOwner,
            Context = header.Context, Camera = header.Camera, ContextSlot = context.Slot, ContextType = context.Type,
            ContextReferences = context.References, ContextActive = context.Active };
        return true;
    }

    private static nint EntryAddress(PortraitOwnedSceneSlotRoots roots, uint slot)
        => roots.ClientObjects + 0x10 + (nint)((0xF0 + slot) * 0x10);

    private static bool SDKLayoutMatches() => sizeof(RenderManager) == 0x38390 && sizeof(SceneCameraManager) == 0x130
        && sizeof(OffscreenManager) == 0x190 && sizeof(RenderTargetManager) == 0x740 && sizeof(ClientObjectManager) == 0xFA0
        && sizeof(ClientObjectManager.BattleCharaEntry) == 0x10
        && Marshal.OffsetOf<CharaView>(nameof(CharaView.State)) == 8
        && Marshal.OffsetOf<CharaView>(nameof(CharaView.ClientObjectId)) == 0xC
        && Marshal.OffsetOf<CharaView>(nameof(CharaView.ClientObjectIndex)) == 0x10
        && Marshal.OffsetOf<CharaView>(nameof(CharaView.CameraManager)) == 0x18
        && Marshal.OffsetOf<CharaView>(nameof(CharaView.Camera)) == 0x20
        && Marshal.OffsetOf<CharaView>(nameof(CharaView.Agent)) == 0x30
        && Marshal.OffsetOf<ClientObjectManager.BattleCharaEntry>(nameof(ClientObjectManager.BattleCharaEntry.ObjectKind)) == 8
        && Marshal.OffsetOf<ClientObjectManager.BattleCharaEntry>(nameof(ClientObjectManager.BattleCharaEntry.Index)) == 0xC
        && Marshal.OffsetOf<ClientObjectManager.BattleCharaEntry>(nameof(ClientObjectManager.BattleCharaEntry.MemoryIndex)) == 0xE;
}
