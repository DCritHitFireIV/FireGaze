using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FireGaze.PortraitPreview;

internal static class HostReservationCases
{
    private static long moduleBase = 0x900000000;

    internal static void Run(Action<bool, string> check)
    {
        FirstCreateReservesBeforeReentry(check);
        AtomicBatchAcquisition(check);
        ResolutionAcquisitionRace(check);
        RetirementFailureRetention(check);
        InFlightRetention(check);
        RootReplacementRefuses(check);
        CollectibleRegistries(check);
    }

    private static void FirstCreateReservesBeforeReentry(Action<bool, string> check)
    {
        var plan = Plan();
        var reservations = new RegistryProbe();
        var competitor = new RegistryProbe(reservations);
        object? competingLease = null;
        var competitorSucceeded = false;
        var factory = new Factory
        {
            BeforeFirstCreate = () => competitorSucceeded = competitor.TryAcquire(
                plan.Targets.Select(target => target.Address).ToArray(), out competingLease),
        };
        using var observer = new PortraitNativePhaseObservation();
        var host = NewHost(factory, observer, reservations, plan);
        var started = host.TryStart(TimeSpan.FromSeconds(1), out var reason);
        check(plan.Targets.All(target => reservations.IsReserved(target.Address)), "all entries remain reserved while hooks are installed");
        host.Stop();
        check(plan.Targets.All(target => reservations.IsReserved(target.Address)), "stopping only the recorder retains every native entry reservation");
        if (competingLease is not null) competitor.Release(competingLease);
        var retired = host.BeginRetirement(out _) && host.TryCompleteRetirement(out _);
        check(started && retired, "first-create reservation fixture uses the real host installation/retirement: " + reason);
        check(!competitorSucceeded,
            "same-plugin external acquisition is excluded before FirstCreate; a query-only external guard leaves this race open");
    }

    private static PortraitNativePhaseHost NewHost(Factory factory, PortraitNativePhaseObservation observer,
        RegistryProbe registry, PortraitNativePhaseResolvedPlan plan)
    {
        bool Resolve(Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? value, out string reason)
        { value = plan; reason = "reservation fixture"; return !plan.Targets.Any(target => reserved(target.Address)); }
        return new(factory, observer, registry.Raw, Resolve);
    }

    private static void AtomicBatchAcquisition(Action<bool, string> check)
    {
        var first = new RegistryProbe(); var second = new RegistryProbe();
        var address = Plan().ModuleBase;
        foreach (var invalid in new[] { Array.Empty<nint>(), new nint[] { 0 }, new nint[] { -1 }, new[] { address, address } })
            check(!first.TryAcquire(invalid, out _), "invalid/empty/duplicate native entry batches are rejected without acquisition");
        using var ready = new Barrier(2);
        object? firstLease = null; object? secondLease = null;
        var a = Task.Run(() => { ready.SignalAndWait(); return first.TryAcquire([address, address + 1], out firstLease); });
        var b = Task.Run(() => { ready.SignalAndWait(); return second.TryAcquire([address + 1, address + 2], out secondLease); });
        Task.WaitAll(a, b);
        check(a.Result != b.Result, "independent registry wrappers atomically exclude intersecting concurrent batches");
        var winner = a.Result ? first : second; var lease = a.Result ? firstLease! : secondLease!;
        var unowned = a.Result ? address + 2 : address;
        check(!first.IsReserved(unowned), "losing batch acquisition cannot retain its non-conflicting prefix/suffix");
        check(winner.Release(lease), "complete owner releases its batch once");
        check(first.TryAcquire([address, address + 1, address + 2], out var replacement), "all addresses can be reused only after owner release");
        check(winner.Release(lease) && new[] { address, address + 1, address + 2 }.All(first.IsReserved),
            "releasing an old already-released lease cannot delete a later owner's reservations");
        check(first.Release(replacement!), "replacement owner releases its own token");
        var detachedAddresses = new[] { address };
        check(first.TryAcquire(detachedAddresses, out var detached), "caller address batch is accepted");
        detachedAddresses[0]++;
        check(first.Release(detached!) && !first.IsReserved(address), "lease retains a detached immutable address batch");
        var noRegistry = false;
        try { _ = new PortraitNativePhaseHost(new Factory(), new PortraitNativePhaseObservation(), null!); }
        catch (ArgumentNullException) { noRegistry = true; }
        check(noRegistry, "host requires an explicitly supplied shared registry and rejects a missing one");
    }

    private static void ResolutionAcquisitionRace(Action<bool, string> check)
    {
        var registry = new RegistryProbe(); var competitor = new RegistryProbe(); var plan = Plan(); var factory = new Factory();
        object? competing = null;
        bool Resolve(Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? value, out string reason)
        {
            value = plan; reason = "fixture after resolver checks";
            check(!plan.Targets.Any(target => reserved(target.Address)), "resolution initially observes free native entries");
            check(competitor.TryAcquire([plan.Targets[2].Address], out competing), "another owner can acquire after resolver's final query");
            return true;
        }
        using var observation = new PortraitNativePhaseObservation();
        var host = new PortraitNativePhaseHost(factory, observation, registry.Raw, Resolve);
        check(!host.TryStart(TimeSpan.FromSeconds(1), out _) && factory.Creates == 0,
            "atomic acquisition rejects a resolve-to-Create race before any native factory call");
        check(plan.Targets.Where((_, index) => index != 2).All(target => !registry.IsReserved(target.Address)),
            "failed installation cannot acquire the non-conflicting portion of a plan");
        check(competitor.Release(competing!), "resolver race fixture owner releases");
    }

    private static void RetirementFailureRetention(Action<bool, string> check)
    {
        foreach (var failure in new[] { "disable", "dispose", "ownership", "unknown-create" })
        {
            var registry = new RegistryProbe(); var plan = Plan(); var factory = new Factory();
            if (failure == "disable") factory.FailDisable = true;
            if (failure == "dispose") factory.FailDispose = true;
            if (failure == "unknown-create") factory.ThrowAfterCreateAttempt = true;
            using var observation = new PortraitNativePhaseObservation();
            var host = NewHost(factory, observation, registry, plan);
            var started = host.TryStart(TimeSpan.FromSeconds(1), out _);
            check(started == (failure != "unknown-create"), "failure fixture reaches the intended native lifecycle boundary: " + failure);
            if (failure == "ownership")
            {
                // Fail the real production ReleaseOwnership after TryRetireTo and
                // Hook.Dispose have succeeded, rather than only failing an earlier check.
                factory.AfterDispose = hook =>
                {
                    if (!ReferenceEquals(hook, factory.Hooks[0])) return;
                    var entry = hook.Detour.Target!;
                    entry.GetType().GetField("Owner")!.SetValue(entry, new object());
                };
            }
            var disabled = host.BeginRetirement(out _);
            var retired = host.TryCompleteRetirement(out _);
            check(!retired && host.Snapshot().State == PortraitNativePhaseHostState.Faulted,
                "unproven native retirement does not report successful disposal: " + failure);
            if (failure == "ownership") check(factory.Hooks[0].IsDisposed,
                "gateway ownership-release failure occurs after native disposal and must still retain the whole reservation");
            check((failure != "disable" || !disabled) && plan.Targets.All(target => registry.IsReserved(target.Address)),
                "the full batch remains isolated after native rollback/retirement failure: " + failure);
            var competitor = new RegistryProbe();
            check(!competitor.TryAcquire(plan.Targets.Select(target => target.Address).ToArray(), out _),
                "a new participating hook owner cannot reuse a failed generation: " + failure);
            if (failure == "unknown-create")
                check(!host.WaitForRetirementAsync(TimeSpan.FromMilliseconds(10)).GetAwaiter().GetResult(),
                    "unknown provider creation cannot be mistaken for an asynchronously unloadable component");
        }
    }

    private static void InFlightRetention(Action<bool, string> check)
    {
        var registry = new RegistryProbe(); var plan = Plan(); var factory = new Factory();
        using var observation = new PortraitNativePhaseObservation(); var host = NewHost(factory, observation, registry, plan);
        check(host.TryStart(TimeSpan.FromSeconds(1), out _), "in-flight reservation fixture installs");
        // The real gateway field counts a copied Original until its invocation's finally.
        // This avoids calling or patching any game address in the lifecycle-only fixture.
        var entry = factory.Hooks[0].Detour.Target!; var active = entry.GetType().GetField("Active")!;
        active.SetValue(entry, 1);
        check(host.BeginRetirement(out _) && !host.TryCompleteRetirement(out _), "in-flight original prevents physical retirement");
        check(!host.WaitForRetirementAsync(TimeSpan.FromMilliseconds(10)).GetAwaiter().GetResult()
            && plan.Targets.All(target => registry.IsReserved(target.Address)), "asynchronous timeout retains every address until native drain");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        check(!host.WaitForRetirementAsync(TimeSpan.FromMilliseconds(10), cancelled.Token).GetAwaiter().GetResult()
            && plan.Targets.All(target => registry.IsReserved(target.Address)), "cancelled disposal does not release native address ownership");
        active.SetValue(entry, 0);
        check(host.TryCompleteRetirement(out _) && plan.Targets.All(target => !registry.IsReserved(target.Address)),
            "the entire address lease releases after the actual active count drains and all resources retire");
    }

    private static void RootReplacementRefuses(Action<bool, string> check)
    {
        const string key = "FireGaze.NativeHookReservations.v1";
        var registry = new RegistryProbe(); var address = Plan().ModuleBase;
        check(registry.TryAcquire([address], out var lease), "root replacement fixture acquires a native entry");
        var original = AppDomain.CurrentDomain.GetData(key);
        try
        {
            foreach (var replacement in new object?[] { new object(), null, Tuple.Create(new object(), new Dictionary<nint, object>()) })
            {
                AppDomain.CurrentDomain.SetData(key, replacement);
                var anotherGeneration = new RegistryProbe();
                check(registry.IsReserved(address) && anotherGeneration.IsReserved(address)
                    && !anotherGeneration.TryAcquire([address], out _) && !registry.Release(lease!),
                    "missing/replaced/incompatible process root cannot silently create a second exclusion table");
            }
        }
        finally { AppDomain.CurrentDomain.SetData(key, original); }
        check(registry.Release(lease!), "restored exact process root can release the original token");
    }

    private static void CollectibleRegistries(Action<bool, string> check)
    {
        var address = Plan().ModuleBase;
        var released = InCollectibleContext(address, true, check);
        ForceGC(released);
        check(!released.IsAlive, "a released registry and lease cannot root an actual collectible plugin ALC");
        var retained = InCollectibleContext(address + 10, false, check);
        ForceGC(retained);
        check(!retained.IsAlive && new RegistryProbe().IsReserved(address + 10),
            "failed-generation reservation remains process-wide without rooting the old collectible plugin ALC");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference InCollectibleContext(nint address, bool release, Action<bool, string> check)
    {
        var registry = new RegistryProbe();
        var context = new AssemblyLoadContext("Reservation collectible fixture " + Guid.NewGuid(), true);
        var assembly = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Probe", "PortraitPhaseCollectibleProbe.dll"));
        var probe = assembly.GetType("ReservationCollectibleProbe")!;
        check((bool)probe.GetMethod("Acquire")!.Invoke(null, [address])!, "collectible generation acquires through its own actual registry type");
        check(registry.IsReserved(address) && !registry.TryAcquire([address], out _),
            "default-context registry sees and rejects a collectible generation's real native-entry reservation");
        if (release)
            check((bool)probe.GetMethod("Release")!.Invoke(null, null)! && !registry.IsReserved(address),
                "collectible lease releases the same shared BCL token observed by the default context");
        var weak = new WeakReference(context); context.Unload(); return weak;
    }

    private static void ForceGC(WeakReference weak)
    { for (var attempt = 0; attempt < 20 && weak.IsAlive; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); } }

    private static PortraitNativePhaseResolvedPlan Plan()
    {
        var address = (nint)Interlocked.Add(ref moduleBase, 0x1000000);
        return new(address, 0xA00000, PortraitNativePhaseObservationBinding.EXE, PortraitNativePhaseObservationBinding.SDK,
            typeof(FFXIVClientStructs.FFXIV.Client.UI.Misc.CharaView).Assembly.ManifestModule.ModuleVersionId,
            PortraitNativePhaseObservationBinding.Inventory.Select(contract =>
                new PortraitNativePhaseBoundTarget(contract.Phase, address + (nint)contract.RVA, contract.Delegate)).ToArray());
    }

    private sealed class RegistryProbe
    {
        private readonly PortraitNativePhaseHostReservations raw = new();
        internal RegistryProbe() { }
        internal RegistryProbe(RegistryProbe _) { }
        internal PortraitNativePhaseHostReservations Raw => raw;
        internal bool IsReserved(nint address) => raw.IsReserved(address);
        internal bool TryAcquire(nint[] addresses, out object? lease)
        {
            var accepted = raw.TryAcquireBatch(addresses, out var acquired, out _);
            lease = acquired; return accepted;
        }
        internal bool Release(object lease)
        {
            return ((PortraitNativePhaseHostReservation)lease).TryRelease(out _);
        }
    }

    private sealed class Factory : IPortraitNativePhaseHostFactory
    {
        internal Action? BeforeFirstCreate;
        internal Action<Hook>? AfterDispose;
        internal bool FailDisable, FailDispose, ThrowAfterCreateAttempt;
        internal int Creates;
        internal readonly List<Hook> Hooks = [];
        public string Identity => "reservation-only fake official backend";
        public bool TryQualifyStartDisabled(out string reason) { reason = "fixture"; return true; }
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason) { reason = "fixture"; return true; }
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type type) => Native(target.Phase, type);
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type type, Delegate detour)
        {
            if (Creates++ == 0) BeforeFirstCreate?.Invoke();
            if (ThrowAfterCreateAttempt) throw new InvalidOperationException("Create entered without a handle/compensation result");
            var hook = new Hook(this, target.Address, Native(target.Phase, type), detour); Hooks.Add(hook); return hook;
        }
    }
    private sealed class Hook(Factory factory, nint address, Delegate original, Delegate detour) : IPortraitNativePhaseHostHook
    {
        internal Delegate Detour => detour;
        public nint Address => address;
        public string BackendName => "SafetyHook";
        public bool IsEnabled { get; private set; }
        public bool IsDisposed { get; private set; }
        public Delegate Original => original;
        public void Enable() => IsEnabled = true;
        public void Disable() { if (factory.FailDisable) throw new InvalidOperationException("disable fixture"); IsEnabled = false; }
        public void Dispose() { if (factory.FailDispose) throw new InvalidOperationException("dispose fixture"); IsDisposed = true; factory.AfterDispose?.Invoke(this); }
    }
    private static Delegate Native(PortraitNativeObservedPhase phase, Type type)
        => Delegate.CreateDelegate(type, typeof(Program).GetMethod(phase switch
        {
            PortraitNativeObservedPhase.ManagerRender => nameof(Program.DirectManager),
            PortraitNativeObservedPhase.ManagerUpdateCandidate => nameof(Program.DirectDelta),
            PortraitNativeObservedPhase.SceneCameraUpdate => nameof(Program.DirectCamera),
            _ => nameof(Program.DirectView),
        })!);
}
