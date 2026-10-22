using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Compatibility;

/// <summary>
/// Only the reviewed HMS managed read sites receive this pointer. The owned buffers are never
/// registered as actors or passed to native functions. Unread pointer fields remain zero.
/// </summary>
public sealed unsafe class HmsShadowActor : IDisposable
{
    private readonly IHmsShadowSource source;
    private readonly IPluginLog? log;
    private readonly ThreadLocal<Stack<Scope>> scopes = new(() => new());
    private volatile bool disposed;

    public HmsShadowActor(IHmsShadowSource source, IPluginLog? log = null) { this.source = source; this.log = log; }

    public bool TryBeginCapture(out IDisposable? scope)
    {
        scope = null;
        if (disposed || !HmsNativeLayout.IsSupported) return false;
        Capture? unleased = null;
        try
        {
            var stack = scopes.Value!;
            Capture capture;
            if (stack.TryPeek(out var parent))
            {
                capture = parent.Capture;
                if (!source.IsHmsCaptureFrameCurrent(capture.Frame)) return false;
            }
            else
            {
                if (!source.TryGetHmsCaptureFrame(out var frame) || frame.Address == 0
                    || !source.IsHmsCaptureFrameCurrent(frame)) return false;
                capture = unleased = new Capture(frame);
                if (!capture.TryPopulate()) { capture.Dispose(); return false; }
                if (!source.IsHmsCaptureFrameCurrent(frame)) { capture.Dispose(); return false; }
            }
            var lease = new Scope(stack, capture);
            stack.Push(lease);
            scope = lease;
            unleased = null;
            return true;
        }
        catch (Exception ex)
        {
            try { log?.Warning(ex, "[LocalPreview] HMS capture shadow refused; no live preview pointer is substituted."); } catch { }
            return false;
        }
        finally { unleased?.Dispose(); }
    }

    public nint ReadDetectorAddress(IGameObject obj) => ReadDetectorAddress(obj.Address, obj.GameObjectId);
    public nint ReadBodyAddress(IGameObject obj) => ReadBodyAddress(obj.Address, obj.GameObjectId);
    public nint ReadDetectorAddress(nint address, ulong actorId) => ReadAddress(address, actorId);
    public nint ReadBodyAddress(nint address, ulong actorId) => ReadAddress(address, actorId);
    private nint ReadAddress(nint address, ulong actorId)
    {
        if (disposed || !scopes.Value!.TryPeek(out var scope) || scope.IsDisposed
            || !source.IsHmsCaptureFrameCurrent(scope.Capture.Frame)
            || scope.Capture.Frame.Address != address || scope.Capture.Frame.ActorId != actorId)
            throw new InvalidOperationException("HMS capture binding or scope changed.");
        return scope.Capture.Frame.Project ? (nint)scope.Capture.Root : address;
    }

    // Active synchronous callers retain their own lease until their finalizer. Marking this
    // bridge disposed prevents further reads but cannot free memory underneath an HMS stack.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        scopes.Dispose();
    }

    private sealed class Scope : IDisposable
    {
        private readonly Stack<Scope> stack;
        public Capture Capture { get; }
        public bool IsDisposed { get; private set; }
        public Scope(Stack<Scope> stack, Capture capture) { this.stack = stack; Capture = capture; capture.References++; }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            // Out-of-order disposal cannot expose a dead outer scope or free a live inner view.
            while (stack.TryPeek(out var top) && top.IsDisposed) stack.Pop();
            if (--Capture.References == 0) Capture.Dispose();
        }
    }

    private sealed class Capture : IDisposable
    {
        public readonly HmsCaptureFrame Frame;
        public int References;
        public NativeCharacter* Root;
        private NativeCharacter* mount;
        private Companion* minion;
        private Ornament* ornament;
        private DrawObject* rootDraw;
        private DrawObject* mountDraw;
        private DrawObject* minionDraw;
        private DrawObject* ornamentDraw;
        public Capture(HmsCaptureFrame frame) => Frame = frame;
        public bool TryPopulate()
        {
            if (!Frame.Project) return true;
            var live = (NativeCharacter*)Frame.Address;
            var baseline = Frame.Baseline;
            if (!HmsLocomotionProjection.TryProject(live->Timeline.TimelineSequencer.TimelineIds[0],
                baseline.Root.Timeline.T0, baseline.Root.Timeline.WeaponDrawn,
                baseline.Root.Mode == CharacterModes.Mounted && baseline.MountId != 0,
                Frame.OwnMount, Frame.OwnEmoteTimeline, out ushort timeline)) return false;
            Root = Allocate<NativeCharacter>();
            WriteActor(Root, baseline.Root, live->Position, live->Rotation, ref rootDraw);
            Root->Timeline.TimelineSequencer.TimelineIds[0] = timeline;
            Root->Mount.MountId = baseline.MountId;
            Root->CompanionData.CompanionId = baseline.MinionId;
            Root->OrnamentData.OrnamentId = baseline.OrnamentId;
            if (baseline.Mount.Present)
            {
                mount = Allocate<NativeCharacter>();
                WriteChild(mount, baseline.Mount, live->Position, ref mountDraw);
                Root->Mount.MountObject = mount;
                // A real mount may animate while another compatible category is previewed.
                // Preview mount state is never forwarded, even when its id matches the baseline.
                if (!Frame.OwnMount && baseline.Root.Mode == CharacterModes.Mounted && live->Mount.MountObject != null
                    && live->Mount.MountId == baseline.MountId)
                {
                    ushort mt = live->Mount.MountObject->Timeline.TimelineSequencer.TimelineIds[0];
                    if (!HmsLocomotionProjection.IsReviewedLocomotion(mt)) return false;
                    mount->Timeline.TimelineSequencer.TimelineIds[0] = mt;
                    if (mountDraw != null && live->Mount.MountObject->DrawObject != null)
                        mountDraw->Rotation = live->Mount.MountObject->DrawObject->Rotation;
                }
            }
            if (baseline.Minion.Present)
            {
                minion = Allocate<Companion>();
                WriteChild((NativeCharacter*)minion, baseline.Minion, live->Position, ref minionDraw);
                minion->Behavior = (CompanionMove)baseline.Minion.Behaviour;
                Root->CompanionData.CompanionObject = minion;
            }
            if (baseline.Ornament.Present)
            {
                ornament = Allocate<Ornament>();
                WriteChild((NativeCharacter*)ornament, baseline.Ornament, live->Position, ref ornamentDraw);
                ornament->OrnamentId = baseline.Ornament.OrnamentId;
                ornament->CustomizeGroupId = baseline.Ornament.CustomizeGroupId;
                Root->OrnamentData.OrnamentObject = ornament;
            }
            return true;
        }
        private static void WriteChild(NativeCharacter* actor, HmsChildPresentation state, Vector3 ownerPosition, ref DrawObject* draw)
        {
            WriteActor(actor, state.Actor, ownerPosition + state.RelativePosition, state.Rotation, ref draw);
            actor->BaseId = state.BaseId;
        }
        private static void WriteActor(NativeCharacter* actor, HmsActorPresentation state, Vector3 position, float rotation, ref DrawObject* draw)
        {
            actor->Mode = state.Mode; actor->ModeParam = state.ModeParam;
            actor->Position = position; actor->Rotation = rotation; actor->DrawOffset = state.DrawOffset;
            actor->EmoteController.EmoteId = state.EmoteId; actor->EmoteController.Stance = state.Stance;
            actor->EmoteController.CurrentPoseType = (EmoteController.PoseType)state.PoseType;
            actor->EmoteController.CPoseState = state.CPoseState;
            actor->Timeline.BaseOverride = state.Timeline.BaseOverride;
            actor->Timeline.ModelState = state.Timeline.ModelState;
            actor->Timeline.TimelineSequencer.TimelineIds[0] = state.Timeline.T0;
            actor->Timeline.TimelineSequencer.TimelineIds[1] = state.Timeline.T1;
            actor->Timeline.TimelineSequencer.TimelineIds[2] = state.Timeline.T2;
            actor->Timeline.TimelineSequencer.TimelineIds[3] = state.Timeline.T3;
            actor->Timeline.IsWeaponDrawn = state.Timeline.WeaponDrawn;
            actor->Timeline.OwnerObject = actor; actor->Mount.OwnerObject = actor;
            actor->CompanionData.OwnerObject = actor; actor->OrnamentData.OwnerObject = actor;
            if (state.HasDraw)
            {
                draw = Allocate<DrawObject>();
                draw->Position = position + state.BodyOffset; draw->Rotation = state.DrawRotation;
                actor->DrawObject = draw;
            }
        }
        private static T* Allocate<T>() where T : unmanaged
        {
            var pointer = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
            if (pointer == null) throw new OutOfMemoryException();
            return pointer;
        }
        public void Dispose()
        {
            NativeMemory.Free(rootDraw); rootDraw = null; NativeMemory.Free(mountDraw); mountDraw = null;
            NativeMemory.Free(minionDraw); minionDraw = null; NativeMemory.Free(ornamentDraw); ornamentDraw = null;
            NativeMemory.Free(mount); mount = null; NativeMemory.Free(minion); minion = null;
            NativeMemory.Free(ornament); ornament = null; NativeMemory.Free(Root); Root = null;
        }
    }
}

/// <summary>Gate the actual loaded ClientStructs layout before any buffer is allocated.</summary>
public static unsafe class HmsNativeLayout
{
    public static bool IsSupported { get; } = sizeof(NativeCharacter) == 0x2370 && sizeof(Companion) == 0x2470
        && sizeof(Ornament) == 0x2390 && sizeof(GameObject) == 0x1A0 && sizeof(DrawObject) == 0x90
        && sizeof(TimelineContainer) == 0x350 && sizeof(OrnamentContainer) == 0x78
        && Marshal.OffsetOf<NativeCharacter>(nameof(NativeCharacter.Mount)) == 0x670
        && Marshal.OffsetOf<NativeCharacter>(nameof(NativeCharacter.CompanionData)) == 0x6D8
        && Marshal.OffsetOf<NativeCharacter>(nameof(NativeCharacter.OrnamentData)) == 0x960
        && Marshal.OffsetOf<NativeCharacter>(nameof(NativeCharacter.Timeline)) == 0xA30
        && Marshal.OffsetOf<NativeCharacter>(nameof(NativeCharacter.EmoteController)) == 0x630
        && Marshal.OffsetOf<NativeCharacter>(nameof(NativeCharacter.Mode)) == 0x2364
        && Marshal.OffsetOf<NativeCharacter>(nameof(NativeCharacter.ModeParam)) == 0x2365
        && Marshal.OffsetOf<EmoteController>(nameof(EmoteController.EmoteId)) == 0x14
        && Marshal.OffsetOf<EmoteController>(nameof(EmoteController.Stance)) == 0x16
        && Marshal.OffsetOf<EmoteController>(nameof(EmoteController.CurrentPoseType)) == 0x20
        && Marshal.OffsetOf<EmoteController>(nameof(EmoteController.CPoseState)) == 0x21
        && Marshal.OffsetOf<TimelineContainer>(nameof(TimelineContainer.TimelineSequencer)) == 0x10
        && Marshal.OffsetOf<TimelineContainer>(nameof(TimelineContainer.ModelState)) == 0x2C0
        && Marshal.OffsetOf<TimelineContainer>(nameof(TimelineContainer.BaseOverride)) == 0x2E6
        && Marshal.OffsetOf<TimelineContainer>(nameof(TimelineContainer.Flags3)) == 0x34E
        && Marshal.OffsetOf<ActionTimelineSequencer>("_timelineIds") == 0xE0
        && Marshal.OffsetOf<MountContainer>(nameof(MountContainer.MountObject)) == 0x10
        && Marshal.OffsetOf<MountContainer>(nameof(MountContainer.MountId)) == 0x18
        && Marshal.OffsetOf<CompanionContainer>(nameof(CompanionContainer.CompanionObject)) == 0x10
        && Marshal.OffsetOf<CompanionContainer>(nameof(CompanionContainer.CompanionId)) == 0x18
        && Marshal.OffsetOf<OrnamentContainer>(nameof(OrnamentContainer.OrnamentObject)) == 0x10
        && Marshal.OffsetOf<OrnamentContainer>(nameof(OrnamentContainer.OrnamentId)) == 0x18
        && Marshal.OffsetOf<Companion>(nameof(Companion.Behavior)) == 0x2394
        && Marshal.OffsetOf<Ornament>(nameof(Ornament.OrnamentId)) == 0x2370
        && Marshal.OffsetOf<Ornament>(nameof(Ornament.CustomizeGroupId)) == 0x2374
        && Marshal.OffsetOf<GameObject>(nameof(GameObject.Position)) == 0xB0
        && Marshal.OffsetOf<GameObject>(nameof(GameObject.DrawObject)) == 0x100
        && Marshal.OffsetOf<GameObject>(nameof(GameObject.DrawOffset)) == 0xE0
        && Marshal.OffsetOf<GameObject>(nameof(GameObject.BaseId)) == 0x84
        && Marshal.OffsetOf<FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object>(nameof(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object.Position)) == 0x50
        && Marshal.OffsetOf<FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object>(nameof(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object.Rotation)) == 0x60;
}

/// <summary>
/// Read-side classification only. No animation, movement, mode or flight operation is executed.
/// Numeric families are guarded together with the exact reviewed HMS assembly fingerprint.
/// </summary>
internal static class HmsLocomotionProjection
{
    public static bool IsReviewedLocomotion(ushort value) => IsGround(value)
        || value is >= 4947 and <= 4963 or >= 4968 and <= 4985 or >= 4040 and <= 4058
        or 165 or 166 or 167 or 168 or 190 or 191 or >= 5003 and <= 5005;
    private static bool IsGround(ushort value) => value is 3 or 4 or 5 or 6 or 7 or 8 or 13 or 14 or 15 or 16
        or 22 or 23 or 24 or 25 or 26 or 27 or 30 or 31 or 32 or 33 or 34 or 35 or 36 or 41 or 42 or 43 or 44
        or 50 or 51 or 52 or 58 or 59 or 60 or 61;
    public static bool TryProject(ushort live, ushort baseline, bool weaponDrawn, bool genuinelyMounted,
        bool ownsMount, bool ownsEmoteTimeline, out ushort result)
    {
        result = baseline;
        if (ownsEmoteTimeline) return true;
        if (IsGround(live)) { result = NormalizeWeapon(live, weaponDrawn); return true; }
        if (live is >= 4947 and <= 4963 or >= 4968 and <= 4985) { result = live; return true; }
        if (live is >= 4040 and <= 4058 or >= 5003 and <= 5005 or 165 or 166 or 167 or 168 or 190 or 191)
        {
            if (genuinelyMounted && !ownsMount) { result = live; return true; }
            if (ownsMount && live is 166 or 167) { result = NormalizeWeapon(live == 166 ? (ushort)3 : (ushort)22, weaponDrawn); return true; }
            return false;
        }
        if (live == 7367) { result = NormalizeWeapon(3, weaponDrawn); return true; }
        return live == baseline;
    }
    private static ushort NormalizeWeapon(ushort value, bool armed)
    {
        ushort normal = value switch { 34 => 3, 35 => 7, 36 => 8, 41 => 13, 42 => 14, 43 => 15, 44 => 16,
            50 => 22, 51 => 23, 52 => 24, 58 => 30, 59 => 31, 60 => 32, 61 => 33, _ => value };
        if (!armed) return normal;
        return normal switch { 3 or 4 or 5 or 6 => 34, 7 => 35, 8 => 36, 13 => 41, 14 => 42, 15 => 43, 16 => 44,
            22 or 25 or 26 or 27 => 50, 23 => 51, 24 => 52, 30 => 58, 31 => 59, 32 => 60, 33 => 61, _ => normal };
    }
}
