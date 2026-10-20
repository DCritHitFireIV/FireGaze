#if !RESOURCE_INGRESS
Console.Error.WriteLine("FAIL: actual RT cleanup void/recreate byte typed ingress is absent; resource mutations cannot revoke live scene phases.");
return 1;
#else
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FireGaze.PortraitPreview;
using RTManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

internal static unsafe class Program
{
    private static int checks, nativeCalls, directCalls;
    private static long moduleKey = 0x7400000000;
    private static Action? duringNative;
    private static Exception? nativeError;
    private static byte result;
    private static nint lastReceiver;
    private static byte[] image = [], sdk = [];
    private static int imageSize;
    private static readonly uint[] rvas = [0x2DBB90, 0x2DBFE0];
    private static readonly (uint Begin, uint End, uint Unwind)[][] spans = [
        [(0x2DBB90,0x2DBBAA,0x25F05F8),(0x2DBBAA,0x2DBF33,0x25F0600),(0x2DBF33,0x2DBF60,0x25F0620),(0x2DBF60,0x2DBFA4,0x25F063C),(0x2DBFA4,0x2DBFD5,0x25F0650)],
        [(0x2DBFE0,0x2DC01C,0x25F0660),(0x2DC01C,0x2DC1B6,0x25F0678),(0x2DC1B6,0x2DC1CE,0x25F069C),(0x2DC1CE,0x2DCCA6,0x25F06AC)]
    ];
    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--game-exe") return 2;
        try
        {
            image = File.ReadAllBytes(args[1]); sdk = File.ReadAllBytes(typeof(RTManager).Assembly.Location);
            using var pe = new PEReader(new MemoryStream(image, false)); imageSize = pe.PEHeaders.PEHeader!.SizeOfImage;
            ABIAndBytePassThrough(); GatewayCallbacksCannotReplaceOriginal(); ResolutionNeedsEveryFragment(); MutationStateDoesNotBecomeReady(); RecordingStopAndErrors(); RetirementAndRegistryReuse(); CollectibleResourceCycles(); BatchBoundaries();
            Console.WriteLine($"PortraitResourceIngressTests: {checks} checks passed; game process access/hooks/scene operations: 0."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void ABIAndBytePassThrough()
    {
        Check(PortraitNativeResourceIngressContract.ABIValid(), "actual entries use void and byte Cdecl receiver ABI");
        Check(!PortraitNativePhaseHostGateway.TryCreate(6, [typeof(RTManager*)], 0x10000001, out _, out _), "old void schema rejects resource discriminator");
        Check(!PortraitNativePhaseHostGateway.TryCreateResource(6, [typeof(void*)], typeof(void), 0x10000002, out _, out _), "actual RT receiver cannot be replaced with callback void pointer");
        Check(!PortraitNativePhaseHostGateway.TryCreateResource(7, [typeof(RTManager*)], typeof(bool), 0x10000003, out _, out _), "bool cannot normalize actual AL return");
        Check(!PortraitNativePhaseHostGateway.TryCreateResource(6, [typeof(RTManager*)], typeof(byte), 0x10000004, out _, out _), "cleanup never receives a byte contract");
        Reset(); var factory = new FakeFactory(); var guards = 0; var released = 0;
        var host = Host(factory, () => { guards++; return new Guard(() => released++); });
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "actual resource pair installs explicitly");
        Check(factory.Hooks.Count == 2 && factory.Events.Take(2).All(e => e == "create"), "both disabled Originals bind before either enable");
        foreach (var value in new byte[] { 0, 1, 0xA7, 0xFF })
        {
            result = value; var before = nativeCalls;
            duringNative = () => Check(guards == before + 1 && released == before, "detached guard remains held during Original");
            Check(factory.Call(PortraitNativeObservedPhase.ResourceRecreate, unchecked((nint)0xDEADDEAD)) == value, "AL passes through unchanged: " + value);
            Check(lastReceiver == unchecked((nint)0xDEADDEAD) && nativeCalls == before + 1 && released == nativeCalls, "Original exactly once, same numerical receiver, guard finally released");
        }
        duringNative = null; factory.Call(PortraitNativeObservedPhase.ResourceCleanup, 0);
        var rows = host.Snapshot().Observation.Records;
        Check(rows.Count == 10 && rows.Where(r => r.Phase == PortraitNativeObservedPhase.ResourceCleanup).All(r => r.NativeReturn is null), "cleanup cannot record arbitrary AL as success");
        Check(rows.Where(r => r.Phase == PortraitNativeObservedPhase.ResourceRecreate && r.Edge == PortraitNativeObservedEdge.Return).Select(r => r.NativeReturn).SequenceEqual(new byte?[] { 0, 1, 0xA7, 0xFF }), "recorder preserves exact returned byte only on recreate Return");
        Check(rows.All(r => r.CounterSlot is null && r.Frame.Origin == PortraitNativeFrameOrigin.None), "resource record invents no frame or counter slot");
        Retire(host);
    }
    private static void ResolutionNeedsEveryFragment()
    {
        var process = Fresh(); Check(Resolve(process, out var plan) && plan!.Targets.Count == 2, "full actual disk bodies resolve exactly cleanup/recreate");
        var chunks = spans.Sum(group => group.Sum(s => ((int)(s.End - s.Begin) + 63) / 64));
        Check(process.MemoryReads == 2 * chunks && process.MaxRead <= 64 && process.ModuleReads == 3 && process.SDKReads == 3, "two complete rounds plus final identities copy every fragment in at most 64-byte chunks");
        Check(!PortraitNativePhaseObservationResolver.TryResolveResourceIngressCurrentProcess(static _ => false, out _, out _), "current adapter refuses non-game process");
        void Reject(FakeProcess bad, string text) => Check(!Resolve(bad, out var absent) && absent is null, text);
        foreach (var group in spans)
        foreach (var span in group)
        {
            foreach (var point in new[] { span.Begin, span.Begin + (span.End - span.Begin) / 2, span.End - 1 })
            {
                var bad = Fresh(); bad.ChangeRVA = point; Reject(bad, "changed complete fragment byte refuses " + point.ToString("X"));
                bad = Fresh(); bad.ChangeRVA = point; bad.SecondOnly = true; Reject(bad, "second-round full fragment drift refuses " + point.ToString("X"));
            }
            var unreadable = Fresh(); unreadable.UnreadableRVA = span.End - 1; Reject(unreadable, "unreadable tail chunk refuses " + span.End.ToString("X"));
        }
        var p = Fresh(); p.ChangeBase = true; Reject(p, "module identity replacement refuses");
        p = Fresh(); p.ChangeMVID = true; Reject(p, "SDK MVID replacement refuses");
        p = Fresh(); p.ChangeBaseAfterCopy = chunks * 2; Reject(p, "module replacement during final chunk cannot publish a stale plan");
        p = Fresh(); p.ChangeMVIAfterCopy = chunks * 2; Reject(p, "SDK MVID replacement during final chunk cannot publish a stale plan");
        p = Fresh(); p.ReserveAfter = chunks * 2; Reject(p, "late shared reservation rejects entire pair");
        using var pe = new PEReader(new MemoryStream(image, false));
        foreach (var group in spans)
        foreach (var span in group)
        {
            var mutated = (byte[])image.Clone(); mutated[Offset(pe, span.End - 1)] ^= 1;
            Check(!Inspect(mutated), "disk hash includes final byte of every fragment");
            var directory = pe.PEHeaders.PEHeader!.ExceptionTableDirectory; var table = Offset(pe, (uint)directory.RelativeVirtualAddress);
            var row = Enumerable.Range(0, directory.Size / 12).Select(i => table + i * 12).Single(i => BitConverter.ToUInt32(image, i) == span.Begin);
            mutated = (byte[])image.Clone(); BitConverter.GetBytes(span.Unwind + 4).CopyTo(mutated, row + 8);
            Check(!Inspect(mutated), "exact fragment unwind owner is required");
            mutated = (byte[])image.Clone(); BitConverter.GetBytes(span.End - 1).CopyTo(mutated, row + 4);
            Check(!Inspect(mutated), "missing fragment byte in pdata refuses");
            var unwindOffset = Offset(pe, span.Unwind);
            if ((image[unwindOffset] >> 3 & 4) != 0)
            {
                var chainOffset = unwindOffset + 4 + ((image[unwindOffset+2] + 1) & ~1) * 2;
                mutated = (byte[])image.Clone(); var end = BitConverter.ToUInt32(mutated, chainOffset + 4);
                BitConverter.GetBytes(end + 1).CopyTo(mutated, chainOffset + 4);
                Check(!Inspect(mutated), "chain tuple cannot invent a parent range despite an unchanged root");
            }
        }
        // The middle RET must not hide the remaining ~2.7KB branch body.
        p = Fresh(); p.ChangeRVA = 0x2DCCA5; Reject(p, "recreate range after its middle RET remains qualified");
    }

    private static void GatewayCallbacksCannotReplaceOriginal()
    {
        Reset();
        Check(PortraitNativePhaseHostGateway.TryCreateResource(7, [typeof(RTManager*)], typeof(byte), (nint)0x10000077, out var gateway, out _), "resource gateway creates with exact byte schema");
        gateway!.PublishOriginal(Delegate.CreateDelegate(gateway.DelegateType, typeof(Program).GetMethod(nameof(Recreate))!));
        try { gateway.SetObserver((_,_,_,_) => null); throw new Exception("accepted wrong schema"); } catch (InvalidOperationException) { Check(true, "resource entry refuses old observer schema"); }
        result = 0xFF; gateway.SetResourceObserver((_,_) => throw new IOException("prefix"));
        Check(CallGateway(gateway.Detour, 0) == 0xFF && nativeCalls == 1 && gateway.ActiveCount == 0, "gateway prefix exception never replaces Original or byte");
        gateway.SetResourceObserver((_,_) => (_,_) => throw new IOException("completion"));
        result = 0xA7; Check(CallGateway(gateway.Detour, 0) == 0xA7 && nativeCalls == 2 && gateway.ActiveCount == 0, "gateway completion exception cannot replace raw AL");
        nativeError = new IOException("actual Original");
        try { CallGateway(gateway.Detour, 0); throw new Exception("swallowed Original"); }
        catch (IOException error) { Check(ReferenceEquals(error, nativeError) && nativeCalls == 3 && gateway.ActiveCount == 0, "completion exception cannot replace Original failure identity"); }
        gateway.DetachObserver(); Check(gateway.TryRetireTo(Delegate.CreateDelegate(gateway.DelegateType, typeof(Program).GetMethod(nameof(DirectRecreate))!)), "direct byte callback fixture retires"); gateway.ReleaseOwnership();
    }

    private static void CollectibleResourceCycles()
    {
        Reset(); var cycle = BuildResourceProbe(true); ForceGC(cycle.Context);
        Check(!cycle.Context.IsAlive && CallGateway(cycle.Entry, 0) == 0xA7 && directCalls == 1, "actual collectible resource owner unloads while late byte entry survives");
        var count = GatewayAssemblyCount(); var entry = cycle.Entry;
        for (var index = 0; index < 8; index++)
        {
            cycle = BuildResourceProbe(true); ForceGC(cycle.Context);
            Check(!cycle.Context.IsAlive && ReferenceEquals(entry, cycle.Entry) && GatewayAssemblyCount() == count, "same resource address/schema reuses permanent entry and unloads each plugin generation");
        }
        var attached = BuildResourceProbe(false); ForceGC(attached.Context); Check(attached.Context.IsAlive, "control proves attached resource observer roots collectible owner");
        ClearProbe(attached.Entry); ForceGC(attached.Context);
        Check(!attached.Context.IsAlive && CallGateway(attached.Entry, 0) == 0xA7, "clearing resource observer permits actual ALC unload without corrupting byte target");
    }
    private static int GatewayAssemblyCount() => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name!.StartsWith("FireGaze.NativePhaseGateway.", StringComparison.Ordinal));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, Delegate Entry) BuildResourceProbe(bool retire)
    {
        var context = new AssemblyLoadContext("resource owner fixture", true);
        var assembly = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory,"Probe","PortraitPhaseCollectibleProbe.dll"));
        var method = assembly.GetType("CollectibleProbe")!.GetMethod("BuildResource")!;
        var entry = (Delegate)method.Invoke(null, [typeof(Program).GetMethod(nameof(Recreate))!, typeof(Program).GetMethod(nameof(DirectRecreate))!, retire])!;
        var weak = new WeakReference(context); context.Unload(); return (weak,entry);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearProbe(Delegate entry)
    {
        var instance = entry.Target!; var type = instance.GetType(); var gate = type.GetField("Gate")!.GetValue(instance)!;
        lock (gate)
        {
            type.GetField("Observer")!.SetValue(instance,null);
            type.GetField("Original")!.SetValue(instance,Delegate.CreateDelegate(entry.GetType(), typeof(Program).GetMethod(nameof(DirectRecreate))!));
            type.GetField("Retired")!.SetValue(instance,true); type.GetField("Owner")!.SetValue(instance,null);
        }
    }
    private static void ForceGC(WeakReference value)
    { for (var i=0; i<15 && value.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); } }
    private static byte CallGateway(Delegate entry, nint receiver)
    {
        var m = new DynamicMethod("CallRawAL",typeof(byte),[typeof(Delegate),typeof(nint)],typeof(Program).Module,true);
        var il=m.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass,entry.GetType()); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Callvirt,entry.GetType().GetMethod("Invoke")!); il.Emit(OpCodes.Ret);
        return ((Func<Delegate,nint,byte>)m.CreateDelegate(typeof(Func<Delegate,nint,byte>)))(entry,receiver);
    }
    private static void MutationStateDoesNotBecomeReady()
    {
        Reset(); var factory = new FakeFactory(); var depth = 0; var host = Host(factory, () => { depth++; return new Guard(() => depth--); });
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "resource state fixture starts");
        foreach (var value in new byte[] { 0, 1 })
        {
            result = value;
            duringNative = () => Check(depth == 1 && host.Snapshot().Resource.Depth == 1 && host.Snapshot().Resource.Transition == PortraitNativeResourceTransition.Transitioning && !host.Snapshot().Resource.Ready, "in-flight Original is an unqualified transition");
            factory.Call(PortraitNativeObservedPhase.ResourceRecreate, 0x1111);
            var state = host.Snapshot().Resource;
            Check(state.Depth == 0 && state.Transition == PortraitNativeResourceTransition.RecreateReturned && state.NativeReturn == value && !state.Ready, "recreate 0 and 1 both remain unqualified");
        }
        duringNative = null; factory.Call(PortraitNativeObservedPhase.ResourceCleanup, 0x1111);
        Check(host.Snapshot().Resource is { Transition: PortraitNativeResourceTransition.CleanupReturned, NativeReturn: null, Generation: 3, Ready: false }, "same receiver cleanup advances generation without a success byte or Ready");
        nativeError = new IOException("Original failure");
        try { factory.Call(PortraitNativeObservedPhase.ResourceRecreate, 0); throw new Exception("swallowed Original"); }
        catch (IOException error) { Check(ReferenceEquals(error, nativeError) && depth == 0, "Original failure identity propagates and releases guard"); }
        Check(host.Snapshot().Resource is { Transition: PortraitNativeResourceTransition.OriginalThrew, NativeReturn: null, Ready: false } && host.Snapshot().Observation.Records.Last().NativeReturn is null, "throw cannot invent return byte or restore old epoch");
        Retire(host);
    }
    private static void RecordingStopAndErrors()
    {
        foreach (var failure in new[] { "none", "throw", "null", "dispose" })
        {
            Reset(); var factory = new FakeFactory(); var guards = 0;
            var host = Host(factory, () => { guards++; return failure switch { "throw" => throw new IOException(), "null" => null, "dispose" => new Guard(() => throw new IOException()), _ => new Guard(() => { }) }; });
            Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "guard fixture starts " + failure);
            host.Stop(); result = 0xA7; Check(factory.Call(PortraitNativeObservedPhase.ResourceRecreate, 0) == 0xA7 && guards == 1 && nativeCalls == 1, "stopped recorder cannot remove actual mutation invalidation");
            Check(host.Snapshot().ResourceInvalidationFailed == (failure != "none"), "fault is explicit sticky uncertainty " + failure);
            Check(host.TryStart(TimeSpan.FromSeconds(30), out _) && host.Snapshot().ResourceInvalidationFailed == (failure != "none"), "restart cannot clear resource fault");
            Retire(host);
        }
        Reset(); var now = 0L; var guardDepth = 0; var recorder = new PortraitNativePhaseObservation(timestamp: () => now, timestampFrequency: 1000, threadStamp: () => new(1, Environment.CurrentManagedThreadId));
        var f = new FakeFactory(); var h = Host(f, () => { guardDepth++; return new Guard(() => guardDepth--); }, recorder: recorder);
        Check(h.TryStart(TimeSpan.FromSeconds(1), out _), "expiry fixture starts"); now = 1001; _ = h.Snapshot();
        duringNative = () => Check(guardDepth == 1, "expired recorder retains guard through Original"); f.Call(PortraitNativeObservedPhase.ResourceCleanup, 0); Check(guardDepth == 0, "expired guard releases physically"); Retire(h);
    }
    private static void RetirementAndRegistryReuse()
    {
        Reset(); var process = Fresh(); var f = new FakeFactory(); var guards = 0; var h = Host(f, () => { guards++; return new Guard(() => { }); }, process);
        Check(h.TryStart(TimeSpan.FromSeconds(30), out _), "retirement fixture starts");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        duringNative = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }; result = 0xFF;
        var worker = Task.Run(() => f.Call(PortraitNativeObservedPhase.ResourceRecreate, 0x1234));
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "real byte Original is pending");
        Check(h.BeginRetirement(out _) && !h.TryCompleteRetirement(out _) && h.Snapshot().InFlight == 1, "active Original retains hook/provider/reservation");
        Check(!h.WaitForRetirementAsync(TimeSpan.FromMilliseconds(20)).GetAwaiter().GetResult(), "bounded wait cannot free a running byte Original");
        release.Set(); Check(worker.GetAwaiter().GetResult() == 0xFF, "active retirement preserves byte"); duringNative = null;
        Check(h.WaitForRetirementAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(), "resource pair retires after actual Original returns");
        Check(f.Call(PortraitNativeObservedPhase.ResourceRecreate, 0) == 0xA7 && directCalls == 1 && guards == 1, "late entry uses exact typed direct byte target without old observer");
        var firstType = f.Hooks[1].Detour.GetType();
        var f2 = new FakeFactory(); var h2 = Host(f2, () => new Guard(() => { }), process);
        Check(h2.TryStart(TimeSpan.FromSeconds(30), out _) && f2.Hooks[1].Detour.GetType() == firstType, "next generation reuses stable resource registry entry"); Retire(h2);
        Check(!firstType.Assembly.IsCollectible && firstType.GetMethod("Invoke")!.ReturnType == typeof(byte), "stable entry retains raw byte return ABI across reload");
    }
    private static void BatchBoundaries()
    {
        Reset(); var f = new FakeFactory { RejectVerify = 2 }; var h = Host(f, () => new Guard(() => { }));
        Check(!h.TryStart(TimeSpan.FromSeconds(30), out _) && f.EnableCalls == 0 && f.Hooks.All(x => x.IsDisposed), "full body failure after both binds retires disabled pair before Enable");
        var p = Fresh(); Check(Resolve(p, out var plan), "reservation plan resolves"); var reservations = new PortraitNativePhaseHostReservations();
        Check(reservations.TryAcquireBatch([plan!.Targets[1].Address], out var held, out _), "peer resource address can be reserved");
        f = new FakeFactory(); h = Host(f, () => new Guard(() => { }), p);
        Check(!h.TryStart(TimeSpan.FromSeconds(30), out _) && f.CreateCalls == 0 && !reservations.IsReserved(plan.Targets[0].Address), "shared reservation rejects entire pair before Create"); Check(held!.TryRelease(out _), "peer releases");
        f = new FakeFactory { FailCreate = 1 }; p = Fresh(); h = Host(f, () => new Guard(() => { }), p);
        Check(!h.TryStart(TimeSpan.FromSeconds(30), out _) && !h.TryCompleteRetirement(out _) && reservations.IsReserved(p.Base + 0x2DBB90) && reservations.IsReserved(p.Base + 0x2DBFE0), "unknown native handle retains entire resource batch");
        p = Fresh(); Check(Resolve(p, out plan), "default plan fixture resolves");
        f = new FakeFactory(); h = new(f, new(), new(), (Func<nint,bool> _, out PortraitNativePhaseResolvedPlan? value, out string why) => { value = plan; why = "fixture"; return true; });
        Check(!h.TryStart(TimeSpan.FromSeconds(30), out _) && f.CreateCalls == 0, "default four phases reject resource target set");
    }
    private static bool Inspect(byte[] bytes) => PortraitNativePhaseObservationResolver.TryInspectExecutableImage(bytes, imageSize, PortraitNativePhaseTargetSet.ResourceIngress, out _, out _);
    private static bool Resolve(FakeProcess p, out PortraitNativePhaseResolvedPlan? plan) => new PortraitNativePhaseObservationResolver(p, PortraitNativePhaseTargetSet.ResourceIngress).TryResolve(out plan, out _);
    private static FakeProcess Fresh() => new((nint)Interlocked.Add(ref moduleKey, 0x10000000));
    private static PortraitNativePhaseHost Host(FakeFactory f, Func<IDisposable?> guard, FakeProcess? p = null, PortraitNativePhaseObservation? recorder = null)
    {
        p ??= Fresh(); return PortraitNativePhaseHost.CreateResourceIngress(f, recorder ?? new(threadStamp: () => new(1, Environment.CurrentManagedThreadId)), new(), guard,
            (Func<nint,bool> reserved, out PortraitNativePhaseResolvedPlan? plan, out string why) => { p.Reservation = reserved; return new PortraitNativePhaseObservationResolver(p, PortraitNativePhaseTargetSet.ResourceIngress).TryResolve(out plan, out why); });
    }
    private static void Retire(PortraitNativePhaseHost h) => Check(h.BeginRetirement(out _) && h.TryCompleteRetirement(out _), "resource host retires completely");
    private static void Reset() { nativeCalls = directCalls = 0; duringNative = null; nativeError = null; result = 0; }
    private static void Check(bool valid, string message) { checks++; if (!valid) throw new Exception("FAIL: " + message); }
    private static int Offset(PEReader pe, uint rva) { var s = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData); return checked(s.PointerToRawData + (int)rva - s.VirtualAddress); }
    public static void Cleanup(RTManager* receiver) { lastReceiver = (nint)receiver; Interlocked.Increment(ref nativeCalls); duringNative?.Invoke(); if (nativeError is not null) throw nativeError; }
    public static byte Recreate(RTManager* receiver) { Cleanup(receiver); return result; }
    public static void DirectCleanup(RTManager* receiver) => Interlocked.Increment(ref directCalls);
    public static byte DirectRecreate(RTManager* receiver) { DirectCleanup(receiver); return 0xA7; }
    private sealed class Guard(Action end) : IDisposable { public void Dispose() => end(); }
    private sealed class FakeFactory : IPortraitNativePhaseHostFactory
    {
        public int CreateCalls, EnableCalls, VerifyCalls, FailCreate = -1, RejectVerify = -1;
        public List<FakeHook> Hooks = []; public List<string> Events = [];
        public string Identity => "external hook boundary only";
        public bool TryQualifyStartDisabled(out string reason) { reason = "fixture"; return true; }
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason) { reason = "fixture"; return ++VerifyCalls != RejectVerify; }
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type type) => Delegate.CreateDelegate(type, typeof(Program).GetMethod(target.Phase == PortraitNativeObservedPhase.ResourceCleanup ? nameof(DirectCleanup) : nameof(DirectRecreate))!);
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type type, Delegate detour)
        {
            Events.Add("create"); if (CreateCalls++ == FailCreate) throw new IOException();
            var original = Delegate.CreateDelegate(type, typeof(Program).GetMethod(target.Phase == PortraitNativeObservedPhase.ResourceCleanup ? nameof(Cleanup) : nameof(Recreate))!);
            var hook = new FakeHook(this, target, detour, original); Hooks.Add(hook); return hook;
        }
        public byte Call(PortraitNativeObservedPhase kind, nint receiver)
        {
            var detour = Hooks.Single(h => h.Kind == kind).Detour; var m = new DynamicMethod("CallResource", typeof(byte), [typeof(Delegate),typeof(nint)], typeof(Program).Module, true);
            var il = m.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, detour.GetType()); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Callvirt, detour.GetType().GetMethod("Invoke")!);
            if (kind == PortraitNativeObservedPhase.ResourceCleanup) il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            return ((Func<Delegate,nint,byte>)m.CreateDelegate(typeof(Func<Delegate,nint,byte>)))(detour, receiver);
        }
    }
    private sealed class FakeHook(FakeFactory factory, PortraitNativePhaseBoundTarget target, Delegate detour, Delegate original) : IPortraitNativePhaseHostHook
    {
        public PortraitNativeObservedPhase Kind => target.Phase; public nint Address => target.Address; public Delegate Detour => detour; public Delegate Original => original;
        public string BackendName => "SafetyHook"; public bool IsEnabled { get; private set; } public bool IsDisposed { get; private set; }
        public void Enable() { factory.Events.Add("enable"); factory.EnableCalls++; IsEnabled = true; } public void Disable() => IsEnabled = false;
        public void Dispose() { if (IsEnabled) throw new Exception("freeing enabled hook"); IsDisposed = true; }
    }
    private sealed class FakeProcess(nint baseAddress) : IPortraitNativePhaseProcess
    {
        public nint Base = baseAddress; public Func<nint,bool>? Reservation;
        public int MemoryReads, ModuleReads, SDKReads, MaxRead, ReserveAfter = int.MaxValue, ChangeBaseAfterCopy = int.MaxValue, ChangeMVIAfterCopy = int.MaxValue;
        public uint ChangeRVA, UnreadableRVA; public bool SecondOnly, ChangeBase, ChangeMVID;
        public bool TryReadModule(out PortraitNativePhaseProcessModule module) { ModuleReads++; module = new("ffxiv_dx11.exe", Base + (ChangeBase && ModuleReads > 1 || MemoryReads >= ChangeBaseAfterCopy ? 1 : 0), imageSize, image); return true; }
        public bool TryReadSDK(out PortraitNativePhaseSDKImage s) { SDKReads++; s = new(sdk, ChangeMVID && SDKReads > 1 || MemoryReads >= ChangeMVIAfterCopy ? Guid.NewGuid() : typeof(RTManager).Assembly.ManifestModule.ModuleVersionId); return true; }
        public nint SDKManagerRender => Base + 0x2BA030; public nint SDKSourceRender => Base + 0x9CD4D0;
        public bool IsAddressReserved(nint address) => Reservation?.Invoke(address) == true || MemoryReads >= ReserveAfter;
        public bool TryReadMemory(nint address, Span<byte> output)
        {
            MemoryReads++; MaxRead = Math.Max(MaxRead, output.Length); if (output.Length > 64) return false;
            var rva = checked((uint)(address - Base)); var length = output.Length; if (!spans.Any(group => group.Any(s => rva >= s.Begin && (long)rva + length <= s.End))) return false;
            if (UnreadableRVA >= rva && UnreadableRVA < (long)rva + output.Length) return false;
            using var pe = new PEReader(new MemoryStream(image, false)); image.AsSpan(Offset(pe, rva), output.Length).CopyTo(output);
            if (ChangeRVA >= rva && ChangeRVA < (long)rva + output.Length && (!SecondOnly || ModuleReads > 1)) output[(int)(ChangeRVA-rva)] ^= 1;
            return true;
        }
    }
}
#endif
