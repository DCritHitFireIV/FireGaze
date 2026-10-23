using System.Reflection;
using System.Reflection.Emit;
using FireGaze.PortraitPreview;
using RTManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

// Real source/reader and real stable typed host. Only native Hook and Original
// are external fixtures; no process/GameObject/model/GPU resources are borrowed.
internal static unsafe class ResourcePhaseCases
{
    private static Action? duringResource, duringCounter;
    private static int checks, calls;
    internal static int Run()
    {
        using var fixture = new Fixture(); fixture.SetIdle(6);
        var phases = new PortraitOwnedSceneSlotNativePhaseSource(fixture.Memory, () => true, () => true, () => 1);
        var reader = new PortraitOwnedSceneSlotReader(fixture.Memory, phases);
        using var recorder = new PortraitNativePhaseObservation(); var f = new Factory();
        var host = PortraitNativePhaseHost.CreateResourceIngress(f, recorder, new(), phases.BeginResourceMutation,
            (Func<nint,bool> _, out PortraitNativePhaseResolvedPlan? plan, out string why) => {
                var module = unchecked((nint)0x5B00000000L);
                plan = new(module, 0x3000000, PortraitNativePhaseObservationBinding.EXE, PortraitNativePhaseObservationBinding.SDK, Guid.Parse("c72f17a8-6ed1-4a30-a097-74137dc08f9e"), [
                    new(PortraitNativeObservedPhase.ResourceCleanup, module + 0x2DBB90, typeof(PortraitNativeResourceCleanup)),
                    new(PortraitNativeObservedPhase.ResourceRecreate, module + 0x2DBFE0, typeof(PortraitNativeResourceRecreate))]); why = "fixture"; return true;
            });
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "actual RT host starts");
        var oldScope = phases.EnterSource(fixture.Phase.Source, 1, 1);
        Check(oldScope is not null && phases.TryCapture(out var old) && reader.TryReadIdle(6, old, out _), "actual phase authorizes its SDK slot initially");
        phases.TryCapture(out old); var reads = fixture.Memory.Reads;
        Check(reader.TryReadCensus(old, out var baseline) && baseline.Complete && baseline.Rows.Count == 8,
            "actual source scope admits a complete observation-only census before resource mutation");
        reads = fixture.Memory.Reads;
        void Blocked(string message)
        {
            Check(!phases.TryCapture(out _) && !reader.TryReadIdle(6, old, out _) && !reader.TryReadCensus(old, out _)
                && fixture.Memory.Reads == reads, message + " revokes old token and entire census before native reads");
            using var source = phases.EnterSource(fixture.Phase.Source, 99, 2);
            using var retirement = phases.EnterRetirement(fixture.Phase.Source, 99, 2);
            Check(source is null && retirement is null && fixture.Memory.Reads == reads, message + " rejects source and retirement with zero native reads");
        }
        duringResource = () => Blocked("whole cleanup Original"); f.Call(PortraitNativeObservedPhase.ResourceCleanup);
        Check(calls == 1 && !phases.TryCapture(out _), "cleanup Original returns once without restoring old token");
        using (var next = phases.EnterSource(fixture.Phase.Source, 2, 1))
        {
            Check(next is not null && phases.TryCapture(out var current) && current.ManagerGeneration > old.ManagerGeneration, "same manager addresses require a new phase generation");
            oldScope!.Dispose(); Check(phases.TryCapture(out _), "old phase disposal cannot revoke fresh token");
        }
        reads = fixture.Memory.Reads;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        duringResource = () => { Blocked("worker recreate Original"); entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var worker = Task.Run(() => f.Call(PortraitNativeObservedPhase.ResourceRecreate));
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "actual byte Original pending on another thread"); Blocked("concurrent main thread");
        release.Set(); Check(worker.GetAwaiter().GetResult() == 0xA7, "concurrent Original AL preserved");
        Check(!phases.TryCapture(out _), "worker completion cannot restore prior phase");
        using var counterRecorder = new PortraitNativePhaseObservation(); var cf = new Factory();
        var ch = PortraitNativePhaseHost.CreateCounterIngress(cf, counterRecorder, new(), phases.BeginCounterMutation,
            (Func<nint,bool> _, out PortraitNativePhaseResolvedPlan? plan, out string why) => {
                var module = unchecked((nint)0x5B00000000L);
                plan = new(module, 0x3000000, PortraitNativePhaseObservationBinding.EXE, PortraitNativePhaseObservationBinding.SDK, Guid.Parse("c72f17a8-6ed1-4a30-a097-74137dc08f9e"), [
                    new(PortraitNativeObservedPhase.CounterAdd, module + 0x2BA770, typeof(PortraitNativeSlotCounter)),
                    new(PortraitNativeObservedPhase.CounterSub, module + 0x2BA790, typeof(PortraitNativeSlotCounter))]); why = "fixture"; return true;
            });
        Check(ch.TryStart(TimeSpan.FromSeconds(30), out _), "actual counter host shares same phase sink");
        duringCounter = () => Blocked("counter nested inside RT");
        duringResource = () => { cf.Call(PortraitNativeObservedPhase.CounterAdd); Blocked("RT outer after inner counter completion"); };
        f.Call(PortraitNativeObservedPhase.ResourceRecreate);
        duringResource = () => Blocked("RT nested inside counter");
        duringCounter = () => { f.Call(PortraitNativeObservedPhase.ResourceCleanup); Blocked("counter outer after inner RT completion"); };
        cf.Call(PortraitNativeObservedPhase.CounterSub);
        var nested = false;
        duringResource = () => { if (!nested) { nested = true; f.Call(PortraitNativeObservedPhase.ResourceRecreate); Blocked("RT outer after recursive completion"); } else Blocked("RT recursive Original"); };
        f.Call(PortraitNativeObservedPhase.ResourceCleanup);
        host.Stop(); duringResource = () => { Blocked("Original throws with recorder stopped"); throw new IOException("fixture"); };
        try { f.Call(PortraitNativeObservedPhase.ResourceRecreate); throw new Exception("swallowed native failure"); } catch (IOException) { }
        using (var after = phases.EnterSource(fixture.Phase.Source, 3, 2)) Check(after is not null && phases.TryCapture(out _), "physical Original failure releases final shared mutation depth");
        Check(!host.Snapshot().ResourceInvalidationFailed && !ch.Snapshot().CounterInvalidationFailed, "cross-host detached mutation guards remain qualified");
        Check(host.BeginRetirement(out _) && host.TryCompleteRetirement(out _) && ch.BeginRetirement(out _) && ch.TryCompleteRetirement(out _), "both actual ingress batches retire");
        duringResource = duringCounter = null;
        Console.WriteLine($"ResourcePhaseCases: {checks} checks passed; self-allocated SDK memory only."); return 0;
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception("FAIL: " + message); }
    public static void Cleanup(RTManager* manager) { calls++; duringResource?.Invoke(); }
    public static byte Recreate(RTManager* manager) { Cleanup(manager); return 0xA7; }
    public static void Counter(RenderManager* manager, uint slot) => duringCounter?.Invoke();
    public static void DirectCleanup(RTManager* manager) { }
    public static byte DirectRecreate(RTManager* manager) => 0xFF;
    public static void DirectCounter(RenderManager* manager, uint slot) { }
    private sealed class Factory : IPortraitNativePhaseHostFactory
    {
        private readonly Dictionary<PortraitNativeObservedPhase,Delegate> entries = [];
        public string Identity => "external boundary only";
        public bool TryQualifyStartDisabled(out string reason) { reason = "fixture"; return true; }
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason) { reason = "fixture"; return true; }
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type type) => Delegate.CreateDelegate(type, typeof(ResourcePhaseCases).GetMethod(target.Phase switch {
            PortraitNativeObservedPhase.ResourceCleanup => nameof(DirectCleanup), PortraitNativeObservedPhase.ResourceRecreate => nameof(DirectRecreate), _ => nameof(DirectCounter) })!);
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type type, Delegate detour)
        {
            entries.Add(target.Phase, detour);
            var original = Delegate.CreateDelegate(type, typeof(ResourcePhaseCases).GetMethod(target.Phase switch {
                PortraitNativeObservedPhase.ResourceCleanup => nameof(Cleanup), PortraitNativeObservedPhase.ResourceRecreate => nameof(Recreate), _ => nameof(Counter) })!);
            return new Hook(target.Address, original);
        }
        public byte Call(PortraitNativeObservedPhase kind)
        {
            var entry = entries[kind]; var signature = entry.GetType().GetMethod("Invoke")!;
            var m = new DynamicMethod("ResourceNative", typeof(byte), [typeof(Delegate)], typeof(ResourcePhaseCases).Module, true);
            var il = m.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, entry.GetType()); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_U);
            if (signature.GetParameters().Length == 2) il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, signature); if (signature.ReturnType == typeof(void)) il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            return ((Func<Delegate,byte>)m.CreateDelegate(typeof(Func<Delegate,byte>)))(entry);
        }
    }
    private sealed class Hook(nint address, Delegate original) : IPortraitNativePhaseHostHook
    {
        public nint Address => address; public Delegate Original => original; public string BackendName => "SafetyHook";
        public bool IsEnabled { get; private set; } public bool IsDisposed { get; private set; }
        public void Enable() => IsEnabled = true; public void Disable() => IsEnabled = false; public void Dispose() => IsDisposed = true;
    }
}
