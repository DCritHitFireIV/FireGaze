using System.Numerics;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>Detached, bounded lifecycle evidence; no live pointer is retained by consumers.</summary>
public readonly record struct CompanionDisplayLifecycleEvidence(
    bool EverReady, string FirstPendingPhase, string LastPendingPhase,
    ulong LastObservedDrawAddress, ulong LastReadyDrawAddress, byte DrawFlags, byte DrawLoadState,
    bool? NativeReadinessFlag, ulong HostRenderFlags, ulong ChildRenderFlags);

internal enum NativeCompanionAllocationStatus
{
    Failed,
    Created,
    CapacityPending,
}

/// <summary>
/// One independent native companion and its client-object-manager-owned, invisible host.
/// All operations must run on the Framework thread. The host is never copied from the
/// player and never enabled for drawing. The genuine player's companion slot is read only.
/// </summary>
public sealed unsafe partial class NativeCompanionDisplayActor : IDisposable
{
    // Same initializer generation audited by NativePreviewActor; changes on pointer/slot ABA.
    private const int GenerationOffset = 0x74;
    private static readonly TimeSpan DrawTimeout = TimeSpan.FromSeconds(12);
    private readonly TimeProvider clock = TimeProvider.System;
    private readonly ClientObjectManager* manager;
    private readonly ushort slot;
    private readonly NativeCharacter* owner;
    private readonly ushort ownerIndex;
    private readonly uint ownerGeneration;
    private readonly NativeCharacter* source;
    private readonly uint sourceGeneration;
    private readonly uint sourceEntityId;
    private readonly IPluginLog? log;
    private Companion* child;
    private ushort childIndex;
    private uint childGeneration;
    private int frames;
    private long drawWaitStarted;
    private bool drawWaitActive;
    private bool drawRequested;
    private bool childDetached;
    private bool retirementRequested;
    private bool deleteIssued;
    private bool retiring;
    private bool disposed;
    private nint provenVisibleDraw;
    private long provenVisibleBindingRevision;
    private byte provenVisibleLoadState;
    private CompanionDisplayLifecycleEvidence lifecycleEvidence = new(false, "", "", 0, 0, 0, 0, null, 0, 0);

    private NativeCompanionDisplayActor(ClientObjectManager* manager, ushort slot,
        NativeCharacter* owner, NativeCharacter* source, ushort companionId, ushort expectedModelId, IPluginLog? log)
    {
        this.manager = manager;
        this.slot = slot;
        this.owner = owner;
        ownerIndex = owner->ObjectIndex;
        ownerGeneration = Generation(owner);
        this.source = source;
        sourceGeneration = Generation(source);
        sourceEntityId = source->EntityId;
        CompanionId = companionId;
        ExpectedModelId = expectedModelId;
        this.log = log;
    }

    /// <summary>Fires before deletion, including an internal failure, to clear owned target references.</summary>
    public event Action? Retiring;
    /// <summary>Optional host gate: false retains this actor until target cleanup can be retried.</summary>
    public Func<bool>? BeforeRetire { get; set; }
    public ushort CompanionId { get; }
    /// <summary>Companion.Model.RowId retained for diagnostics; native child identity is CompanionId.</summary>
    public ushort ExpectedModelId { get; }
    public ushort OwnerObjectIndex => ownerIndex;
    public ushort CompanionObjectIndex => IsCurrentCompanion(child) ? childIndex : ushort.MaxValue;
    public Companion* Companion => IsCurrentCompanion(child) ? child : null;
    /// <summary>Internal diagnostics only: address of the hidden owned host.</summary>
    public nint OwnerAddress => (nint)owner;
    /// <summary>Internal diagnostics only: raw child address, including a stale child during teardown.</summary>
    public nint ChildAddress => (nint)child;
    public bool IsReady { get; private set; }
    /// <summary>Continuity only: exact previously ready draw is currently hidden by native code.
    /// This does not promise visible output or authorize native interaction.</summary>
    public bool IsNativeHidden { get; private set; }
    public CompanionDisplayLifecycleEvidence LifecycleEvidence => lifecycleEvidence;
    public bool IsDisposed => retirementRequested;
    public bool IsPhysicallyRetired { get; private set; }
    public string FailureReason { get; private set; } = string.Empty;
    /// <summary>Internal lifecycle phase; never shown in the player-facing UI.</summary>
    public string DiagnosticPhase { get; private set; } = "allocation";

    /// <summary>
    /// Creates a reserved independent child slot. No appearance copy, player hiding,
    /// native player companion setup, or host draw request occurs on this route.
    /// </summary>
    public static NativeCompanionDisplayActor? Create(NativeCharacter* source, ushort companionId, IPluginLog? log = null)
        => Create(source, companionId, companionId, log);

    /// <summary>
    /// Creates a display actor using the UI/catalog Companion row id and retains the
    /// catalog model row separately for diagnostics. The native client APIs identify
    /// both SetupCompanion and the resulting child BaseId by the Companion sheet row.
    /// </summary>
    public static NativeCompanionDisplayActor? Create(NativeCharacter* source, ushort companionId,
        ushort expectedModelId, IPluginLog? log = null)
        => Create(source, companionId, expectedModelId, log, out _);

    /// <summary>Allocation-aware overload used by the FIFO session. UINT_MAX from the
    /// native automatic allocator means no host/child pair was allocated; setup failures
    /// after a valid slot remain Failed so partial resources are never hidden as capacity.
    /// </summary>
    internal static NativeCompanionDisplayActor? Create(NativeCharacter* source, ushort companionId,
        ushort expectedModelId, IPluginLog? log, out NativeCompanionAllocationStatus status)
        => Create(source, companionId, expectedModelId, null, log, out status);

    /// <summary>Initializes only the owned host before native child setup. The caller
    /// reserves this horizontal anchor from the same planner used by later frames.</summary>
    internal static NativeCompanionDisplayActor? Create(NativeCharacter* source, ushort companionId,
        ushort expectedModelId, Vector3? initialAnchor, IPluginLog? log, out NativeCompanionAllocationStatus status)
    {
        status = NativeCompanionAllocationStatus.Failed;
        NativeCompanionDisplayActor? result = null;
        ClientObjectManager* manager = null;
        NativeCharacter* allocated = null;
        uint allocatedSlot = uint.MaxValue;
        uint allocatedGeneration = 0;
        try
        {
            if (source == null || companionId == 0 || companionId > short.MaxValue
                || (initialAnchor is { } requested && (!float.IsFinite(requested.X)
                    || !float.IsFinite(requested.Y) || !float.IsFinite(requested.Z)))
                || (NativeCharacter*)Control.GetLocalPlayer() != source
                || sizeof(GameObject) != 0x1A0 || sizeof(NativeCharacter) != 0x2370
                || sizeof(Companion) != 0x2470
                || ClientObjectManager.Addresses.CreateBattleCharacter.Value == 0
                || ClientObjectManager.Addresses.GetObjectByIndex.Value == 0
                || ClientObjectManager.Addresses.DeleteObjectByIndex.Value == 0
                || CompanionContainer.Addresses.SetupCompanion.Value == 0) return null;
            uint generation = Generation(source);
            uint entityId = source->EntityId;
            manager = ClientObjectManager.Instance();
            if (manager == null) return null;
            allocatedSlot = manager->CreateBattleCharacter(uint.MaxValue, 1);
            if (allocatedSlot == uint.MaxValue)
            {
                status = NativeCompanionAllocationStatus.CapacityPending;
                return null;
            }
            if (allocatedSlot >= manager->BattleCharas.Length) return null;
            allocated = (NativeCharacter*)manager->BattleCharas[(int)allocatedSlot].BattleChara;
            if (allocated == null) return null;
            allocatedGeneration = Generation(allocated);
            result = new(manager, (ushort)allocatedSlot, allocated, source, companionId, expectedModelId, log);
            if ((NativeCharacter*)Control.GetLocalPlayer() != source || Generation(source) != generation
                || source->EntityId != entityId || result.GetOwnedHost() != allocated
                || allocated == source || allocated->CompanionData.OwnerObject != allocated
                || allocated->DrawObject != null)
                throw new InvalidOperationException("Independent companion host allocation changed or acquired a draw.");

            // The client companion setup expects a fully initialized CharacterSetup container.
            // Copy the source setup into the hidden host by value, then self-normalize the
            // container just like the ordinary preview actor. The host is never drawn, so this
            // does not create a second visible player or mutate the real actor.
            result.DiagnosticPhase = "companion-setup";
            allocated->CharacterSetup.CopyFromCharacter(source,
                CharacterSetupContainer.CopyFlags.WeaponHiding | CharacterSetupContainer.CopyFlags.ClassJob);
            if ((NativeCharacter*)Control.GetLocalPlayer() != source || Generation(source) != generation
                || source->EntityId != entityId || result.GetOwnedHost() != allocated
                || allocated->DrawObject != null)
                throw new InvalidOperationException("Independent companion host setup changed or acquired a draw.");
            allocated->CharacterSetup.CopyFromCharacter(allocated, CharacterSetupContainer.CopyFlags.None);
            if ((NativeCharacter*)Control.GetLocalPlayer() != source || Generation(source) != generation
                || source->EntityId != entityId || result.GetOwnedHost() != allocated
                || allocated->DrawObject != null || allocated->CompanionData.OwnerObject != allocated)
                throw new InvalidOperationException("Independent companion host setup lost ownership.");

            // The hidden host drives native Companion behavior independently for each lease.
            allocated->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
            // Legacy callers choose the genuine current source anchor only after
            // source identity and copied host setup have passed their original gates.
            var requestedAnchor = initialAnchor ?? (Vector3)source->Position;
            var hostAnchor = new Vector3(requestedAnchor.X, source->Position.Y, requestedAnchor.Z);
            if (!result.Follow(hostAnchor, source->Rotation))
                throw new InvalidOperationException("Independent companion source changed while positioning.");
            allocated->DefaultPosition = hostAnchor;
            allocated->DefaultRotation = source->Rotation;
            if (!result.BindingCurrent()) throw new InvalidOperationException("Independent companion binding changed before setup.");
            // SetupCompanion takes the Companion sheet row id. Companion.Model.RowId is
            // a separate catalog/model diagnostic and must never be used as this id.
            allocated->CompanionData.SetupCompanion((short)companionId, 0);
            result.DiagnosticPhase = "child-binding";
            if (!result.BindingCurrent() || !result.TryBindChild())
                throw new InvalidOperationException("Independent companion setup did not preserve ownership.");
            // Reserved BattleChara hosts start with Nameplate suppression. Current
            // CN Companion.Update treats that host flag as invisible and propagates
            // flag15 to the child, even after its model has finished loading.
            // Remove only this flag on our exact, empty-name, non-targetable host.
            // It retains no DrawObject and never receives EnableDraw; all other
            // native visibility restrictions remain intact. The real player and
            // its own companion are untouched.
            allocated->RenderFlags &= ~VisibilityFlags.Nameplate;
            status = NativeCompanionAllocationStatus.Created;
            return result;
        }
        catch (Exception ex)
        {
            try { log?.Warning(ex, "[LocalPreview] Independent companion creation refused."); } catch { }
            if (result != null)
            {
                result.FailureReason = "Independent companion creation failed.";
                if (!result.TryRetire()) return result;
            }
            else if (manager != null && ClientObjectManager.Instance() == manager
                && allocatedSlot < manager->BattleCharas.Length && allocated != null
                && manager->BattleCharas[(int)allocatedSlot].BattleChara == (BattleChara*)allocated
                && Generation(allocated) == allocatedGeneration)
            {
                try { manager->DeleteObjectByIndex((ushort)allocatedSlot, 0); } catch { }
            }
            return null;
        }
    }

    /// <summary>
    /// Moves only the invisible host. Native follow/wander/swim animation and the child's
    /// transform remain controlled by the client; a stationary pet is never teleported every frame.
    /// </summary>
    public bool Tick(NativeCharacter* currentSource, Vector3 anchor, float rotation)
        => TickCore(currentSource, anchor, rotation, moveHost: true);

    /// <summary>Refreshes model readiness after the separately receipted follow request.</summary>
    internal bool Observe(NativeCharacter* currentSource)
        => currentSource != null && TickCore(currentSource, (Vector3)currentSource->Position, currentSource->Rotation, moveHost: false);

    private bool TickCore(NativeCharacter* currentSource, Vector3 anchor, float rotation, bool moveHost)
    {
        if (disposed || retiring || retirementRequested) return false;
        // Publish this observation, never the previous frame's draw readiness.
        // A proven lease can survive a temporary native child-link gap while
        // callers must treat that gap as pending and avoid stale target access.
        IsReady = false;
        IsNativeHidden = false;
        try
        {
            if (currentSource != source || !BindingCurrent()) return Fail("Companion source or host allocation changed.");
            if (!float.IsFinite(anchor.X) || !float.IsFinite(anchor.Y) || !float.IsFinite(anchor.Z)
                || !float.IsFinite(rotation)) return Fail("Companion anchor is invalid.");
            if (moveHost && !Follow(anchor, rotation)) return Fail("Companion binding changed during follow.");
            if (!TryBindChild()) return Fail("Companion child identity changed.");
            frames++;
            if (!drawWaitActive) { drawWaitStarted = clock.GetTimestamp(); drawWaitActive = true; }
            if (child == null)
            {
                ClearVisibleProof();
                SetPendingPhase("child-binding");
                return clock.GetElapsedTime(drawWaitStarted) >= DrawTimeout ? Fail("Companion child setup timed out.") : false;
            }

            var current = child;
            if (!IsCurrentCompanion(current))
            {
                // During movement the client may briefly clear the native child
                // link while refreshing its animation state. Keep the proven
                // actor lease alive through that transient gap; a non-null
                // foreign replacement still fails closed.
                if (owner->CompanionData.CompanionObject == null && BindingCurrent())
                {
                    ClearVisibleProof();
                    SetPendingPhase("child-binding");
                    return clock.GetElapsedTime(drawWaitStarted) >= DrawTimeout
                        ? Fail("Companion child setup timed out.") : false;
                }
                return Fail("Companion allocation is no longer owned.");
            }
            var draw = current->DrawObject;
            ObserveDraw(current, draw);
            if (draw == null || (nint)draw != provenVisibleDraw
                || bindingRevision != provenVisibleBindingRevision || draw->LoadState != provenVisibleLoadState)
                ClearVisibleProof();
            if (drawRequested && draw == null) drawRequested = false;
            bool nativeReady = false;
            if (drawRequested || frames >= 2)
            {
                DiagnosticPhase = "draw-readiness";
                nativeReady = ((GameObject*)current)->IsReadyToDraw();
                lifecycleEvidence = lifecycleEvidence with { NativeReadinessFlag = nativeReady };
                if (!BindingCurrent() || !IsCurrentCompanion(current)) return Fail("Companion binding changed during readiness.");
                if (!drawRequested && nativeReady)
                {
                    DiagnosticPhase = "draw-visibility";
                    ((GameObject*)current)->EnableDraw();
                    if (!BindingCurrent() || !IsCurrentCompanion(current)) return Fail("Companion binding changed during draw.");
                    drawRequested = true;
                }
            }
            draw = current->DrawObject;
            ObserveDraw(current, draw);
            // Bit3 is cleared during ordinary native render updates. Persistent
            // readiness requires current resources and draw-enable bit0, not a
            // render submission in the instant this Framework frame observes it.
            IsReady = drawRequested && nativeReady && DrawEnabled(draw);
            if (IsReady)
            {
                DiagnosticPhase = "ready";
                current->TargetableStatus |= ObjectTargetableFlags.IsTargetable;
                provenVisibleDraw = (nint)draw;
                provenVisibleBindingRevision = bindingRevision;
                provenVisibleLoadState = draw->LoadState;
                lifecycleEvidence = lifecycleEvidence with { EverReady = true, LastReadyDrawAddress = (ulong)draw };
                frames = 0;
                drawWaitActive = false;
            }
            else
            {
                if (drawRequested && draw != null && provenVisibleDraw == (nint)draw
                    && provenVisibleBindingRevision == bindingRevision && provenVisibleLoadState == draw->LoadState)
                {
                    // The audited API is a native readiness flag, not a complete resource
                    // validator. Retain only continuity of an already ready exact draw;
                    // never force visibility or claim that a hidden actor is interactive.
                    if (nativeReady && current->DrawObject == draw && provenVisibleDraw == (nint)draw
                        && provenVisibleBindingRevision == bindingRevision
                        && !DrawEnabled(draw) && draw->LoadState == provenVisibleLoadState)
                    {
                        IsNativeHidden = true;
                        SetPendingPhase("native-hidden");
                        drawWaitActive = false;
                        return false;
                    }
                    ClearVisibleProof();
                }
                SetPendingPhase(drawRequested && draw != null ? "draw-visibility" : "draw-readiness");
                if (clock.GetElapsedTime(drawWaitStarted) >= DrawTimeout) return Fail("Companion draw setup timed out.");
            }
            return IsReady;
        }
        catch (Exception ex)
        {
            try { log?.Warning(ex, "[LocalPreview] Independent companion update failed."); } catch { }
            return Fail("Companion native update failed.");
        }
    }

    private static bool DrawEnabled(DrawObject* draw) => draw != null && (draw->Flags & 1) != 0;

    private void ClearVisibleProof()
    {
        provenVisibleDraw = 0;
        provenVisibleBindingRevision = 0;
    }

    private void SetPendingPhase(string phase)
    {
        DiagnosticPhase = phase;
        lifecycleEvidence = lifecycleEvidence with
        {
            FirstPendingPhase = lifecycleEvidence.FirstPendingPhase.Length == 0 ? phase : lifecycleEvidence.FirstPendingPhase,
            LastPendingPhase = phase,
        };
    }

    private void ObserveDraw(Companion* current, DrawObject* draw)
        => lifecycleEvidence = lifecycleEvidence with
        {
            LastObservedDrawAddress = (ulong)draw,
            DrawFlags = draw == null ? (byte)0 : draw->Flags,
            DrawLoadState = draw == null ? (byte)0 : draw->LoadState,
            HostRenderFlags = (ulong)owner->RenderFlags,
            ChildRenderFlags = (ulong)current->RenderFlags,
        };

    /// <summary>
    /// Raw ownership check with no native calls. Remains usable during Retiring even if the
    /// player logged out, so target references can be removed before deleting this owned host.
    /// </summary>
    public bool IsCurrentCompanion(Companion* candidate)
    {
        if (disposed || child == null || candidate == null || candidate != child
            || GetRawOwnedHost() == null || owner->CompanionData.OwnerObject != owner
            || owner->CompanionData.CompanionObject != child) return false;
        return child->ObjectIndex == childIndex && Generation((NativeCharacter*)child) == childGeneration
            && child->Owner == (BattleChara*)owner && child->BaseId == CompanionId
            && child->ObjectKind == ObjectKind.Companion;
    }

    private bool TryBindChild()
    {
        if (!BindingCurrent()) return false;
        var current = owner->CompanionData.CompanionObject;
        // The client can briefly clear CompanionObject while it refreshes a
        // companion's movement/animation state. Keep the proven child lease
        // alive through that gap; the next frame can bind it again.
        if (child != null)
        {
            if (current == null)
            {
                childDetached = true;
                return true;
            }
            if (IsCurrentCompanion(current))
            {
                childDetached = false;
                return true;
            }

            // Some client refreshes release and recreate the Companion object
            // instead of restoring the exact pointer. Only adopt that new
            // pointer after observing the transient null link above, and only
            // when the native owner, row and object kind still match this
            // hidden host. A non-null replacement without that proof remains
            // a failed-closed ownership change.
            if (!childDetached || !IsOwnedReplacement(current)) return false;
            child = current;
            childIndex = current->ObjectIndex;
            childGeneration = Generation((NativeCharacter*)current);
            bindingRevision++;
            childDetached = false;
            drawRequested = false;
            frames = 0;
            drawWaitActive = false;
            DiagnosticPhase = "draw-readiness";
            return IsCurrentCompanion(current);
        }
        if (current == null) return true;
        // The player's existing companion and every player attachment are categorically foreign.
        if ((NativeCharacter*)current == source || (NativeCharacter*)current == owner
            || current == source->CompanionData.CompanionObject
            || (NativeCharacter*)current == source->ChildObject
            || (NativeCharacter*)current == source->Mount.MountObject
            || (NativeCharacter*)current == (NativeCharacter*)source->OrnamentData.OrnamentObject
            || current->Owner != (BattleChara*)owner || current->BaseId != CompanionId
            || current->ObjectKind != ObjectKind.Companion) return false;
        child = current;
        childIndex = current->ObjectIndex;
        childGeneration = Generation((NativeCharacter*)current);
        bindingRevision++;
        DiagnosticPhase = "draw-readiness";
        return IsCurrentCompanion(current);
    }

    private bool IsOwnedReplacement(Companion* current)
        => current != null
            && (NativeCharacter*)current != source
            && (NativeCharacter*)current != owner
            && current != source->CompanionData.CompanionObject
            && (NativeCharacter*)current != source->ChildObject
            && (NativeCharacter*)current != source->Mount.MountObject
            && (NativeCharacter*)current != (NativeCharacter*)source->OrnamentData.OrnamentObject
            && current->Owner == (BattleChara*)owner
            && current->BaseId == CompanionId
            && current->ObjectKind == ObjectKind.Companion;

    private bool Follow(Vector3 position, float rotation)
    {
        if (!BindingCurrent()) return false;
        owner->GameObject.SetPosition(position.X, position.Y, position.Z);
        if (!BindingCurrent()) return false;
        owner->GameObject.SetRotation(rotation);
        return BindingCurrent();
    }

    /// <summary>
    /// Ordinary ground follow moves only our hidden host. Applied acknowledges that
    /// exact host transform; it does not claim the native child followed or animated.
    /// Native motion reset remains unqualified; its refusal is separate from a
    /// successfully applied host transform so ordinary position continuity is preserved.
    /// </summary>
    internal CompanionFollowReceipt ExecuteFollow(CompanionFollowRequest request, NativeCharacter* currentSource,
        TimeSpan now, Func<bool> scopeCurrent)
    {
        CompanionFollowReceipt Result(CompanionFollowExecutionState state, string? reason, bool binding,
            bool hostTransformApplied = false)
            => new(request.Key, request.Sequence, request.Sequence, now, state,
                binding ? (Vector3)owner->Position : Vector3.Zero, binding, false, reason, hostTransformApplied);
        try
        {
            if (request.Sequence <= 0 || request.Key.SourceID != sourceEntityId
                || request.Key.SourceGeneration != sourceGeneration || request.Key.SceneEpoch <= 0
                || request.Key.LeaseID <= 0 || request.Key.LeaseGeneration <= 0 || request.Deadline <= now
                || currentSource != source || scopeCurrent is null || !scopeCurrent() || !BindingCurrent()
                || !Finite(request.TargetPosition) || !float.IsFinite(request.Rotation))
                return Result(CompanionFollowExecutionState.Refused, "follow-binding-or-request", false);
            owner->GameObject.SetPosition(request.TargetPosition.X, request.TargetPosition.Y, request.TargetPosition.Z);
            if (!scopeCurrent() || !BindingCurrent())
                return Result(CompanionFollowExecutionState.Invalidated, "follow-binding-changed-after-position", false);
            owner->GameObject.SetRotation(request.Rotation);
            if (!scopeCurrent() || !BindingCurrent())
                return Result(CompanionFollowExecutionState.Invalidated, "follow-binding-changed-after-rotation", false);
            if ((Vector3)owner->Position != request.TargetPosition || owner->Rotation != request.Rotation)
                return Result(CompanionFollowExecutionState.Failed, "follow-transform-not-applied", true);
            return request.ResetMotion
                ? Result(CompanionFollowExecutionState.Refused, "native-motion-reset-unqualified", true, true)
                : Result(CompanionFollowExecutionState.Applied, null, true, true);
        }
        catch (Exception)
        {
            return Result(CompanionFollowExecutionState.Failed, "follow-native-exception", false);
        }
    }

    private bool SourceCurrent() => (NativeCharacter*)Control.GetLocalPlayer() == source
        && Generation(source) == sourceGeneration && source->EntityId == sourceEntityId;

    private bool BindingCurrent() => !disposed && !retirementRequested && SourceCurrent() && GetRawOwnedHost() != null
        && owner->CompanionData.OwnerObject == owner && owner->DrawObject == null;

    private NativeCharacter* GetRawOwnedHost()
    {
        if (ClientObjectManager.Instance() != manager || slot >= manager->BattleCharas.Length
            || manager->BattleCharas[slot].BattleChara != (BattleChara*)owner) return null;
        return owner->ObjectIndex == ownerIndex && Generation(owner) == ownerGeneration ? owner : null;
    }

    private NativeCharacter* GetOwnedHost()
    {
        if (GetRawOwnedHost() == null) return null;
        var resolved = manager->GetObjectByIndex(slot);
        return resolved == owner && GetRawOwnedHost() == owner ? owner : null;
    }

    private static uint Generation(NativeCharacter* character) => *(uint*)((byte*)character + GenerationOffset);

    private bool Fail(string reason)
    {
        FailureReason = reason;
        DiagnosticPhase = "failed";
        Dispose();
        return false;
    }

    public void Dispose() => TryRetire();

    private bool ChildSafeForTeardown()
    {
        var current = owner->CompanionData.CompanionObject;
        if (current == null) return true;
        if (child != null) return IsCurrentCompanion(current);
        // Failed setup can leave an arbitrary child reference. Keeping the invisible host
        // quarantined is safer than allowing its native destructor to delete someone else's pet.
        return (NativeCharacter*)current != source && (NativeCharacter*)current != owner
            && current->Owner == (BattleChara*)owner && current->ObjectKind == ObjectKind.Companion
            && current->BaseId == CompanionId;
    }

    /// <summary>
    /// Requests deletion at most once, acknowledging retirement only after the manager
    /// no longer owns this exact generation. Pending teardown must retain its host lease.
    /// </summary>
    public bool TryRetire()
    {
        if (IsPhysicallyRetired) return true;
        if (retiring) return false;
        retirementRequested = true;
        retiring = true;
        if (FailureReason.Length == 0) DiagnosticPhase = "retiring";
        IsReady = false;
        IsNativeHidden = false;
        ClearVisibleProof();
        try
        {
            if (GetRawOwnedHost() == null) return ConfirmRetired();
            if (deleteIssued) return false;
            if (!ChildSafeForTeardown())
            {
                FailureReason = "Companion retirement is waiting for a safe child ownership binding.";
                return false;
            }
            // A target-cleanup failure must keep the native actor alive for a later retry.
            if (BeforeRetire?.Invoke() == false) return false;
            Retiring?.Invoke();
            if (GetRawOwnedHost() == null) return ConfirmRetired();
            if (!ChildSafeForTeardown()) return false;
            // Child teardown belongs to the owning manager; never delete a foreign/reused
            // object slot and never call SetupCompanion on the real player's container.
            manager->DeleteObjectByIndex(slot, 0);
            deleteIssued = true;
            return GetRawOwnedHost() == null && ConfirmRetired();
        }
        catch (Exception ex)
        {
            FailureReason = "Independent companion retirement is waiting for cleanup.";
            try { log?.Warning(ex, "[LocalPreview] Could not retire independent companion host."); } catch { }
            return false;
        }
        finally { retiring = false; }
    }

    private bool ConfirmRetired()
    {
        disposed = true;
        IsPhysicallyRetired = true;
        DiagnosticPhase = "retired";
        Retiring = null;
        BeforeRetire = null;
        return true;
    }
}
