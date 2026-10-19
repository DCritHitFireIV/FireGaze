using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

public static class Program
{
    private static int checks;
    private static int calls;
    private static int directCalls;
    private static readonly List<nint> receivers = [];
    private static readonly List<float> deltas = [];
    private static readonly List<uint> frames = [];
    private static Action? duringOriginal;
    private static Exception? originalFailure;
    private static Type? gatewayType;
    private static int targetKey = 0x7700000;
    private static long moduleKey = 0x600000000;
    private static readonly Action<Delegate, nint, uint, float>[] callers = [Caller(0), Caller(1), Caller(2), Caller(3)];

    public static int Main()
    {
        gatewayType = Assembly.GetExecutingAssembly().GetType("FireGaze.PortraitPreview.PortraitNativePhaseHostGateway");
        if (gatewayType is null) { Console.Error.WriteLine("FAIL: stable gateway is absent; native late-entry cannot survive collectible plugin unload."); return 1; }
        try
        {
            TypedPassThrough(); ObserverFailures(); ConcurrentAndRetire(); RecursivePassThrough(); CollectibleUnload(); MultipleCollectibleCycles();
            Check(Assembly.GetExecutingAssembly().GetType("FireGaze.PortraitPreview.PortraitNativePhaseHost") is not null,
                "explicit four-phase official-hook host is absent");
            HostBehavior();
            HostReservationCases.Run(Check);
            Console.WriteLine($"PortraitNativePhaseHostTests: {checks} checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void TypedPassThrough()
    {
        for (var phase = 0; phase < 4; phase++)
        {
            Reset(); var bridge = NewGateway(phase); var seen = new List<string>();
            bridge.Publish(Original(bridge, phase));
            bridge.Observe((observedPhase, receiver, frame, delta) => {
                Check(observedPhase == phase && receiver == (nint)0x1234, "observer preserves phase/receiver");
                Check(frame == (phase == 3 ? uint.MaxValue : 0) && delta == (phase == 1 ? -0.25f : 0), "observer preserves only actual arguments");
                seen.Add("enter"); return returned => seen.Add(returned ? "return" : "throw");
            });
            callers[phase](bridge.Detour, 0x1234, uint.MaxValue, -0.25f);
            Check(calls == 1 && receivers.Single() == (nint)0x1234, "typed original called exactly once");
            Check(seen.SequenceEqual(new[] { "enter", "return" }), "observer encloses original");
            Check(phase != 1 || deltas.Single() == -0.25f, "float ABI preserved");
            Check(phase != 3 || frames.Single() == uint.MaxValue, "uint ABI preserved");
            Check(bridge.Active == 0, "active returns to zero");
            Check(!bridge.BridgeType.Assembly.IsCollectible && AssemblyLoadContext.GetLoadContext(bridge.BridgeType.Assembly) == AssemblyLoadContext.Default,
                "bridge lives in default noncollectible runtime context");
            Check(bridge.Detour.GetType().Assembly == bridge.BridgeType.Assembly, "native ABI delegate is stable too");
            Check(bridge.BridgeType.GetFields().All(field => !field.FieldType.Assembly.IsCollectible), "stable state fields have no collectible types");
            bridge.Detach(); Check(bridge.TryRetire(Direct(bridge, phase)), "zero-active bridge redirects");
            callers[phase](bridge.Detour, 0x2345, 7, 1.5f);
            Check(directCalls == 1 && calls == 1 && seen.Count == 2, "late entry uses target without observer/trampoline");
        }
    }

    private static void ObserverFailures()
    {
        Reset(); var bridge = NewGateway(0); bridge.Publish(Original(bridge, 0));
        bridge.Observe((_, _, _, _) => throw new InvalidOperationException("observer entry"));
        callers[0](bridge.Detour, 19, 0, 0); Check(calls == 1 && bridge.Active == 0, "publisher failure cannot suppress original");
        bridge.Observe((_, _, _, _) => _ => throw new InvalidOperationException("observer exit"));
        var expected = new InvalidOperationException("actual Original"); originalFailure = expected;
        Exception? actual = null; try { callers[0](bridge.Detour, 20, 0, 0); } catch (Exception error) { actual = error; }
        Check(ReferenceEquals(actual, expected) && calls == 2 && bridge.Active == 0, "original exception survives failing observer completion");
        originalFailure = null; bridge.Detach(); Check(bridge.TryRetire(Direct(bridge, 0)), "failure bridge still retires");
    }

    private static void ConcurrentAndRetire()
    {
        Reset(); var bridge = NewGateway(0); bridge.Publish(Original(bridge, 0));
        using var both = new CountdownEvent(2); using var release = new ManualResetEventSlim();
        duringOriginal = () => { both.Signal(); Check(release.Wait(TimeSpan.FromSeconds(10)), "blocked Original released"); };
        var exits = 0; bridge.Observe((_, _, _, _) => _ => Interlocked.Increment(ref exits));
        var first = Task.Run(() => callers[0](bridge.Detour, 1, 0, 0));
        var second = Task.Run(() => callers[0](bridge.Detour, 2, 0, 0));
        Check(both.Wait(TimeSpan.FromSeconds(5)), "Original runs concurrently across native threads");
        Check(bridge.Active == 2, "active counts entire Original across threads");
        bridge.Detach(); Check(!bridge.TryRetire(Direct(bridge, 0)), "in-flight original prevents trampoline retirement");
        release.Set(); Task.WaitAll(first, second); duringOriginal = null;
        Check(exits == 2 && bridge.Active == 0, "already-captured observer completes before retirement");
        Check(bridge.TryRetire(Direct(bridge, 0)), "retire after all old originals return");
        Parallel.For(0, 100, index => callers[0](bridge.Detour, index, 0, 0));
        Check(directCalls == 100 && calls == 2 && exits == 2, "late concurrent entries never revive observer or old Original");
    }

    private static void RecursivePassThrough()
    {
        Reset(); var bridge = NewGateway(0); bridge.Publish(Original(bridge, 0)); var depth = 0; var maximum = 0;
        bridge.Observe((_, _, _, _) => { maximum = Math.Max(maximum, bridge.Active); return _ => { }; });
        duringOriginal = () => { if (++depth < 4) callers[0](bridge.Detour, depth, 0, 0); depth--; };
        callers[0](bridge.Detour, 0, 0, 0); duringOriginal = null;
        Check(calls == 4 && maximum == 4 && bridge.Active == 0, "same-thread recursion preserves original and active count");
        bridge.Detach(); Check(bridge.TryRetire(Direct(bridge, 0)), "recursive bridge retires");
    }

    private static void CollectibleUnload()
    {
        Reset(); var retired = BuildInCollectibleContext(true);
        ForceGC(retired.Context); Check(!retired.Context.IsAlive, "actual collectible plugin ALC unloads while native gateway remains rooted");
        callers[0](retired.Entry, 88, 0, 0); Check(directCalls == 1 && calls == 0, "late call survives after actual plugin unload");
        var attached = BuildInCollectibleContext(false);
        ForceGC(attached.Context); Check(attached.Context.IsAlive, "control confirms attached plugin observer really roots collectible ALC");
        ClearStableState(attached.Entry);
        ForceGC(attached.Context); Check(!attached.Context.IsAlive, "clearing plugin observer and redirecting releases collectible ALC");
        callers[0](attached.Entry, 89, 0, 0); Check(directCalls == 2, "detached control late entry remains functional");
    }

    private static void MultipleCollectibleCycles()
    {
        var baseline = GatewayAssemblyCount(); var entries = new List<Delegate>();
        for (var index = 0; index < 12; index++)
        {
            var cycle = BuildInCollectibleContext(true); entries.Add(cycle.Entry); ForceGC(cycle.Context);
            Check(!cycle.Context.IsAlive, "every collectible generation unloads");
            Check(GatewayAssemblyCount() == baseline, "reloading same entry/ABI cannot add noncollectible assemblies");
            Check(ReferenceEquals(entries[0], cycle.Entry), "same native entry reuses one stable gateway across ALC generations");
        }
        var before = directCalls; foreach (var entry in entries) callers[0](entry, 90, 0, 0);
        Check(directCalls == before + entries.Count, "all retained old native delegates remain safe after repeated reload");
    }

    private static int GatewayAssemblyCount() => AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetName().Name!.StartsWith("FireGaze.NativePhaseGateway.", StringComparison.Ordinal));

    private static void HostBehavior()
    {
        Reset(); var plan = Plan(); var factory = new FakeFactory(); var resolutions = 0; long now = 0;
        using var observation = new PortraitNativePhaseObservation(timestamp: () => now, timestampFrequency: 1000);
        bool Resolve(Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? value, out string reason)
        { resolutions++; value = plan; reason = "fixture"; return !plan.Targets.Any(target => reserved(target.Address)); }
        var host = new PortraitNativePhaseHost(factory, observation, new PortraitNativePhaseHostReservations(), Resolve);
        Check(factory.Created == 0 && factory.Qualifications == 0 && resolutions == 0, "host construction has no native/process/install side effects");
        factory.Qualified = false;
        Check(!host.TryStart(TimeSpan.FromSeconds(1), out _) && factory.Created == 0 && resolutions == 0, "unproven StartDisabled backend rejects before resolver/create");
        factory.Qualified = true;
        Check(host.TryStart(TimeSpan.FromSeconds(1), out var reason), "four-phase host explicitly starts: " + reason);
        Check(factory.Created == 4 && factory.Verifications == 2 && factory.Events.Take(5).SequenceEqual(new[] { "verify", "create0", "create1", "create2", "create3" }), "all create and complete preflight precede enable");
        Check(factory.Events.IndexOf("enable0") > factory.Events.LastIndexOf("create3") && plan.Targets.All(target => PortraitNativePhaseHost.IsReserved(target.Address)), "real address leases cover all installed entries");
        for (var phase = 0; phase < 4; phase++) callers[phase](factory.Hooks[phase].Detour, 0x500 + phase, 0, 0.125f);
        var status = host.Snapshot();
        Check(status.State == PortraitNativePhaseHostState.Observing && status.Hooks == 4 && !status.ProductionAdmission && !status.Observation.ProductionAdmission, "host cannot issue scene admission");
        Check(calls == 4 && status.Observation.Records.Count == 8, "all four actual typed callbacks are observed while original runs once");
        Check(status.Observation.Records.Where(row => row.Phase == PortraitNativeObservedPhase.SourceCharaViewRender).All(row => row.Frame.Value == 0 && row.Frame.Origin == PortraitNativeFrameOrigin.SourceRenderArgument), "source frame zero remains actual argument evidence");
        Check(status.Observation.Records.Where(row => row.Phase != PortraitNativeObservedPhase.SourceCharaViewRender).All(row => row.Frame.Origin == PortraitNativeFrameOrigin.None), "independent phases do not inherit last source frame");
        host.Stop(); callers[0](factory.Hooks[0].Detour, 1, 0, 0);
        Check(calls == 5 && host.Snapshot().Observation.Records.Count == 8 && factory.Hooks.All(hook => hook.IsEnabled && !hook.IsDisposed), "stop detaches recorder and retains original pass-through installation");
        Check(host.TryStart(TimeSpan.FromMilliseconds(50), out _) && factory.Created == 4 && resolutions == 1, "capture rearm reuses installed hooks without re-reading own jump prefix");
        now = 100;
        Check(host.Snapshot().State == PortraitNativePhaseHostState.Installed && !host.Snapshot().Observation.Observing, "bounded recorder expiration detaches observers");
        Check(host.BeginRetirement(out _) && host.TryCompleteRetirement(out _), "idle host explicitly retires");
        Check(factory.Hooks.All(hook => hook.IsDisposed) && plan.Targets.All(target => !PortraitNativePhaseHost.IsReserved(target.Address)), "retirement releases native resources and address leases");
        Check(factory.Events.Where(item => item.StartsWith("disable", StringComparison.Ordinal)).Take(4).SequenceEqual(new[] { "disable3", "disable2", "disable1", "disable0" }), "native hooks disable in reverse order");
        for (var phase = 0; phase < 4; phase++) callers[phase](factory.Hooks[phase].Detour, 0x600 + phase, 1, 0);
        Check(directCalls == 4 && host.Snapshot().InFlight == 0, "retired host late entries use safe direct targets");

        RejectionAndRollback(); AsyncRetirementConsumer();
    }

    private static void RejectionAndRollback()
    {
        var reservedPlan = Plan(); var firstFactory = new FakeFactory(); using var firstObserver = new PortraitNativePhaseObservation();
        var first = Host(firstFactory, firstObserver, reservedPlan);
        Check(first.TryStart(TimeSpan.FromSeconds(1), out _), "reservation owner starts");
        var secondFactory = new FakeFactory(); using var secondObserver = new PortraitNativePhaseObservation(); var second = Host(secondFactory, secondObserver, reservedPlan);
        Check(!second.TryStart(TimeSpan.FromSeconds(1), out _) && secondFactory.Created == 0, "second host cannot create on already-leased native addresses");
        Check(first.BeginRetirement(out _) && first.TryCompleteRetirement(out _) && second.TryStart(TimeSpan.FromSeconds(1), out _), "same ABI gateway reused after lease owner fully retires");
        Check(second.BeginRetirement(out _) && second.TryCompleteRetirement(out _), "second owner retires");
        var externalPlan = Plan(); var externalFactory = new FakeFactory(); using var externalObserver = new PortraitNativePhaseObservation();
        var external = Host(externalFactory, externalObserver, externalPlan, address => address == externalPlan.Targets[0].Address);
        Check(!external.TryStart(TimeSpan.FromSeconds(1), out _) && externalFactory.Created == 0, "required external same-plugin reservation is honored");
        for (var failure = 0; failure < 4; failure++)
        {
            var createPlan = Plan(); var createFactory = new FakeFactory { FailCreate = failure }; using var createObserver = new PortraitNativePhaseObservation(); var createHost = Host(createFactory, createObserver, createPlan);
            Check(!createHost.TryStart(TimeSpan.FromSeconds(1), out _) && createFactory.Hooks.All(hook => hook.IsDisposed), "partial create failure rolls back every returned hook");
            Check(createPlan.Targets.All(target => PortraitNativePhaseHost.IsReserved(target.Address))
                && createHost.Snapshot().State == PortraitNativePhaseHostState.Faulted,
                "a Create that throws without a returned handle retains all leases because compensation is unproven");
            var enablePlan = Plan(); var enableFactory = new FakeFactory { FailEnable = failure }; using var enableObserver = new PortraitNativePhaseObservation(); var enableHost = Host(enableFactory, enableObserver, enablePlan);
            Check(!enableHost.TryStart(TimeSpan.FromSeconds(1), out _) && enableFactory.Hooks.Count == 4 && enableFactory.Hooks.All(hook => hook.IsDisposed), "partial enable failure rolls back all four hooks");
            Check(enablePlan.Targets.All(target => !PortraitNativePhaseHost.IsReserved(target.Address)), "enable failure clears all address leases");
        }
        foreach (var verification in new[] { 1, 2 })
        {
            var failedPlan = Plan(); var failedFactory = new FakeFactory { FailVerification = verification }; using var failedObserver = new PortraitNativePhaseObservation(); var failedHost = Host(failedFactory, failedObserver, failedPlan);
            Check(!failedHost.TryStart(TimeSpan.FromSeconds(1), out _) && failedFactory.Hooks.All(hook => hook.IsDisposed), "changed runtime entry prevents observation and cleans partial creation");
            Check(failedFactory.Events.All(item => !item.StartsWith("enable", StringComparison.Ordinal)) && failedPlan.Targets.All(target => !PortraitNativePhaseHost.IsReserved(target.Address)), "entry recheck failure enables nothing and releases leases");
        }
        var directPlan = Plan(); var directFactory = new FakeFactory { FailDirect = 1 }; using var directObserver = new PortraitNativePhaseObservation(); var directHost = Host(directFactory, directObserver, directPlan);
        Check(!directHost.TryStart(TimeSpan.FromSeconds(1), out _), "direct delegate preparation failure rejects before native create");
        _ = directHost.BeginRetirement(out _); Check(directHost.TryCompleteRetirement(out _), "failed unpublished gateway can retire without a native hook");
        var retryFactory = new FakeFactory(); using var retryObserver = new PortraitNativePhaseObservation(); var retry = Host(retryFactory, retryObserver, directPlan);
        Check(retry.TryStart(TimeSpan.FromSeconds(1), out _), "failed direct preparation cannot permanently leak stable gateway ownership");
        Check(retry.BeginRetirement(out _) && retry.TryCompleteRetirement(out _), "retry owner retires");
    }

    private static void AsyncRetirementConsumer()
    {
        Reset(); var plan = Plan(); var factory = new FakeFactory(); using var observation = new PortraitNativePhaseObservation(); var host = Host(factory, observation, plan);
        Check(host.TryStart(TimeSpan.FromSeconds(10), out _), "async lifecycle host starts");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        duringOriginal = () => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)), "async in-flight Original released"); };
        var call = Task.Run(() => callers[0](factory.Hooks[0].Detour, 42, 0, 0));
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "native Original entered before async retirement");
        Check(host.BeginRetirement(out _) && !host.TryCompleteRetirement(out _), "begin retirement disables immediately without waiting across Original");
        var scopeDisposed = false;
        async Task DisposePluginAsync()
        {
            Check(await host.WaitForRetirementAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false), "IAsyncDalamudPlugin can await real native drain");
            Check(factory.Hooks.All(hook => hook.IsDisposed), "all hooks disposed before provider service scope"); scopeDisposed = true;
        }
        var ownerDispose = DisposePluginAsync();
        Check(!ownerDispose.IsCompleted && !scopeDisposed && host.Snapshot().InFlight == 1 && !factory.Hooks[0].IsDisposed,
            "provider scope is held alive while an original still owns the trampoline");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check(!host.WaitForRetirementAsync(TimeSpan.FromSeconds(1), cancelled.Token).GetAwaiter().GetResult() && !factory.Hooks[0].IsDisposed,
            "cancelled await does not pretend native retirement completed");
        release.Set(); Task.WaitAll(call, ownerDispose); duringOriginal = null;
        Check(scopeDisposed && host.Snapshot().State == PortraitNativePhaseHostState.Retired, "async consumer releases scope after native retirement only");
        callers[0](factory.Hooks[0].Detour, 43, 0, 0); Check(calls == 1 && directCalls == 1, "late call after async disposal never invokes old trampoline");
    }

    private static PortraitNativePhaseResolvedPlan Plan()
    {
        var baseAddress = (nint)Interlocked.Add(ref moduleKey, 0x1000000);
        return new(baseAddress, 0xA00000, PortraitNativePhaseObservationBinding.EXE, PortraitNativePhaseObservationBinding.SDK,
            typeof(CharaView).Assembly.ManifestModule.ModuleVersionId, PortraitNativePhaseObservationBinding.Inventory.Select(contract =>
                new PortraitNativePhaseBoundTarget(contract.Phase, baseAddress + (nint)contract.RVA, contract.Delegate)).ToArray());
    }

    private static PortraitNativePhaseHost Host(FakeFactory factory, PortraitNativePhaseObservation observation, PortraitNativePhaseResolvedPlan plan,
        Func<nint, bool>? external = null)
    {
        bool Resolve(Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? value, out string reason)
        { value = plan; reason = "fixture"; return !plan.Targets.Any(target => reserved(target.Address)); }
        var reservations = new PortraitNativePhaseHostReservations();
        if (external is not null)
        {
            var addresses = plan.Targets.Where(target => external(target.Address)).Select(target => target.Address).ToArray();
            if (addresses.Length != 0 && !reservations.TryAcquireBatch(addresses, out _, out _))
                throw new InvalidOperationException("External reservation fixture could not reserve its entries.");
        }
        return new(factory, observation, reservations, Resolve);
    }

    private sealed class FakeFactory : IPortraitNativePhaseHostFactory
    {
        public bool Qualified = true;
        public int Qualifications, Created, Verifications;
        public int FailCreate = -1, FailEnable = -1, FailDirect = -1, FailVerification = -1;
        public readonly List<string> Events = [];
        public readonly List<FakeHook> Hooks = [];
        public string Identity => "test StartDisabled SafetyHook";
        public bool TryQualifyStartDisabled(out string reason) { Qualifications++; reason = "fake StartDisabled evidence"; return Qualified; }
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason)
        { Events.Add("verify"); reason = "fake entry bytes changed"; return ++Verifications != FailVerification; }
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type delegateType)
        {
            if ((int)target.Phase == FailDirect) throw new InvalidOperationException("direct failure");
            return NativeDelegate(target.Phase, delegateType, true);
        }
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type delegateType, Delegate detour)
        {
            Events.Add("create" + (int)target.Phase); Created++;
            if ((int)target.Phase == FailCreate) throw new InvalidOperationException("create failure");
            var hook = new FakeHook(this, target, NativeDelegate(target.Phase, delegateType, false), detour); Hooks.Add(hook); return hook;
        }
    }

    private sealed class FakeHook(FakeFactory factory, PortraitNativePhaseBoundTarget target, Delegate original, Delegate detour) : IPortraitNativePhaseHostHook
    {
        public Delegate Detour { get; } = detour;
        public nint Address => target.Address;
        public string BackendName => "SafetyHook";
        public bool IsEnabled { get; private set; }
        public bool IsDisposed { get; private set; }
        public Delegate Original => original;
        public void Enable()
        {
            factory.Events.Add("enable" + (int)target.Phase);
            if ((int)target.Phase == factory.FailEnable) throw new InvalidOperationException("enable failure"); IsEnabled = true;
        }
        public void Disable() { factory.Events.Add("disable" + (int)target.Phase); IsEnabled = false; }
        public void Dispose() { Check(!IsEnabled, "fake provider only destroys disabled hook"); factory.Events.Add("dispose" + (int)target.Phase); IsDisposed = true; }
    }

    private static Delegate NativeDelegate(PortraitNativeObservedPhase phase, Type type, bool direct)
        => Delegate.CreateDelegate(type, Method(((direct ? "Direct" : "Native") + phase) switch {
            "NativeManagerRender" => nameof(NativeManager), "NativeManagerUpdateCandidate" => nameof(NativeDelta),
            "NativeSceneCameraUpdate" => nameof(NativeCamera), "NativeSourceCharaViewRender" => nameof(NativeView),
            "DirectManagerRender" => nameof(DirectManager), "DirectManagerUpdateCandidate" => nameof(DirectDelta),
            "DirectSceneCameraUpdate" => nameof(DirectCamera), _ => nameof(DirectView),
        }));
    public static unsafe void NativeManager(RenderManager* receiver) => OriginalOne(receiver);
    public static unsafe void NativeDelta(RenderManager* receiver, float delta) => OriginalFloat(receiver, delta);
    public static unsafe void NativeCamera(SceneCameraManager* receiver) => OriginalOne(receiver);
    public static unsafe void NativeView(CharaView* receiver, uint frame) => OriginalUInt(receiver, frame);
    public static unsafe void DirectManager(RenderManager* receiver) => DirectOne(receiver);
    public static unsafe void DirectDelta(RenderManager* receiver, float delta) => DirectFloat(receiver, delta);
    public static unsafe void DirectCamera(SceneCameraManager* receiver) => DirectOne(receiver);
    public static unsafe void DirectView(CharaView* receiver, uint frame) => DirectUInt(receiver, frame);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, Delegate Entry) BuildInCollectibleContext(bool retire)
    {
        var context = new AssemblyLoadContext("FireGaze collectible fixture " + Guid.NewGuid(), true);
        var assembly = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Probe", "PortraitPhaseCollectibleProbe.dll"));
        var method = assembly.GetType("CollectibleProbe")!.GetMethod("Build")!;
        var entry = (Delegate)method.Invoke(null, new object[] { Method(nameof(OriginalOne)), Method(nameof(DirectOne)), retire })!;
        var weak = new WeakReference(context); context.Unload(); return (weak, entry);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearStableState(Delegate entry)
    {
        var instance = entry.Target!; var type = instance.GetType(); var gate = type.GetField("Gate")!.GetValue(instance)!;
        lock (gate)
        {
            type.GetField("Observer")!.SetValue(instance, null);
            type.GetField("Original")!.SetValue(instance, Delegate.CreateDelegate(entry.GetType(), Method(nameof(DirectOne))));
            type.GetField("Retired")?.SetValue(instance, true);
            type.GetField("Owner")?.SetValue(instance, null);
        }
    }

    private static void ForceGC(WeakReference reference)
    { for (var index = 0; index < 15 && reference.IsAlive; index++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); } }
    private static void Reset() { calls = directCalls = 0; receivers.Clear(); deltas.Clear(); frames.Clear(); duringOriginal = null; originalFailure = null; }
    private static void Check(bool condition, string name) { Interlocked.Increment(ref checks); if (!condition) throw new Exception("FAIL: " + name); }
    private static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
    private static Delegate Original(Gateway bridge, int phase) => Delegate.CreateDelegate(bridge.DelegateType, Method(phase switch { 1 => nameof(OriginalFloat), 3 => nameof(OriginalUInt), _ => nameof(OriginalOne) }));
    private static Delegate Direct(Gateway bridge, int phase) => Delegate.CreateDelegate(bridge.DelegateType, Method(phase switch { 1 => nameof(DirectFloat), 3 => nameof(DirectUInt), _ => nameof(DirectOne) }));
    public static unsafe void OriginalOne(void* receiver) { Interlocked.Increment(ref calls); lock (receivers) receivers.Add((nint)receiver); duringOriginal?.Invoke(); if (originalFailure is not null) throw originalFailure; }
    public static unsafe void OriginalFloat(void* receiver, float delta) { lock (deltas) deltas.Add(delta); OriginalOne(receiver); }
    public static unsafe void OriginalUInt(void* receiver, uint frame) { lock (frames) frames.Add(frame); OriginalOne(receiver); }
    public static unsafe void DirectOne(void* receiver) => Interlocked.Increment(ref directCalls);
    public static unsafe void DirectFloat(void* receiver, float delta) => DirectOne(receiver);
    public static unsafe void DirectUInt(void* receiver, uint frame) => DirectOne(receiver);

    private static unsafe Gateway NewGateway(int phase)
    {
        var parameters = phase switch { 1 => new[] { typeof(void*), typeof(float) }, 3 => [typeof(void*), typeof(uint)], _ => [typeof(void*)] };
        var create = gatewayType!.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static)!;
        var arguments = create.GetParameters().Length == 5 ? new object?[] { phase, parameters, (nint)Interlocked.Increment(ref targetKey), null, null }
            : new object?[] { phase, parameters, null, null };
        Check((bool)create.Invoke(null, arguments)!, "stable gateway created: " + arguments[^1]);
        return new(arguments[^2]!);
    }

    private static Action<Delegate, nint, uint, float> Caller(int phase)
    {
        // Bind native signature lazily per actual generated delegate type; the cached
        // DynamicMethod caller itself only lives in this default-context test harness.
        var cache = new Dictionary<Type, Action<Delegate, nint, uint, float>>();
        return (entry, receiver, frame, delta) => {
            Action<Delegate, nint, uint, float> call;
            lock (cache)
            {
                if (!cache.TryGetValue(entry.GetType(), out call!))
                {
                    var invoke = entry.GetType().GetMethod("Invoke")!;
                    var method = new DynamicMethod("CallStable", typeof(void), [typeof(Delegate), typeof(nint), typeof(uint), typeof(float)], typeof(Program).Module, true);
                    var il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, entry.GetType()); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_U);
                    if (phase == 1) il.Emit(OpCodes.Ldarg_3); if (phase == 3) il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Callvirt, invoke); il.Emit(OpCodes.Ret);
                    call = (Action<Delegate, nint, uint, float>)method.CreateDelegate(typeof(Action<Delegate, nint, uint, float>)); cache.Add(entry.GetType(), call);
                }
            }
            call(entry, receiver, frame, delta);
        };
    }

    private sealed class Gateway(object instance)
    {
        private readonly Type type = instance.GetType();
        public Delegate Detour => (Delegate)type.GetProperty("Detour")!.GetValue(instance)!;
        public Type DelegateType => (Type)type.GetProperty("DelegateType")!.GetValue(instance)!;
        public Type BridgeType => (Type)type.GetProperty("BridgeType")!.GetValue(instance)!;
        public int Active => (int)type.GetProperty("ActiveCount")!.GetValue(instance)!;
        public void Publish(Delegate original) => type.GetMethod("PublishOriginal")!.Invoke(instance, [original]);
        public void Observe(Func<int, nint, uint, float, Action<bool>?> observer) => type.GetMethod("SetObserver")!.Invoke(instance, [observer]);
        public void Detach() => type.GetMethod("DetachObserver")!.Invoke(instance, null);
        public bool TryRetire(Delegate direct) => (bool)type.GetMethod("TryRetireTo")!.Invoke(instance, [direct])!;
    }
}
