using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FireGaze.PortraitPreview;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;
using OffscreenManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.OffscreenRenderingManager;
using RenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

#if !OWNED_SLOT
throw new Exception("FAIL: the native eight-slot reader has not been implemented.");
#else
return Tests.Run();

internal static unsafe class Tests
{
    private static int checks;
    private static void Check(bool ok, string name)
    { checks++; if (!ok) throw new Exception("FAIL: " + name); }
    public static int Run()
    {
        using (var f = new Fixture())
        {
            Check(f.Memory.Reads == 0 && f.Phases.Reads == 0, "constructing reader performs no native or phase read");
            foreach (var slot in new uint[] { 8, uint.MaxValue })
                Check(!f.Reader.TryReadIdle(slot, f.Phase, out _), "out of range slot rejected before native access " + slot);
            Check(f.Memory.Reads == 0 && f.Phases.Reads == 0, "invalid slots cannot probe memory");
            for (uint slot = 0; slot < 8; slot++)
            {
                f.SetIdle(slot);
                Check(f.Reader.TryReadIdle(slot, f.Phase, out var read), "real allocated layout idle slot " + slot);
                Check(read.Slot == slot && read.ActiveCount == 0 && read.Character == 0 && read.RegisteredCamera == 0,
                    "idle values copied from correct slot " + slot);
                Check(read.OwnerStage is null && read.Ownership == PortraitOwnedSceneSlotOwnership.Idle,
                    "idle result never manufactures an owner stage " + slot);
                Check(read.RetainedOffscreenCamera == (nint)(0x50000 + slot * 16)
                    && read.RetainedViewCamera == (nint)(0x60000 + slot * 16) && read.RenderActive,
                    "retained strong references and stale active flag do not block idle slot " + slot);
            }
        }
        foreach (var mutation in new Action<Fixture>[] {
            f => f.Entry(7)->BattleChara = f.Actor,
            f => f.Entry(7)->ObjectKind = (ObjectKind)1,
            f => *(nint*)((byte*)f.CameraManager + 0xD8) = (nint)0x123456,
            f => *((int*)((byte*)f.RenderManager + 0x3835C) + 7) = 1,
            f => *((int*)((byte*)f.RenderManager + 0x3835C) + 7) = -1,
        })
        {
            using var f = new Fixture(); f.SetIdle(7); mutation(f);
            Check(!f.Reader.TryReadIdle(7, f.Phase, out _), "occupied or malformed slot cannot become idle");
        }
        foreach (var mutation in new Func<PortraitOwnedSceneSlotPhase, PortraitOwnedSceneSlotPhase>[] {
            p => p with { BuildValidated = false }, p => p with { InNativePhase = false },
            p => p with { PhaseToken = 0 }, p => p with { SessionEpoch = 0 },
            p => p with { ManagerGeneration = 0 }, p => p with { ThreadID = p.ThreadID + 1 },
            p => p with { Source = p.Source with { Generation = 0 } },
        })
        {
            using var f = new Fixture(); f.SetIdle(0); var invalid = mutation(f.Phase); f.Phases.Current = invalid;
            Check(!f.Reader.TryReadIdle(0, invalid, out _), "unqualified native phase rejected");
            Check(f.Memory.Reads == 0, "unqualified phase performs no native read");
        }
        foreach (var mutation in new Action<Fixture>[] {
            f => f.Editor->EditorState = null,
            f => f.State->AgentBannerEditor = null,
            f => f.State->CharaView = f.Owned,
            f => *(uint*)((byte*)f.SourceActor + 0x74) = 99,
            f => f.SourceActor->DrawData.OwnerObject = (Character*)f.Actor,
            f => f.Source->ClientObjectId = 247,
            f => f.Source->ClientObjectIndex = 7,
            f => f.Source->CameraManager = 0,
            f => f.Source->Camera = null,
            f => *((byte*)f.Source->CameraManager + 0x18) = 0,
            f => *(nint*)((byte*)f.CameraManager + 0xA8) = 0,
        })
        {
            using var f = new Fixture(); f.SetIdle(0); mutation(f);
            Check(!f.Reader.TryReadIdle(0, f.Phase, out _), "source owner chain and generation must remain current");
        }
        foreach (var change in new Func<PortraitOwnedSceneSlotRoots, PortraitOwnedSceneSlotRoots>[] {
            r => r with { Renderer = r.Renderer + 16 }, r => r with { SceneCameras = r.SceneCameras + 16 },
            r => r with { Offscreen = r.Offscreen + 16 }, r => r with { RenderTargets = r.RenderTargets + 16 },
            r => r with { ClientObjects = r.ClientObjects + 16 },
        })
        {
            using var f = new Fixture(); f.SetIdle(1); f.Memory.Roots = change(f.Memory.Roots);
            Check(!f.Reader.TryReadIdle(1, f.Phase, out _), "all five manager identities are required");
        }
        foreach (var change in new Func<PortraitOwnedSceneSlotPhase, PortraitOwnedSceneSlotPhase>[] {
            p => p with { NativeFrame = p.NativeFrame + 1 }, p => p with { PhaseToken = p.PhaseToken + 1 },
            p => p with { SessionEpoch = p.SessionEpoch + 1 }, p => p with { ManagerGeneration = p.ManagerGeneration + 1 },
            p => p with { Source = p.Source with { Generation = p.Source.Generation + 1 } },
            p => p with { InNativePhase = false },
        })
        {
            using var f = new Fixture(); f.SetIdle(2);
            f.Memory.AfterRead = address => { if (address == f.CountAddress(2)) f.Phases.Current = change(f.Phase); };
            Check(!f.Reader.TryReadIdle(2, f.Phase, out _), "native read cannot escape frame epoch source or phase");
        }
        using (var f = new Fixture())
        {
            f.SetIdle(4); var fired = false;
            f.Memory.AfterRead = address => { if (!fired && address == f.CountAddress(4)) { fired = true; f.Entry(4)->ObjectKind = (ObjectKind)1; } };
            Check(!f.Reader.TryReadIdle(4, f.Phase, out _), "second sample detects mid-read native takeover");
        }
        using (var f = new Fixture())
        {
            f.SetIdle(4); var fired = false;
            f.Memory.AfterRead = address => { if (!fired && address == f.CountAddress(4)) { fired = true; f.Memory.Roots = f.Memory.Roots with { RenderTargets = f.Memory.Roots.RenderTargets + 16 }; } };
            Check(!f.Reader.TryReadIdle(4, f.Phase, out var rejected) && rejected == default,
                "manager replacement after copy discards all partial values");
        }
        using (var f = new Fixture())
        {
            f.SetIdle(3); var fired = false;
            f.Memory.AfterRead = address => { if (!fired && address == f.CountAddress(3)) { fired = true; f.SourceActor->DrawData.OwnerObject = (Character*)f.Actor; } };
            Check(!f.Reader.TryReadIdle(3, f.Phase, out _), "source native identity rechecked after slot sampling");
        }
        for (uint slot = 0; slot < 8; slot++)
        {
            using var f = new Fixture(); var owner = f.SetOwned(slot);
            Check(!f.Reader.TryReadIdle(slot, f.Phase, out _), "owned never reports idle " + slot);
            Check(f.Reader.TryReadOwned(slot, owner, f.Phase, out var read), "native owned identity slot " + slot);
            Check(read.Character == (nint)f.Actor && read.CharacterGeneration == 37 && read.Context == f.Context,
                "owned actor generation and context copied " + slot);
            Check(!read.RenderActive, "inactive render-consumption flag is not an ownership lock " + slot);
            Check(read.OwnerStage == PortraitOwnedSceneSlotStage.Prepared && read.Ownership == PortraitOwnedSceneSlotOwnership.Owned,
                "prepared snapshot explicitly describes its ownership stage " + slot);
        }
        foreach (var state in new uint[] { 2, 3, 6 })
        {
            using var f = new Fixture(); var owner = f.SetOwned(5); f.Owned->State = state;
            Check(f.Reader.TryReadOwned(5, owner, f.Phase, out _), "prepared native ownership precedes ready state " + state);
        }
        for (uint slot = 0; slot < 8; slot++)
        {
            using var f = new Fixture(); var owner = f.SetInitialized(slot);
            Check(f.Reader.TryReadOwned(slot, owner, f.Phase, out var read), "setup-owned actor can receive its first Render " + slot);
            Check(read.Character == (nint)f.Actor && read.ActiveCount == 0 && read.Context == 0 && read.RegisteredCamera == 0,
                "initialized ownership is a distinct stage, not an idle claim " + slot);
            Check(read.OwnerStage == PortraitOwnedSceneSlotStage.Initialized, "initialized snapshot preserves its explicit stage " + slot);
            Check(!f.Reader.TryReadIdle(slot, f.Phase, out _), "early actor ownership never passes idle " + slot);
        }
        foreach (var mutation in new Action<Fixture>[] {
            f => f.Owned->State = 0, f => f.Owned->State = 2, f => f.Owned->State = 5,
            f => f.Owned->CameraManager = f.Context,
            f => f.Owned->Camera = f.Source->Camera,
            f => *(int*)f.CountAddress(2) = 1,
            f => *(nint*)((byte*)f.CameraManager + 0xB0) = (nint)f.Source->Camera,
            f => *(uint*)((byte*)f.Actor + 0x74) = 38,
        })
        {
            using var f = new Fixture(); var owner = f.SetInitialized(2); mutation(f);
            Check(!f.Reader.TryReadOwned(2, owner, f.Phase, out _), "early stage rejects partially configured or foreign native state");
        }
        foreach (var mutation in new Action<Fixture>[] {
            f => *(uint*)((byte*)f.Actor + 0x74) = 38,
            f => f.Actor->DrawData.OwnerObject = (Character*)f.SourceActor,
            f => f.Entry(1)->BattleChara = f.SourceActor,
            f => f.Entry(1)->ObjectKind = 0,
            f => f.Owned->ClientObjectId = 242,
            f => f.Owned->ClientObjectIndex = 2,
            f => f.Owned->CameraType = 1,
            f => f.Owned->CameraManager = (nint)f.Source->CameraManager,
            f => f.Owned->Camera = f.Source->Camera,
            f => *(uint*)((byte*)f.Context + 0x10) = 2,
            f => *((byte*)f.Context + 0x18) = 0,
            f => *(nint*)((byte*)f.CameraManager + 0xA8) = (nint)f.Source->Camera,
            f => *((int*)((byte*)f.RenderManager + 0x3835C) + 1) = 2,
            f => f.Owned->State = 0, f => f.Owned->State = 1, f => f.Owned->State = 5,
        })
        {
            using var f = new Fixture(); var owner = f.SetOwned(1); mutation(f);
            Check(!f.Reader.TryReadOwned(1, owner, f.Phase, out _), "foreign or shared native identity cannot pass owned read");
        }
        using (var f = new Fixture())
        {
            var owner = f.SetOwned(2); var fired = false;
            f.Memory.AfterRead = address => { if (!fired && address == f.CountAddress(2)) { fired = true; *(uint*)((byte*)f.Actor + 0x74) = 38; } };
            Check(!f.Reader.TryReadOwned(2, owner, f.Phase, out _), "owned generation changes during sample fail closed");
        }
        using (var f = new Fixture())
        {
            f.SetIdle(0); f.Memory.RejectAddress = f.CountAddress(0);
            Check(!f.Reader.TryReadIdle(0, f.Phase, out _), "unreadable native field does not expose a partial snapshot");
        }
        var denied = new PortraitOwnedSceneSlotNativeApi();
        Check(!denied.TryRead<ulong>((nint)1, out _), "live native API defaults to admission denied");
        Check(!denied.TryReadRoots(out _), "live static root API remains disabled");
        using (var f = new Fixture())
        {
            var permittedCopy = new PortraitOwnedSceneSlotNativeApi(() => true);
            *(int*)f.CountAddress(0) = 173;
            Check(permittedCopy.TryRead<int>(f.CountAddress(0), out var value) && value == 173,
                "actual native copy boundary reads detached self-allocated bytes without game access");
            Check(!permittedCopy.TryRead<ulong>((nint)1, out var rejected) && rejected == 0,
                "actual native copy boundary rejects inaccessible memory");
        }
        Console.WriteLine($"PortraitOwnedSceneSlotTests: {checks} checks passed."); return 0;
    }
}

internal sealed class PhaseSource : IPortraitOwnedSceneSlotPhaseSource
{
    public PortraitOwnedSceneSlotPhase Current; public int Reads;
    public bool TryCapture(out PortraitOwnedSceneSlotPhase phase) { Reads++; phase = Current; return true; }
}

internal sealed unsafe class Fixture : IDisposable
{
    private readonly List<(nint Address, nuint Size)> allocations = [];
    public readonly MemoryApi Memory; public readonly PhaseSource Phases = new();
    public readonly PortraitOwnedSceneSlotReader Reader;
    public AgentBannerEditor* Editor; public AgentBannerEditorState* State;
    public CharaViewPortrait* Source; public CharaViewPortrait* Owned;
    public BattleChara* SourceActor; public BattleChara* Actor;
    public RenderManager* RenderManager; public SceneCameraManager* CameraManager;
    public ClientObjectManager* ClientObjects; public nint Context;
    public PortraitOwnedSceneSlotPhase Phase;
    public Fixture()
    {
        Editor = Allocate<AgentBannerEditor>(); State = Allocate<AgentBannerEditorState>();
        Source = Allocate<CharaViewPortrait>(); Owned = Allocate<CharaViewPortrait>();
        SourceActor = Allocate<BattleChara>(); Actor = Allocate<BattleChara>();
        RenderManager = Allocate<RenderManager>(); CameraManager = Allocate<SceneCameraManager>();
        ClientObjects = Allocate<ClientObjectManager>();
        var offscreen = Allocate<OffscreenManager>(); var targets = Allocate<RenderTargetManager>();
        Context = (nint)AllocateBytes(0x20);
        Source->Camera = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera*)AllocateBytes(0x100);
        Source->CameraManager = (nint)AllocateBytes(0x20);
        Editor->EditorState = State; State->AgentBannerEditor = Editor; State->CharaView = Source;
        Source->State = 6; Source->CharaViewPortraitCharacterLoaded = true;
        *(uint*)((byte*)SourceActor + 0x74) = 19; SourceActor->DrawData.OwnerObject = (Character*)SourceActor;
        var roots = new PortraitOwnedSceneSlotRoots((nint)RenderManager, (nint)CameraManager, (nint)offscreen, (nint)targets, (nint)ClientObjects);
        Phase = new(new((nint)Editor, (nint)State, (nint)Source, 19, (ulong)SourceActor), roots,
            2, 3, 73, 91, Environment.CurrentManagedThreadId, true, true);
        Phases.Current = Phase; Memory = new(allocations, roots); Reader = new(Memory, Phases);
    }
    private void* AllocateBytes(nuint size)
    { var ptr = NativeMemory.AllocZeroed(size); allocations.Add(((nint)ptr, size)); return ptr; }
    private T* Allocate<T>() where T : unmanaged => (T*)AllocateBytes((nuint)sizeof(T));
    public ClientObjectManager.BattleCharaEntry* Entry(uint slot) => (ClientObjectManager.BattleCharaEntry*)((byte*)ClientObjects + 0x10 + (0xF0 + slot) * 16);
    public nint CountAddress(uint slot) => (nint)((byte*)RenderManager + 0x3835C + slot * 4);
    public void SetIdle(uint slot)
    {
        *Entry(slot) = default;
        var sourceSlot = (slot + 1) % 8;
        Source->ClientObjectId = 0xF0 + sourceSlot; Source->ClientObjectIndex = sourceSlot;
        Entry(sourceSlot)->BattleChara = SourceActor; Entry(sourceSlot)->ObjectKind = (ObjectKind)1;
        *(uint*)(Source->CameraManager + 0x10) = sourceSlot;
        *(int*)(Source->CameraManager + 8) = 1; *((byte*)Source->CameraManager + 0x18) = 1;
        *(nint*)((byte*)CameraManager + 0xA0 + sourceSlot * 8) = (nint)Source->Camera;
        *(int*)CountAddress(sourceSlot) = 1;
        *(nint*)((byte*)CameraManager + 0xA0 + slot * 8) = 0; *(int*)CountAddress(slot) = 0;
        *((byte*)RenderManager + 0x3837C + slot) = 1;
        *(nint*)(Phase.Roots.Offscreen + 0xC8 + (nint)(slot * 8)) = (nint)(0x50000 + slot * 16);
        *(nint*)((byte*)RenderManager + 0x2738 + slot * 24) = (nint)(0x60000 + slot * 16);
    }
    public PortraitOwnedSceneSlotOwner SetOwned(uint slot)
    {
        SetIdle(slot); Entry(slot)->BattleChara = Actor; Entry(slot)->ObjectKind = (ObjectKind)1;
        Entry(slot)->Index = (byte)(0xF0 + slot); Entry(slot)->MemoryIndex = 37;
        Actor->DrawData.OwnerObject = (Character*)Actor; *(uint*)((byte*)Actor + 0x74) = 37;
        Owned->State = 6; Owned->ClientObjectId = 0xF0 + slot; Owned->ClientObjectIndex = slot;
        Owned->CameraManager = Context;
        Owned->Camera = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera*)AllocateBytes(0x100);
        *(uint*)(Context + 0x10) = slot; *((byte*)Context + 0x18) = 1; *(int*)(Context + 8) = 1;
        *(nint*)((byte*)CameraManager + 0xA0 + slot * 8) = (nint)Owned->Camera;
        *(int*)CountAddress(slot) = 1; *((byte*)RenderManager + 0x3837C + slot) = 0;
        return new((nint)Owned, (nint)Actor, 37, Context, (nint)Owned->Camera);
    }
    public PortraitOwnedSceneSlotOwner SetInitialized(uint slot)
    {
        var owner = SetOwned(slot);
        Owned->State = 1; Owned->CameraManager = 0; Owned->Camera = null;
        *(nint*)((byte*)CameraManager + 0xA0 + slot * 8) = 0; *(int*)CountAddress(slot) = 0;
        return owner with { Context = 0, Camera = 0, Stage = PortraitOwnedSceneSlotStage.Initialized };
    }
    public void Dispose() { foreach (var entry in allocations) NativeMemory.Free((void*)entry.Address); }
}

internal sealed unsafe class MemoryApi(List<(nint Address, nuint Size)> regions, PortraitOwnedSceneSlotRoots roots) : IPortraitOwnedSceneSlotNativeApi
{
    public PortraitOwnedSceneSlotRoots Roots = roots; public int Reads; public nint RejectAddress;
    public Action<nint>? AfterRead;
    public bool TryReadRoots(out PortraitOwnedSceneSlotRoots value) { Reads++; value = Roots; return true; }
    public bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        Reads++; value = default;
        if (address == RejectAddress || !regions.Any(r => address >= r.Address && (nuint)(address-r.Address) <= r.Size
            && (nuint)sizeof(T) <= r.Size-(nuint)(address-r.Address))) return false;
        value = Unsafe.ReadUnaligned<T>((void*)address); AfterRead?.Invoke(address); return true;
    }
}
#endif
