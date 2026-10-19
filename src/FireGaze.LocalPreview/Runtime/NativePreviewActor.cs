using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>
/// A client-object-manager-owned display actor. Call every member on the framework thread.
/// Native presentation operations may target Actor; the real player's gameplay state is never
/// copied back. This is separate from the read-only, unregistered HMS capture buffers.
/// </summary>
public sealed unsafe class NativePreviewActor : IDisposable
{
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
    private readonly GroundedPreviewMotion motion = new();
    private GroundedFlightPoseClips flightPoseClips = GroundedFlightPoseClips.Standard;
    private readonly IPluginLog? log;
    private ulong allocationId;
    private bool hasAllocationId;
    private VisibilityLease sourceVisibility;
    private bool drawRequested;
    private bool bodyWasVisible;
    private bool sourceSuppressionActive;
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
        sourceVisibility = new VisibilityLease(source);
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

    /// <summary>Stores only verified display-loop IDs. No native state is changed.</summary>
    public void SetFlightPoseClips(GroundedFlightPoseClips clips)
    {
        if (clips.IsValid) flightPoseClips = clips;
    }

    // Read-only ownership check for native detours. Generated singleton reads only;
    // no native object lookup may re-enter while the engine is updating this actor.
    public bool IsCurrentAllocation(NativeCharacter* actor)
    {
        if (disposed || actor == null || actor != allocatedActor) return false;
        var source = (NativeCharacter*)Control.GetLocalPlayer();
        return source != null && source == sourceAddress && Generation(source) == sourceGeneration
            && ClientObjectManager.Instance() == manager && slot < manager->BattleCharas.Length
            && manager->BattleCharas[slot].BattleChara == (BattleChara*)actor
            && actor->ObjectIndex == objectIndex && Generation(actor) == allocationGeneration;
    }

    /// <summary>Read-only identity guard for the genuine local actor update hook.</summary>
    public bool IsCurrentSource(NativeCharacter* actor)
        => !disposed && actor != null && actor == sourceAddress && GetSource() == actor;

    /// <summary>
    /// Reapply the existing source draw lease after the client has refreshed a real
    /// actor. This is deliberately limited to an already-ready, owned preview and
    /// performs no native setup or object allocation.
    /// </summary>
    public bool ReassertSourceVisibility()
        => !disposed && IsReady && sourceSuppressionActive && SetSourceVisibility(true);

    /// <summary>Read-only mounted child guard; never performs native object lookups.</summary>
    public bool IsCurrentMountChild(NativeCharacter* child)
    {
        if (disposed || !IsReady || child == null || !IsCurrentAllocation(allocatedActor)) return false;
        var actor = allocatedActor;
        var source = sourceAddress;
        if (actor->Mount.OwnerObject != actor || actor->Mount.MountId == 0 || actor->Mount.MountObject != child
            || child == actor || child == source || child == source->ChildObject
            || child == source->Mount.MountObject || child == (NativeCharacter*)source->CompanionData.CompanionObject
            || child == (NativeCharacter*)source->OrnamentData.OrnamentObject
            || child == (NativeCharacter*)actor->CompanionData.CompanionObject
            || child == (NativeCharacter*)actor->OrnamentData.OrnamentObject
            || cloneChildren[0].Address != child || cloneChildren[0].Generation != Generation(child)
            || child->Timeline.OwnerObject != child || child->Timeline.TimelineSequencer.Parent != child
            || child->DrawObject == null || !child->DrawObject->IsVisible) return false;
        return true;
    }

    /// <summary>
    /// Read-only guard used inside the native weapon visibility queries. Only an existing
    /// source draw lease can suppress a result; no native lookup or presentation call is
    /// allowed here, because those can re-enter the query while the client updates a weapon.
    /// Logical slots 0..2 are the regular weapons; slot 3 is the extra weapon.
    /// </summary>
    public bool ShouldSuppressWeaponVisibility(DrawDataContainer* container, int weaponSlot)
    {
        if (disposed || !IsReady || !sourceSuppressionActive || container == null
            || weaponSlot is < 0 or > 3) return false;
        // Both getters below are generated static-address reads, not native functions.
        var source = (NativeCharacter*)Control.GetLocalPlayer();
        if (source == null || source != sourceAddress || Generation(source) != sourceGeneration
            || ClientObjectManager.Instance() != manager || slot >= manager->BattleCharas.Length
            || manager->BattleCharas[slot].BattleChara != (BattleChara*)allocatedActor) return false;
        if (allocatedActor->ObjectIndex != objectIndex || Generation(allocatedActor) != allocationGeneration
            || allocatedActor->DrawObject == null || !allocatedActor->DrawObject->IsVisible) return false;

        if (container == &source->DrawData)
            return container->OwnerObject == source
                && sourceVisibility.OwnsWeapon(source, weaponSlot);
        for (var i = 0; i < sourceChildren.Length; i++)
        {
            var child = GetChild(source, i);
            if (sourceChildren[i].MatchesRaw(child) && container == &child->DrawData)
                return container->OwnerObject == child
                    && sourceChildren[i].OwnsWeapon(child, weaponSlot);
        }
        return false;
    }

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
    /// Follow the genuine local player's transform, target and selected voice. Return true once the display
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

    /// <summary>Animates the owned rider and mount from read-only, verified locomotion loops.</summary>
    public void UpdateGroundedMotion(bool emoteActuallyActive, Func<bool>? canApply = null, bool flightPose = false)
    {
        if (disposed || !IsReady) return;
        var actor = GetOwnedActor();
        var source = GetSource();
        if (actor == null || source == null || !BindingMatches(source, actor)) return;
        var mountId = actor->Mount.MountId;
        var child = mountId != 0 ? actor->Mount.MountObject : null;
        var childGeneration = child != null ? Generation(child) : 0;
        NativeCharacter* sampledSourceMount = null;
        ushort sampledSourceMountId = 0;
        uint sampledSourceMountGeneration = 0;
        bool mountedSourceSample = false;

        bool MountedSourceCurrent() => source->Mode == CharacterModes.Mounted
            && source->Timeline.OwnerObject == source && source->Timeline.TimelineSequencer.Parent == source
            && source->Mount.OwnerObject == source && sampledSourceMountId != 0
            && source->Mount.MountId == sampledSourceMountId && source->Mount.MountObject == sampledSourceMount
            && sampledSourceMount != null && sampledSourceMount != source && sampledSourceMount != actor
            && sampledSourceMount != child && sampledSourceMount != (NativeCharacter*)source->CompanionData.CompanionObject
            && sampledSourceMount != (NativeCharacter*)source->OrnamentData.OrnamentObject
            && sampledSourceMount != (NativeCharacter*)actor->CompanionData.CompanionObject
            && sampledSourceMount != (NativeCharacter*)actor->OrnamentData.OrnamentObject
            && Generation(sampledSourceMount) == sampledSourceMountGeneration
            && sampledSourceMount->Timeline.OwnerObject == sampledSourceMount
            && sampledSourceMount->Timeline.TimelineSequencer.Parent == sampledSourceMount;

        bool Current()
        {
            // Native identity lookups and the request guard can re-enter plugin code.
            // Finish with raw allocation/container checks before every lane write.
            if (canApply is not null && !canApply() || !BindingMatches(source, actor)) return false;
            if (canApply is not null && !canApply()) return false;
            if (disposed || !IsReady || (NativeCharacter*)Control.GetLocalPlayer() != source
                || source != sourceAddress || Generation(source) != sourceGeneration
                || ClientObjectManager.Instance() != manager || slot >= manager->BattleCharas.Length
                || manager->BattleCharas[slot].BattleChara != (BattleChara*)actor
                || actor != allocatedActor || actor->ObjectIndex != objectIndex
                || Generation(actor) != allocationGeneration || actor->Mount.OwnerObject != actor
                || actor->Mount.MountId != mountId || actor->Timeline.OwnerObject != actor
                || actor->Timeline.TimelineSequencer.Parent != actor || actor->DrawObject == null
                || !actor->DrawObject->IsVisible) return false;
            if (mountedSourceSample && !MountedSourceCurrent()) return false;
            if (mountId == 0) return true;
            if (child == null || actor->Mount.MountObject != child || child == actor || child == source
                || child == source->ChildObject || child == source->Mount.MountObject
                || child == (NativeCharacter*)source->CompanionData.CompanionObject
                || child == (NativeCharacter*)source->OrnamentData.OrnamentObject
                || child == (NativeCharacter*)actor->CompanionData.CompanionObject
                || child == (NativeCharacter*)actor->OrnamentData.OrnamentObject
                || Generation(child) != childGeneration || cloneChildren[0].Address != child
                || cloneChildren[0].Generation != childGeneration || child->Timeline.OwnerObject != child
                || child->Timeline.TimelineSequencer.Parent != child || child->DrawObject == null
                || !child->DrawObject->IsVisible) return false;
            return true;
        }

        if (!Current()) { motion.Reset(); return; }
        var sourceTimeline = source->Timeline.TimelineSequencer.TimelineIds[0];
        if (sourceTimeline is 166 or 167 or 6709 or 4047 or 4048 or 6703)
        {
            // Genuine mounted loops are only visual inputs. The source hierarchy
            // must remain owned and current through both display-lane writes.
            // Flying rider_run shares walk/back/run; its child's verified loops
            // disambiguate walk directions. Rider glide uses our existing run
            // appearance, not native flight manoeuvres or takeoff/landing events.
            sampledSourceMount = source->Mount.MountObject;
            sampledSourceMountId = source->Mount.MountId;
            sampledSourceMountGeneration = sampledSourceMount != null ? Generation(sampledSourceMount) : 0;
            mountedSourceSample = MountedSourceCurrent();
            sourceTimeline = mountedSourceSample ? sourceTimeline switch
            {
                166 or 4047 => (ushort)3,
                6709 => (ushort)13,
                4048 => sampledSourceMount->Timeline.TimelineSequencer.TimelineIds[0] switch
                    { 4053 => (ushort)13, 4046 => (ushort)16, _ => (ushort)22 },
                _ => (ushort)22,
            } : (ushort)0;
        }
        // Handheld accessories have their own posture resolver. An on-foot source
        // cannot establish the corresponding accessory clip, so leave it native.
        var sample = GroundedMotionClassifier.Classify(sourceTimeline,
            actor->OrnamentData.OrnamentId == 0,
            source->Mode == CharacterModes.Normal && source->EmoteController.EmoteId == 0
                && (byte)source->EmoteController.CurrentPoseType == 0 && source->EmoteController.CPoseState > 0);
        // A planner is scoped to this allocation; generation 1 never crosses actors.
        var plan = motion.Plan(sample, new(1, mountId, (ulong)(nuint)child, childGeneration, flightPose, flightPoseClips), emoteActuallyActive,
            actor->Timeline.BaseOverride, child != null ? child->Timeline.BaseOverride : (ushort)0);
        if (!Current()) { motion.Reset(); return; }
        if (plan.WriteBaseOverride) actor->Timeline.BaseOverride = plan.BaseOverride;
        if (plan.PlayTimeline != 0) actor->Timeline.TimelineSequencer.PlayTimeline(plan.PlayTimeline);
        // The first native playback may replace an allocation, request or child.
        // Never continue the old plan on that hierarchy after re-entry.
        if (!Current()) { motion.Reset(); return; }
        if (child == null) return;
        if (plan.WriteMountBaseOverride) child->Timeline.BaseOverride = plan.MountBaseOverride;
        if (plan.PlayMountTimeline != 0) child->Timeline.TimelineSequencer.PlayTimeline(plan.PlayMountTimeline);
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
        if (!IsCurrentAllocation(actor) || source->Vfx.OwnerObject != source || actor->Vfx.OwnerObject != actor) return false;
        actor->TargetId = target;
        actor->SoftTargetId = softTarget;
        // VoiceId is outside CustomizeData. Both native appearance setup calls
        // choose a race/sex default, which need not be the player's selected voice.
        // Only the owned display actor follows the live player's current setting;
        // the native animation sound events still choose their normal variation.
        actor->Vfx.VoiceId = source->Vfx.VoiceId;
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
        sourceSuppressionActive = false;
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
            sourceVisibility.HideResolved(source);
            for (var i = 0; i < sourceChildren.Length; i++)
                sourceChildren[i].HideResolved(children[i]);
            sourceSuppressionActive = true;
        }
        else
        {
            sourceVisibility.ReleaseResolved(source);
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
        // Bit 0 controls presentation; bit 3 is rewritten by native culling. Keeping
        // the GameObject model flags untouched preserves its model and gameplay updates.
        private const byte VisibleBit = 1;
        // Body plus both render pointers for main hand, off hand, system and the
        // client's extra weapon slot. Aliases are collected only once per frame.
        private const int MaxDrawParts = 9;
        private readonly DrawPart[] parts;
        private int partCount;

        private struct DrawPart
        {
            public DrawObject* Draw;
            public nint Owner;
            public ulong Model;
            public byte RestoreBits;
            public readonly bool SameIdentity(in DrawPart other)
                => Draw == other.Draw && Owner == other.Owner && Model == other.Model;
        }

        public VisibilityLease(NativeCharacter* actor)
        {
            address = actor;
            generation = Generation(actor);
            id = actor->GetGameObjectId().Id;
            parts = new DrawPart[MaxDrawParts];
            partCount = 0;
        }

        public readonly bool MatchesRaw(NativeCharacter* current) => current != null && current == address
            && Generation(current) == generation;

        public readonly NativeCharacter* Resolve(NativeCharacter* current) => MatchesRaw(current)
            && current->GetGameObjectId().Id == id && MatchesRaw(current) ? current : null;

        public readonly bool OwnsWeapon(NativeCharacter* current, int weaponSlot)
        {
            if (!MatchesRaw(current) || parts == null || partCount == 0) return false;
            ref var weapon = ref (weaponSlot < 3
                ? ref current->DrawData.WeaponData[weaponSlot]
                : ref current->DrawData.UnkWeaponData[0]);
            var render = (DrawObject*)weapon.Weapon;
            var attached = weapon.DrawData.DrawObject;
            var owner = (nint)weapon.Weapon;
            var model = weapon.ModelId.Value;
            return (render != null || attached != null)
                && (render == null || OwnsPart(render, owner, model))
                && (attached == null || OwnsPart(attached, owner, model));
        }

        private readonly bool OwnsPart(DrawObject* draw, nint owner, ulong model)
        {
            for (var i = 0; i < partCount; i++)
                if (parts[i].Draw == draw && parts[i].Owner == owner && parts[i].Model == model) return true;
            return false;
        }

        public void HideResolved(NativeCharacter* current)
        {
            if (parts is null) return;
            Span<DrawPart> live = stackalloc DrawPart[MaxDrawParts];
            var liveCount = CollectParts(current, live);
            // Determine ownership before overwriting the previous set. A render pointer
            // that moved between aliased slots keeps one lease, never two restore owners.
            for (var i = 0; i < liveCount; i++)
            {
                for (var j = 0; j < partCount; j++)
                    if (live[i].SameIdentity(parts[j])) { live[i].RestoreBits = parts[j].RestoreBits; break; }
            }
            for (var i = 0; i < liveCount; i++)
            {
                live[i].RestoreBits |= (byte)(live[i].Draw->Flags & VisibleBit);
                live[i].Draw->Flags &= unchecked((byte)~VisibleBit);
                parts[i] = live[i];
            }
            // A removed or replaced draw may already be freed. Discard its lease
            // without dereferencing it; only currently bound render objects are touched.
            partCount = liveCount;
        }

        public void ReleaseResolved(NativeCharacter* current)
        {
            if (parts is null) return;
            Span<DrawPart> live = stackalloc DrawPart[MaxDrawParts];
            var liveCount = CollectParts(current, live);
            for (var i = 0; i < liveCount; i++)
                for (var j = 0; j < partCount; j++)
                    if (live[i].SameIdentity(parts[j]))
                    {
                        live[i].Draw->Flags |= parts[j].RestoreBits;
                        break;
                    }
            partCount = 0;
        }

        private static int CollectParts(NativeCharacter* current, Span<DrawPart> live)
        {
            if (current == null) return 0;
            var count = 0;
            AddPart(live, ref count, current->DrawObject, (nint)current, 0);
            for (var i = 0; i < current->DrawData.WeaponData.Length; i++)
                AddWeapon(live, ref count, ref current->DrawData.WeaponData[i]);
            for (var i = 0; i < current->DrawData.UnkWeaponData.Length; i++)
                AddWeapon(live, ref count, ref current->DrawData.UnkWeaponData[i]);
            return count;
        }

        private static void AddWeapon(Span<DrawPart> live, ref int count, ref DrawObjectData weapon)
        {
            AddPart(live, ref count, (DrawObject*)weapon.Weapon, (nint)weapon.Weapon, weapon.ModelId.Value);
            AddPart(live, ref count, weapon.DrawData.DrawObject, (nint)weapon.Weapon, weapon.ModelId.Value);
        }

        private static void AddPart(Span<DrawPart> live, ref int count, DrawObject* draw, nint owner, ulong model)
        {
            if (draw == null) return;
            for (var i = 0; i < count; i++) if (live[i].Draw == draw) return;
            live[count++] = new DrawPart { Draw = draw, Owner = owner, Model = model };
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
