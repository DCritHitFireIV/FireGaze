using System.Reflection;
using System.Reflection.Emit;
using FireGaze.PortraitPreview;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

// This is the actual counter host + stable gateway + native-phase source + SDK
// slot reader. Only native Hook construction and Original are external fixtures.
internal static unsafe class CounterPhaseCases
{
    private static Action? original;
    private static int calls, checks;
    internal static int Run()
    {
        using var fixture = new Fixture(); fixture.SetIdle(6);
        var phases = new PortraitOwnedSceneSlotNativePhaseSource(fixture.Memory, () => true, () => true, () => 1);
        var reader = new PortraitOwnedSceneSlotReader(fixture.Memory, phases);
        using var recorder = new PortraitNativePhaseObservation(); var factory = new CounterFactory();
        var host = PortraitNativePhaseHost.CreateCounterIngress(factory, recorder, new(), phases.BeginCounterMutation,
            (Func<nint, bool> _, out PortraitNativePhaseResolvedPlan? plan, out string reason) => {
                var module = unchecked((nint)0x5A00000000L);
                plan = new(module, 0x3000000, PortraitNativePhaseObservationBinding.EXE, PortraitNativePhaseObservationBinding.SDK,
                    typeof(RenderManager).Assembly.ManifestModule.ModuleVersionId, [
                        new(PortraitNativeObservedPhase.CounterAdd, module + 0x2BA770, typeof(PortraitNativeSlotCounter)),
                        new(PortraitNativeObservedPhase.CounterSub, module + 0x2BA790, typeof(PortraitNativeSlotCounter))]);
                reason = "native boundary fixture"; return true;
            });
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "typed counter host starts");
        var previousScope = phases.EnterSource(fixture.Phase.Source, 75, 1);
        Check(previousScope is not null && phases.TryCapture(out var previous) && reader.TryReadIdle(6, previous, out _), "real native phase initially authorizes its scoped slot read");
        phases.TryCapture(out previous);
        var reads = fixture.Memory.Reads;
        original = () => {
            Check(!phases.TryCapture(out _) && !reader.TryReadIdle(6, previous, out _)
                && fixture.Memory.Reads == reads, "counter prefix revokes actual reader before Original without native dereference");
            using var forbidden = phases.EnterSource(fixture.Phase.Source, 999, 3);
            Check(forbidden is null && fixture.Memory.Reads == reads,
                "native counter Original still running cannot reopen a source phase before the real count write");
            using var retirement = phases.EnterRetirement(fixture.Phase.Source, 999, 3);
            Check(retirement is null && fixture.Memory.Reads == reads, "counter Original cannot manufacture a retirement native-read phase");
        };
        factory.Call(PortraitNativeObservedPhase.CounterAdd, 0);
        Check(calls == 1, "actual phase revocation keeps Original once");
        using var nextScope = phases.EnterSource(fixture.Phase.Source, 76, 1);
        Check(nextScope is not null && phases.TryCapture(out var next) && next.ManagerGeneration > previous.ManagerGeneration,
            "same manager addresses after counter mutation get a newer actual phase generation");
        phases.TryCapture(out next);
        previousScope!.Dispose(); Check(phases.TryCapture(out _), "old scope cannot cancel the new phase token");
        reads = fixture.Memory.Reads;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        original = () => {
            Check(!phases.TryCapture(out _) && fixture.Memory.Reads == reads, "off-thread counter revokes without reading another thread's phase");
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        var worker = Task.Run(() => factory.Call(PortraitNativeObservedPhase.CounterSub, 7));
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "off-thread Original is actually pending");
        using (var forbidden = phases.EnterSource(fixture.Phase.Source, 999, 4))
            Check(forbidden is null && fixture.Memory.Reads == reads, "main thread cannot reacquire while worker counter Original has not written/returned");
        using (var forbiddenRetirement = phases.EnterRetirement(fixture.Phase.Source, 999, 4))
            Check(forbiddenRetirement is null && fixture.Memory.Reads == reads, "main thread cannot enter native retirement during a worker counter write");
        release.Set(); worker.GetAwaiter().GetResult();
        Check(!phases.TryCapture(out _) && calls == 2, "off-thread native prefix synchronously invalidates the main-thread scope");
        using var lastScope = phases.EnterSource(fixture.Phase.Source, 77, 1);
        Check(lastScope is not null && phases.TryCapture(out var last) && last.ManagerGeneration > next.ManagerGeneration,
            "off-thread revocation cannot reuse the prior generation");
        host.Stop(); original = () => Check(!phases.TryCapture(out _), "recorder stop still revokes actual phase");
        factory.Call(PortraitNativeObservedPhase.CounterAdd, 1);
        Check(host.Snapshot().CounterInvalidationEpoch == 3 && !host.Snapshot().CounterInvalidationFailed, "detached epoch records all three real phase invalidations");
        original = () => throw new IOException("actual native failure");
        try { factory.Call(PortraitNativeObservedPhase.CounterSub, 2); throw new Exception("native exception was swallowed"); }
        catch (IOException) { }
        using (var afterThrow = phases.EnterSource(fixture.Phase.Source, 78, 2))
            Check(afterThrow is not null && phases.TryCapture(out _), "Original exception releases mutation scope even with recorder stopped");
        using (var outer = phases.BeginCounterMutation())
        {
            using (var inner = phases.BeginCounterMutation())
                Check(phases.EnterSource(fixture.Phase.Source, 79, 3) is null, "nested detached mutation blocks all source admission");
            Check(phases.EnterSource(fixture.Phase.Source, 79, 3) is null, "inner completion cannot reopen the outer native-write window");
        }
        using (var afterNested = phases.EnterSource(fixture.Phase.Source, 79, 3))
            Check(afterNested is not null, "only the last mutation completion permits a new independently qualified phase");
        Check(host.BeginRetirement(out _) && host.TryCompleteRetirement(out _), "integrated counter host retires");
        original = null; Console.WriteLine($"CounterPhaseCases: {checks} checks passed; self-allocated SDK memory only."); return 0;
    }
    private static void Check(bool valid, string name) { checks++; if (!valid) throw new InvalidOperationException(name); }
    public static void Native(RenderManager* manager, uint slot) { calls++; original?.Invoke(); }
    public static void Direct(RenderManager* manager, uint slot) { }

    private sealed class CounterFactory : IPortraitNativePhaseHostFactory
    {
        private readonly Dictionary<PortraitNativeObservedPhase, Delegate> entries = [];
        public string Identity => "external boundary only";
        public bool TryQualifyStartDisabled(out string reason) { reason = "fixture"; return true; }
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason) { reason = "fixture"; return true; }
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type type) => Delegate.CreateDelegate(type, typeof(CounterPhaseCases).GetMethod(nameof(Direct))!);
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type type, Delegate detour)
        { entries.Add(target.Phase, detour); return new CounterHook(target.Address, Delegate.CreateDelegate(type, typeof(CounterPhaseCases).GetMethod(nameof(Native))!)); }
        public void Call(PortraitNativeObservedPhase kind, uint slot)
        {
            var entry = entries[kind];
            var method = new DynamicMethod("CounterNative", typeof(void), [typeof(Delegate), typeof(uint)], typeof(CounterPhaseCases).Module, true);
            var il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, entry.GetType()); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt, entry.GetType().GetMethod("Invoke")!); il.Emit(OpCodes.Ret);
            ((Action<Delegate, uint>)method.CreateDelegate(typeof(Action<Delegate, uint>)))(entry, slot);
        }
    }
    private sealed class CounterHook(nint address, Delegate original) : IPortraitNativePhaseHostHook
    {
        public nint Address => address; public Delegate Original => original; public string BackendName => "SafetyHook";
        public bool IsEnabled { get; private set; } public bool IsDisposed { get; private set; }
        public void Enable() => IsEnabled = true; public void Disable() => IsEnabled = false; public void Dispose() => IsDisposed = true;
    }
}
