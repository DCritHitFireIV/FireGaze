using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

var passed = 0; var failed = 0; var cases = 0;
void Check(bool condition, string message)
{ if (!condition) throw new InvalidOperationException(message); passed++; Console.WriteLine("PASS " + message); }
void Case(string name, Action test)
{ cases++; try { test(); } catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.GetType().Name} {error.Message}"); } }
void Throws<T>(Action action, string message) where T : Exception
{ try { action(); } catch (T) { Check(true, message); return; } throw new InvalidOperationException(message); }

unsafe
{
    Case("construction and disabled wrappers are inert", () => {
        var reads = 0;
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => { reads++; return 0; }, timestampFrequency: 1000,
            threadStamp: () => { reads++; return new(1, 2); });
        var wrap = new PortraitNativePhaseObservationWrappers(recorder); var calls = 0;
        var render = wrap.ManagerRender(manager => { Check((nint)manager == 0x1234, "disabled wrapper preserves its receiver"); calls++; });
        render((RenderManager*)0x1234);
        Check(reads == 0 && calls == 1 && recorder.Snapshot().Records.Count == 0 && !recorder.Snapshot().ProductionAdmission,
            "construction and disabled observation read no clocks, threads, native state, or targets");
    });
    Case("bounded ring and detached snapshots", () => {
        long now = 0;
        using var recorder = new PortraitNativePhaseObservation(capacity: 4, timestamp: () => now++, timestampFrequency: 1000);
        Check(recorder.TryStart(TimeSpan.FromSeconds(1)), "capture starts only by explicit bounded opt-in");
        var render = new PortraitNativePhaseObservationWrappers(recorder).ManagerRender(_ => { });
        for (var i = 0; i < 5; i++) render((RenderManager*)0x1010);
        var snapshot = recorder.Snapshot();
        Check(snapshot.Records.Select(row => row.Sequence).SequenceEqual(new long[] { 7, 8, 9, 10 }) && snapshot.OverwrittenRecords == 6,
            "ten events retain only the newest four ordered records and account for overwritten records");
        var detached = snapshot.Records.ToArray(); detached[0] = default;
        Check(recorder.Snapshot().Records[0].Sequence == 7 && snapshot.ObservedThreads == 1,
            "snapshot consumers cannot mutate the recorder and thread storage remains bounded");
    });
    Case("nested phase order and evidence provenance", () => {
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000);
        Check(recorder.TryStart(TimeSpan.FromSeconds(1)), "nested capture starts");
        var wrap = new PortraitNativePhaseObservationWrappers(recorder);
        var update = wrap.ManagerUpdateCandidate((manager, delta) => Check((nint)manager == 0x1000 && delta == .125f, "update candidate forwards this and XMM1 float unchanged"));
        var source = wrap.SourceRender((view, frame) => { Check((nint)view == 0x3000 && frame == 77, "source render forwards the native frame argument"); update((RenderManager*)0x1000, .125f); });
        var camera = wrap.SceneCameraUpdate(manager => { Check((nint)manager == 0x2000, "camera update forwards this"); source((CharaView*)0x3000, 77); });
        wrap.ManagerRender(_ => camera((SceneCameraManager*)0x2000))((RenderManager*)0x1000);
        var rows = recorder.Snapshot().Records;
        Check(rows.Count == 8 && rows.Select(row => row.Edge).SequenceEqual(new[] { PortraitNativeObservedEdge.Enter, PortraitNativeObservedEdge.Enter,
            PortraitNativeObservedEdge.Enter, PortraitNativeObservedEdge.Enter, PortraitNativeObservedEdge.Return, PortraitNativeObservedEdge.Return,
            PortraitNativeObservedEdge.Return, PortraitNativeObservedEdge.Return }), "nested entry and exit order brackets the exact original calls");
        Check(rows.Select(row => row.Depth).SequenceEqual(new[] { 0, 1, 2, 3, 3, 2, 1, 0 })
            && rows[3].ParentCall == rows[2].Call && rows[2].ParentCall == rows[1].Call && rows[1].ParentCall == rows[0].Call,
            "parent call identities and thread-local depth preserve native nesting");
        Check(rows[2].Frame == new PortraitNativeFrameEvidence(PortraitNativeFrameOrigin.SourceRenderArgument, 77, 0x3000)
            && rows[3].Frame == new PortraitNativeFrameEvidence(PortraitNativeFrameOrigin.EnclosingSourceRenderArgument, 77, 0x3000)
            && rows[0].Frame.Origin == PortraitNativeFrameOrigin.None && rows[6].Frame.Origin == PortraitNativeFrameOrigin.None,
            "frame provenance comes only from the observed source argument and its same-thread nested scope");
    });
    Case("sequential phases never inherit a guessed frame", () => {
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
        var wrap = new PortraitNativePhaseObservationWrappers(recorder);
        wrap.SourceRender((_, _) => { })((CharaView*)0x3333, uint.MaxValue);
        wrap.ManagerRender(_ => { })((RenderManager*)0x1111);
        wrap.SceneCameraUpdate(_ => { })((SceneCameraManager*)0x2222);
        var rows = recorder.Snapshot().Records;
        Check(rows[0].Frame.Value == uint.MaxValue && rows.Skip(2).All(row => row.Frame.Origin == PortraitNativeFrameOrigin.None),
            "independent later manager or camera calls remain uncorrelated instead of borrowing a previous frame or log sequence");
    });
    Case("recursive source frames restore the enclosing evidence", () => {
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
        var wrap = new PortraitNativePhaseObservationWrappers(recorder);
        var manager = wrap.ManagerRender(_ => { });
        var inner = wrap.SourceRender((_, _) => manager((RenderManager*)0x1111));
        wrap.SourceRender((_, _) => { inner((CharaView*)0x4444, 0); manager((RenderManager*)0x1111); })((CharaView*)0x3333, uint.MaxValue);
        var enters = recorder.Snapshot().Records.Where(row => row.Edge == PortraitNativeObservedEdge.Enter).ToArray();
        Check(enters[2].Frame.Value == 0 && enters[2].Frame.SourceView == 0x4444 && enters[3].Frame.Value == uint.MaxValue
            && enters[3].Frame.SourceView == 0x3333, "native frame zero and wraparound remain data while nested evidence restores on return");
    });
    Case("real native threads have independent stacks", () => {
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var wrap = new PortraitNativePhaseObservationWrappers(recorder);
        var first = new Thread(() => wrap.SourceRender((_, _) => { entered.Set(); release.Wait(); })((CharaView*)0x3333, 90));
        first.Start(); entered.Wait();
        var second = new Thread(() => wrap.ManagerRender(_ => { })((RenderManager*)0x1111)); second.Start(); second.Join(); release.Set(); first.Join();
        var rows = recorder.Snapshot().Records;
        Check(rows.Select(row => row.Thread.NativeID).Distinct().Count() == 2 && rows.Select(row => row.Thread.ManagedID).Distinct().Count() == 2
            && rows.All(row => row.Thread.NativeID != 0), "records capture actual Windows native thread ids as well as managed ids");
        Check(rows.All(row => row.Depth == 0 && row.ParentCall == 0) && rows[1].Frame.Origin == PortraitNativeFrameOrigin.None,
            "overlapping work on another native thread cannot inherit nesting or source-frame evidence");
    });
    Case("original exception identity survives", () => {
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
        var expected = new InvalidOperationException("native original failure"); var count = 0; Exception? actual = null;
        try { new PortraitNativePhaseObservationWrappers(recorder).ManagerRender(_ => { count++; throw expected; })((RenderManager*)0x1111); }
        catch (Exception error) { actual = error; }
        Check(count == 1 && ReferenceEquals(actual, expected), "the original runs exactly once and its exact exception propagates");
        Check(recorder.Snapshot().Records.Select(row => row.Edge).SequenceEqual(new[] { PortraitNativeObservedEdge.Enter, PortraitNativeObservedEdge.Throw }),
            "an original exception records a throw edge without claiming a successful return");
    });
    Case("every typed original is invoked exactly once on failure", () => {
        foreach (var phase in Enum.GetValues<PortraitNativeObservedPhase>()) {
            using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
            var wrap = new PortraitNativePhaseObservationWrappers(recorder); var count = 0;
            var expected = new InvalidOperationException("original " + phase); Exception? actual = null;
            try {
                switch (phase) {
                    case PortraitNativeObservedPhase.ManagerRender: wrap.ManagerRender(_ => { count++; throw expected; })((RenderManager*)0x1111); break;
                    case PortraitNativeObservedPhase.ManagerUpdateCandidate: wrap.ManagerUpdateCandidate((_, _) => { count++; throw expected; })((RenderManager*)0x1111, .25f); break;
                    case PortraitNativeObservedPhase.SceneCameraUpdate: wrap.SceneCameraUpdate(_ => { count++; throw expected; })((SceneCameraManager*)0x2222); break;
                    case PortraitNativeObservedPhase.SourceCharaViewRender: wrap.SourceRender((_, _) => { count++; throw expected; })((CharaView*)0x3333, 5); break;
                }
            } catch (Exception error) { actual = error; }
            Check(count == 1 && ReferenceEquals(actual, expected) && recorder.Snapshot().Records.Select(row => row.Edge)
                .SequenceEqual(new[] { PortraitNativeObservedEdge.Enter, PortraitNativeObservedEdge.Throw }),
                "typed original and exception identity are preserved for " + phase);
        }
    });
    Case("observer entry failure never replaces native execution", () => {
        var calls = 0; var reads = 0;
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => reads++ == 0 ? 0 : throw new Exception("diagnostic clock"), timestampFrequency: 1000);
        Check(recorder.TryStart(TimeSpan.FromSeconds(1)), "observer-failure capture starts without game work");
        new PortraitNativePhaseObservationWrappers(recorder).ManagerRender(_ => calls++)((RenderManager*)0x1111);
        var snapshot = recorder.Snapshot();
        Check(calls == 1 && !snapshot.Observing && snapshot.Stop == PortraitNativeObservationStop.ObserverFailure && snapshot.Records.Count == 0,
            "diagnostic entry failure disables observation while the original still executes once");
    });
    Case("observer exit failure cannot hide an original exception", () => {
        var reads = 0; var calls = 0;
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => ++reads <= 2 ? 0 : throw new Exception("diagnostic clock"), timestampFrequency: 1000);
        recorder.TryStart(TimeSpan.FromSeconds(1)); var expected = new ApplicationException("original"); Exception? actual = null;
        try { new PortraitNativePhaseObservationWrappers(recorder).ManagerRender(_ => { calls++; throw expected; })((RenderManager*)0x1111); }
        catch (Exception error) { actual = error; }
        Check(calls == 1 && ReferenceEquals(actual, expected) && recorder.Snapshot().Stop == PortraitNativeObservationStop.ObserverFailure,
            "diagnostic exit failure is distinguished from and never suppresses the original exception");
    });
    Case("expiry stops at the deadline without fake exit events", () => {
        long now = 0;
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => now, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
        var count = 0; var render = new PortraitNativePhaseObservationWrappers(recorder).ManagerRender(_ => { count++; now = 1000; });
        render((RenderManager*)0x1111); render((RenderManager*)0x1111);
        var snapshot = recorder.Snapshot();
        Check(count == 2 && !snapshot.Observing && snapshot.Stop == PortraitNativeObservationStop.Expired
            && snapshot.Records.Count == 1 && snapshot.Records[0].Edge == PortraitNativeObservedEdge.Enter,
            "expired observation stops while originals continue and leaves incomplete capture explicit");
    });
    Case("explicit stop and new captures reject stale completion", () => {
        long now = 0;
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => now, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
        var wrap = new PortraitNativePhaseObservationWrappers(recorder);
        wrap.ManagerRender(_ => { recorder.Stop(); Check(recorder.TryStart(TimeSpan.FromSeconds(1)), "a stopped recorder requires a new explicit capture");
            wrap.SceneCameraUpdate(_ => { })((SceneCameraManager*)0x2222); })((RenderManager*)0x1111);
        var snapshot = recorder.Snapshot();
        Check(snapshot.Records.Count == 2 && snapshot.Records.All(row => row.Phase == PortraitNativeObservedPhase.SceneCameraUpdate && row.Depth == 0)
            && snapshot.Capture == 2, "a prior capture's returning stack cannot corrupt a newly started capture");
        recorder.Stop(); var count = snapshot.Records.Count; wrap.ManagerRender(_ => { })((RenderManager*)0x1111);
        Check(recorder.Snapshot().Stop == PortraitNativeObservationStop.Explicit && recorder.Snapshot().Records.Count == count, "explicit stop retains evidence and records no later calls");
        recorder.Dispose(); Check(!recorder.TryStart(TimeSpan.FromSeconds(1)) && recorder.Snapshot().Stop == PortraitNativeObservationStop.Disposed,
            "disposed observation cannot be rearmed");
    });
    Case("bounded thread and depth storage stop safely", () => {
        using var depth = new PortraitNativePhaseObservation(maxDepth: 1, timestamp: () => 0, timestampFrequency: 1000); depth.TryStart(TimeSpan.FromSeconds(1));
        var wrap = new PortraitNativePhaseObservationWrappers(depth); var calls = 0;
        wrap.ManagerRender(_ => wrap.SceneCameraUpdate(_ => calls++)((SceneCameraManager*)0x2222))((RenderManager*)0x1111);
        Check(calls == 1 && depth.Snapshot().Stop == PortraitNativeObservationStop.DepthLimit && depth.Snapshot().Records.Count == 1,
            "depth overflow stops observation before allocating an unbounded stack without skipping original work");
        uint native = 1;
        using var threads = new PortraitNativePhaseObservation(maxThreads: 1, timestamp: () => 0, timestampFrequency: 1000, threadStamp: () => new(native, 1));
        threads.TryStart(TimeSpan.FromSeconds(1)); var render = new PortraitNativePhaseObservationWrappers(threads).ManagerRender(_ => calls++);
        render((RenderManager*)0x1111); native = 2; render((RenderManager*)0x1111);
        Check(calls == 3 && threads.Snapshot().Stop == PortraitNativeObservationStop.ThreadLimit && threads.Snapshot().ObservedThreads == 1,
            "thread inventory overflow stops observation with bounded storage and preserves native execution");
    });
    Case("invalid options and wrapper inputs are rejected", () => {
        Throws<ArgumentOutOfRangeException>(() => new PortraitNativePhaseObservation(capacity: 0), "zero-capacity recording cannot start");
        Throws<ArgumentOutOfRangeException>(() => new PortraitNativePhaseObservation(maxThreads: 65), "thread inventory has a fixed upper limit");
        Throws<ArgumentOutOfRangeException>(() => new PortraitNativePhaseObservation(maxDepth: 129), "nesting stack has a fixed upper limit");
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000);
        Check(!recorder.TryStart(TimeSpan.Zero) && !recorder.TryStart(TimeSpan.FromMinutes(6)), "capture duration must be positive and at most five minutes");
        Throws<ArgumentNullException>(() => new PortraitNativePhaseObservationWrappers(null!), "a null observation component cannot create wrappers which skip the original on entry");
        Throws<ArgumentNullException>(() => new PortraitNativePhaseObservationWrappers(recorder).ManagerRender(null!), "null original cannot produce a wrapper");
    });
    Case("a clock regression stops evidence instead of inventing order", () => {
        long now = 100;
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => now, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
        var wrap = new PortraitNativePhaseObservationWrappers(recorder); var calls = 0;
        now = 110; wrap.ManagerRender(_ => calls++)((RenderManager*)0x1111);
        now = 109; wrap.ManagerRender(_ => calls++)((RenderManager*)0x1111);
        Check(calls == 2 && recorder.Snapshot().Stop == PortraitNativeObservationStop.ObserverFailure && recorder.Snapshot().Records.Count == 2,
            "a regressed diagnostic timestamp disables capture while the original still executes");
    });
    Case("out of order completion cannot fabricate a nesting trace", () => {
        using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
        var first = recorder.Begin(PortraitNativeObservedPhase.ManagerRender, 0x1111);
        recorder.Begin(PortraitNativeObservedPhase.SceneCameraUpdate, 0x2222);
        recorder.End(first, true);
        Check(recorder.Snapshot().Stop == PortraitNativeObservationStop.InvalidNesting && recorder.Snapshot().Records.Count == 2,
            "out-of-order completion stops capture and retains the unmatched entries as evidence");
    });
    Case("wrappers never touch native receiver memory", () => {
        var memory = (byte*)NativeMemory.Alloc(4096); new Span<byte>(memory, 4096).Fill(0xA7);
        try {
            var before = new ReadOnlySpan<byte>(memory, 4096).ToArray(); var receiver = (nint)memory;
            using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000); recorder.TryStart(TimeSpan.FromSeconds(1));
            var wrap = new PortraitNativePhaseObservationWrappers(recorder); var calls = 0;
            wrap.ManagerRender(_ => calls++)((RenderManager*)receiver);
            wrap.ManagerUpdateCandidate((_, delta) => { Check(BitConverter.SingleToInt32Bits(delta) == unchecked((int)0xFFC01234), "unusual floating argument bits pass through unchanged"); calls++; })((RenderManager*)receiver, BitConverter.Int32BitsToSingle(unchecked((int)0xFFC01234)));
            wrap.SceneCameraUpdate(_ => calls++)((SceneCameraManager*)receiver); wrap.SourceRender((_, _) => calls++)((CharaView*)receiver, 12);
            Check(calls == 4 && new ReadOnlySpan<byte>(memory, 4096).SequenceEqual(before), "all four wrappers observe identifiers without writing receiver state or invoking extra originals");
        } finally { NativeMemory.Free(memory); }
    });
}

BindingCases.Run((condition, message) => Check(condition, message), Case);
Console.WriteLine($"PortraitNativePhaseObservationTests: {passed} checks, {cases} cases, {failed} failed; game calls/hooks/creation/texture operations: 0");
return failed == 0 ? 0 : 1;
