using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Reflection;

internal static unsafe class OwnedPassiveHandoffCases
{
    private static int checks;
    private static Action? ownedOriginal;
    private static int ownedOriginalCalls;
    private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
    internal static int Run()
    {
        ColdStart(); ActualThreeHostDrain(false); ActualThreeHostDrain(true); ContextChange();
        NativeQualificationRefusal(); CensusAttemptReplacement(); PartialEnableReturn();
        EntryBeforeArm(); ForeignReturn(); WorkerReturn(); ThrowingOriginal(); ContextChangeAfterReturn();
        DeadlineDuringEnable();
        Console.WriteLine($"OwnedPassiveHandoffCases: {checks} checks passed; actual census/Hosts/shared reservations/typed owned dispatcher."); return 0;
    }
    private static void ColdStart()
    {
        using var boundary = new CensusFeatureBoundary();
        var provider = new OwnedPhysicalCases.Provider();
        var controller = Controller(boundary, provider, out _);
        Check(!controller.TryStart(out _) && provider.Created.Count == 0 && !controller.BlocksOperations,
            "never-started Off with null three Hosts cannot hand off or fabricate baseline");
    }
    private static PortraitOwnedScenePassiveDiagnostic Controller(CensusFeatureBoundary boundary, IPortraitQualifiedNativeHookProvider provider,
        out List<PortraitOwnedSceneHooks> created, Func<long>? timestamp = null, long timestampFrequency = 0)
    {
        var list = new List<PortraitOwnedSceneHooks>(); created = list;
        return new(() => true, boundary.Feature.CensusContext, boundary.Feature.CensusController, () => {
            var module = boundary.Feature.CensusSnapshot!.Source!.Plan!.ModuleBase;
            var hooks = new PortraitOwnedSceneHooks(new PassiveEnvironment(module), timestamp: timestamp, timestampFrequency: timestampFrequency);
            list.Add(hooks); return hooks;
        }, new DalamudPortraitOwnedSceneHookFactory(provider, new OwnedPhysicalCases.Proof()),
            timestamp: timestamp, timestampFrequency: timestampFrequency);
    }
    private static void CompleteCensus(CensusFeatureBoundary boundary)
    {
        boundary.Feature.CensusCommand("native-census", "start");
        boundary.Source(0); boundary.Feature.CensusTick(); boundary.Source(1); boundary.Feature.CensusTick();
        Check(boundary.Feature.CensusSnapshot?.Result is { Complete: true, Rows.Count: 8 }, "actual old census must have eight rows");
        Check(boundary.Feature.CensusController.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "old census fully retired");
    }
    private static void ActualThreeHostDrain(bool cancel)
    {
        using var boundary = new CensusFeatureBoundary();
        boundary.Feature.CensusCommand("native-census", "start");
        boundary.Source(0); boundary.Feature.CensusTick(); boundary.Source(1);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        SourceReturnCases.sourceOriginal = () => { entered.Set(); release.Wait(); };
        var worker = Task.Run(() => boundary.Source(2));
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "old source Original physically entered before handoff");
        boundary.Feature.CensusTick();
        Check(boundary.Feature.CensusSnapshot?.Result is { Complete: true, Rows.Count: 8 }
            && !boundary.Feature.CensusController.DrainPhysicalAsync().IsCompleted, "historical complete rows do not imply old physical drain");
        var provider = new OwnedPhysicalCases.Provider(); var controller = Controller(boundary, provider, out var created);
        try
        {
            Check(controller.TryStart(out var reason) && controller.BlocksOperations, "bounded handoff starts: " + reason);
            controller.Update();
            Check(provider.Created.Count == 0 && created.Count == 0 && !controller.DrainPhysicalAsync().IsCompleted,
                "actual pending three-Host task prohibits all owned construction and blocks mutations");
            if (cancel)
            {
                controller.Stop("cancelled while old actual Original is still active");
                Check(!controller.DrainPhysicalAsync().IsCompleted && provider.Created.Count == 0,
                    "cancel never fabricates old physical drain or constructs an owned prefix");
            }
            release.Set(); Check(worker.Wait(TimeSpan.FromSeconds(5)), "old physical Original returned"); SourceReturnCases.sourceOriginal = null;
            Check(boundary.Feature.CensusController.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "actual old three-Host drain completes");
            if (cancel)
            {
                Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && !controller.BlocksOperations && provider.Created.Count == 0,
                    "cancelled handoff finishes only after old actual physical drain"); return;
            }
            provider.OnEnable = () => Check(boundary.Feature.CensusController.ReadSafetyState() is { State: "Off", PhysicalDrainCompleted: true }
                && boundary.Factory.Hooks.Values.All(hook => hook.IsDisposed), "owned Enable never overlaps any old physical Host");
            controller.Update();
            Check(provider.Created.Count == 39 && created.Count == 1 && controller.Snapshot.State == PortraitOwnedScenePassiveDiagnosticState.WaitingForSourceReturn,
                "new owner installs exactly one complete 39-entry generation after actual old drain");
            Check(controller.Snapshot.SourceReturn is null && created[0].PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Waiting,
                "old census return and slot rows cannot seed the new-owner source baseline");
            var native = provider.Created.Single(hook => hook.Address == boundary.Feature.CensusSnapshot!.Source!.Plan!.ModuleBase + 0x9CD4D0);
            var dispatch = (CharaView.Delegates.Render)Delegate.CreateDelegate(typeof(CharaView.Delegates.Render), native.Detour.Target, native.Detour.Method);
            dispatch((CharaView*)boundary.Memory.Source, 0); controller.Update();
            Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && !controller.BlocksOperations
                && controller.Snapshot is { State: PortraitOwnedScenePassiveDiagnosticState.Off,
                    SourceReturn: { State: PortraitOwnedScenePassiveBaselineState.Observed, NativeFrame: 0, ProductionAdmission: false } },
                "only a future actual new-owner native frame-zero return produces historical result before full physical retirement");
            Check(provider.Created.All(hook => hook.IsDisposed) && !created[0].ReservationHeld,
                "successful bounded probe physically retires all 39 hooks and the entire shared reservation");
        }
        finally
        {
            release.Set(); worker.Wait(TimeSpan.FromSeconds(5)); SourceReturnCases.sourceOriginal = null;
            controller.Stop("fixture cleanup"); controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5));
        }
    }
    private static void ContextChange()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        var provider = new OwnedPhysicalCases.Provider(); var controller = Controller(boundary, provider, out _);
        Check(controller.TryStart(out _), "unchanged historical context starts handoff");
        boundary.Feature.CensusMutation(); controller.Update();
        Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && provider.Created.Count == 0,
            "source session epoch change before ownership acquisition rejects all native Create");
    }
    private static void NativeQualificationRefusal()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        var provider = new OwnedPhysicalCases.Provider { Qualified = false };
        var controller = Controller(boundary, provider, out var created);
        Check(controller.TryStart(out _), "logical handoff may wait for actual native qualification");
        controller.Update();
        Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && !controller.BlocksOperations
            && provider.Created.Count == 0 && created.Count == 1 && !created[0].ReservationHeld
            && controller.Snapshot.SourceReturn is null,
            "unqualified real provider boundary cannot Create, reserve or publish a source return");
    }
    private static void CensusAttemptReplacement()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        var provider = new OwnedPhysicalCases.Provider(); var controller = Controller(boundary, provider, out var created);
        var originalAttempt = boundary.Feature.CensusSnapshot!.Attempt;
        Check(controller.TryStart(out _), "same-attempt handoff request starts");
        boundary.Feature.CensusCommand("native-census", "start");
        Check(boundary.Feature.CensusSnapshot!.Attempt > originalAttempt, "actual census controller starts a later attempt");
        controller.Update();
        Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && provider.Created.Count == 0 && created.Count == 0,
            "captured old drain cannot certify the current replacement census attempt");
        Check(boundary.Feature.CensusController.BlocksOperations,
            "rejecting a stale handoff does not pretend the newer census owner physically retired");
    }
    private static CharaView.Delegates.Render Dispatch(CensusFeatureBoundary boundary, OwnedPhysicalCases.Provider provider)
    {
        var address = boundary.Feature.CensusSnapshot!.Source!.Plan!.ModuleBase + 0x9CD4D0;
        var hook = provider.Created.Single(value => value.Address == address);
        return (CharaView.Delegates.Render)Delegate.CreateDelegate(typeof(CharaView.Delegates.Render), hook.Detour.Target, hook.Detour.Method);
    }
    private static void Stop(PortraitOwnedScenePassiveDiagnostic controller)
    {
        controller.Stop("independent handoff test cleanup");
        Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "independent handoff test confirms physical drain");
    }
    private static void PartialEnableReturn()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        var provider = new OwnedPhysicalCases.Provider(); var controller = Controller(boundary, provider, out var created);
        provider.OnEnable = () => {
            Dispatch(boundary, provider)((CharaView*)boundary.Memory.Source, 17);
            Check(created[0].PassiveSourceBaseline is null,
                "no source return during any partial Enable can arm or seed the new baseline");
        };
        try
        {
            Check(controller.TryStart(out _), "partial Enable observation fixture starts"); controller.Update();
            Check(provider.Created.Count == 39 && controller.Snapshot.SourceReturn is null
                && created[0].PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Waiting,
                "all pre-arm installation returns remain historical and the new owner waits for a future invocation");
            Dispatch(boundary, provider)((CharaView*)boundary.Memory.Source, 18); controller.Update();
            Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5))
                && controller.Snapshot.SourceReturn?.NativeFrame == 18,
                "only the subsequent completely installed and armed source return is accepted");
        }
        finally { Stop(controller); }
    }
    private static void EntryBeforeArm()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var provider = new OwnedPhysicalCases.Provider();
        var controller = Controller(boundary, new OriginalProvider(provider), out var created);
        Task? worker = null; var started = false;
        ownedOriginalCalls = 0;
        ownedOriginal = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("owned Original release"); };
        provider.OnEnable = () => {
            if (started) return; started = true;
            worker = Task.Run(() => Dispatch(boundary, provider)((CharaView*)boundary.Memory.Source, 19));
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "actual owned Original enters before full enabling and arming");
        };
        try
        {
            Check(controller.TryStart(out _), "pre-arm native entry fixture starts"); controller.Update();
            Check(created[0].PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Waiting,
                "new baseline arms only after the complete batch enables despite a pre-existing Original");
            release.Set(); Check(worker!.Wait(TimeSpan.FromSeconds(5)), "pre-arm invocation actually returns after arm"); ownedOriginal = null;
            controller.Update();
            Check(ownedOriginalCalls == 1 && controller.Snapshot.SourceReturn is null
                && controller.Snapshot.State == PortraitOwnedScenePassiveDiagnosticState.WaitingForSourceReturn,
                "an invocation entered before arm cannot become a future source return by completing after arm");
            Dispatch(boundary, provider)((CharaView*)boundary.Memory.Source, 20); controller.Update();
            Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5))
                && controller.Snapshot.SourceReturn?.NativeFrame == 20, "a genuinely later new-owner invocation qualifies independently");
        }
        finally { release.Set(); worker?.Wait(TimeSpan.FromSeconds(5)); ownedOriginal = null; Stop(controller); }
    }
    private static void ForeignReturn()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        var provider = new OwnedPhysicalCases.Provider(); var controller = Controller(boundary, provider, out var created);
        try
        {
            Check(controller.TryStart(out _), "foreign receiver fixture starts"); controller.Update();
            var reads = boundary.Memory.Memory.Reads;
            Dispatch(boundary, provider)((CharaView*)((nint)boundary.Memory.Source + 16), 21); controller.Update();
            Check(controller.Snapshot.SourceReturn is null && created[0].PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Waiting
                && boundary.Memory.Memory.Reads == reads,
                "an opaque foreign receiver cannot be dereferenced, adopted or substituted for the exact source return");
        }
        finally { Stop(controller); }
    }
    private static void WorkerReturn()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        var provider = new OwnedPhysicalCases.Provider(); var controller = Controller(boundary, provider, out var created);
        try
        {
            Check(controller.TryStart(out _), "wrong native/managed thread fixture starts"); controller.Update();
            var reads = boundary.Memory.Memory.Reads;
            Task.Run(() => Dispatch(boundary, provider)((CharaView*)boundary.Memory.Source, 22)).GetAwaiter().GetResult();
            controller.Update();
            Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && controller.Snapshot.SourceReturn is null
                && created[0].PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Revoked
                && boundary.Memory.Memory.Reads == reads,
                "worker source returns revoke the independent thread baseline without reading native slots or creating admission");
        }
        finally { Stop(controller); }
    }
    private static void ThrowingOriginal()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        var provider = new OwnedPhysicalCases.Provider(); var controller = Controller(boundary, new OriginalProvider(provider), out _);
        var failure = new InvalidOperationException("exact native-boundary Original failure");
        ownedOriginalCalls = 0;
        try
        {
            Check(controller.TryStart(out _), "throwing Original fixture starts"); controller.Update();
            ownedOriginal = () => throw failure;
            Exception? actual = null;
            try { Dispatch(boundary, provider)((CharaView*)boundary.Memory.Source, 23); }
            catch (Exception error) { actual = error; }
            ownedOriginal = null; controller.Update();
            Check(ReferenceEquals(actual, failure) && ownedOriginalCalls == 1,
                "typed forwarding preserves the exact Original exception and executes Original once");
            Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && controller.Snapshot.SourceReturn is null,
                "an exceptional Original cannot publish successful source return or shorten physical retirement");
        }
        finally { ownedOriginal = null; Stop(controller); }
    }
    private static void ContextChangeAfterReturn()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        var provider = new OwnedPhysicalCases.Provider(); var controller = Controller(boundary, provider, out var created);
        try
        {
            Check(controller.TryStart(out _), "source drift after actual return fixture starts"); controller.Update();
            Dispatch(boundary, provider)((CharaView*)boundary.Memory.Source, 24);
            Check(created[0].PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Observed,
                "actual native return is detached before the later Framework acceptance check");
            boundary.Feature.CensusMutation(); controller.Update();
            Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && controller.Snapshot.SourceReturn is null,
                "a successful detached return cannot survive a changed current session before acceptance");
        }
        finally { Stop(controller); }
    }
    private static void DeadlineDuringEnable()
    {
        using var boundary = new CensusFeatureBoundary(); CompleteCensus(boundary);
        var now = 0L;
        var provider = new OwnedPhysicalCases.Provider();
        var controller = Controller(boundary, provider, out var created, () => now, 1000);
        provider.OnEnable = () => now = 5000;
        try
        {
            Check(controller.TryStart(out _), "total deadline during Enable fixture starts"); controller.Update();
            Check(controller.Snapshot.State != PortraitOwnedScenePassiveDiagnosticState.WaitingForSourceReturn
                && created[0].PassiveSourceBaseline is not { State: PortraitOwnedScenePassiveBaselineState.Waiting },
                "a fallible native install that reaches the total deadline cannot arm any new future source baseline");
            Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && controller.Snapshot.SourceReturn is null,
                "expiry during native install physically retires without any source publication");
        }
        finally { Stop(controller); }
    }
    public static void RenderOriginal(CharaView* view, uint frame) { Interlocked.Increment(ref ownedOriginalCalls); ownedOriginal?.Invoke(); }
    private sealed class OriginalProvider(OwnedPhysicalCases.Provider inner) : IPortraitQualifiedNativeHookProvider
    {
        public string Identity => inner.Identity;
        public bool TryQualifyStartDisabled(out string reason) => inner.TryQualifyStartDisabled(out reason);
        public bool QualificationPreserved() => inner.QualificationPreserved();
        public Delegate DirectTarget(nint address, Type type) => inner.DirectTarget(address, type);
        public IPortraitNativePhaseHostHook CreateQualifiedHook(nint address, Type type, Delegate detour, Func<bool> verifyEntry)
        {
            var hook = inner.CreateQualifiedHook(address, type, detour, verifyEntry);
            var invoke = type.GetMethod("Invoke")!;
            if (invoke.ReturnType != typeof(void) || !invoke.GetParameters().Select(value => value.ParameterType)
                .SequenceEqual(new[] { typeof(CharaView*), typeof(uint) })) return hook;
            return new OriginalHook(hook, Delegate.CreateDelegate(type, typeof(OwnedPassiveHandoffCases).GetMethod(nameof(RenderOriginal), BindingFlags.Public | BindingFlags.Static)!));
        }
    }
    private sealed class OriginalHook(IPortraitNativePhaseHostHook inner, Delegate original) : IPortraitNativePhaseHostHook
    {
        public nint Address => inner.Address;
        public string BackendName => inner.BackendName;
        public bool IsEnabled => inner.IsEnabled;
        public bool IsDisposed => inner.IsDisposed;
        public Delegate Original => original;
        public void Enable() => inner.Enable();
        public void Disable() => inner.Disable();
        public void Dispose() => inner.Dispose();
    }
    private sealed class PassiveEnvironment(nint module) : IPortraitOwnedSceneHookEnvironment
    {
        public bool IsFrameworkThread => true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason) { reason = "scene admission refused"; return false; }
        public bool IsSourceCurrent(PortraitIdentity source) => false;
        public bool IsSlotIdle(uint slot) => false;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = uint.MaxValue; return false; }
    }
}
