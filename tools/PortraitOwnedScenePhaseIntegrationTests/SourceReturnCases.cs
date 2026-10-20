#if !SOURCE_RETURN
internal static class SourceReturnCases
{
    internal static int Run() => throw new InvalidOperationException("FAIL: actual SourceRender return cannot yet feed the existing issued-phase census.");
}
#else
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;
using RTManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

// The host, stable gateway, source phase and census are production components.
// Only official Hook creation/Original and process copies use external fixtures.
internal static unsafe class SourceReturnCases
{
    private static int checks, calls;
    internal static Action? sourceOriginal;
    private static Action? counterOriginal, resourceOriginal;
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("FAIL: " + message); }
    internal static int Run()
    {
        PublicationBoundaries();
        RejectForgedCompletion(); ConfirmedReturn(); AdmissionFailures(); CancellationAndNesting(); MutationHealth(); NoPeerLock();
        NoExternalDelegateUnderSinkGate(); PhysicalRetirement(); CompletionRetirement();
        Console.WriteLine($"SourceReturnCases: {checks} checks passed; real Host/gateway/phase/reader, self-allocated SDK memory only.");
        return 0;
    }
    private static bool AfterReadScope(SourceFixture fixture, int readsBefore)
        => fixture.Memory.Memory.Reads > readsBefore && typeof(PortraitOwnedSceneSourceCensus)
            .GetField("activeReturn", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Census) is null;

    private static void PublicationBoundaries()
    {
        var failures = new List<string>();
        foreach (var (name, test) in new (string, Action)[] {
            ("direct recorder capture ABA", RecorderCaptureABA), ("publication gate contention", PublicationContention),
            ("final deadline check follows recorder", FinalDeadlineOrder) })
        {
            try { test(); Console.WriteLine("PASS SourceReturnBoundary " + name); }
            catch (Exception error) { failures.Add(name + ": " + error.Message); Console.WriteLine("FAIL SourceReturnBoundary " + name + ": " + error.Message); }
        }
        if (failures.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static void RecorderCaptureABA()
    {
        using var f = new SourceFixture(); f.Start();
        Check(f.Census.TryArm(1, TimeSpan.FromSeconds(1)), "capture ABA request arms");
        f.sourceRecorder.Stop(); Check(f.sourceRecorder.TryStart(TimeSpan.FromSeconds(1)), "recorder directly restarts without changing Host health");
        var reads = f.Memory.Memory.Reads; f.Source(40);
        Check(!f.Census.TryTake(out _) && f.Memory.Memory.Reads == reads,
            "an old armed request cannot borrow a new direct recorder capture");
        Check(f.Census.TryArm(2, TimeSpan.FromSeconds(1)), "new request may explicitly freeze the replacement capture");
        f.Source(41); Check(f.Census.TryTake(out _), "replacement capture works only after fresh arming");
    }

    private static void PublicationContention()
    {
        using var f = new SourceFixture(); f.Start(); Check(f.Census.TryArm(1, TimeSpan.FromSeconds(1)), "contended publication request arms");
        var reads = f.Memory.Memory.Reads;
        var gate = typeof(PortraitOwnedSceneSourceCensus).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Census)!;
        var publisher = Thread.CurrentThread; Task? holder = null;
        using var entered = new ManualResetEventSlim(); using var clockReturning = new ManualResetEventSlim();
        f.TimestampAction = () => {
            if (holder is not null || !AfterReadScope(f, reads)) return;
            holder = Task.Run(() => {
                lock (gate) {
                    entered.Set();
                    Check(clockReturning.Wait(TimeSpan.FromSeconds(5)), "final clock is returning before gate contention is exercised");
                    Check(SpinWait.SpinUntil(() => (publisher.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5)), "publisher reaches the held gate before its owner releases it");
                }
            });
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "another thread holds the publication gate at the final clock check");
            clockReturning.Set();
        };
        f.Source(42); holder?.GetAwaiter().GetResult(); f.TimestampAction = null;
        Check(holder is not null && !f.Census.TryTake(out _), "publication contention drops the sample instead of waiting after its time check");
    }

    private static void FinalDeadlineOrder()
    {
        using var f = new SourceFixture(); f.Start(); Check(f.Census.TryArm(1, TimeSpan.FromSeconds(1)), "final deadline order request arms");
        var reads = f.Memory.Memory.Reads; var recorderChecked = false;
        f.RecorderTimestampAction = () => { if (AfterReadScope(f, reads)) { recorderChecked = true; f.Now = 1000; } };
        f.Source(43); f.RecorderTimestampAction = null;
        Check(recorderChecked && !f.Census.TryTake(out _), "request deadline is checked after the final potentially blocking recorder check");
    }
    private static void RejectForgedCompletion()
    {
        using var f = new SourceFixture(); f.Start(); f.Census.TryArm(1, TimeSpan.FromSeconds(1));
        var call = f.sourceRecorder.Begin(PortraitNativeObservedPhase.SourceCharaViewRender, (nint)f.Memory.Source, 44);
        var reads = f.Memory.Memory.Reads;
        var begin = typeof(IPortraitNativeSourceReturnSink).GetMethod("Begin")!;
        Action<bool>? fake = null;
        try {
            object? marker = null;
            if (begin.GetParameters().Length == 1) {
                var markerType = begin.GetParameters()[0].ParameterType;
                marker = Activator.CreateInstance(markerType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [f.SourceHost, new object(), call, (nint)f.Memory.Source, 44u, f.SourceHost.Health], null);
                markerType.GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(marker, [new object(), true]);
            }
            fake = (Action<bool>?)begin.Invoke(f.Census, begin.GetParameters().Length == 4
                ? [call, (nint)f.Memory.Source, 44u, f.SourceHost.Health] : [marker]);
        } catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { }
        f.sourceRecorder.TryEnd(call, true); fake?.Invoke(true);
        Check(f.Memory.Memory.Reads == reads && !f.Census.TryTake(out _),
            "manually replayed recorder call and completion cannot manufacture the private real Host return window");
    }
    private static void ConfirmedReturn()
    {
        using var f = new SourceFixture(); f.Start();
        Check(f.Census.TryArm(1, TimeSpan.FromSeconds(1)), "independently qualified known source arms bounded read-only observation");
        var reads = f.Memory.Memory.Reads;
        sourceOriginal = () => Check(f.Memory.Memory.Reads == reads && !f.Census.IsNativeReadAdmitted,
            "actual Original runs before all root/raw reads and before a return marker exists");
        var before = calls; f.Source(0); sourceOriginal = null;
        var hasResult = f.Census.TryTake(out var result);
        Check(calls == before + 1 && hasResult && result is { Complete: true } && result.Rows.Count == 8,
            "actual frame zero Original returns once and synchronously publishes a complete eight-slot census");
        Check(result!.Phase.NativeFrame == 0 && result.Rows[7].Kind == PortraitOwnedSceneSlotCensusKind.Source
            && result.Rows[6].Kind == PortraitOwnedSceneSlotCensusKind.ProvedIdle && !result.ProductionAdmission,
            "source slot, real frame zero and detached observation-only result survive the actual composition");
        Check(!f.Census.IsNativeReadAdmitted && !f.Census.TryTake(out _), "physical completion clears read marker and result is consumed only once");
        // An unreadable stranger is table data; dereferencing it would fail the allocated-memory fixture.
        f.Memory.Entry(2)->BattleChara = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)1;
        f.Memory.Entry(2)->ObjectKind = (FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind)1;
        f.Source(5); Check(f.Census.TryTake(out result) && result!.Rows[2].Kind == PortraitOwnedSceneSlotCensusKind.OccupiedUnknown,
            "unknown occupied actor is retained without dereferencing its invalid pointer");
        f.Source(6); f.Source(7); f.Source(8);
        Check(f.Census.TryTake(out var a) && f.Census.TryTake(out var b) && !f.Census.TryTake(out _)
            && a!.Phase.NativeFrame == 7 && b!.Phase.NativeFrame == 8, "bounded detached buffer retains only the newest complete observations");
    }
    private static void AdmissionFailures()
    {
        foreach (var mode in new[] { "default", "unarmed", "foreign", "thread-native", "thread-managed", "owner", "session", "build", "source", "same-value-reissue", "clock", "expired", "recorder-expired" }) {
            using var f = new SourceFixture(defaultQualification: mode == "default"); f.Start();
            var armed = f.Census.TryArm(1, TimeSpan.FromSeconds(1));
            if (mode == "default") Check(!armed, "default qualification denies observation rather than accepting callback identity");
            else Check(armed, "failure fixture starts with genuine external qualification: " + mode);
            switch (mode) {
                case "unarmed": f.Census.Cancel(); break;
                case "thread-native": f.Qualifications.Publish(f.Qualification with { Thread = f.Qualification.Thread with { NativeID = f.Qualification.Thread.NativeID + 1 } }); break;
                case "thread-managed": f.Qualifications.Publish(f.Qualification with { Thread = f.Qualification.Thread with { ManagedID = f.Qualification.Thread.ManagedID + 1 } }); break;
                case "owner": f.Qualifications.Publish(f.Qualification with { OwnerEpoch = 2 }); break;
                case "session": f.Qualifications.Publish(f.Qualification with { SessionEpoch = 2 }); break;
                case "build": f.Qualifications.Publish(f.Qualification with { BuildEpoch = 2 }); break;
                case "source": f.Qualifications.Publish(f.Qualification with { Source = f.Qualification.Source with { Model = 1 } }); break;
                case "same-value-reissue": f.Qualifications.Publish(f.Qualification); break;
                case "clock": f.Now = -1; break;
                case "expired": f.Now = 1000; break;
                case "recorder-expired": f.RecorderNow = 1000; break;
            }
            var reads = f.Memory.Memory.Reads; f.Source(1, mode == "foreign" ? 1 : null);
            Check(!f.Census.TryTake(out _) && f.Memory.Memory.Reads == reads && !f.Census.IsNativeReadAdmitted,
                "unqualified/stale source-return makes zero root/raw copies: " + mode);
        }
        using var expiry = new SourceFixture(); expiry.Start(); expiry.Census.TryArm(1, TimeSpan.FromSeconds(1));
        var readCount = 0;
        expiry.Memory.Memory.AfterRead = _ => { if (++readCount == 3) expiry.RecorderNow = 1000; };
        expiry.Source(9); Check(!expiry.Census.TryTake(out _) && !expiry.Census.IsNativeReadAdmitted,
            "recorder deadline expires during census without Snapshot polling and discards partial copies");
        Check(readCount == 3, "capture expiry prevents the next raw copy after the failing boundary");
    }
    private static void CancellationAndNesting()
    {
        foreach (var mode in new[] { "stop", "stop-start", "cancel", "rearm", "qualification", "nested-source", "nested-foreign", "throw" }) {
            using var f = new SourceFixture(); f.Start(); f.Census.TryArm(1, TimeSpan.FromSeconds(1));
            var reads = f.Memory.Memory.Reads; var nested = false; var expected = new IOException("native source fixture");
            sourceOriginal = () => {
                if (nested) return;
                switch (mode) {
                    case "stop": f.SourceHost.Stop(); break;
                    case "stop-start": f.SourceHost.Stop(); f.SourceHost.TryStart(TimeSpan.FromSeconds(1), out _); break;
                    case "cancel": f.Census.Cancel(); break;
                    case "rearm": f.Census.Cancel(); Check(f.Census.TryArm(2, TimeSpan.FromSeconds(1)), "new request can arm while old Original returns"); break;
                    case "qualification": f.Qualifications.Publish(f.Qualification); break;
                    case "nested-source": nested = true; f.Source(3); break;
                    case "nested-foreign": nested = true; f.Source(3, 1); break;
                    case "throw": throw expected;
                }
            };
            Exception? actual = null; try { f.Source(2); } catch (Exception error) { actual = error; } finally { sourceOriginal = null; }
            Check(mode == "throw" ? ReferenceEquals(actual, expected) : actual is null, "source Original exception identity is preserved: " + mode);
            Check(!f.Census.TryTake(out _) && f.Memory.Memory.Reads == reads && !f.Census.IsNativeReadAdmitted,
                "old or nested completion cannot adopt fresh request or issue phase: " + mode);
        }
        using var mid = new SourceFixture(); mid.Start(); mid.Census.TryArm(1, TimeSpan.FromSeconds(1));
        var after = 0; mid.Memory.Memory.AfterRead = _ => { if (++after == 5) mid.Census.Cancel(); };
        mid.Source(10); Check(!mid.Census.TryTake(out _) && after == 5 && !mid.Census.IsNativeReadAdmitted,
            "cancel between real copies stops further native reads and prevents partial publication");
    }
    private static void MutationHealth()
    {
        foreach (var kind in new[] { PortraitNativeObservedPhase.CounterAdd, PortraitNativeObservedPhase.CounterSub,
            PortraitNativeObservedPhase.ResourceCleanup, PortraitNativeObservedPhase.ResourceRecreate }) {
            using var f = new SourceFixture(); f.Start(); f.Census.TryArm(1, TimeSpan.FromSeconds(1));
            var reads = f.Memory.Memory.Reads;
            sourceOriginal = () => f.Factory.Call(kind, 0, 0); f.Source(11); sourceOriginal = null;
            Check(!f.Census.TryTake(out _) && f.Memory.Memory.Reads == reads, "even completed same-address mutation invalidates frozen source: " + kind);
            Check(f.Census.TryArm(2, TimeSpan.FromSeconds(1)), "new independently frozen request may observe after mutation: " + kind);
            f.Source(12); Check(f.Census.TryTake(out _), "fresh phase reads after physical mutation returned: " + kind);
            f.CounterHost.Stop(); f.ResourceHost.Stop();
            Check(f.CounterHost.Health.MutationDepth == 0 && f.ResourceHost.Health.MutationDepth == 0
                && f.CounterHost.Health.IsMutationLive && f.ResourceHost.Health.IsMutationLive,
                "recorder Stop preserves live mutation guards instead of pretending ingress retired");
            Check(f.Census.TryArm(3, TimeSpan.FromSeconds(1)), "stopped peer recorder still permits qualified mutation ingress");
            sourceOriginal = () => f.Factory.Call(kind, 0, 0); f.Source(13); sourceOriginal = null;
            Check(!f.Census.TryTake(out _), "peer mutation after recorder Stop still invalidates source sample: " + kind);
            Check(f.Census.TryArm(4, TimeSpan.FromSeconds(1)), "mid-copy mutation request arms: " + kind);
            var copies = 0;
            f.Memory.Memory.AfterRead = _ => { if (++copies == 5) f.Factory.Call(kind, 0, 0); };
            f.Source(15); f.Memory.Memory.AfterRead = null;
            Check(copies == 5 && !f.Census.TryTake(out _) && !f.Census.IsNativeReadAdmitted,
                "actual mutation mid-census prevents the next raw copy and discards whole observation: " + kind);
        }
        using (var worker = new SourceFixture()) {
            worker.Start(); worker.Census.TryArm(1, TimeSpan.FromSeconds(1));
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            resourceOriginal = () => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)), "worker mutation Original released"); };
            var pending = Task.Run(() => worker.Factory.Call(PortraitNativeObservedPhase.ResourceRecreate, 0, 0));
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "real resource Original pending on worker");
            var before = worker.Memory.Memory.Reads; worker.Source(14);
            Check(worker.ResourceHost.Health.MutationDepth == 1 && !worker.Census.TryTake(out _) && worker.Memory.Memory.Reads == before,
                "worker Original depth and immutable health block source before any root copy");
            release.Set(); pending.GetAwaiter().GetResult(); resourceOriginal = null;
        }
        foreach (var failure in new[] { "begin", "null", "dispose" }) {
            using var fault = new SourceFixture(failMutation: failure == "begin", nullMutation: failure == "null", disposeFailure: failure == "dispose"); fault.Start();
            fault.Factory.Call(PortraitNativeObservedPhase.CounterAdd, 0, 0);
            Check(fault.CounterHost.Health.Failed && !fault.Census.TryArm(1, TimeSpan.FromSeconds(1)),
                "failed actual invalidation scope is sticky and denies all future census requests: " + failure);
            fault.CounterHost.Stop(); fault.CounterHost.TryStart(TimeSpan.FromSeconds(1), out _);
            Check(fault.CounterHost.Health.Failed && !fault.Census.TryArm(2, TimeSpan.FromSeconds(1)), "Stop/Start cannot erase sticky peer uncertainty: " + failure);
        }
    }
    private static void NoPeerLock()
    {
        using var f = new SourceFixture(); f.Start();
        var peerGate = typeof(PortraitNativePhaseHost).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.CounterHost)!;
        Task task;
        var completed = false;
        Monitor.Enter(peerGate);
        try {
            task = Task.Run(() => {
                f.Qualifications.Publish(f.Qualification with { Thread = new(GetCurrentThreadId(), Environment.CurrentManagedThreadId) });
                Check(f.Census.TryArm(1, TimeSpan.FromSeconds(1)), "independent worker request arms while peer diagnostic lock is held");
                f.Source(18);
            });
            completed = task.Wait(TimeSpan.FromSeconds(3));
        } finally { Monitor.Exit(peerGate); }
        task.GetAwaiter().GetResult();
        Check(completed && f.Census.TryTake(out _), "source-return native reads never acquire a peer Host Snapshot lock");
    }
    private static void NoExternalDelegateUnderSinkGate()
    {
        using (var f = new SourceFixture())
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var source = Task.Run(() => {
                f.Start();
                f.Qualifications.Publish(f.Qualification with { Thread = new(GetCurrentThreadId(), Environment.CurrentManagedThreadId) });
                Check(f.Census.TryArm(1, TimeSpan.FromSeconds(1)), "prefix gate fixture arms");
                f.TimestampEntered = entered; f.TimestampRelease = release; f.BlockTimestampAfter = f.TimestampCalls + 1;
                f.Source(30);
            });
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "prefix readiness check reached the blocking clock delegate");
            var cancel = Task.Run(f.Census.Cancel);
            try { Check(cancel.Wait(TimeSpan.FromSeconds(1)), "Cancel is not blocked by source prefix readiness checks"); }
            finally { release.Set(); }
            source.GetAwaiter().GetResult(); cancel.GetAwaiter().GetResult();
            Check(!f.Census.TryTake(out _), "prefix cancellation prevents a later completion sample");
        }
        using (var f = new SourceFixture())
        {
            f.Start(); Check(f.Census.TryArm(1, TimeSpan.FromSeconds(1)), "publication gate fixture arms");
            var readsBefore = f.Memory.Memory.Reads;
            f.Source(29);
            var readsPerCensus = f.Memory.Memory.Reads - readsBefore;
            Check(f.Census.TryTake(out _), "publication calibration census completes");
            f.Census.Cancel();
            f.SourceHost.Stop(); f.CounterHost.Stop(); f.ResourceHost.Stop();
            f.Start();
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var publicationReads = f.Memory.Memory.Reads + readsPerCensus;
            f.TimestampEntered = entered; f.TimestampRelease = release; f.BlockTimestampAfterReadCount = publicationReads;
            f.BlockTimestampOnlyWhenSinkGateHeld = true;
            var source = Task.Run(() => {
                f.Qualifications.Publish(f.Qualification with { Thread = new(GetCurrentThreadId(), Environment.CurrentManagedThreadId) });
                Check(f.Census.TryArm(2, TimeSpan.FromSeconds(1)), "publication worker source qualifies before callback");
                f.Source(31);
            });
            var blockedUnderGate = entered.Wait(TimeSpan.FromMilliseconds(500));
            if (blockedUnderGate)
            {
                var cancel = Task.Run(f.Census.Cancel);
                try { Check(cancel.Wait(TimeSpan.FromSeconds(1)), "Cancel is not blocked by completion publication checks"); }
                finally { release.Set(); }
                cancel.GetAwaiter().GetResult();
            }
            source.GetAwaiter().GetResult();
            Check(!blockedUnderGate, "timestamp delegate is never called under the sink publication gate");
            f.Census.Cancel();
        }
    }
    private static void PhysicalRetirement()
    {
        using var f = new SourceFixture(); f.Start(); f.Census.TryArm(1, TimeSpan.FromSeconds(1));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        sourceOriginal = () => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)), "pending source Original released"); };
        var worker = Task.Run(() => {
            f.Qualifications.Publish(f.Qualification with { Thread = new(GetCurrentThreadId(), Environment.CurrentManagedThreadId) });
            Check(f.Census.TryArm(2, TimeSpan.FromSeconds(1)), "pending worker source has independent pre-callback thread qualification");
            f.Source(20);
        });
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "actual source Original blocked before physical return");
        Check(f.SourceHost.BeginRetirement(out _) && !f.SourceHost.TryCompleteRetirement(out _), "in-flight gateway prevents provider retirement");
        Check(f.Factory.Hooks.Where(pair => pair.Key == PortraitNativeObservedPhase.SourceCharaViewRender).All(pair => !pair.Value.IsDisposed),
            "source trampoline remains retained while Original is pending");
        release.Set(); worker.GetAwaiter().GetResult(); sourceOriginal = null;
        Check(!f.Census.TryTake(out _) && f.SourceHost.TryCompleteRetirement(out _), "retiring completion reads nothing and retires only after physical return");
    }
    private static void CompletionRetirement()
    {
        using var f = new SourceFixture(); f.Start();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var read = 0;
        f.Memory.Memory.AfterRead = _ => {
            if (Interlocked.Increment(ref read) == 1) {
                entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)), "in-flight actual census copy released");
            }
        };
        var worker = Task.Run(() => {
            f.Qualifications.Publish(f.Qualification with { Thread = new(GetCurrentThreadId(), Environment.CurrentManagedThreadId) });
            Check(f.Census.TryArm(1, TimeSpan.FromSeconds(1)), "completion worker qualification precedes actual Render"); f.Source(21);
        });
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "actual Original returned and completion is physically blocked in first real raw copy");
        Check(f.SourceHost.BeginRetirement(out _) && !f.SourceHost.TryCompleteRetirement(out _)
            && !f.Factory.Hooks[PortraitNativeObservedPhase.SourceCharaViewRender].IsDisposed,
            "gateway Active retains native trampoline through entire in-flight census completion");
        release.Set(); worker.GetAwaiter().GetResult(); f.Memory.Memory.AfterRead = null;
        Check(read == 1 && !f.Census.TryTake(out _) && !f.Census.IsNativeReadAdmitted && f.SourceHost.TryCompleteRetirement(out _),
            "retirement cancels next copy, discards in-flight partial census, and then releases provider");
    }

    private sealed class SourceFixture : IDisposable
    {
        internal readonly Fixture Memory = new(); internal readonly Factory Factory = new();
        internal readonly PortraitSourceCensusQualifications Qualifications = new();
        internal readonly PortraitSourceCensusQualification Qualification;
        internal readonly PortraitNativePhaseHost SourceHost, CounterHost, ResourceHost;
        internal readonly PortraitOwnedSceneSourceCensus Census;
        internal readonly PortraitNativePhaseObservation sourceRecorder;
        private readonly PortraitNativePhaseObservation counterRecorder = new(), resourceRecorder = new();
        internal long Now, RecorderNow;
        internal ManualResetEventSlim? TimestampEntered, TimestampRelease;
        internal int BlockTimestampAfter;
        internal int BlockTimestampAfterReadCount;
        internal bool BlockTimestampOnlyWhenSinkGateHeld;
        internal Action? TimestampAction, RecorderTimestampAction;
        private int timestampCalls;
        internal int TimestampCalls => Volatile.Read(ref timestampCalls);
        internal SourceFixture(bool defaultQualification = false, bool failMutation = false, bool nullMutation = false, bool disposeFailure = false)
        {
            Memory.SetIdle(6);
            Qualification = new(Memory.Phase.Source, 1, 1, 1, new(GetCurrentThreadId(), Environment.CurrentManagedThreadId));
            if (!defaultQualification) Qualifications.Publish(Qualification);
            sourceRecorder = new(timestamp: () => { RecorderTimestampAction?.Invoke(); return RecorderNow; }, timestampFrequency: 1000);
            SourceHost = new(Factory, sourceRecorder, new(), Resolve(PortraitNativePhaseTargetSet.PhaseObservation));
            PortraitOwnedSceneSourceCensus? binding = null;
            CounterHost = PortraitNativePhaseHost.CreateCounterIngress(Factory, counterRecorder, new(),
                () => failMutation ? throw new IOException("mutation fixture") : nullMutation ? null
                    : disposeFailure ? new DisposeFailure(binding!.BeginCounterMutation()) : binding?.BeginCounterMutation(), Resolve(PortraitNativePhaseTargetSet.CounterIngress));
            ResourceHost = PortraitNativePhaseHost.CreateResourceIngress(Factory, resourceRecorder, new(),
                () => binding?.BeginResourceMutation(), Resolve(PortraitNativePhaseTargetSet.ResourceIngress));
            Census = binding = new(SourceHost, CounterHost, ResourceHost, Qualifications, Memory.Memory, ReadTimestamp, 1000, capacity: 2);
        }
        private long ReadTimestamp()
        {
            var call = Interlocked.Increment(ref timestampCalls);
            TimestampAction?.Invoke();
            var gateHeld = Monitor.IsEntered(typeof(PortraitOwnedSceneSourceCensus)
                .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Census)!);
            if ((BlockTimestampAfter > 0 && call >= BlockTimestampAfter)
                || (BlockTimestampAfterReadCount > 0 && Memory.Memory.Reads >= BlockTimestampAfterReadCount
                    && (!BlockTimestampOnlyWhenSinkGateHeld || gateHeld)))
            {
                TimestampEntered?.Set();
                if (!(TimestampRelease?.Wait(TimeSpan.FromSeconds(10)) ?? false)) throw new TimeoutException("timestamp fixture release missing");
            }
            return Now;
        }
        internal void Start()
        {
            Check(CounterHost.TryStart(TimeSpan.FromSeconds(30), out _) && ResourceHost.TryStart(TimeSpan.FromSeconds(30), out _)
                && SourceHost.TryStart(TimeSpan.FromSeconds(1), out _), "actual phase/counter/resource batches start without production admission");
        }
        internal void Source(uint frame, nint? receiver = null) => Factory.Call(PortraitNativeObservedPhase.SourceCharaViewRender, receiver ?? (nint)Memory.Source, frame);
        public void Dispose()
        {
            sourceOriginal = counterOriginal = resourceOriginal = null;
            foreach (var host in new[] { SourceHost, CounterHost, ResourceHost }) { host.BeginRetirement(out _); Check(host.TryCompleteRetirement(out _), "fixture host fully retires"); }
            sourceRecorder.Dispose(); counterRecorder.Dispose(); resourceRecorder.Dispose(); Memory.Dispose();
        }
    }
    private sealed class DisposeFailure(IDisposable actual) : IDisposable
    { public void Dispose() { actual.Dispose(); throw new IOException("external scope disposal fixture"); } }
    internal static PortraitNativePhaseHostResolve Resolve(PortraitNativePhaseTargetSet set)
        => (Func<nint, bool> _, out PortraitNativePhaseResolvedPlan? plan, out string reason) => {
            var module = unchecked((nint)0x5C00000000L);
            plan = new(module, 0x3000000, PortraitNativePhaseObservationBinding.EXE, PortraitNativePhaseObservationBinding.SDK,
                typeof(RenderManager).Assembly.ManifestModule.ModuleVersionId, PortraitNativeCounterIngressContract.For(set)
                    .Select(contract => new PortraitNativePhaseBoundTarget(contract.Phase, module + (nint)contract.RVA, contract.Delegate)).ToArray());
            reason = "external native fixture"; return true;
        };
    public static void SourceNative(CharaView* view, uint frame) { Interlocked.Increment(ref calls); sourceOriginal?.Invoke(); }
    public static void ManagerNative(RenderManager* manager) { }
    public static void UpdateNative(RenderManager* manager, float delta) { }
    public static void CameraNative(SceneCameraManager* manager) { }
    public static void CounterNative(RenderManager* manager, uint slot) => counterOriginal?.Invoke();
    public static void CleanupNative(RTManager* manager) => resourceOriginal?.Invoke();
    public static byte RecreateNative(RTManager* manager) { resourceOriginal?.Invoke(); return 0xA7; }
    public static void DirectSource(CharaView* view, uint frame) { }
    internal sealed class Factory : IPortraitNativePhaseHostFactory
    {
        internal readonly Dictionary<PortraitNativeObservedPhase, Hook> Hooks = [];
        internal int Creates;
        internal bool UnknownCreate = false;
        internal Action? OnCreate;
        private readonly Dictionary<PortraitNativeObservedPhase, Delegate> detours = [];
        public string Identity => "external Hook boundary only";
        public bool TryQualifyStartDisabled(out string reason) { reason = "fixture"; return true; }
        public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason) { reason = "fixture"; return true; }
        private static string Method(PortraitNativeObservedPhase phase) => phase switch {
            PortraitNativeObservedPhase.SourceCharaViewRender => nameof(SourceNative), PortraitNativeObservedPhase.ManagerRender => nameof(ManagerNative),
            PortraitNativeObservedPhase.ManagerUpdateCandidate => nameof(UpdateNative), PortraitNativeObservedPhase.SceneCameraUpdate => nameof(CameraNative),
            PortraitNativeObservedPhase.ResourceCleanup => nameof(CleanupNative), PortraitNativeObservedPhase.ResourceRecreate => nameof(RecreateNative), _ => nameof(CounterNative) };
        public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type type) => Delegate.CreateDelegate(type,
            typeof(SourceReturnCases).GetMethod(target.Phase == PortraitNativeObservedPhase.SourceCharaViewRender ? nameof(DirectSource) : Method(target.Phase))!);
        public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type type, Delegate detour)
        {
            Creates++;
            OnCreate?.Invoke();
            if (UnknownCreate) throw new IOException("unknown Create fixture");
            detours[target.Phase] = detour; var hook = new Hook(target.Address, Delegate.CreateDelegate(type, typeof(SourceReturnCases).GetMethod(Method(target.Phase))!));
            Hooks[target.Phase] = hook; return hook;
        }
        internal byte Call(PortraitNativeObservedPhase phase, nint receiver, uint frame)
        {
            var entry = detours[phase]; var signature = entry.GetType().GetMethod("Invoke")!;
            var m = new DynamicMethod("SourceNativeCall", typeof(byte), [typeof(Delegate), typeof(nint), typeof(uint)], typeof(SourceReturnCases).Module, true);
            var il = m.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, entry.GetType()); il.Emit(OpCodes.Ldarg_1);
            if (signature.GetParameters().Length == 2) { if (phase == PortraitNativeObservedPhase.ManagerUpdateCandidate) il.Emit(OpCodes.Ldc_R4, 0f); else il.Emit(OpCodes.Ldarg_2); }
            il.Emit(OpCodes.Callvirt, signature); if (signature.ReturnType == typeof(void)) il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            return ((Func<Delegate, nint, uint, byte>)m.CreateDelegate(typeof(Func<Delegate, nint, uint, byte>)))(entry, receiver, frame);
        }
    }
    internal sealed class Hook(nint address, Delegate original) : IPortraitNativePhaseHostHook
    {
        public nint Address => address; public Delegate Original => original; public string BackendName => "SafetyHook";
        public bool IsEnabled { get; private set; } public bool IsDisposed { get; private set; }
        public void Enable() => IsEnabled = true; public void Disable() => IsEnabled = false; public void Dispose() => IsDisposed = true;
    }
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetCurrentThreadId();
}
#endif
