#if !CENSUS_DIAGNOSTIC
internal static class CensusDiagnosticCases
{
    internal static int Run() => throw new InvalidOperationException("FAIL: no real single-attempt source census controller is connected.");
}
#else
using FireGaze.PortraitPreview;
using System.Runtime.InteropServices;

internal static unsafe class CensusDiagnosticCases
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException("FAIL: " + name); }
    internal static int Run()
    {
        DefaultOff(); FutureReturn(); RejectContexts(); RejectThread(); Revocation(); Expiry(); Retirement(); UnknownCreation();
        Console.WriteLine($"CensusDiagnosticCases: {checks} checks passed; real Host/gateway/census, no process/game requests.");
        return 0;
    }
    private static void DefaultOff()
    {
        using var f = new DiagnosticFixture();
        Check(f.Controller.GetType().GetMethod("ReadSafetyState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) is not null,
            "controller exposes a read-only fresh physical retirement sample for formal safety evidence");
        f.Controller.Update(); _ = f.Controller.Snapshot;
        var safety = f.Controller.ReadSafetyState();
        Check(safety is { Known: true, State: "Off", Attempt: 0, BlocksOperations: false, PhysicalDrainCompleted: true,
            Source: null, Counters: null, Resources: null, ProductionAdmission: false },
            "never-started real controller has no physical Host ownership");
        Check(f.Factory.Creates == 0 && f.ThreadReads == 0 && f.ContextReads == 0 && f.ClockReads == 0 && f.Memory.Memory.Reads == 0,
            "construction and inactive polling perform no native/thread/context/clock reads");
    }
    private static void FutureReturn()
    {
        using var f = new DiagnosticFixture();
        Check(f.Controller.TryStart(out _), "explicit Framework start installs the actual eight-target batches");
        Check(f.Factory.Creates == 8 && f.ThreadBeforeHooks && f.Controller.Snapshot.State == PortraitSourceCensusDiagnosticState.ObservingThread,
            "expected Framework thread is independently frozen before Hook creation");
        var activeSafety = f.Controller.ReadSafetyState();
        Check(activeSafety is { BlocksOperations: true, PhysicalDrainCompleted: false, Source: { Hooks: 4, ReservationHeld: true },
            Counters: { Hooks: 2, ReservationHeld: true }, Resources: { Hooks: 2, ReservationHeld: true } },
            "formal lifetime evidence includes all eight installed Hooks and all three physical reservations");
        f.Source(0);
        Check(f.Memory.Memory.Reads == 0 && f.Controller.Snapshot.Result is null, "first real frame-zero return only records passive evidence");
        f.Controller.Update();
        Check(f.Controller.Snapshot.State == PortraitSourceCensusDiagnosticState.Armed && f.Memory.Memory.Reads == 0,
            "next Framework update cross-checks detached source evidence and arms only a future call");
        f.Source(1); f.Controller.Update();
        Check(f.Controller.Snapshot.Result is { Complete: true, Rows.Count: 8, ProductionAdmission: false } result
            && result.Phase.NativeFrame == 1, "future actual SourceRender return yields one historical eight-slot result");
        Check(f.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "successful one-shot attempt retires all three batches");
        Check(f.Controller.Snapshot.State == PortraitSourceCensusDiagnosticState.Off && !f.Controller.IsActive,
            "successful attempt never auto-rearms and becomes off after confirmed retirement");
        Check(f.Controller.ReadSafetyState() is { BlocksOperations: false, PhysicalDrainCompleted: true,
            Source: { State: "Retired", Hooks: 0, InFlight: 0, ReservationHeld: false },
            Counters: { State: "Retired", Hooks: 0, InFlight: 0, ReservationHeld: false },
            Resources: { State: "Retired", Hooks: 0, InFlight: 0, ReservationHeld: false } },
            "successful physical completion reports retired Hooks and released reservations for every Host");
        var reads = f.Memory.Memory.Reads;
        f.Source(2); f.Controller.Update();
        Check(f.Memory.Memory.Reads == reads, "retired late gateway calls cannot sample again");
        Check(f.Controller.TryStart(out _), "explicit subsequent attempt creates fresh hosts after confirmed retirement");
        Check(f.Controller.Snapshot.Result is null && f.Factory.Creates == 16, "new attempt cannot replay the previous result or reuse its queue");
    }
    private static void RejectContexts()
    {
        foreach (var mode in new[] { "not-framework", "source", "owner", "session", "busy", "physical-tail", "cleanup" })
        {
            using var f = new DiagnosticFixture();
            if (mode == "not-framework") f.Framework = false;
            else f.Context = mode switch {
                "source" => f.Context with { Source = default }, "owner" => f.Context with { ContentID = 0 },
                "session" => f.Context with { SessionEpoch = 0 }, _ => f.Context with { BlockedReason = mode } };
            Check(!f.Controller.TryStart(out _) && f.Factory.Creates == 0 && f.Memory.Memory.Reads == 0,
                "unqualified entry refuses all Hook creation: " + mode);
        }
    }
    private static void RejectThread()
    {
        using var f = new DiagnosticFixture(); Check(f.Controller.TryStart(out _), "thread mismatch fixture starts");
        Task.Run(() => f.Source(4)).GetAwaiter().GetResult(); f.Controller.Update();
        Check(!f.Controller.IsActive && f.Controller.Snapshot.Result is null && f.Memory.Memory.Reads == 0,
            "actual worker SourceRender evidence cannot self-qualify a callback thread");
    }
    private static void Revocation()
    {
        foreach (var mode in new[] { "owner", "session", "source", "stop", "native-finalize" })
        {
            using var f = new DiagnosticFixture(); f.Controller.TryStart(out _); f.Source(0); f.Controller.Update();
            if (mode == "stop") f.Controller.Stop("explicit stop");
            else if (mode == "native-finalize") f.Controller.Revoke("PreFinalize");
            else f.Context = mode switch {
                "owner" => f.Context with { AccountID = 17 }, "session" => f.Context with { SessionEpoch = 2 },
                _ => f.Context with { Source = f.Context.Source with { Generation = 2 } } };
            if (mode is not ("stop" or "native-finalize")) f.Controller.Update();
            f.Source(1); f.Controller.Update();
            Check(f.Memory.Memory.Reads == 0 && f.Controller.Snapshot.Result is null && !f.Controller.IsActive,
                "revocation removes old request and cannot adopt a replacement/ABA context: " + mode);
        }
        using var queued = new DiagnosticFixture(); queued.Controller.TryStart(out _); queued.Source(0); queued.Controller.Update();
        queued.Source(1); queued.Controller.Stop("stop before polling");
        Check(queued.Controller.Snapshot.Result is null, "stop discards a queued detached result before acceptance");
    }
    private static void Expiry()
    {
        using (var readonlyExpiry = new DiagnosticFixture())
        {
            Check(readonlyExpiry.Controller.TryStart(out _), "read-only expiry fixture owns fresh reservations");
            var reads = readonlyExpiry.ClockReads; readonlyExpiry.Now = 5000;
            var before = readonlyExpiry.Controller.ReadSafetyState(); var after = readonlyExpiry.Controller.ReadSafetyState();
            Check(readonlyExpiry.ClockReads == reads && before == after && readonlyExpiry.Controller.IsActive,
                "formal lifetime reads do not poll/expire recorders, consult clocks or start retirement");
        }
        using var f = new DiagnosticFixture(); f.Controller.TryStart(out _); f.Now = 5000; f.Controller.Update();
        Check(!f.Controller.IsActive && f.Memory.Memory.Reads == 0 && f.Controller.Snapshot.Result is null, "bounded observation expires without any native read");
        using var armed = new DiagnosticFixture(); armed.Controller.TryStart(out _); armed.Source(0); armed.Controller.Update();
        armed.Now = 1000; armed.Source(1); armed.Controller.Update();
        Check(!armed.Controller.IsActive && armed.Controller.Snapshot.Result is null && armed.Memory.Memory.Reads == 0, "armed deadline does not extend total attempt or admit late returns");
        using var delayed = new DiagnosticFixture();
        delayed.Factory.OnCreate = () => delayed.Now = 500;
        delayed.Controller.TryStart(out _); delayed.Now = 4500; delayed.Source(0); delayed.Controller.Update();
        delayed.Now = 5000; delayed.Source(1);
        Check(delayed.Memory.Memory.Reads == 0, "native request cannot exceed total attempt deadline when Hook installation delayed recorder start");
    }
    private static void Retirement()
    {
        using var f = new DiagnosticFixture(); f.Controller.TryStart(out _); f.Source(0); f.Controller.Update();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        SourceReturnCases.sourceOriginal = () => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(5)), "native original fixture releases"); };
        var native = Task.Run(() => f.Source(2)); Check(entered.Wait(TimeSpan.FromSeconds(5)), "native Original entered before Framework close");
        f.Controller.Stop("Feature dispose"); var drain = f.Controller.DrainPhysicalAsync();
        var inFlightSafety = f.Controller.ReadSafetyState();
        Check(!drain.IsCompleted && !f.Controller.TryStart(out _), "Framework stop returns but provider drain and new admission remain blocked while Original is active");
        // A completed false host wait is not a confirmed retirement or controller drain.
        var run = typeof(PortraitSourceCensusDiagnostic).GetField("run", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Controller)!;
        var host = (PortraitNativePhaseHost)run.GetType().GetField("Source")!.GetValue(run)!;
        Check(!host.WaitForRetirementAsync(TimeSpan.FromMilliseconds(1)).GetAwaiter().GetResult() && !drain.IsCompleted,
            "false native-retirement wait cannot release provider scope");
        release.Set(); native.GetAwaiter().GetResult(); SourceReturnCases.sourceOriginal = null;
        Check(inFlightSafety is { State: "Retiring", BlocksOperations: true, PhysicalDrainCompleted: false,
            Source: { State: "Retiring", InFlight: 1, ReservationHeld: true } },
            "stop is not physical retirement while an actual Original/completion remains in flight");
        Check(drain.Wait(TimeSpan.FromSeconds(5)) && f.Controller.Snapshot.Result is null, "physical drain finishes only after Original/completion unwind and rejects stopped result");
    }
    private static void UnknownCreation()
    {
        // Deliberately retain this controller as the plugin lifetime must do.
        using var memory = new Fixture(); var factory = new SourceReturnCases.Factory { UnknownCreate = true };
        var context = new PortraitSourceCensusContext(memory.Phase.Source, 1, 2, 1);
        var controller = Create(() => true, () => context, factory, memory, () => 0, () => new(GetCurrentThreadID(), Environment.CurrentManagedThreadId));
        Check(!controller.TryStart(out _), "unknown official Create does not start a diagnostic");
        Thread.Sleep(30);
        Check(!controller.DrainPhysicalAsync().IsCompleted && !controller.TryStart(out _)
            && controller.Snapshot.State == PortraitSourceCensusDiagnosticState.Faulted,
            "unknown Create fault retains complete reservations/provider lifetime instead of completing false retirement");
        Check(controller.ReadSafetyState() is { State: "Faulted", BlocksOperations: true, PhysicalDrainCompleted: false,
            Counters: { State: "Faulted", ReservationHeld: true } },
            "unknown Create is visible as retained faulted ownership even when no Hook handle was returned");
        GC.KeepAlive(controller);
    }
    private static PortraitSourceCensusDiagnostic Create(Func<bool> framework, Func<PortraitSourceCensusContext?> context,
        SourceReturnCases.Factory factory, Fixture memory, Func<long> clock, Func<PortraitNativeThreadStamp> thread)
        => new(framework, context, factory, memory.Memory, clock, 1000, thread,
            SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.PhaseObservation),
            SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.CounterIngress),
            SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.ResourceIngress));
    private sealed class DiagnosticFixture : IDisposable
    {
        internal readonly Fixture Memory = new();
        internal readonly SourceReturnCases.Factory Factory = new();
        internal readonly PortraitSourceCensusDiagnostic Controller;
        internal PortraitSourceCensusContext Context;
        internal bool Framework = true, ThreadBeforeHooks;
        internal long Now;
        internal int ThreadReads, ContextReads, ClockReads;
        internal DiagnosticFixture()
        {
            Memory.SetIdle(6); Context = new(Memory.Phase.Source, 1, 2, 1);
            Controller = Create(() => Framework, () => { ContextReads++; return Context; }, Factory, Memory,
                () => { ClockReads++; return Now; },
                () => { ThreadReads++; ThreadBeforeHooks = Factory.Creates == 0; return new(GetCurrentThreadID(), Environment.CurrentManagedThreadId); });
        }
        internal void Source(uint frame) => Factory.Call(PortraitNativeObservedPhase.SourceCharaViewRender, (nint)Memory.Source, frame);
        public void Dispose()
        {
            SourceReturnCases.sourceOriginal = null; Controller.Stop("fixture close");
            Check(Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "diagnostic fixture confirms physical retirement");
            Memory.Dispose();
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)] private static extern uint GetCurrentThreadID();
}
#endif
