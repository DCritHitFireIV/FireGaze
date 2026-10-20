#if !COUNTER_INGRESS
Console.Error.WriteLine("FAIL: CountAdd/Sub typed ingress is absent; independent native counter writes cannot invalidate the existing scene phase.");
return 1;
#else
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.PortableExecutable;
using FireGaze.PortraitPreview;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

internal static class Program
{
    private static int checks, nativeCalls, directCalls;
    private static long moduleKey = 0x700000000;
    private static readonly List<(nint Receiver, uint Slot)> nativeArguments = [];
    private static Action? duringNative;
    private static Exception? nativeError;
    private static byte[] image = [], sdk = [];
    private static int imageSize;
    private static readonly uint[] rvas = [0x2BA770, 0x2BA790];
    private static readonly byte[][] leafBytes = [
        Convert.FromHexString("83FA0873098BC2FF84815C830300C3CC"),
        Convert.FromHexString("83FA0873218BC2BA00000000488D0C8183815C830300FF8B815C8303000F48C289815C830300C3CC")
    ];

    public static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--game-exe") { Console.Error.WriteLine("--game-exe <current audited client> required; disk only, no game process."); return 2; }
        try
        {
            image = File.ReadAllBytes(args[1]); sdk = File.ReadAllBytes(typeof(RenderManager).Assembly.Location);
            using var pe = new PEReader(new MemoryStream(image, false)); imageSize = pe.PEHeaders.PEHeader!.SizeOfImage;
            ResolverRejectsDrift(); LeafContractRejectsMalformedImages(); TypedCallsInvalidateBeforeOriginal();
            PrefixFailuresNeverReplaceOriginal(); MutationGuardFailuresRemainExplicit(); RecordingStopPreservesInvalidation(); FrameAndSlotHaveSeparateMeaning();
            NestedCounterCalls(); CounterGatewayRejectsWrongABI(); RetirementWaitsForRealOriginal();
            BatchAndFailureBoundaries(); DefaultPhaseHostRemainsFourEntries();
            Console.WriteLine($"PortraitCounterIngressTests: {checks} checks passed; game process access/hooks/scene operations: 0."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    // Removing two-round qualification or admitting changed code must break these actual resolver cases.
    private static void ResolverRejectsDrift()
    {
        var valid = Fresh(); Check(Resolve(valid, out var plan) && plan is { Targets.Count: 2 }, "actual disk leaf bodies resolve two inert targets");
        Check(plan!.Targets[0].Phase == PortraitNativeObservedPhase.CounterAdd && plan.Targets[1].Phase == PortraitNativeObservedPhase.CounterSub
            && plan.Targets[0].Address == valid.Base + 0x2BA770 && plan.Targets[1].Address == valid.Base + 0x2BA790, "counter discriminator is explicit and preserves ASLR");
        Check(valid.MemoryReads == 4 && valid.ModuleReads == 2 && valid.SDKReads == 2, "both complete leaf bodies and identities are reread before qualification");
        Check(!PortraitNativePhaseObservationResolver.TryResolveCounterIngressCurrentProcess(static _ => false, out _, out _), "real adapter refuses this test process without native target reads");
        void Reject(FakeProcess process, string name) => Check(!Resolve(process, out var absent) && absent is null, name);
        var bad = Fresh(); bad.Name = "another.exe"; Reject(bad, "different process name refuses");
        bad = Fresh(); bad.Executable = (byte[])image.Clone(); bad.Executable[^1] ^= 1; Reject(bad, "EXE drift refuses");
        bad = Fresh(); bad.SDK = (byte[])sdk.Clone(); bad.SDK[^1] ^= 1; Reject(bad, "SDK drift refuses");
        bad = Fresh(); bad.MVID = Guid.NewGuid(); Reject(bad, "loaded SDK MVID drift refuses");
        bad = Fresh(); bad.Base = nint.MaxValue - 1; Reject(bad, "module arithmetic overflow refuses");
        bad = Fresh(); bad.ChangeBase = true; Reject(bad, "module replacement between rounds refuses");
        bad = Fresh(); bad.ChangeMVID = true; Reject(bad, "SDK replacement between rounds refuses");
        bad = Fresh(); bad.ThrowRead = true; Reject(bad, "failed external copy cannot expose a partial plan");
        for (var index = 0; index < 2; index++)
        {
            bad = Fresh(); bad.Unreadable = index; Reject(bad, "unreadable leaf " + index + " refuses");
            bad = Fresh(); bad.Changed = index; Reject(bad, "changed complete leaf " + index + " refuses");
            bad = Fresh(); bad.SecondRound = index; Reject(bad, "late leaf replacement " + index + " refuses");
            bad = Fresh(); bad.Reserved = index; Reject(bad, "reserved counter entry " + index + " refuses");
        }
        bad = Fresh(); bad.ReserveAfterRead = 4; Reject(bad, "reservation appearing during final copy refuses obsolete plan");
    }

    // Relaxing leaf bounds/RET/padding/pdata validation must break independent PE mutations.
    private static void LeafContractRejectsMalformedImages()
    {
        Check(Inspect(image), "complete actual leaf functions pass without fabricated unwind roots");
        using var pe = new PEReader(new MemoryStream(image, false));
        for (var index = 0; index < 2; index++)
        {
            var offset = Offset(pe, rvas[index]);
            foreach (var mutation in new[] { 0, 4, leafBytes[index].Length - 2, leafBytes[index].Length - 1 })
            {
                var changed = (byte[])image.Clone(); changed[offset + mutation] ^= 1;
                Check(!Inspect(changed), "body/conditional branch/RET/INT3 mutation refuses " + index + "/" + mutation);
            }
            var duplicated = image.Concat(leafBytes[index]).ToArray(); Check(!Inspect(duplicated), "duplicated complete leaf refuses " + index);
        }
        var bad = (byte[])image.Clone(); bad[0] = 0; Check(!Inspect(bad), "invalid PE refuses");
        var directory = pe.PEHeaders.PEHeader!.ExceptionTableDirectory;
        var row = Offset(pe, (uint)directory.RelativeVirtualAddress);
        bad = (byte[])image.Clone(); BitConverter.GetBytes(0x2BA770u).CopyTo(bad, row); BitConverter.GetBytes(0x2BA77Fu).CopyTo(bad, row + 4);
        Check(!Inspect(bad), "unwind ownership overlapping the explicit leaf refuses");
    }

    // Wrong unsigned argument or calling Original before invalidation must fail here.
    private static void TypedCallsInvalidateBeforeOriginal()
    {
        Reset(); var invalidations = 0; var observation = Recorder(); var factory = new FakeFactory();
        var host = Host(factory, observation, () => invalidations++);
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "counter batch starts explicitly");
        Check(factory.Hooks.Count == 2 && factory.EnableCalls == 2 && factory.Events.Take(2).All(e => e.StartsWith("create")), "both Originals publish before either enable");
        foreach (var kind in new[] { PortraitNativeObservedPhase.CounterAdd, PortraitNativeObservedPhase.CounterSub })
        {
            foreach (var slot in new uint[] { 0, 7, 8, uint.MaxValue })
            {
                var before = invalidations; duringNative = () => Check(invalidations == before + (slot < 8 ? 1 : 0), "valid-slot invalidation precedes Original without probing receiver");
                factory.Call(kind, unchecked((nint)0xDEADDEAD), slot);
                Check(nativeArguments[^1] == (unchecked((nint)0xDEADDEAD), slot), "native receiver and full unsigned slot pass unchanged");
            }
        }
        Check(nativeCalls == 8 && invalidations == 4, "each call executes once; out-of-range no-ops do not revoke phase");
        var records = host.Snapshot().Observation.Records;
        Check(records.Count == 16 && records.All(r => r.Frame.Origin == PortraitNativeFrameOrigin.None)
            && records.Where(r => r.Edge == PortraitNativeObservedEdge.Enter).Select(r => r.CounterSlot).SequenceEqual(new uint?[] { 0, 7, 8, uint.MaxValue, 0, 7, 8, uint.MaxValue }), "counter slots are distinct from source native-frame evidence");
        Check(!host.Snapshot().ProductionAdmission, "counter observation never admits a scene");
        Retire(host);
    }

    private static void PrefixFailuresNeverReplaceOriginal()
    {
        Reset(); var observation = Recorder(); var factory = new FakeFactory();
        var host = Host(factory, observation, () => throw new IOException("detached revocation failure"));
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "fault fixture installs");
        factory.Call(PortraitNativeObservedPhase.CounterAdd, 0, 1);
        Check(nativeCalls == 1 && nativeArguments.Single() == ((nint)0, 1u), "throwing revocation sink cannot skip Original or dereference null manager");
        Check(host.Snapshot().CounterInvalidationFailed && host.Snapshot().CounterInvalidationEpoch == 1,
            "revocation exception is permanently distinct from a successful detached epoch");
        nativeError = new IOException("native boundary failure");
        try { factory.Call(PortraitNativeObservedPhase.CounterSub, 0x1234, 2); throw new Exception("Original failure was swallowed"); }
        catch (IOException error) { Check(ReferenceEquals(error, nativeError) && nativeCalls == 2, "Original exception survives prefix failure without retry"); }
        Retire(host); Reset();
        observation = Recorder(); factory = new FakeFactory(); host = Host(factory, observation, static () => { });
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "throwing Original fixture installs");
        nativeError = new InvalidOperationException("native error");
        try { factory.Call(PortraitNativeObservedPhase.CounterAdd, 0x2345, 3); throw new Exception("Original failure was swallowed"); }
        catch (InvalidOperationException error) { Check(ReferenceEquals(error, nativeError) && nativeCalls == 1, "Original failure executes exactly once"); }
        Check(host.Snapshot().Observation.Records.Last().Edge == PortraitNativeObservedEdge.Throw, "counter completion records actual failure edge");
        Retire(host);
    }

    private static void MutationGuardFailuresRemainExplicit()
    {
        foreach (var failure in new[] { "null", "dispose" })
        {
            Reset(); var factory = new FakeFactory(); var recorder = Recorder();
            var host = HostWithScope(factory, recorder, () => failure == "null" ? null : new ThrowingGuard());
            Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "guard failure fixture starts: " + failure);
            factory.Call(PortraitNativeObservedPhase.CounterAdd, 0x2345, 0);
            Check(nativeCalls == 1 && host.Snapshot().CounterInvalidationFailed && !host.Snapshot().ProductionAdmission,
                "missing/failed scope keeps Original once and sets sticky admission fault: " + failure);
            host.Stop(); Check(host.TryStart(TimeSpan.FromSeconds(30), out _) && host.Snapshot().CounterInvalidationFailed,
                "recording restart cannot clear prior mutation guard uncertainty: " + failure);
            Retire(host);
        }
    }

    private static void RecordingStopPreservesInvalidation()
    {
        Reset(); var now = 0L; var invalidations = 0;
        var observation = new PortraitNativePhaseObservation(timestamp: () => now, timestampFrequency: 1000, threadStamp: () => new(1, 1));
        var factory = new FakeFactory(); var host = Host(factory, observation, () => invalidations++);
        Check(host.TryStart(TimeSpan.FromSeconds(1), out _), "deadline fixture installs");
        now = 1001; Check(host.Snapshot().State == PortraitNativePhaseHostState.Installed, "expired recorder stops collecting");
        factory.Call(PortraitNativeObservedPhase.CounterSub, 0x4567, 1);
        Check(invalidations == 1 && host.Snapshot().Observation.Records.Count == 0, "deadline does not detach phase invalidation");
        Check(host.TryStart(TimeSpan.FromSeconds(1), out _), "installed counter recorder restarts");
        host.Stop(); factory.Call(PortraitNativeObservedPhase.CounterAdd, 0x4567, 2);
        Check(invalidations == 2 && host.Snapshot().Observation.Records.Count == 0, "explicit recording stop retains invalidation");
        Check(host.BeginRetirement(out _), "retirement detaches counter observer before hook disable");
        factory.Call(PortraitNativeObservedPhase.CounterAdd, 0x4567, 3);
        Check(invalidations == 2, "retirement immediately disconnects invalidation even for a late entry");
        Check(host.TryCompleteRetirement(out _), "detached counter batch retires");
    }

    private static void FrameAndSlotHaveSeparateMeaning()
    {
        Reset(); var observation = Recorder(); var factory = new FakeFactory(); var host = Host(factory, observation, static () => { });
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "nested recorder installs");
        var enclosing = observation.Begin(PortraitNativeObservedPhase.SourceCharaViewRender, 0x3456, 0x11223344);
        factory.Call(PortraitNativeObservedPhase.CounterAdd, 0x4567, uint.MaxValue); observation.End(enclosing, true);
        var row = host.Snapshot().Observation.Records.Single(r => r.Phase == PortraitNativeObservedPhase.CounterAdd && r.Edge == PortraitNativeObservedEdge.Enter);
        Check(row.CounterSlot == uint.MaxValue && row.Frame.Value == 0x11223344 && row.Frame.Origin == PortraitNativeFrameOrigin.EnclosingSourceRenderArgument
            && row.Frame.SourceView == (nint)0x3456, "only actual enclosing source Render contributes a native frame");
        Retire(host);
    }

    private static void RetirementWaitsForRealOriginal()
    {
        Reset(); var invalidations = 0; var observation = Recorder(); var factory = new FakeFactory(); var host = Host(factory, observation, () => invalidations++);
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "concurrent fixture installs");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        duringNative = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var worker = Task.Run(() => factory.Call(PortraitNativeObservedPhase.CounterAdd, 0x5678, 7));
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "counter Original is actually in flight on worker thread");
        Check(host.BeginRetirement(out _) && !host.TryCompleteRetirement(out _), "retirement does not free an active Original or block on it");
        Check(factory.Hooks.Any(h => !h.IsDisposed), "active trampoline stays owned");
        release.Set(); worker.GetAwaiter().GetResult(); duringNative = null;
        Check(host.WaitForRetirementAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(), "retirement completes after physical call returns");
        factory.Call(PortraitNativeObservedPhase.CounterAdd, 0x5678, 3);
        Check(nativeCalls == 1 && directCalls == 1 && invalidations == 1, "late entry uses stable direct target with no old observer/trampoline");
    }

    private static void NestedCounterCalls()
    {
        Reset(); var invalidations = 0; var observation = Recorder(); var factory = new FakeFactory();
        var host = Host(factory, observation, () => {
            if (++invalidations == 1) factory.Call(PortraitNativeObservedPhase.CounterSub, 0x2222, 7);
        });
        Check(host.TryStart(TimeSpan.FromSeconds(30), out _), "reentrant prefix fixture installs");
        factory.Call(PortraitNativeObservedPhase.CounterAdd, 0x1111, 0);
        Check(nativeCalls == 2 && invalidations == 2 && nativeArguments.SequenceEqual(new[] { ((nint)0x2222, 7u), ((nint)0x1111, 0u) }),
            "reentrant invalidation invokes each exact Original once in native nesting order");
        var rows = host.Snapshot().Observation.Records;
        Check(rows.Count == 4 && rows[0].Phase == PortraitNativeObservedPhase.CounterSub && rows[0].ParentCall == 0
            && rows[2].Phase == PortraitNativeObservedPhase.CounterAdd && rows[2].ParentCall == 0
            && rows.All(row => row.Frame.Origin == PortraitNativeFrameOrigin.None), "revocation precedes recording and nested prefix does not fabricate a source parent");
        Retire(host);
    }

    private static unsafe void CounterGatewayRejectsWrongABI()
    {
        Check(!PortraitNativePhaseHostGateway.TryCreate(4, [typeof(void*), typeof(float)], 0x1000001, out _, out _), "CountAdd cannot use float ABI");
        Check(!PortraitNativePhaseHostGateway.TryCreate(5, [typeof(void*)], 0x1000002, out _, out _), "CountSub cannot omit its unsigned slot");
        Check(!PortraitNativePhaseHostGateway.TryCreate(4, [typeof(nint), typeof(uint)], 0x1000003, out _, out _), "counter receiver must use a stable native pointer ABI");
        Check(!PortraitNativePhaseHostGateway.TryCreate(6, [typeof(void*), typeof(uint)], 0x1000004, out _, out _), "unknown native entry discriminator cannot create a gateway");
    }

    private static void BatchAndFailureBoundaries()
    {
        Reset(); var factory = new FakeFactory { Qualified = false }; var host = Host(factory, Recorder(), static () => { });
        Check(!host.TryStart(TimeSpan.FromSeconds(30), out _) && factory.CreateCalls == 0, "unqualified/eager backend refuses before factory entry");
        var process = Fresh(); Check(Resolve(process, out var plan), "reservation plan resolves");
        var reservations = new PortraitNativePhaseHostReservations();
        Check(reservations.TryAcquireBatch([plan!.Targets[1].Address], out var held, out _), "competing owner reserves one counter");
        factory = new FakeFactory(); host = Host(factory, Recorder(), static () => { }, process);
        Check(!host.TryStart(TimeSpan.FromSeconds(30), out _) && factory.CreateCalls == 0
            && !reservations.IsReserved(plan.Targets[0].Address), "overlap refuses the entire pair without reserving its peer");
        Check(held!.TryRelease(out _), "competing test reservation releases");
        factory = new FakeFactory { FailCreate = 1 }; process = Fresh(); host = Host(factory, Recorder(), static () => { }, process);
        Check(!host.TryStart(TimeSpan.FromSeconds(30), out _) && host.Snapshot().State == PortraitNativePhaseHostState.Faulted
            && !host.TryCompleteRetirement(out _), "unreturned native handle cannot claim retirement");
        Check(reservations.IsReserved(process.Base + 0x2BA770) && reservations.IsReserved(process.Base + 0x2BA790), "uncertain factory failure retains complete two-address reservation");
        factory = new FakeFactory { RejectVerify = 2 }; host = Host(factory, Recorder(), static () => { });
        Check(!host.TryStart(TimeSpan.FromSeconds(30), out _) && factory.EnableCalls == 0 && factory.Hooks.All(h => h.IsDisposed), "last body confirmation failure retires both disabled handles before enable");
        process = Fresh(); Check(Resolve(process, out plan), "malformed plan fixture resolves");
        var missing = plan! with { Targets = [plan.Targets[0]] };
        factory = new FakeFactory();
        host = PortraitNativePhaseHost.CreateCounterIngress(factory, Recorder(), new(), static () => new DetachedGuard(),
            (Func<nint, bool> _, out PortraitNativePhaseResolvedPlan? output, out string reason) => { output = missing; reason = "fixture"; return true; });
        Check(!host.TryStart(TimeSpan.FromSeconds(30), out _) && factory.CreateCalls == 0, "missing peer cannot form a counter host batch");
    }

    private static void DefaultPhaseHostRemainsFourEntries()
    {
        var process = Fresh();
        var counterPlan = new PortraitNativePhaseObservationResolver(process, PortraitNativePhaseTargetSet.CounterIngress);
        Check(counterPlan.TryResolve(out var plan, out _), "default host refusal fixture resolves counters");
        var factory = new FakeFactory();
        var host = new PortraitNativePhaseHost(factory, Recorder(), new(),
            (Func<nint, bool> _, out PortraitNativePhaseResolvedPlan? output, out string reason) => { output = plan; reason = "fixture"; return true; });
        Check(!host.TryStart(TimeSpan.FromSeconds(30), out _) && factory.CreateCalls == 0, "default four-phase host cannot accept a counter pair");
    }

    private static PortraitNativePhaseObservation Recorder() => new(threadStamp: () => new(1, Environment.CurrentManagedThreadId));
    private static FakeProcess Fresh() => new(image, sdk, imageSize, (nint)Interlocked.Add(ref moduleKey, 0x10000000));
    private static bool Resolve(FakeProcess process, out PortraitNativePhaseResolvedPlan? plan)
        => new PortraitNativePhaseObservationResolver(process, PortraitNativePhaseTargetSet.CounterIngress).TryResolve(out plan, out _);
    private static bool Inspect(byte[] bytes) => PortraitNativePhaseObservationResolver.TryInspectExecutableImage(bytes, imageSize,
        PortraitNativePhaseTargetSet.CounterIngress, out _, out _);
    private static int Offset(PEReader pe, uint rva)
    {
        var section = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData);
        return checked(section.PointerToRawData + (int)rva - section.VirtualAddress);
    }
    private static PortraitNativePhaseHost Host(FakeFactory factory, PortraitNativePhaseObservation observation, Action invalidate, FakeProcess? process = null)
        => HostWithScope(factory, observation, () => { invalidate(); return new DetachedGuard(); }, process);
    private static PortraitNativePhaseHost HostWithScope(FakeFactory factory, PortraitNativePhaseObservation observation,
        Func<IDisposable?> invalidate, FakeProcess? process = null)
    {
        process ??= Fresh();
        return PortraitNativePhaseHost.CreateCounterIngress(factory, observation, new(), invalidate,
            (Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? plan, out string reason) => {
                process.Reservation = reserved; return new PortraitNativePhaseObservationResolver(process, PortraitNativePhaseTargetSet.CounterIngress).TryResolve(out plan, out reason);
            });
    }
    private static void Retire(PortraitNativePhaseHost host) { Check(host.BeginRetirement(out _) && host.TryCompleteRetirement(out _), "counter host retires completely"); }
    private static void Reset() { nativeCalls = directCalls = 0; nativeArguments.Clear(); duringNative = null; nativeError = null; }
    private sealed class DetachedGuard : IDisposable { public void Dispose() { } }
    private sealed class ThrowingGuard : IDisposable { public void Dispose() => throw new IOException("mutation scope disposal unproven"); }
    private static void Check(bool valid, string message) { checks++; if (!valid) throw new Exception("FAIL: " + message); }
    public static unsafe void Native(RenderManager* receiver, uint slot) { Interlocked.Increment(ref nativeCalls); lock (nativeArguments) nativeArguments.Add(((nint)receiver, slot)); duringNative?.Invoke(); if (nativeError is not null) throw nativeError; }
    public static unsafe void Direct(RenderManager* receiver, uint slot) => Interlocked.Increment(ref directCalls);

    private sealed class FakeFactory : IPortraitNativePhaseHostFactory
    {
        public bool Qualified = true; public int CreateCalls, EnableCalls, VerifyCalls, FailCreate = -1, RejectVerify = -1;
        public string Identity => "Audited test boundary; no real hooks";
        public List<FakeHook> Hooks = []; public List<string> Events = [];
        public bool TryQualifyStartDisabled(out string reason) { reason = "test backend contract"; return Qualified; }
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason) { reason = "test entry copy"; return ++VerifyCalls != RejectVerify; }
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type type) => Delegate.CreateDelegate(type, typeof(Program).GetMethod(nameof(Direct))!);
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type type, Delegate detour)
        {
            Events.Add("create " + target.Phase); if (CreateCalls++ == FailCreate) throw new IOException("native factory returned no handle");
            var hook = new FakeHook(this, target, detour, Delegate.CreateDelegate(type, typeof(Program).GetMethod(nameof(Native))!)); Hooks.Add(hook); return hook;
        }
        public void Call(PortraitNativeObservedPhase kind, nint receiver, uint slot)
        {
            var detour = Hooks.Single(h => h.Kind == kind).Detour;
            var method = new DynamicMethod("CallCounter", typeof(void), [typeof(Delegate), typeof(nint), typeof(uint)], typeof(Program).Module, true);
            var il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, detour.GetType()); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, detour.GetType().GetMethod("Invoke")!); il.Emit(OpCodes.Ret);
            ((Action<Delegate, nint, uint>)method.CreateDelegate(typeof(Action<Delegate, nint, uint>)))(detour, receiver, slot);
        }
    }
    private sealed class FakeHook(FakeFactory factory, PortraitNativePhaseBoundTarget target, Delegate detour, Delegate original) : IPortraitNativePhaseHostHook
    {
        public PortraitNativeObservedPhase Kind => target.Phase; public nint Address => target.Address;
        public Delegate Detour => detour; public Delegate Original => original; public string BackendName => "SafetyHook";
        public bool IsEnabled { get; private set; } public bool IsDisposed { get; private set; }
        public void Enable() { factory.Events.Add("enable " + target.Phase); factory.EnableCalls++; IsEnabled = true; }
        public void Disable() => IsEnabled = false;
        public void Dispose() { if (IsEnabled) throw new Exception("freeing enabled hook"); IsDisposed = true; }
    }
    private sealed class FakeProcess(byte[] executable, byte[] sdkImage, int size, nint moduleBase) : IPortraitNativePhaseProcess
    {
        public byte[] Executable = executable, SDK = sdkImage; public int ImageSize = size;
        public nint Base = moduleBase; public Guid MVID = typeof(RenderManager).Assembly.ManifestModule.ModuleVersionId;
        public string Name = "ffxiv_dx11.exe"; public int ModuleReads, SDKReads, MemoryReads;
        public int Unreadable = -1, Changed = -1, SecondRound = -1, Reserved = -1, ReserveAfterRead = int.MaxValue;
        public bool ChangeBase, ChangeMVID, ThrowRead; public Func<nint, bool>? Reservation;
        public bool TryReadModule(out PortraitNativePhaseProcessModule module) { ModuleReads++; module = new(Name, Base + (ChangeBase && ModuleReads > 1 ? 1 : 0), ImageSize, Executable); return true; }
        public bool TryReadSDK(out PortraitNativePhaseSDKImage sdkModule) { SDKReads++; sdkModule = new(SDK, ChangeMVID && SDKReads > 1 ? Guid.NewGuid() : MVID); return true; }
        public nint SDKManagerRender => Base + 0x2BA030; public nint SDKSourceRender => Base + 0x9CD4D0;
        public bool IsAddressReserved(nint address) => Reservation?.Invoke(address) == true || Reserved >= 0 && address == Base + (nint)rvas[Reserved] || MemoryReads >= ReserveAfterRead;
        public bool TryReadMemory(nint address, Span<byte> destination)
        {
            if (ThrowRead) throw new IOException("native copy failed");
            var index = Array.FindIndex(rvas, rva => address == Base + (nint)rva); MemoryReads++;
            if (index < 0 || Unreadable == index || destination.Length != leafBytes[index].Length) return false;
            leafBytes[index].CopyTo(destination); if (Changed == index || SecondRound == index && MemoryReads > 2) destination[0] ^= 1; return true;
        }
    }
}
#endif
