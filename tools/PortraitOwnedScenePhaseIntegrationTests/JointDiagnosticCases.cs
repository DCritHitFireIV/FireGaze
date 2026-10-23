using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static unsafe class JointDiagnosticCases
{
    private static int checks;
    internal static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("FAIL joint diagnostic " + message); checks++; }
    internal static int Run()
    {
#if !JOINT_DIAGNOSTIC
        Console.Error.WriteLine("FAIL same-installation joint source-return census and explicit diagnostic controller are absent");
        return 1;
#else
        using (var b = new JointDiagnosticBoundary())
        {
            Check(b.Memory.Memory.Reads == 0 && b.Provider.Created.Count == 0, "construction has no native reads or hooks");
            Check(b.Controller.TryStart(out var reason), reason);
            Check(b.Memory.Memory.Reads == 0 && b.Provider.Created.Count == 46, "start installs one 46-entry batch without reading slots");
            b.Source(0);
            var value = b.Controller.Snapshot;
            Check(value.Census is { Slots: { Complete: true, Rows.Count: 8 }, SourceReturn.NativeFrame: 0 }
                && value.Census.Installation == value.Host!.Installation
                && value.Census.SourceReturn.Installation == value.Host.Installation
                && !value.Host.ProductionAdmission && !value.Census.Slots.ProductionAdmission,
                "one actual same-installation source return supplies eight historical rows without scene admission");
            var reads = b.Memory.Memory.Reads; b.Source(1);
            Check(b.Memory.Memory.Reads == reads, "one-shot census performs no additional native reads on a later return");
            b.Controller.Update();
            Check(b.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && !b.Controller.BlocksOperations
                && b.Provider.Created.All(h => h.IsDisposed), "success retires all 46 actual hooks before unblocking operations");
        }
        using (var b = new JointDiagnosticBoundary())
        {
            Check(b.Controller.TryStart(out _), "foreign return fixture start");
            b.Source(1, (nint)b.Memory.Source + 16);
            Check(b.Memory.Memory.Reads == 0 && b.Controller.Snapshot.Census is null, "foreign receiver cannot authorize one native read");
            Task.Run(() => b.Source(2)).GetAwaiter().GetResult();
            Check(b.Memory.Memory.Reads == 0 && b.Controller.Snapshot.Census is null, "worker source callback cannot sample the eight-slot table");
        }
        using (var b = new JointDiagnosticBoundary())
        {
            Check(b.Controller.TryStart(out _), "mutation fixture start"); var fired = false;
            b.Memory.Memory.AfterRead = _ => { if (!fired) { fired = true; b.Counter(); } };
            b.Source(3);
            Check(fired && b.Controller.Snapshot.Census is null, "typed counter mutation invalidates an in-progress same-installation census");
            b.Memory.Memory.AfterRead = null; b.Source(4);
            Check(b.Controller.Snapshot.Census is { SourceReturn.NativeFrame: 4, Slots.Rows.Count: 8 },
                "a later actual source return can produce a fresh complete census after mutation drains");
        }
        using (var b = new JointDiagnosticBoundary())
        {
            Check(b.Controller.TryStart(out _), "session loss fixture start");
            b.Memory.Memory.AfterRead = _ => { b.Context = b.Context with { SessionEpoch = 8 }; b.Controller.Revoke("Source lifecycle invalidated the detached request."); };
            b.Source(5);
            Check(b.Controller.Snapshot.Census is null, "lifecycle revocation during a native read rejects every partial row");
        }
        var publicationClockChecks = 0;
        using (var b = new JointDiagnosticBoundary())
        {
            Check(b.Controller.TryStart(out _), "publication clock baseline fixture start");
            b.OnClock = () =>
            {
                if (b.Memory.Memory.Reads > 0 && !b.NativeScopeOpen && !b.HasCensusDetached) publicationClockChecks++;
            };
            b.Source(6); b.OnClock = null;
            Check(publicationClockChecks > 0 && b.Controller.Snapshot.Census is not null,
                "clean baseline reaches and counts every post-scope timestamp through final publication");
        }
        for (var offset = 1; offset <= publicationClockChecks; offset++)
        {
            using var b = new JointDiagnosticBoundary();
            Check(b.Controller.TryStart(out _), "publication mutation fixture start");
            var reads = 0; var fired = false;
            b.OnClock = () =>
            {
                if (b.Memory.Memory.Reads > 0 && !b.NativeScopeOpen && !b.HasCensusDetached && ++reads == offset)
                { fired = true; b.Counter(); }
            };
            b.Source(6); b.OnClock = null;
            Check(fired, "counter injection reaches post-scope timestamp " + offset + " of " + publicationClockChecks);
            Check(b.Controller.Snapshot.Census is null,
                "counter mutation during post-scope publication checks rejects the old-epoch census");
        }
        using (var b = new JointDiagnosticBoundary())
        {
            b.OnCreate = () => b.Controller.Revoke("Lifecycle revoked during managed host construction.");
            Check(!b.Controller.TryStart(out _) && b.Provider.Created.Count == 0,
                "revocation during managed host construction rejects all native installation");
            Check(b.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && !b.Controller.BlocksOperations,
                "revoked managed construction retires its owner and unblocks after confirmed drain");
        }
        Console.WriteLine($"JointDiagnosticCases: {checks} checks passed; actual joint gateways and allocated eight-slot memory.");
        return 0;
#endif
    }
}

#if JOINT_DIAGNOSTIC
internal sealed unsafe class JointDiagnosticBoundary : IDisposable
{
    internal readonly Fixture Memory = new();
    internal readonly OwnedPhysicalCases.Provider Provider = new();
    internal readonly PortraitOwnedSceneJointPlan Plan;
    internal readonly PortraitOwnedSceneJointDiagnostic Controller;
    internal PortraitSourceCensusContext Context;
    internal Func<PortraitSourceCensusContext?>? ReadContext = null;
    internal PortraitOwnedSceneJointHost? Host;
    internal Action? OnClock = null;
    internal Action? OnCreate = null;
    internal long Now = 0;
    private object CensusObject
    {
        get
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var run = Controller.GetType().GetField("run", flags)!.GetValue(Controller)!;
            return run.GetType().GetField("Census", flags)!.GetValue(run)!;
        }
    }
    internal bool HasCensusDetached => CensusObject.GetType().GetProperty("Result",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(CensusObject) is not null;
    internal bool NativeScopeOpen
    {
        get
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var census = CensusObject;
            var phase = census.GetType().GetField("phases", flags)!.GetValue(census)!;
            return (long)phase.GetType().GetField("token", flags)!.GetValue(phase)! != 0;
        }
    }
    private readonly PortraitNativePhaseHostReservations reservations = new();
    private static long nextBase = 0xBC0000000;
    internal JointDiagnosticBoundary()
    {
        Memory.SetIdle(6);
        Context = new(Memory.Phase.Source, 1, 2, 7);
        if (!PortraitOwnedSceneJointPlan.TryCreate((nint)Interlocked.Add(ref nextBase, 0x10000000), out var plan, out var reason)) throw new InvalidOperationException(reason);
        Plan = plan!;
        var phases = new PhaseProvider(Provider);
        var factory = new DalamudPortraitOwnedSceneJointHookFactory(Provider, new OwnedPhysicalCases.Proof(), phases, Resolve);
        Controller = new(() => true, () => ReadContext?.Invoke() ?? Context,
            bindings => {
                Host = new(new Environment(Plan.ModuleBase), factory, reservations, bindings.Counters, bindings.Resources,
                    bindings.RequestCurrent, () => { OnClock?.Invoke(); return Now; }, 1000, PortraitSourceCensusDiagnostic.CurrentThread,
                    mutationReceiver: bindings.MutationReceiver, sourceReceiver: bindings.SourceReturn);
                OnCreate?.Invoke(); return Host;
            },
            () => Plan, Memory.Memory);
    }
    private bool Resolve(PortraitNativePhaseTargetSet set, Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? plan, out string reason)
    {
        plan = new(Plan.ModuleBase, 0x3811000, PortraitNativePhaseObservationBinding.EXE, PortraitNativePhaseObservationBinding.SDK,
            Guid.Parse("c72f17a8-6ed1-4a30-a097-74137dc08f9e"), PortraitNativeCounterIngressContract.For(set)
                .Select(c => new PortraitNativePhaseBoundTarget(c.Phase, Plan.ModuleBase + (nint)c.RVA, c.Delegate)).ToArray());
        reason = "test exact external resolution"; return plan.Targets.All(t => !reserved(t.Address));
    }
    internal void Source(uint frame, nint receiver = 0)
        => Call<CharaView.Delegates.Render>(PortraitNativeObservedPhase.SourceCharaViewRender)((CharaView*)(receiver == 0 ? (nint)Memory.Source : receiver), frame);
    internal void Counter() => Call<PortraitNativeSlotCounter>(PortraitNativeObservedPhase.CounterAdd)((FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager*)Memory.RenderManager, 6);
    internal byte RecreateResource() => Call<PortraitNativeResourceRecreate>(PortraitNativeObservedPhase.ResourceRecreate)(
        (FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager*)Memory.Phase.Roots.RenderTargets);
    internal void ClearIdleRetainedRenderState(uint slot)
    {
        *((byte*)Memory.RenderManager + 0x3837C + slot) = 0;
        *(nint*)(Memory.Phase.Roots.Offscreen + 0xC8 + (nint)(slot * 8)) = 0;
        *(nint*)((nint)Memory.RenderManager + 0x2738 + (nint)(slot * 24)) = 0;
    }
    private T Call<T>(PortraitNativeObservedPhase phase) where T : Delegate
    {
        var address = Plan.Targets.Single(t => t.Roles.Contains(new(null, phase))).Address;
        var detour = Provider.Created.Last(h => h.Address == address).Detour;
        return (T)Delegate.CreateDelegate(typeof(T), detour.Target, detour.Method);
    }
    public void Dispose() { Controller.Stop("test cleanup"); Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)); Memory.Dispose(); }
    private sealed class PhaseProvider(OwnedPhysicalCases.Provider provider) : IPortraitNativePhaseHostFactory
    {
        public string Identity => provider.Identity;
        public bool TryQualifyStartDisabled(out string reason) => provider.TryQualifyStartDisabled(out reason);
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason)
        { reason = "test targets unmodified"; return plan.Targets.All(t => provider.Created.All(h => h.Address != t.Address || !h.IsEnabled)); }
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type type) => provider.DirectTarget(target.Address, type);
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type type, Delegate detour)
            => provider.CreateQualifiedHook(target.Address, type, detour, () => true);
    }
    private sealed class Environment(nint module) : IPortraitOwnedSceneHookEnvironment
    {
        public bool IsFrameworkThread => true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason) { reason = "production disabled"; return false; }
        public bool IsSourceCurrent(PortraitIdentity source) => false;
        public bool IsSlotIdle(uint slot) => false;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = 0; return false; }
    }
}
#endif
