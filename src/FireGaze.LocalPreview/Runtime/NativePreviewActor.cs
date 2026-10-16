using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>
/// A client-object-manager-owned display actor. Call every member on the framework thread.
/// Native presentation operations may target Actor; the real player's gameplay state is never
/// copied back. This is separate from the read-only, unregistered HMS capture buffers.
/// </summary>
public sealed unsafe class NativePreviewActor : IDisposable
{
    private const VisibilityFlags Hidden = (VisibilityFlags)0x02;
    // The reviewed CN GameObject initializer allocates this generation at RVA 0x89D45A.
    // Unlike GetGameObjectId, it changes when the same local object slot is reused.
    private const int GenerationOffset = 0x74;
    private const int DrawTimeoutFrames = 180;
    private readonly ClientObjectManager* manager;
    private readonly ushort slot;
    private readonly ushort objectIndex;
    private readonly NativeCharacter* allocatedActor;
    private readonly uint allocationGeneration;
    private readonly NativeCharacter* sourceAddress;
    private readonly ulong sourceId;
    private readonly uint sourceGeneration;
    private readonly VisibilityLease[] sourceChildren = new VisibilityLease[3];
    private readonly ChildDrawState[] cloneChildren = new ChildDrawState[3];
    private readonly IPluginLog? log;
    private ulong allocationId;
    private bool hasAllocationId;
    private bool ownsSourceHide;
    private bool drawRequested;
    private bool bodyWasVisible;
    private int drawFrames;
    private bool disposed;

    private NativePreviewActor(ClientObjectManager* manager, ushort slot, NativeCharacter* actor,
        NativeCharacter* source, IPluginLog? log)
    {
        this.manager = manager;
        this.slot = slot;
        allocatedActor = actor;
        objectIndex = actor->ObjectIndex;
        allocationGeneration = Generation(actor);
        sourceAddress = source;
        sourceId = source->GetGameObjectId().Id;
        sourceGeneration = Generation(source);
        this.log = log;
        for (var i = 0; i < sourceChildren.Length; i++)
        {
            var child = GetChild(source, i);
            if (child != null) sourceChildren[i] = new VisibilityLease(child);
        }
    }

    /// <summary>Null after disposal or when the owned allocation has been replaced.</summary>
    public NativeCharacter* Actor => !disposed ? GetOwnedActor() : null;
    public ushort ObjectIndex => objectIndex;
    public bool IsReady { get; private set; }
    public bool IsDisposed => disposed;

    /// <summary>
    /// Reserve a character and its companion slot, then copy appearance and equipment through
    /// the client's setup routine. Mode, mounts, minions, ornaments and emotes stay independent.
    /// Null means no display actor was retained. This never hides the source during creation.
    /// </summary>
    public static NativePreviewActor? Create(NativeCharacter* source, IPluginLog? log = null)
    {
        NativePreviewActor? result = null;
        ClientObjectManager* owner = null;
        uint createdSlot = uint.MaxValue;
        NativeCharacter* createdActor = null;
        uint createdGeneration = 0;
        try
        {
            if (source == null || sizeof(GameObject) != 0x1A0 || sizeof(NativeCharacter) != 0x2370
                || (NativeCharacter*)Control.GetLocalPlayer() != source) return null;
            owner = ClientObjectManager.Instance();
            if (owner == null) return null;
            createdSlot = owner->CreateBattleCharacter(uint.MaxValue, 1);
            if (createdSlot >= owner->BattleCharas.Length) return null;

            // Read the manager entry before another native setup call can re-enter plugin code.
            createdActor = (NativeCharacter*)owner->BattleCharas[(int)createdSlot].BattleChara;
            if (createdActor == null)
            {
                owner->DeleteObjectByIndex((ushort)createdSlot, 0);
                return null;
            }
            createdGeneration = Generation(createdActor);
            result = new(owner, (ushort)createdSlot, createdActor, source, log);
            if (owner->GetObjectByIndex((ushort)createdSlot) != createdActor) throw new InvalidOperationException("Preview allocation changed during creation.");

            // CopyFromCharacter copies customize/model/equipment by value. The self-copy lets
            // appearance tools resolve the new actor, matching the established native workflow.
            createdActor->CharacterSetup.CopyFromCharacter(source,
                CharacterSetupContainer.CopyFlags.WeaponHiding | CharacterSetupContainer.CopyFlags.ClassJob);
            // Native setup can re-enter hooks. Never issue the next operation on a replaced slot.
            if (result.GetOwnedActor() != createdActor || result.GetSource() != source)
                throw new InvalidOperationException("Preview allocation changed during native setup.");
            createdActor->CharacterSetup.CopyFromCharacter(createdActor, CharacterSetupContainer.CopyFlags.None);
            if (result.GetOwnedActor() != createdActor || result.GetSource() != source)
                throw new InvalidOperationException("Preview allocation changed during native setup.");
            result.allocationId = createdActor->GetGameObjectId().Id;
            result.hasAllocationId = true;
            if (result.GetOwnedActor() == null || result.GetSource() == null
                || createdActor->ChildObject == null || createdActor->ChildObject == source->ChildObject
                || createdActor->Timeline.OwnerObject != createdActor
                || createdActor->Mount.OwnerObject != createdActor
                || createdActor->CompanionData.OwnerObject != createdActor
                || createdActor->OrnamentData.OwnerObject != createdActor)
                throw new InvalidOperationException("Preview actor does not own its native containers.");

            // The display actor must not become a second gameplay target at the player's feet.
            createdActor->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
            if (!result.Follow(source, createdActor))
                throw new InvalidOperationException("Preview binding changed while following its source.");
            createdActor->DefaultPosition = source->Position;
            createdActor->DefaultRotation = source->Rotation;
            return result;
        }
        catch (Exception ex)
        {
            try { log?.Warning(ex, "[LocalPreview] Could not create the local display actor."); } catch { }
            if (result != null) result.Dispose();
            else if (owner != null && createdSlot < owner->BattleCharas.Length && createdActor != null
                && owner->BattleCharas[(int)createdSlot].BattleChara == (BattleChara*)createdActor
                && Generation(createdActor) == createdGeneration)
            {
                try { owner->DeleteObjectByIndex((ushort)createdSlot, 0); } catch { }
            }
            return null;
        }
    }

    /// <summary>
    /// Follow the genuine local player's transform and target. Return true once the display
    /// actor is visible. Presentation state on the clone is left to the preview runtime.
    /// </summary>
    public bool Tick(NativeCharacter* source)
    {
        if (disposed) return false;
        try
        {
            var actor = GetOwnedActor();
            if (source == null || source != GetSource() || actor == null) { Dispose(); return false; }
            if (!Follow(source, actor)) { Dispose(); return false; }
            drawFrames++;
            // A draw can disappear while resources or a native attachment are rebuilding.
            if (drawRequested && bodyWasVisible && actor->DrawObject == null)
            {
                drawRequested = false;
                bodyWasVisible = false;
            }
            if (!drawRequested)
            {
                if (drawFrames >= 2 && actor->GameObject.IsReadyToDraw())
                {
                    if (!BindingMatches(source, actor)) { Dispose(); return false; }
                    actor->GameObject.EnableDraw();
                    if (!BindingMatches(source, actor)) { Dispose(); return false; }
                    drawRequested = true;
                }
            }
            var draw = actor->DrawObject;
            IsReady = drawRequested && draw != null && draw->IsVisible;
            if (IsReady) bodyWasVisible = true;
            for (var i = 0; i < cloneChildren.Length; i++)
            {
                var childReady = TickChildDraw(source, actor, i);
                if (!BindingMatches(source, actor)) { Dispose(); return false; }
                IsReady &= childReady;
            }
            if (IsReady)
            {
                if (!SetSourceVisibility(true)) { Dispose(); return false; }
                drawFrames = 0;
            }
            else
            {
                ReleaseSourceHide();
                if (drawFrames >= DrawTimeoutFrames) Dispose();
            }
            return IsReady;
        }
        catch (Exception ex)
        {
            try { log?.Warning(ex, "[LocalPreview] Local display actor stopped."); } catch { }
            Dispose();
            return false;
        }
    }

    private bool BindingMatches(NativeCharacter* source, NativeCharacter* actor) =>
        GetSource() == source && GetOwnedActor() == actor;

    private bool TickChildDraw(NativeCharacter* source, NativeCharacter* actor, int index)
    {
        if (!BindingMatches(source, actor)) return false;
        var child = GetChild(actor, index);
        if (child == null) { cloneChildren[index] = default; return true; }
        // An attachment must belong to the display hierarchy, never to the real player.
        if (child == source || child == source->ChildObject) return false;
        for (var i = 0; i < sourceChildren.Length; i++)
            if (child == GetChild(source, i)) return false;

        ref var state = ref cloneChildren[index];
        var generation = Generation(child);
        if (state.Address != child || state.Generation != generation)
            state = new ChildDrawState { Address = child, Generation = generation };
        child->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        // Each new attachment gets at least one framework frame to finish native setup.
        state.Frames++;
        var draw = child->DrawObject;
        if (draw != null && draw->IsVisible) { state.WasVisible = true; return true; }
        if (state.WasVisible && draw == null) { state.Requested = false; state.WasVisible = false; }
        if (state.Requested || state.Frames < 2 || !child->GameObject.IsReadyToDraw()) return false;
        if (!BindingMatches(source, actor) || GetChild(actor, index) != child
            || Generation(child) != generation) return false;
        child->GameObject.EnableDraw();
        if (!BindingMatches(source, actor) || GetChild(actor, index) != child
            || Generation(child) != generation) return false;
        state.Requested = true;
        draw = child->DrawObject;
        state.WasVisible = draw != null && draw->IsVisible;
        return state.WasVisible;
    }

    private struct ChildDrawState
    {
        public NativeCharacter* Address;
        public uint Generation;
        public int Frames;
        public bool Requested;
        public bool WasVisible;
    }

    private bool Follow(NativeCharacter* source, NativeCharacter* actor)
    {
        var position = source->Position;
        var rotation = source->Rotation;
        // Local-player targets are resolved through TargetSystem by these native getters;
        // the Character fields can still contain the previous target.
        var target = source->GetTargetId();
        var softTarget = source->GetSoftTargetId();
        if (GetSource() != source || GetOwnedActor() != actor) return false;
        actor->GameObject.SetPosition(position.X, position.Y, position.Z);
        if (GetSource() != source || GetOwnedActor() != actor) return false;
        actor->GameObject.SetRotation(rotation);
        if (GetSource() != source || GetOwnedActor() != actor) return false;
        actor->TargetId = target;
        actor->SoftTargetId = softTarget;
        return true;
    }

    private NativeCharacter* GetSource()
    {
        var current = (NativeCharacter*)Control.GetLocalPlayer();
        // Never dereference the old source after logout, a zone change or actor replacement.
        return current == sourceAddress && current != null && Generation(current) == sourceGeneration
            && current->GetGameObjectId().Id == sourceId ? current : null;
    }

    private NativeCharacter* GetOwnedActor()
    {
        if (GetRawOwnedActor() == null) return null;
        var current = manager->GetObjectByIndex(slot);
        return current == allocatedActor && GetRawOwnedActor() == current ? current : null;
    }

    // Cleanup must not depend on a native lookup that may be the reason setup failed.
    private NativeCharacter* GetRawOwnedActor()
    {
        if (ClientObjectManager.Instance() != manager || slot >= manager->BattleCharas.Length
            || manager->BattleCharas[slot].BattleChara != (BattleChara*)allocatedActor) return null;
        var current = allocatedActor;
        if (current->ObjectIndex != objectIndex || Generation(current) != allocationGeneration
            || (hasAllocationId && current->GetGameObjectId().Id != allocationId)) return null;
        return manager->BattleCharas[slot].BattleChara == (BattleChara*)current
            && current->ObjectIndex == objectIndex && Generation(current) == allocationGeneration ? current : null;
    }

    private static uint Generation(NativeCharacter* actor) => *(uint*)((byte*)actor + GenerationOffset);

    private void ReleaseSourceHide()
    {
        SetSourceVisibility(false);
    }

    // Resolve all native identities before changing any flags. The final pointer/generation
    // checks and flag writes contain no native calls, so root and attachments change together.
    private bool SetSourceVisibility(bool hide)
    {
        var source = GetSource();
        if (source == null) return false;
        NativeCharacter** children = stackalloc NativeCharacter*[3];
        for (var i = 0; i < sourceChildren.Length; i++)
            children[i] = sourceChildren[i].Resolve(GetChild(source, i));
        if (GetSource() != source) return false;
        for (var i = 0; i < sourceChildren.Length; i++)
            if (children[i] != null && (GetChild(source, i) != children[i]
                || !sourceChildren[i].MatchesRaw(children[i]))) children[i] = null;
        if (hide)
        {
            if ((source->RenderFlags & Hidden) == 0)
            {
                source->RenderFlags |= Hidden;
                ownsSourceHide = true;
            }
            for (var i = 0; i < sourceChildren.Length; i++)
                sourceChildren[i].HideResolved(children[i]);
        }
        else
        {
            if (ownsSourceHide) source->RenderFlags &= ~Hidden;
            ownsSourceHide = false;
            for (var i = 0; i < sourceChildren.Length; i++)
                sourceChildren[i].ReleaseResolved(children[i]);
        }
        return true;
    }

    private static NativeCharacter* GetChild(NativeCharacter* actor, int index) => index switch
    {
        0 => actor->Mount.MountObject,
        1 => (NativeCharacter*)actor->CompanionData.CompanionObject,
        _ => (NativeCharacter*)actor->OrnamentData.OrnamentObject,
    };

    private struct VisibilityLease
    {
        private readonly NativeCharacter* address;
        private readonly uint generation;
        private readonly ulong id;
        private bool ownsHide;

        public VisibilityLease(NativeCharacter* actor)
        {
            address = actor;
            generation = Generation(actor);
            id = actor->GetGameObjectId().Id;
            ownsHide = false;
        }

        public readonly bool MatchesRaw(NativeCharacter* current) => current != null && current == address
            && Generation(current) == generation;

        public readonly NativeCharacter* Resolve(NativeCharacter* current) => MatchesRaw(current)
            && current->GetGameObjectId().Id == id && MatchesRaw(current) ? current : null;

        public void HideResolved(NativeCharacter* current)
        {
            if (current == null || (current->RenderFlags & Hidden) != 0) return;
            current->RenderFlags |= Hidden;
            ownsHide = true;
        }

        public void ReleaseResolved(NativeCharacter* current)
        {
            var restore = ownsHide;
            ownsHide = false;
            if (restore && current != null) current->RenderFlags &= ~Hidden;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        IsReady = false;
        // Restore the live source before tearing down the display actor. No draw is destroyed
        // on the real character and no saved gameplay state is written back.
        try { ReleaseSourceHide(); }
        catch (Exception ex) { try { log?.Warning(ex, "[LocalPreview] Could not release display visibility."); } catch { } }
        try
        {
            if (GetRawOwnedActor() != null) manager->DeleteObjectByIndex(slot, 0);
        }
        catch (Exception ex) { try { log?.Warning(ex, "[LocalPreview] Could not delete the owned display actor."); } catch { } }
    }
}
