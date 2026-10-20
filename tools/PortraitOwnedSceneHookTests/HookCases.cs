#if HAS_OWNED_HOOKS
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FireGaze.PortraitPreview;
using System.Runtime.InteropServices;

internal static unsafe class HookCases
{
    static readonly PortraitIdentity Source = new(0x100, 0x200, 0x300, 7, 0x400);
    const nint Owned = 0x500;
    public static int Run()
    {
        var passed = 0; var failed = 0;
        foreach (var (name, run) in new (string, Action)[] {
            ("unproved native coverage never creates a hook or claims a slot", IncompleteCoverage),
            ("failed enabling rolls back every hook and cannot issue a lease", FailedEnable),
            ("source original completes before one own render for a native frame", SourcePhase),
            ("creation idle permits never authorize general access or release", CreationPermitOnly),
            ("wrong thread and source replacement never issue a lease", WrongThreadAndSource),
            ("out-of-domain source slots cannot create a scene request", SourceSlotDomain),
            ("foreign initialize retires the owned view before deleting its slot", NativeTakeover),
            ("foreign update of an existing consumer wins before its original", ExistingConsumer),
            ("source release retires own scene before native source teardown", SourceRelease),
            ("source reinitialization retires even when its new slot is unrelated", SourceReinitialize),
            ("retirement failure still calls native exactly once and forbids late release", FailedRetirement),
            ("ownership loss never permits stale Release", LostOwnership),
            ("self scope does not authorize a different view", ForeignReentry),
            ("a scope escaping its source phase cannot authorize a later frame", EscapedScope),
            ("slot-observer exceptions cannot unwind through a native original", ObserverException),
            ("automatic client-object allocation retires before choosing a UI object index", AutomaticClientObject),
            ("direct source client-object deletion retires before invalidating the source", SourceClientObject),
            ("native Setup ABI preserves both stack arguments after native-first retirement", NativeSetupStack),
            ("native deleting-destructor ABI preserves flags and pointer return", NativeDtorReturn),
            ("closing retires on source phase and tears down hooks after retirement", Closing),
            ("closing before the first source phase completes the unused request", ClosingBeforeFirstPhase),
            ("all native ABI shapes match the installed SDK", Abi),
        })
        {
            try { run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        Console.WriteLine($"{passed} passed; {failed} failed"); return failed == 0 ? 0 : 1;
    }

    static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"expected {expected}; got {actual}"); }
    static void True(bool value) => Equal(value, true);
    static void Trace(Fixture f, string expected) => Equal(string.Join(",", f.Events), expected);

    static void ClosingBeforeFirstPhase()
    {
        using var f = new Fixture(); True(f.Hooks.TryInstall(f.Factory));
        var advances = 0; var retirements = 0;
        True(f.Hooks.TryRequest(Source, 6, _ => advances++, lease => {
            True(lease.IsCurrent); True(lease.CanCreate); retirements++; return true;
        }));
        f.Hooks.RequestStop(); f.Render(Source.Model, 33);
        Equal(advances, 0); Equal(retirements, 1);
        Trace(f, "render:768:33"); True(f.Hooks.TryDispose());
    }

    static void IncompleteCoverage()
    {
        using var f = new Fixture(false);
        Equal(f.Hooks.TryInstall(f.Factory), false); Equal(f.Factory.Created.Count, 0);
        Equal(f.Request(_ => throw new Exception("must not run")), false);
    }
    static void FailedEnable()
    {
        using var f = new Fixture(); f.Factory.FailEnableAt = 2;
        Equal(f.Hooks.TryInstall(f.Factory), false);
        True(f.Factory.Created.Count > 2); True(f.Factory.Created.All(h => h.DisposeCalls == 1 && !h.IsEnabled));
        Equal(f.Request(_ => throw new Exception("must not run")), false);
    }
    static void SourcePhase()
    {
        using var f = new Fixture(); True(f.Hooks.TryInstall(f.Factory));
        IPortraitOwnedSceneLease? captured = null;
        True(f.Request(lease => { captured = lease; True(lease.CanCreate); Create(f,lease);
            using var scope = lease.EnterOwnedCall(Owned); True(scope is not null);
            f.Render(Owned, lease.NativeFrame); }));
        f.Render(Source.Model, 99); Trace(f, "render:768:99,initialize:1280:6,render:1280:99");
        True(captured is not null); Equal(captured!.IsCurrent, false); Equal(captured.CanAccess(Owned), false);
        f.Events.Clear(); f.Render(Source.Model, 99); Trace(f, "render:768:99");
    }
    static void CreationPermitOnly()
    {
        using var f = new Fixture(); True(f.Hooks.TryInstall(f.Factory));
        True(f.Request(lease => {
            True(lease.TryBindCreatedView(Owned)); True(lease.IsCurrent);
            Equal(lease.CanAccess(Owned),false); Equal(lease.CanReleaseView(Owned),false);
            Equal(lease.EnterOwnedCall(Owned),null);
            using(var ctor=lease.EnterCreationCall(Owned,PortraitOwnedSceneCreationCall.Construct))
            { True(ctor is not null); True(lease.ConfirmConstructed(Owned)); }
            Equal(lease.CanAccess(Owned),false);
            using(var setup=lease.EnterCreationCall(Owned,PortraitOwnedSceneCreationCall.Setup))
            { True(setup is not null); Equal(lease.ConfirmSetup(Owned),false);
              f.Initialize(Owned,6); True(lease.ConfirmSetup(Owned)); Equal(lease.CanAccess(Owned),false); }
            True(lease.CanAccess(Owned));
            Equal(lease.EnterCreationCall(Owned,PortraitOwnedSceneCreationCall.Setup),null);
            f.Environment.SlotOwner=0; Equal(lease.IsCurrent,false); Equal(lease.CanReleaseView(Owned),false);
        }));
        f.Render(Source.Model,1);
        True(f.Hooks.LastError is null);
    }

    static void Create(Fixture f,IPortraitOwnedSceneLease lease)
    {
        True(lease.TryBindCreatedView(Owned));
        using(var ctor=lease.EnterCreationCall(Owned,PortraitOwnedSceneCreationCall.Construct))
        { True(ctor is not null); True(lease.ConfirmConstructed(Owned)); }
        using(var setup=lease.EnterCreationCall(Owned,PortraitOwnedSceneCreationCall.Setup))
        { True(setup is not null); f.Initialize(Owned,6); True(lease.ConfirmSetup(Owned)); }
    }
    static void WrongThreadAndSource()
    {
        using var f = new Fixture(); True(f.Hooks.TryInstall(f.Factory)); var calls = 0;
        True(f.Request(_ => calls++)); f.Environment.Framework = false; f.Render(Source.Model, 5); Equal(calls, 0);
        f.Environment.Framework = true; f.Environment.SourceCurrent = false; f.Render(Source.Model, 6); Equal(calls, 0);
    }
    static void SourceSlotDomain()
    {
        using var f = new Fixture(); True(f.Hooks.TryInstall(f.Factory)); f.Environment.Views[Source.Model] = 8;
        Equal(f.Request(_ => throw new Exception("must not acquire")), false);
    }
    static void NativeTakeover()
    {
        using var f = Active(); f.Events.Clear(); f.Environment.Views[0x600] = 6; f.Initialize(0x600, 6);
        Trace(f, "retire,release:1280,initialize:1536:6"); Equal(f.Environment.SlotOwner, (nint)0x600);
        f.Events.Clear(); f.Render(Source.Model, 101); Trace(f, "render:768:101");
    }
    static void ExistingConsumer()
    {
        using var f = Active(); f.Events.Clear(); f.Environment.Views[0x600] = 6;
        f.Update(0x600, 101); Trace(f, "retire,release:1280,update:1536:101");
    }
    static void SourceRelease()
    {
        using var f = Active(); f.Events.Clear(); f.Release(Source.Model);
        Trace(f, "retire,release:1280,release:768");
    }
    static void SourceReinitialize()
    {
        using var f = Active(); f.Events.Clear(); f.Initialize(Source.Model, 5);
        Trace(f, "retire,release:1280,initialize:768:5");
    }
    static void FailedRetirement()
    {
        using var f = Active(false); f.Events.Clear(); f.Environment.Views[0x600] = 6;
        f.Initialize(0x600, 6); Trace(f, "retire,initialize:1536:6");
        f.Events.Clear(); f.Release(Source.Model); Trace(f, "release:768");
        Equal(f.Captured!.CanReleaseView(Owned), false);
    }
    static void LostOwnership()
    {
        using var f = Active(); f.Environment.SlotOwner = 0x700; f.Events.Clear();
        f.Release(Source.Model); Trace(f, "release:768"); Equal(f.Captured!.CanReleaseView(Owned), false);
    }
    static void ForeignReentry()
    {
        using var f = new Fixture(); True(f.Hooks.TryInstall(f.Factory));
        True(f.Request(lease => { f.Captured = lease; Create(f,lease);
            using var scope = lease.EnterOwnedCall(Owned); True(scope is not null);
            f.Environment.Views[0x600] = 6; f.Initialize(0x600, 6); Equal(lease.CanAccess(Owned), false); }));
        f.Render(Source.Model, 90); Trace(f, "render:768:90,initialize:1280:6,retire,release:1280,initialize:1536:6");
    }
    static void EscapedScope()
    {
        using var f = new Fixture(); True(f.Hooks.TryInstall(f.Factory)); IDisposable? escaped = null; var calls = 0;
        True(f.Request(lease => { calls++; f.Captured = lease; Create(f,lease);
            escaped = lease.EnterOwnedCall(Owned); }));
        f.Render(Source.Model, 80); Equal(f.Captured!.IsCurrent, false); f.Render(Source.Model, 81);
        Equal(calls, 1); escaped!.Dispose();
    }
    static void ObserverException()
    {
        using var f = Active(); f.Events.Clear(); f.Environment.ThrowViewRead = true;
        f.Update(0x600, 88); Trace(f, "update:1536:88"); Equal(f.Captured!.CanReleaseView(Owned), false);
    }
    static void AutomaticClientObject()
    {
        using var f = Active(); f.Events.Clear();
        var create = f.Factory.Get<FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager.Delegates.CreateBattleCharacter>(PortraitOwnedSceneNativeKind.CreateClientObject);
        Equal(create(null, uint.MaxValue, 0xB7), 0xAABBCCDDu);
        Trace(f, "retire,release:1280,create:4294967295:183");
    }
    static void SourceClientObject()
    {
        using var f = Active(); f.Events.Clear();
        var delete = f.Factory.Get<FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager.Delegates.DeleteObjectByIndex>(PortraitOwnedSceneNativeKind.DeleteClientObject);
        delete(null, 244, 0xB7); Trace(f, "retire,release:1280,delete:244:183");
    }
    static void NativeSetupStack()
    {
        using var f = Active(); f.Events.Clear(); f.Environment.Views[0x600] = 6;
        var detour = f.Factory.Get<CharaViewPortrait.Delegates.Setup>(PortraitOwnedSceneNativeKind.Setup);
        var address = Marshal.GetFunctionPointerForDelegate(detour);
        ((delegate* unmanaged<CharaViewPortrait*, uint, CharaViewCharacterData*, FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInterface*, int, ulong, void>)address)
            ((CharaViewPortrait*)0x600, 6, (CharaViewCharacterData*)0x123456, (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInterface*)0x456789, -12345, 0xFEDCBA9876543210);
        GC.KeepAlive(detour); Trace(f, "retire,release:1280,setup:1536:6:1193046:4548489:-12345:FEDCBA9876543210");
    }
    static void NativeDtorReturn()
    {
        using var f = Active(); f.Events.Clear(); f.Environment.Views[0x600] = 6;
        var detour = f.Factory.Get<CharaView.Delegates.Dtor>(PortraitOwnedSceneNativeKind.Dtor);
        var address = Marshal.GetFunctionPointerForDelegate(detour);
        var result = ((delegate* unmanaged<CharaView*, byte, CharaView*>)address)((CharaView*)0x600, 0xA5);
        GC.KeepAlive(detour); Equal((nint)result, (nint)0x12345678);
        Trace(f, "retire,release:1280,dtor:1536:165");
    }
    static void Closing()
    {
        using var f = Active(); f.Events.Clear(); f.Hooks.RequestStop();
        Equal(f.Hooks.TryDispose(), false); True(f.Factory.Created.Any(h => h.IsEnabled));
        f.Render(Source.Model, 101); Trace(f, "render:768:101,retire,release:1280");
        True(f.Hooks.TryDispose()); True(f.Factory.Created.All(h => h.DisposeCalls == 1 && !h.IsEnabled));
    }
    static void Abi() => True(PortraitOwnedSceneHooks.NativeAbiMatchesSDK(out _));

    static Fixture Active(bool retireSucceeds = true)
    {
        var f = new Fixture(); True(f.Hooks.TryInstall(f.Factory)); f.RetireSucceeds = retireSucceeds;
        True(f.Request(lease => { f.Captured = lease; if (lease.CanCreate) Create(f,lease);
            using var renderScope = lease.EnterOwnedCall(Owned); True(renderScope is not null); f.Render(Owned, lease.NativeFrame); }));
        f.Render(Source.Model, 100); f.Events.Clear(); return f;
    }

    sealed class Fixture : IDisposable
    {
        public readonly List<string> Events = [];
        public readonly TestEnvironment Environment;
        public readonly FakeFactory Factory;
        public readonly PortraitOwnedSceneHooks Hooks;
        public IPortraitOwnedSceneLease? Captured;
        public bool RetireSucceeds = true;
        public Fixture(bool complete = true) { Environment = new(complete); Factory = new(this); Hooks = new(Environment); }
        public bool Request(Action<IPortraitOwnedSceneLease> advance) => Hooks.TryRequest(Source, 6, advance, lease => {
            Events.Add("retire"); if (!RetireSucceeds) return false;
            using var scope = lease.EnterOwnedCall(Owned); if (scope is null) throw new Exception("retire lost scope");
            True(lease.TryBeginRetirement(Owned));
            True(lease.CanReleaseView(Owned)); Release(Owned); return lease.MarkReleased(Owned);
        });
        public void Render(nint view, uint frame) => Factory.Get<CharaView.Delegates.Render>(PortraitOwnedSceneNativeKind.Render)((CharaView*)view, frame);
        public void Initialize(nint view, uint slot) => Factory.Get<CharaView.Delegates.Initialize>(PortraitOwnedSceneNativeKind.Initialize)((CharaView*)view, null, slot, 0);
        public void Update(nint view, uint frame) => Factory.Get<CharaView.Delegates.Update>(PortraitOwnedSceneNativeKind.Update)((CharaView*)view, frame, null);
        public void Release(nint view) => Factory.Get<CharaView.Delegates.Release>(PortraitOwnedSceneNativeKind.Release)((CharaView*)view);
        public void Dispose() { Hooks.RequestStop(); if (Hooks.TryDispose()) return; Environment.Framework = true; Environment.SourceCurrent = true;
            if (Environment.SlotOwner == Owned) Render(Source.Model, 900); Hooks.TryDispose(); }
    }
    sealed class TestEnvironment(bool complete) : IPortraitOwnedSceneHookEnvironment
    {
        private static long nextModule = 0x140000000;
        private readonly nint module = (nint)Interlocked.Add(ref nextModule, 0x1000000);
        public bool Framework = true, SourceCurrent = true, ThrowViewRead;
        public nint SlotOwner;
        public Dictionary<nint, uint> Views = new() { [Source.Model] = 4, [Owned] = 6 };
        public bool IsFrameworkThread => Framework;
        public bool TryValidateInstallation(out string reason) { reason = complete ? "" : "context ingress unresolved"; return complete; }
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool IsSourceCurrent(PortraitIdentity source) => SourceCurrent && source == Source;
        public bool IsSlotIdle(uint slot) => slot == 6 && SlotOwner == 0;
        public bool IsSlotOwnedBy(uint slot, nint view) => slot == 6 && view != 0 && SlotOwner == view;
        public bool TryReadViewSlot(nint view, out uint slot) { if (ThrowViewRead) throw new InvalidOperationException("observer failed"); return Views.TryGetValue(view, out slot); }
    }
    sealed class FakeFactory(Fixture fixture) : IPortraitOwnedSceneHookFactory
    {
        public readonly List<FakeHookBase> Created = [];
        readonly Dictionary<PortraitOwnedSceneNativeKind, Delegate> detours = [];
        public int FailEnableAt = -1;
        public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target, T detour) where T : Delegate
        {
            detours[target.Kind] = detour;
            Delegate original = target.Kind switch {
                PortraitOwnedSceneNativeKind.Render => (CharaView.Delegates.Render)((v, f) => fixture.Events.Add($"render:{(nint)v}:{f}")),
                PortraitOwnedSceneNativeKind.Initialize => (CharaView.Delegates.Initialize)((v, a, s, c) => { fixture.Events.Add($"initialize:{(nint)v}:{s}"); fixture.Environment.Views[(nint)v] = s; fixture.Environment.SlotOwner = (nint)v; }),
                PortraitOwnedSceneNativeKind.Release => (CharaView.Delegates.Release)(v => { fixture.Events.Add($"release:{(nint)v}"); if (fixture.Environment.SlotOwner == (nint)v) fixture.Environment.SlotOwner = 0; }),
                PortraitOwnedSceneNativeKind.Update => (CharaView.Delegates.Update)((v, f, c) => fixture.Events.Add($"update:{(nint)v}:{f}")),
                PortraitOwnedSceneNativeKind.CreateClientObject => (FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager.Delegates.CreateBattleCharacter)((m, i, p) => { fixture.Events.Add($"create:{i}:{p}"); return 0xAABBCCDDu; }),
                PortraitOwnedSceneNativeKind.DeleteClientObject => (FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager.Delegates.DeleteObjectByIndex)((m, i, p) => fixture.Events.Add($"delete:{i}:{p}")),
                PortraitOwnedSceneNativeKind.Setup => (CharaViewPortrait.Delegates.Setup)((v, s, d, a, i, k) => fixture.Events.Add($"setup:{(nint)v}:{s}:{(nint)d}:{(nint)a}:{i}:{k:X16}")),
                PortraitOwnedSceneNativeKind.Dtor => (CharaView.Delegates.Dtor)((v, flags) => { fixture.Events.Add($"dtor:{(nint)v}:{flags}"); return (CharaView*)0x12345678; }),
                _ => detour,
            };
            var hook = new FakeHook<T>((T)original, Created.Count == FailEnableAt); Created.Add(hook); return hook;
        }
        public T Get<T>(PortraitOwnedSceneNativeKind kind) where T : Delegate => (T)detours[kind];
    }
    abstract class FakeHookBase : IPortraitOwnedSceneNativeHook
    {
        public bool IsEnabled { get; protected set; }
        public bool IsDisposed { get; private set; }
        public int DisposeCalls;
        public abstract void Enable();
        public void Disable() => IsEnabled = false;
        public void Dispose() { DisposeCalls++; IsEnabled = false; IsDisposed = true; }
    }
    sealed class FakeHook<T>(T original, bool fail) : FakeHookBase, IPortraitOwnedSceneNativeHook<T> where T : Delegate
    {
        public T Original => original;
        public override void Enable() { if (fail) throw new InvalidOperationException("native backend enable failed"); IsEnabled = true; }
    }
}
#endif
