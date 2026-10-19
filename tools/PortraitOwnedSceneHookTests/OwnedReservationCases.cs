#if HAS_OWNED_HOOKS
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

// The real observers, dispatcher and shared process registry compete. Only native
// Hook construction/entry points and admission observations are detached fixtures.
internal static unsafe class OwnedReservationCases
{
    private static long moduleBase = 0x1900000000;
    private static readonly PortraitIdentity Source = new(0x100, 0x200, 0x300, 7, 0x400);

    public static int Run()
    {
        var passed = 0; var failed = 0;
        foreach (var (name, test) in new (string, Action)[] {
            ("passive host ownership rejects owned dispatcher before any Create", HostWins),
            ("owned dispatcher reserves its complete batch before the first Create", OwnedWinsBeforeCreate),
            ("normal retirement and stale disposal preserve a later host generation", NormalRetirement),
            ("concurrent owned generations cannot split the same native batch", ConcurrentGenerations),
            ("failed enable retires every returned hook before releasing the batch", EnableFailure),
            ("unknown Create keeps every address isolated across later generations", UnknownCreate),
            ("disable failure retains the entire batch until proven retirement", DisableFailure),
            ("dispose failure retains the entire batch until proven retirement", DisposeFailure),
            ("a returned but unconfirmed disposal cannot release the batch", UnconfirmedDisposal),
            ("changed reservation ownership cannot release any part of the batch", ChangedOwnership),
            ("an active native Original prevents hook and reservation retirement", InFlightOriginal),
        })
        {
            try { test(); passed++; Console.WriteLine("PASS reservation " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL reservation " + name + ": " + error.Message); }
        }
        Console.WriteLine($"{passed} reservation passed; {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static nint NewModule() => (nint)Interlocked.Add(ref moduleBase, 0x1000000);
    private static nint[] Addresses(EnvironmentProof environment) => environment.Targets.Select(target => target.Address).ToArray();
    private static bool AllReserved(EnvironmentProof environment) => Addresses(environment).All(new PortraitNativePhaseHostReservations().IsReserved);
    private static bool NoneReserved(EnvironmentProof environment) => Addresses(environment).All(address => !new PortraitNativePhaseHostReservations().IsReserved(address));

    private static PortraitNativePhaseHost Host(nint address, HostFactory factory)
    {
        var plan = new PortraitNativePhaseResolvedPlan(address, 0xA00000,
            PortraitNativePhaseObservationBinding.EXE, PortraitNativePhaseObservationBinding.SDK,
            typeof(CharaView).Assembly.ManifestModule.ModuleVersionId,
            PortraitNativePhaseObservationBinding.Inventory.Select(contract =>
                new PortraitNativePhaseBoundTarget(contract.Phase, address + (nint)contract.RVA, contract.Delegate)).ToArray());
        bool Resolve(Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? result, out string reason)
        { result = plan; reason = "detached shared-entry fixture"; return !plan.Targets.Any(target => reserved(target.Address)); }
        return new(factory, new PortraitNativePhaseObservation(), new PortraitNativePhaseHostReservations(), Resolve);
    }

    private static void Retire(PortraitNativePhaseHost host)
        => Check(host.BeginRetirement(out _) && host.TryCompleteRetirement(out _), "host did not retire");

    private static void HostWins()
    {
        var environment = new EnvironmentProof(NewModule()); var hostFactory = new HostFactory(); var host = Host(environment.Module, hostFactory);
        Check(host.TryStart(TimeSpan.FromSeconds(1), out _), "passive host did not install");
        var factory = new OwnedFactory(); using var hooks = new PortraitOwnedSceneHooks(environment);
        var installed = hooks.TryInstall(factory);
        try
        {
            Check(!installed && factory.Created.Count == 0, "owned dispatcher constructed a hook over the real host's Render reservation");
            var reserved = new PortraitNativePhaseHostReservations();
            Check(environment.Targets.Where(target => target.Kind != PortraitOwnedSceneNativeKind.Render).All(target => !reserved.IsReserved(target.Address)),
                "losing owned batch acquired a non-overlapping prefix");
        }
        finally { hooks.TryDispose(); Retire(host); }
    }

    private static void OwnedWinsBeforeCreate()
    {
        var environment = new EnvironmentProof(NewModule()); var hostFactory = new HostFactory(); var host = Host(environment.Module, hostFactory);
        var checkedFirstCreate = false;
        var factory = new OwnedFactory { BeforeFirstCreate = () => {
            Check(AllReserved(environment), "owned dispatcher did not reserve every entry before its first Create");
            Check(!host.TryStart(TimeSpan.FromSeconds(1), out _) && hostFactory.Created == 0,
                "a reentrant host installed the overlapping Render hook");
            checkedFirstCreate = true;
        }};
        using var hooks = new PortraitOwnedSceneHooks(environment);
        var installed = hooks.TryInstall(factory);
        try { Check(installed && checkedFirstCreate, hooks.LastError ?? "first Create transaction was not reached"); }
        finally { hooks.TryDispose(); host.BeginRetirement(out _); host.TryCompleteRetirement(out _); }
    }

    private static void NormalRetirement()
    {
        var environment = new EnvironmentProof(NewModule()); var firstFactory = new OwnedFactory(); var first = new PortraitOwnedSceneHooks(environment);
        Check(first.TryInstall(firstFactory), "first owned generation did not install");
        try { Check(AllReserved(environment), "installed owned generation has an unreserved entry"); }
        finally { first.TryDispose(); }
        Check(firstFactory.Created.All(hook => hook.IsDisposed && hook.DisposeCalls == 1) && NoneReserved(environment),
            "normal retirement did not release exactly its completely retired batch");
        var hostFactory = new HostFactory(); var host = Host(environment.Module, hostFactory);
        Check(host.TryStart(TimeSpan.FromSeconds(1), out _), "normal owned retirement did not unblock the host");
        try
        {
            Check(first.TryDispose(), "stale disposal stopped being idempotent");
            Check(new PortraitNativePhaseHostReservations().IsReserved(environment.Module + 0x9CD4D0), "old disposal removed the host's replacement reservation");
            var laterFactory = new OwnedFactory(); using var later = new PortraitOwnedSceneHooks(environment);
            Check(!later.TryInstall(laterFactory) && laterFactory.Created.Count == 0, "later owned generation bypassed the replacement host");
        }
        finally { Retire(host); }
        using var final = new PortraitOwnedSceneHooks(environment); var finalFactory = new OwnedFactory();
        Check(final.TryInstall(finalFactory) && final.TryDispose() && NoneReserved(environment), "third generation could not reuse a fully retired batch");
    }

    private static void ConcurrentGenerations()
    {
        var environment = new EnvironmentProof(NewModule()); using var ready = new Barrier(2);
        using var first = new PortraitOwnedSceneHooks(environment); using var second = new PortraitOwnedSceneHooks(environment);
        var a = new OwnedFactory(); var b = new OwnedFactory();
        var one = Task.Run(() => { ready.SignalAndWait(); return first.TryInstall(a); });
        var two = Task.Run(() => { ready.SignalAndWait(); return second.TryInstall(b); }); Task.WaitAll(one, two);
        try
        {
            Check(one.Result != two.Result, "concurrent owned generations both installed or both lost an idle batch");
            Check((one.Result ? b : a).Created.Count == 0 && AllReserved(environment), "the losing generation acquired or created a partial batch");
        }
        finally { first.TryDispose(); second.TryDispose(); }
        Check(NoneReserved(environment), "concurrent generation retirement leaked reservations");
    }

    private static void EnableFailure()
    {
        var environment = new EnvironmentProof(NewModule()); var factory = new OwnedFactory { FailEnableAt = 3 };
        using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(!hooks.TryInstall(factory), "enable-failure fixture unexpectedly installed");
        Check(factory.Created.All(hook => hook.IsDisposed && !hook.IsEnabled) && NoneReserved(environment),
            "failed enable released incompletely retired hooks or retained a completely retired batch");
        var host = Host(environment.Module, new HostFactory());
        Check(host.TryStart(TimeSpan.FromSeconds(1), out _), "known clean enable rollback did not unblock later generations"); Retire(host);
    }

    private static void UnknownCreate()
    {
        var environment = new EnvironmentProof(NewModule()); var factory = new OwnedFactory { FailCreateAt = 3 };
        using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(!hooks.TryInstall(factory), "unknown-create fixture unexpectedly installed");
        Check(factory.Created.All(hook => hook.IsDisposed), "unknown Create did not retire other returned handles");
        Check(!hooks.TryDispose() && AllReserved(environment), "Create without a returned handle released an unproved native batch");
        var laterFactory = new OwnedFactory(); using var later = new PortraitOwnedSceneHooks(environment);
        Check(!later.TryInstall(laterFactory) && laterFactory.Created.Count == 0, "a later owned generation reused the quarantined batch");
        var hostFactory = new HostFactory(); var host = Host(environment.Module, hostFactory);
        Check(!host.TryStart(TimeSpan.FromSeconds(1), out _) && hostFactory.Created == 0, "the passive host reused an unknown native entry");
        Check(AllReserved(environment), "failed later attempts removed any quarantined reservation");
    }

    private static void DisableFailure() => RetryableFailure("disable");
    private static void DisposeFailure() => RetryableFailure("dispose");
    private static void UnconfirmedDisposal() => RetryableFailure("unconfirmed");
    private static void RetryableFailure(string failure)
    {
        var environment = new EnvironmentProof(NewModule()); var factory = new OwnedFactory(); using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(hooks.TryInstall(factory), "retirement fixture did not install"); var victim = factory.Created[3];
        victim.FailDisable = failure == "disable"; victim.FailDispose = failure == "dispose"; victim.UnconfirmedDispose = failure == "unconfirmed";
        Check(!hooks.TryDispose() && AllReserved(environment), failure + " failure released the native batch or falsely reported retirement");
        if (failure == "disable") Check(victim.DisposeCalls == 0 && victim.IsEnabled, "an enabled hook was disposed after failed Disable");
        var hostFactory = new HostFactory(); var host = Host(environment.Module, hostFactory);
        Check(!host.TryStart(TimeSpan.FromSeconds(1), out _) && hostFactory.Created == 0, failure + " failure allowed an overlapping host");
        victim.FailDisable = victim.FailDispose = victim.UnconfirmedDispose = false;
        Check(hooks.TryDispose() && factory.Created.All(hook => hook.IsDisposed) && NoneReserved(environment),
            "a verified later retirement could not release the retained batch");
        Check(host.TryStart(TimeSpan.FromSeconds(1), out _), "retired failure fixture still blocked the host"); Retire(host);
    }

    private static void ChangedOwnership()
    {
        var environment = new EnvironmentProof(NewModule()); var factory = new OwnedFactory(); using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(hooks.TryInstall(factory), "ownership fixture did not install");
        // Replace one real registry token, preserving the root. Restore only this fixture's
        // saved token after proving that retirement did not delete any replacement owner.
        var root = (Tuple<object, Dictionary<nint, object>>)AppDomain.CurrentDomain.GetData("FireGaze.NativeHookReservations.v1")!;
        var address = Addresses(environment)[3]; object original;
        lock (root.Item1) { original = root.Item2[address]; root.Item2[address] = new object(); }
        try { Check(!hooks.TryDispose() && AllReserved(environment), "lost reservation ownership partially released the batch"); }
        finally { lock (root.Item1) root.Item2[address] = original; }
        Check(hooks.TryDispose() && NoneReserved(environment), "restored exact reservation ownership did not permit final retirement");
    }

    private static void InFlightOriginal()
    {
        var environment = new EnvironmentProof(NewModule()); var factory = new OwnedFactory(); using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(hooks.TryInstall(factory), "in-flight fixture did not install"); var checkedOriginal = false;
        factory.DuringRender = () => {
            checkedOriginal = true;
            Check(!hooks.TryDispose() && AllReserved(environment), "a reentrant native Original released its active hook batch");
            Check(factory.Created.All(hook => hook.IsEnabled && !hook.IsDisposed), "retirement touched a hook while its Original was on the stack");
        };
        factory.Render((CharaView*)Source.Model, 123);
        Check(checkedOriginal && hooks.TryDispose() && NoneReserved(environment), "completed Original did not allow later proven retirement");
    }

    private sealed class EnvironmentProof(nint module) : IPortraitOwnedSceneHookEnvironment
    {
        public nint Module => module;
        public bool IsFrameworkThread => true;
        public bool TryValidateInstallation(out string reason) { reason = "detached complete admission fixture"; return true; }
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool IsSourceCurrent(PortraitIdentity source) => source == Source;
        public bool IsSlotIdle(uint slot) => slot == 6;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = 4; return view == Source.Model; }
    }

    private sealed class OwnedFactory : IPortraitOwnedSceneHookFactory
    {
        public List<OwnedHookBase> Created { get; } = [];
        public int FailCreateAt = -1, FailEnableAt = -1;
        public Action? BeforeFirstCreate, DuringRender;
        public CharaView.Delegates.Render Render = null!;
        public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target, T detour) where T : Delegate
        {
            if (Created.Count == 0) BeforeFirstCreate?.Invoke();
            if (Created.Count == FailCreateAt) throw new InvalidOperationException("Create returned no handle");
            Delegate original = detour;
            if (target.Kind == PortraitOwnedSceneNativeKind.Render)
            { Render = (CharaView.Delegates.Render)(Delegate)detour; original = (CharaView.Delegates.Render)((_, _) => DuringRender?.Invoke()); }
            var hook = new OwnedHook<T>((T)original) { FailEnable = Created.Count == FailEnableAt }; Created.Add(hook); return hook;
        }
    }
    private abstract class OwnedHookBase : IPortraitOwnedSceneNativeHook
    {
        public bool IsEnabled { get; private set; }
        public bool IsDisposed { get; private set; }
        public bool FailEnable, FailDisable, FailDispose, UnconfirmedDispose;
        public int DisposeCalls;
        public void Enable() { if (FailEnable) throw new InvalidOperationException("Enable failed"); IsEnabled = true; }
        public void Disable() { if (FailDisable) throw new InvalidOperationException("Disable failed"); IsEnabled = false; }
        public void Dispose() { DisposeCalls++; if (FailDispose) throw new InvalidOperationException("Dispose failed"); if (!UnconfirmedDispose) IsDisposed = true; }
    }
    private sealed class OwnedHook<T>(T original) : OwnedHookBase, IPortraitOwnedSceneNativeHook<T> where T : Delegate { public T Original => original; }

    private sealed class HostFactory : IPortraitNativePhaseHostFactory
    {
        public int Created;
        public string Identity => "detached official provider fixture";
        public bool TryQualifyStartDisabled(out string reason) { reason = Identity; return true; }
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason) { reason = Identity; return true; }
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type type) => Original(target.Phase, type);
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type type, Delegate detour)
        { Created++; return new HostHook(target.Address, Original(target.Phase, type)); }
        private static Delegate Original(PortraitNativeObservedPhase phase, Type type) => Delegate.CreateDelegate(type,
            typeof(OwnedReservationCases).GetMethod(phase switch {
                PortraitNativeObservedPhase.ManagerRender => nameof(Manager), PortraitNativeObservedPhase.ManagerUpdateCandidate => nameof(Delta),
                PortraitNativeObservedPhase.SceneCameraUpdate => nameof(Camera), _ => nameof(View),
            })!);
    }
    private sealed class HostHook(nint address, Delegate original) : IPortraitNativePhaseHostHook
    {
        public nint Address => address;
        public string BackendName => "SafetyHook";
        public bool IsEnabled { get; private set; }
        public bool IsDisposed { get; private set; }
        public Delegate Original => original;
        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
        public void Dispose() => IsDisposed = true;
    }
    public static void Manager(RenderManager* _) { }
    public static void Delta(RenderManager* _, float __) { }
    public static void Camera(SceneCameraManager* _) { }
    public static void View(CharaView* _, uint __) { }
}
#endif
